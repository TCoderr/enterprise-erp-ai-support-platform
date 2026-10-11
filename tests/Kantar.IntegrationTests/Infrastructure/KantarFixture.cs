using System.Security.Cryptography;
using Kantar.Api;
using Kantar.Application.Abstractions;
using Kantar.Application.Assistance;
using Kantar.Infrastructure.Persistence;
using Kantar.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.MsSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Kantar.IntegrationTests.Infrastructure.KantarFixture))]

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>
/// One disposable SQL Server container and one application host for the whole assembly. The schema
/// is created with the migrator login; the application under test connects only with the restricted
/// kantar_app login, exactly as in the local stack.
/// </summary>
public sealed class KantarFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    private readonly string _runId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private MsSqlContainer? _container;
    private string _saPassword = string.Empty;
    private string _migratorPassword = string.Empty;
    private string _appPassword = string.Empty;
    private string _host = string.Empty;
    private int _port;
    private WebApplicationFactory<Program>? _factory;

    public string DatabaseName => TestDatabaseGuard.DatabasePrefix + _runId[..12];
    public string SysadminConnectionString { get; private set; } = string.Empty;
    public string MigratorConnectionString { get; private set; } = string.Empty;
    public string AppConnectionString { get; private set; } = string.Empty;
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public TestFailpoints Failpoints { get; } = new();
    public ScriptedAnswerGenerator Answers { get; } = new();
    public TestLogs Logs { get; } = new();
    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Fixture not initialised.");
    public IServiceProvider Services => Factory.Services;

    public async ValueTask InitializeAsync()
    {
        var saPassword = NewPassword();
        _saPassword = saPassword;
        _container = new MsSqlBuilder(Image)
            .WithPassword(saPassword)
            .WithEnvironment("MSSQL_PID", "Developer")
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1280")
            .WithLabel("dev.kantar.test", _runId)
            .Build();
        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(MsSqlBuilder.MsSqlPort);
        string Connection(string user, string password, string database) => new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{port}",
            InitialCatalog = database,
            UserID = user,
            Password = password,
            TrustServerCertificate = true,
            Encrypt = true,
            ConnectTimeout = 15,
            ApplicationName = "kantar-tests",
        }.ConnectionString;

        var (migratorPassword, appPassword) = (NewPassword(), NewPassword());
        (_migratorPassword, _appPassword, _host, _port) = (migratorPassword, appPassword, host, port);
        SysadminConnectionString = Connection("sa", saPassword, "master");
        MigratorConnectionString = Connection(DatabaseBootstrapper.MigratorLogin, migratorPassword, DatabaseName);
        AppConnectionString = Connection(DatabaseBootstrapper.AppLogin, appPassword, DatabaseName);

        // Creating a brand-new database inside the container this run just started is the ownership act;
        // the marker row is what every later connection is checked against.
        await DatabaseBootstrapper.BootstrapAsync(SysadminConnectionString, DatabaseName, migratorPassword, appPassword, CancellationToken.None);
        await using (var sa = new SqlConnection(Connection("sa", saPassword, DatabaseName)))
        {
            await sa.OpenAsync();
            await using var command = sa.CreateCommand();
            command.CommandText = """
                IF SCHEMA_ID(N'ops') IS NULL EXEC(N'CREATE SCHEMA ops AUTHORIZATION dbo');
                CREATE TABLE ops.TestRunMarker (RunId char(32) NOT NULL PRIMARY KEY);
                INSERT INTO ops.TestRunMarker (RunId) VALUES (@run);
                GRANT SELECT ON ops.TestRunMarker TO kantar_app;
                """;
            command.Parameters.AddWithValue("@run", _runId);
            await command.ExecuteNonQueryAsync();
        }

        var manifest = new TestDatabaseManifest(_runId, host, port, DatabaseName, _container.Id);
        foreach (var connectionString in new[] { MigratorConnectionString, AppConnectionString })
        {
            await TestDatabaseGuard.EnsureOwnedAsync(TestDatabaseGuard.OptInValue, manifest, new SqlServerProbe(connectionString), CancellationToken.None);
        }

        await DatabaseMigrator.MigrateAsync(MigratorConnectionString, null, NullLogger.Instance, CancellationToken.None);

        // The minimal hosting model reads configuration while building, before a WebApplicationFactory
        // override would apply; environment variables are visible at that point.
        Environment.SetEnvironmentVariable("ConnectionStrings__App", AppConnectionString);
        Environment.SetEnvironmentVariable("Security__Web__CsrfKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable("Security__Web__AnonymousRequestsPerMinute", "100000");
        Environment.SetEnvironmentVariable("Security__Web__UserRequestsPerMinute", "100000");
        Environment.SetEnvironmentVariable("Security__Web__AssistRequestsPerMinute", "100000");
        Environment.SetEnvironmentVariable("Security__PasswordHashing__Iterations", "1000");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                services.RemoveAll<IFailpoints>();
                services.AddSingleton<IFailpoints>(Failpoints);
                services.RemoveAll<IAnswerGenerator>();
                services.AddSingleton<IAnswerGenerator>(Answers);
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        });

        // Forces the host to start so the first test does not pay for it.
        using var client = _factory.CreateClient();
        using var live = await client.GetAsync("/health/live");
        live.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        SqlConnection.ClearAllPools();
        if (_container is not null)
        {
            // The container was created by this run (label dev.kantar.test=<runId>) and is removed with its data.
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// A second, empty database inside this run's own container (for migration tests). It reuses the
    /// run's logins and passwords, so the main database's connections keep working.
    /// </summary>
    public async Task<(string Name, string Migrator, string App, string Sysadmin)> NewDatabaseAsync(string suffix)
    {
        var name = $"{DatabaseName}_{suffix}";
        string Connection(string user, string password) => new SqlConnectionStringBuilder
        {
            DataSource = $"{_host},{_port}", InitialCatalog = name, UserID = user, Password = password,
            TrustServerCertificate = true, Encrypt = true, ConnectTimeout = 15, ApplicationName = "kantar-tests",
        }.ConnectionString;

        await DatabaseBootstrapper.BootstrapAsync(SysadminConnectionString, name, _migratorPassword, _appPassword, CancellationToken.None);
        return (name, Connection(DatabaseBootstrapper.MigratorLogin, _migratorPassword), Connection(DatabaseBootstrapper.AppLogin, _appPassword), Connection("sa", _saPassword));
    }

    public ApiClient NewClient() => new(Factory.CreateDefaultClient(new Uri("https://localhost")));

    public World NewWorld() => new(this);

    /// <summary>Runs a query with the restricted application login, outside the application.</summary>
    public async Task<T?> AppScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> AppExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    internal static string NewPassword() =>
        "Kt!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant() + "aA9";

    private sealed class SqlServerProbe(string connectionString) : ITestDatabaseProbe
    {
        private readonly SqlConnectionStringBuilder _builder = new(connectionString);

        public string Host => _builder.DataSource.Split(',')[0];

        public int Port => int.Parse(_builder.DataSource.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);

        public async Task<string> CurrentDatabaseAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DB_NAME();";
            return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        public async Task<string?> ReadRunMarkerAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT TOP (1) RunId FROM ops.TestRunMarker;";
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
    }
}

/// <summary>Test-only failure injection, registered through DI. Nothing in the product can switch it on.</summary>
public sealed class TestFailpoints : IFailpoints
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<CancellationToken, Task>> _actions = new();

    public IDisposable On(string name, Func<CancellationToken, Task> action)
    {
        _actions[name] = action;
        return new Registration(this, name);
    }

    public Task HitAsync(string name, CancellationToken cancellationToken) =>
        _actions.TryGetValue(name, out var action) ? action(cancellationToken) : Task.CompletedTask;

    private sealed class Registration(TestFailpoints owner, string name) : IDisposable
    {
        public void Dispose() => owner._actions.TryRemove(name, out _);
    }
}

public sealed class InjectedFailureException() : Exception("Injected by a test failpoint.");
