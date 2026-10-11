using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kantar.Infrastructure.Persistence;

/// <summary>
/// Server-level bootstrap, run once with a sysadmin connection: creates the database with its
/// collation and the two logins. Nothing here runs with the application's own connection.
/// </summary>
public static partial class DatabaseBootstrapper
{
    public const string DatabaseCollation = "Turkish_100_CI_AS_SC_UTF8";
    public const string MigratorLogin = "kantar_migrator";
    public const string AppLogin = "kantar_app";

    // \z, not $: "$" also matches before a trailing line feed, which would let "name\n" through.
    [GeneratedRegex(@"^[a-z][a-z0-9_]{2,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();

    public static async Task BootstrapAsync(
        string sysadminConnectionString,
        string databaseName,
        string migratorPassword,
        string appPassword,
        CancellationToken cancellationToken)
    {
        // Identifiers cannot be parameters, so the name is restricted to a strict pattern instead.
        if (!DatabaseNamePattern().IsMatch(databaseName))
        {
            throw new ArgumentException("Database name must match ^[a-z][a-z0-9_]{2,62}$.", nameof(databaseName));
        }

        var master = new SqlConnectionStringBuilder(sysadminConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(
                connection,
                $"""
                IF DB_ID(N'{databaseName}') IS NULL
                    CREATE DATABASE [{databaseName}] COLLATE {DatabaseCollation};
                """,
                cancellationToken);
            await ExecuteAsync(connection, $"ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;", cancellationToken);
            await ExecuteAsync(connection, $"ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION OFF;", cancellationToken);
            await EnsureLoginAsync(connection, MigratorLogin, migratorPassword, cancellationToken);
            await EnsureLoginAsync(connection, AppLogin, appPassword, cancellationToken);
        }

        var target = new SqlConnectionStringBuilder(sysadminConnectionString) { InitialCatalog = databaseName }.ConnectionString;
        await using (var connection = new SqlConnection(target))
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(
                connection,
                $"""
                IF DATABASE_PRINCIPAL_ID(N'{MigratorLogin}') IS NULL CREATE USER [{MigratorLogin}] FOR LOGIN [{MigratorLogin}];
                IF DATABASE_PRINCIPAL_ID(N'{AppLogin}') IS NULL CREATE USER [{AppLogin}] FOR LOGIN [{AppLogin}];
                IF IS_ROLEMEMBER(N'db_owner', N'{MigratorLogin}') = 0 ALTER ROLE db_owner ADD MEMBER [{MigratorLogin}];
                """,
                cancellationToken);
        }
    }

    private static async Task EnsureLoginAsync(SqlConnection connection, string login, string password, CancellationToken cancellationToken)
    {
        // CREATE LOGIN does not accept a parameter for the password; the statement is built by the
        // server from a quoted literal (QUOTENAME with a single quote) and executed dynamically.
        const string sql = """
            DECLARE @statement nvarchar(max);
            IF SUSER_ID(@login) IS NULL
                SET @statement = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N', CHECK_POLICY = ON, DEFAULT_DATABASE = [master];';
            ELSE
                SET @statement = N'ALTER LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N';';
            EXEC sys.sp_executesql @statement;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@login", System.Data.SqlDbType.NVarChar, 128) { Value = login });
        command.Parameters.Add(new SqlParameter("@password", System.Data.SqlDbType.NVarChar, 128) { Value = password });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Schema migration with the migrator login: migrations first, then the runtime grants.</summary>
public static partial class DatabaseMigrator
{
    public static async Task MigrateAsync(string migratorConnectionString, string? targetMigration, ILogger logger, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<KantarDbContext>()
            .UseSqlServer(migratorConnectionString, sql => sql.CommandTimeout(180))
            .Options;
        await using var db = new KantarDbContext(options);

        if (targetMigration is null)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await db.Database.MigrateAsync(targetMigration, cancellationToken);
        }

        await ApplyGrantsAsync(migratorConnectionString, cancellationToken);
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Count();
        LogMigrated(logger, applied);
    }

    public static async Task ApplyGrantsAsync(string migratorConnectionString, CancellationToken cancellationToken)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kantar.Infrastructure.grants.sql")
            ?? throw new InvalidOperationException("Embedded grants.sql is missing.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);

        await using var connection = new SqlConnection(migratorConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Database migrated; {Applied} migrations applied in total and runtime grants refreshed")]
    private static partial void LogMigrated(ILogger logger, int applied);
}
