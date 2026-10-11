using Kantar.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kantar.Infrastructure.Operations;

public sealed class WorkerOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromMinutes(15);
    public int BatchSize { get; set; } = 10;

    /// <summary>Touched on every loop; the container health check reads its age.</summary>
    public string HeartbeatFile { get; set; } = Path.Combine(Path.GetTempPath(), "kantar-worker.heartbeat");
}

/// <summary>
/// Polls the outbox. On shutdown it stops taking new leases and lets the message in hand finish:
/// the in-flight transaction is not cancelled, and anything not finished simply stays leased until
/// the lease runs out and another worker picks it up.
/// </summary>
public sealed partial class OutboxWorker(IServiceScopeFactory scopes, WorkerOptions options, ILogger<OutboxWorker> logger) : BackgroundService
{
    private readonly string _owner = BuildOwner();

    private static string BuildOwner()
    {
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return owner.Length <= 64 ? owner : owner[..64];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, _owner);
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await RunOnceAsync(stoppingToken);
                Heartbeat();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database unavailable and similar: keep the process alive and try again after the poll interval.
                LogLoopFailed(logger, ex.GetType().Name);
            }

            if (handled == 0)
            {
                try
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        LogStopped(logger, _owner);
    }

    public async Task<int> RunOnceAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        var leased = await dispatcher.LeaseAsync(_owner, options.BatchSize, stoppingToken);
        foreach (var message in leased)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Not tied to the stopping token: a message that has started is allowed to commit.
            using var perMessage = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var messageScope = scopes.CreateAsyncScope();
            await messageScope.ServiceProvider.GetRequiredService<OutboxDispatcher>().ProcessAsync(_owner, message, perMessage.Token);
        }

        return leased.Count;
    }

    private void Heartbeat()
    {
        try
        {
            File.WriteAllText(options.HeartbeatFile, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // A missing heartbeat makes the health check fail, which is the correct signal.
        }
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Outbox worker {Owner} started")]
    private static partial void LogStarted(ILogger logger, string owner);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "Outbox worker {Owner} stopped after finishing in-flight work")]
    private static partial void LogStopped(ILogger logger, string owner);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "Outbox loop iteration failed ({ErrorType}); retrying after the poll interval")]
    private static partial void LogLoopFailed(ILogger logger, string errorType);
}

/// <summary>Periodic cleanup of expired rows and the read-only stock reconciliation.</summary>
public sealed partial class MaintenanceWorker(IServiceScopeFactory scopes, WorkerOptions options, TimeProvider clock, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = clock.GetUtcNow();
        var nextReconciliation = clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = clock.GetUtcNow();
                if (now >= nextCleanup)
                {
                    nextCleanup = now + options.CleanupInterval;
                    await using var scope = scopes.CreateAsyncScope();
                    var deleted = await scope.ServiceProvider.GetRequiredService<OperationsService>().CleanupAsync(stoppingToken);
                    LogCleanup(logger, deleted);
                }

                if (now >= nextReconciliation)
                {
                    nextReconciliation = now + options.ReconciliationInterval;
                    await using var scope = scopes.CreateAsyncScope();
                    var run = await scope.ServiceProvider.GetRequiredService<OperationsService>().RunReconciliationAsync(null, stoppingToken);
                    if (run.FindingCount > 0)
                    {
                        LogDrift(logger, run.FindingCount, run.BalancesChecked);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "Maintenance removed {Deleted} expired rows")]
    private static partial void LogCleanup(ILogger logger, int deleted);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Error, Message = "Stock reconciliation found {Findings} differences across {Balances} balances; nothing was changed")]
    private static partial void LogDrift(ILogger logger, int findings, int balances);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning, Message = "Maintenance iteration failed ({ErrorType})")]
    private static partial void LogFailed(ILogger logger, string errorType);
}
