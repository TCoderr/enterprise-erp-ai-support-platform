using Kantar.Application.Abstractions;
using Kantar.Infrastructure;
using Kantar.Infrastructure.Operations;
using Kantar.Infrastructure.Persistence;
using Kantar.Infrastructure.Seeding;

namespace Kantar.Api.Hosting;

/// <summary>Process modes that run once and exit. They never serve HTTP.</summary>
public static class OneShotCommands
{
    private static (IConfiguration Configuration, ILoggerFactory Logging) Setup(string[] args)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
        WebHost.AddSecretFiles(configuration);
        var logging = LoggerFactory.Create(b => b.AddJsonConsole(console => console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "));
        return (configuration, logging);
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} is not configured.");

    /// <summary>Creates the database and the two logins. Needs a sysadmin connection; used once per stack.</summary>
    public static async Task<int> BootstrapAsync(string[] args)
    {
        var (configuration, logging) = Setup(args);
        using var _ = logging;
        await DatabaseBootstrapper.BootstrapAsync(
            Required(configuration, "ConnectionStrings:Bootstrap"),
            configuration["Database:Name"] ?? "kantar",
            Required(configuration, "Database:MigratorPassword"),
            Required(configuration, "Database:AppPassword"),
            CancellationToken.None);
        Console.Out.WriteLine("bootstrap: database and logins are in place");
        return 0;
    }

    public static async Task<int> MigrateAsync(string[] args)
    {
        var (configuration, logging) = Setup(args);
        using var _ = logging;
        await DatabaseMigrator.MigrateAsync(
            Required(configuration, "ConnectionStrings:Migrator"),
            configuration["Database:TargetMigration"],
            logging.CreateLogger("Kantar.Migrate"),
            CancellationToken.None);
        return 0;
    }

    private static HostApplicationBuilder BackgroundHost(string[] args, string correlationPrefix)
    {
        var builder = Host.CreateApplicationBuilder(args);
        WebHost.AddSecretFiles(builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console => console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ");
        builder.Services.AddKantar(builder.Configuration);
        builder.Services.AddSingleton<IRequestContext>(new FixedRequestContext(correlationPrefix + "-" + Guid.CreateVersion7().ToString("N")[..24]));
        return builder;
    }

    /// <summary>Idempotent demo data through the application's own commands, with the restricted login.</summary>
    public static async Task<int> SeedAsync(string[] args)
    {
        var builder = BackgroundHost(args, "seed");
        if (Directory.Exists(SeedSecretsDirectory))
        {
            builder.Configuration.AddKeyPerFile(SeedSecretsDirectory, optional: true);
        }

        builder.Services.AddScoped<DemoSeeder>();
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None);
    }

    /// <summary>The worker process: outbox delivery and maintenance. Stops gracefully on SIGTERM.</summary>
    public static async Task<int> WorkerAsync(string[] args)
    {
        var builder = BackgroundHost(args, "worker");
        builder.Services.AddKantarWorkers();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
        using var host = builder.Build();
        await host.RunAsync();
        return 0;
    }

    /// <summary>Worker health check: the heartbeat file must have been written in the last 30 seconds.</summary>
    public static int WorkerHeartbeat()
    {
        var file = new WorkerOptions().HeartbeatFile;
        return File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromSeconds(30) ? 0 : 1;
    }

    /// <summary>
    /// Runs the synthetic live-model scenarios against the configured LOCAL model server. Returns 4
    /// ("not run") when no local model is configured or the server does not answer; it never falls
    /// back to the demo provider and never contacts a non-local endpoint.
    /// </summary>
    public static async Task<int> AiLiveAsync(string[] args)
    {
        var (configuration, logging) = Setup(args);
        using var _ = logging;
        var options = configuration.GetSection("Ai").Get<Infrastructure.Assistance.AiOptions>() ?? new Infrastructure.Assistance.AiOptions();
        if (options.Provider != "LocalModel")
        {
            Console.Out.WriteLine("ai-live: Ai:Provider is not LocalModel; nothing was called.");
            return 4;
        }

        using var generator = new Infrastructure.Assistance.LocalModelAnswerGenerator(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var results = await Infrastructure.Assistance.LiveModelCheck.RunAsync(generator, timeout.Token);
        if (results.All(r => r.Outcome == nameof(Application.Assistance.GenerationOutcome.Unavailable)))
        {
            Console.Out.WriteLine("ai-live: the local model server did not answer any request.");
            return 4;
        }

        foreach (var result in results)
        {
            Console.Out.WriteLine($"SCENARIO {result.Name} {(result.Passed ? "PASS" : "FAIL")} [{result.Outcome}] {result.Detail}");
            if (result.AnswerText is { Length: > 0 } text)
            {
                // Synthetic questions and synthetic sources only; printed for a person to read.
                Console.Out.WriteLine("  answer: " + text.Replace('\n', ' '));
            }
        }

        Console.Out.WriteLine($"ai-live: model {options.LocalModel.Model}, passed {results.Count(r => r.Passed)} of {results.Count}");
        return results.All(r => r.Passed) ? 0 : 1;
    }

    public const string SeedSecretsDirectory = "/run/seed";

    private sealed class FixedRequestContext(string correlationId) : IRequestContext
    {
        public string CorrelationId { get; } = correlationId;
    }

    /// <summary>Container health probe: GET a local URL and exit 0 on 200. Avoids shipping curl in the image.</summary>
    public static async Task<int> ProbeAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--probe");
        if (index < 0 || index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var url) || !url.IsLoopback)
        {
            return 2;
        }

        // The probe talks to this container's own listener, whose development certificate is not in the trust store.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
