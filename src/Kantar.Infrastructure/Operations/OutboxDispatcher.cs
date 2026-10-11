using System.Data;
using System.Text.Json;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Inventory;
using Kantar.Application.Operations;
using Kantar.Application.Support;
using Kantar.Domain.Identity;
using Kantar.Domain.Operations;
using Kantar.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kantar.Infrastructure.Operations;

public sealed record LeasedMessage(Guid Id, string Type, string PayloadJson, long LeaseVersion, int Attempts);

/// <summary>
/// Delivers outbox rows written by business transactions. Delivery is at-least-once; the effect is
/// exactly-once because each handler records (message, handler) in the same transaction as its effect.
/// A worker owns a message only while its lease is current: completion is conditional on the lease
/// owner and lease version (the fencing token), so a worker that stalled past its lease cannot
/// complete, or double-apply, a message another worker has taken over.
/// </summary>
public sealed partial class OutboxDispatcher(
    KantarDbContext db,
    IUnitOfWork unitOfWork,
    IFailpoints failpoints,
    TimeProvider clock,
    OpsMetrics metrics,
    ILogger<OutboxDispatcher> logger)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    // READPAST skips rows another worker has locked right now; UPDLOCK makes the read-then-update of
    // the selected rows atomic. Valid under READ COMMITTED, which is what this connection uses.
    private const string LeaseSql = """
        WITH next AS (
            SELECT TOP (@batch) Id, Type, PayloadJson, LeaseOwner, LeaseUntil, LeaseVersion, Attempts
            FROM ops.OutboxMessages WITH (READPAST, UPDLOCK, ROWLOCK)
            WHERE ProcessedAt IS NULL AND AvailableAt <= @now AND (LeaseUntil IS NULL OR LeaseUntil < @now)
            ORDER BY CreatedAt, Id)
        UPDATE next
        SET LeaseOwner = @owner, LeaseUntil = @until, LeaseVersion = LeaseVersion + 1, Attempts = Attempts + 1
        OUTPUT inserted.Id, inserted.Type, inserted.PayloadJson, inserted.LeaseVersion, inserted.Attempts;
        """;

    public async Task<IReadOnlyList<LeasedMessage>> LeaseAsync(string owner, int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var command = await db.CreateCommandAsync(LeaseSql, cancellationToken);
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batchSize });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        command.Parameters.Add(new SqlParameter("@until", SqlDbType.DateTime2) { Value = now + LeaseDuration });
        command.Parameters.Add(new SqlParameter("@owner", SqlDbType.VarChar, 64) { Value = owner });

        var leased = new List<LeasedMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            leased.Add(new LeasedMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4)));
        }

        return leased;
    }

    /// <summary>
    /// Handles one leased message in one transaction: consumption marker + effect + conditional
    /// completion. Returns false when the lease was lost, in which case nothing was committed.
    /// </summary>
    public async Task<bool> ProcessAsync(string owner, LeasedMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var completed = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    var handler = HandlerName(message.Type);
                    var alreadyConsumed = await db.OutboxConsumptions.AnyAsync(c => c.MessageId == message.Id && c.Handler == handler, ct);
                    if (!alreadyConsumed)
                    {
                        db.OutboxConsumptions.Add(new OutboxConsumption { MessageId = message.Id, Handler = handler, ConsumedAt = now });
                        await ApplyAsync(message, now, ct);
                        await db.SaveChangesAsync(ct);
                    }

                    await failpoints.HitAsync("outbox.before-complete", ct);

                    var affected = await db.OutboxMessages
                        .Where(m => m.Id == message.Id && m.ProcessedAt == null && m.LeaseOwner == owner && m.LeaseVersion == message.LeaseVersion)
                        .ExecuteUpdateAsync(
                            s => s.SetProperty(m => m.ProcessedAt, now).SetProperty(m => m.LeaseOwner, (string?)null).SetProperty(m => m.LeaseUntil, (DateTime?)null).SetProperty(m => m.LastErrorCode, (string?)null),
                            ct);
                    if (affected == 0)
                    {
                        // Fenced out: another worker holds a newer lease. Rolling back also discards the
                        // consumption marker and effect written above, leaving the message to that worker.
                        throw new LeaseLostException();
                    }

                    return true;
                },
                cancellationToken);

            metrics.Outbox(message.Type, "processed");
            return completed;
        }
        catch (LeaseLostException)
        {
            metrics.Outbox(message.Type, "lease_lost");
            LogLeaseLost(logger, message.Id);
            return false;
        }
        catch (UniqueConstraintException)
        {
            // Another worker consumed it between our check and insert; our transaction rolled back.
            metrics.Outbox(message.Type, "duplicate");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.Outbox(message.Type, "failed");
            LogFailed(logger, message.Id, message.Attempts, ex.GetType().Name);
            await ReleaseAfterFailureAsync(owner, message, ex.GetType().Name, CancellationToken.None);
            return false;
        }
    }

    private async Task ReleaseAfterFailureAsync(string owner, LeasedMessage message, string errorCode, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow().UtcDateTime;
        var dead = message.Attempts >= OutboxStates.MaxAttempts;

        // Exponential backoff, capped. A message that keeps failing is parked as "dead" and reported
        // on the operations screen; it is never dropped silently.
        var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, message.Attempts)));
        var code = dead ? OutboxStates.Dead : (errorCode.Length > 64 ? errorCode[..64] : errorCode);
        var availableAt = dead ? DateTime.MaxValue.AddDays(-1) : now + delay;
        await db.OutboxMessages
            .Where(m => m.Id == message.Id && m.LeaseOwner == owner && m.LeaseVersion == message.LeaseVersion)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.LeaseOwner, (string?)null).SetProperty(m => m.LeaseUntil, (DateTime?)null)
                    .SetProperty(m => m.AvailableAt, availableAt).SetProperty(m => m.LastErrorCode, code),
                cancellationToken);
    }

    private static string HandlerName(string type) => type switch
    {
        InventoryService.StockChangedEvent => "low-stock-notifier",
        TicketService.TicketAssignedEvent => "assignee-notifier",
        _ => "ignored",
    };

    private async Task ApplyAsync(LeasedMessage message, DateTime now, CancellationToken ct)
    {
        switch (message.Type)
        {
            case InventoryService.StockChangedEvent:
                await NotifyLowStockAsync(JsonSerializer.Deserialize<StockChangedPayload>(message.PayloadJson, ApiJson.Options)!, now, ct);
                break;
            case TicketService.TicketAssignedEvent:
                var assigned = JsonSerializer.Deserialize<TicketAssignedPayload>(message.PayloadJson, ApiJson.Options)!;
                db.Notifications.Add(new Notification { UserId = assigned.AssigneeUserId, Kind = "ticket_assigned", Text = $"{assigned.TicketNumber} numaralı destek talebi size atandı.", CreatedAt = now });
                break;
            default:
                // Unknown types are completed without effect so that an old worker cannot block the queue.
                break;
        }
    }

    private async Task NotifyLowStockAsync(StockChangedPayload payload, DateTime now, CancellationToken ct)
    {
        var productIds = payload.ProductIds.ToArray();
        var low = await (
            from b in db.StockBalances.AsNoTracking()
            join p in db.Products.AsNoTracking() on b.ProductId equals p.Id
            join w in db.Warehouses.AsNoTracking() on b.WarehouseId equals w.Id
            where b.WarehouseId == payload.WarehouseId && productIds.Contains(b.ProductId) && p.IsActive && b.OnHand - b.Reserved <= p.ReorderLevel
            select new { p.Sku, WarehouseCode = w.Code, Available = b.OnHand - b.Reserved }).ToListAsync(ct);
        if (low.Count == 0)
        {
            return;
        }

        var supervisors = await db.WarehouseGrants.AsNoTracking()
            .Where(g => g.WarehouseId == payload.WarehouseId && g.Level == WarehouseLevel.Supervise)
            .Select(g => g.UserId).ToListAsync(ct);
        foreach (var userId in supervisors)
        {
            foreach (var item in low)
            {
                var text = $"{item.WarehouseCode} deposunda {item.Sku} yeniden sipariş seviyesinde veya altında.";

                // One open reminder per user and product is enough; a second stock change does not add another.
                if (!await db.Notifications.AnyAsync(n => n.UserId == userId && n.Kind == "low_stock" && n.Text == text && n.ReadAt == null, ct))
                {
                    db.Notifications.Add(new Notification { UserId = userId, Kind = "low_stock", Text = text, CreatedAt = now });
                }
            }
        }
    }

    private sealed class LeaseLostException : Exception;

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Outbox message {MessageId} was completed by another worker; this attempt was rolled back")]
    private static partial void LogLeaseLost(ILogger logger, Guid messageId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Outbox message {MessageId} failed on attempt {Attempt} ({ErrorType})")]
    private static partial void LogFailed(ILogger logger, Guid messageId, int attempt, string errorType);
}
