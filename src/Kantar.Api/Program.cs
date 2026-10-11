using Kantar.Api.Hosting;

namespace Kantar.Api;

public sealed class Program
{
    /// <summary>
    /// One image, several processes: web (default), --worker, and the one-shot --bootstrap, --migrate,
    /// --seed and --probe modes. Only the web and worker modes run with the restricted database login.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--probe"))
        {
            return await OneShotCommands.ProbeAsync(args);
        }

        if (args.Contains("--bootstrap"))
        {
            return await OneShotCommands.BootstrapAsync(args);
        }

        if (args.Contains("--migrate"))
        {
            return await OneShotCommands.MigrateAsync(args);
        }

        if (args.Contains("--ai-live"))
        {
            return await OneShotCommands.AiLiveAsync(args);
        }

        if (args.Contains("--seed"))
        {
            return await OneShotCommands.SeedAsync(args);
        }

        if (args.Contains("--worker-heartbeat"))
        {
            return OneShotCommands.WorkerHeartbeat();
        }

        if (args.Contains("--worker"))
        {
            return await OneShotCommands.WorkerAsync(args);
        }

        var app = WebHost.Build(args);
        await app.RunAsync();
        return 0;
    }
}
