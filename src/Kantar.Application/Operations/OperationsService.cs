using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Operations;
using Kantar.Domain.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Operations;

public sealed record ReconciliationFindingDto(Guid WarehouseId, string WarehouseCode, Guid ProductId, string Sku, string Rule, long Expected, long Actual);

public sealed record ReconciliationRunDto(
    Guid Id, DateTime StartedAt, DateTime CompletedAt, int BalancesChecked, int FindingCount, string Outcome,
    IReadOnlyList<ReconciliationFindingDto> Findings);

public sealed record OutboxSummaryDto(int Pending, int Leased, int Retrying, int Dead, int ProcessedLast24Hours, DateTime? OldestPendingCreatedAt);

public sealed record NotificationDto(Guid Id, string Kind, string Text, DateTime CreatedAt, bool IsRead);

public sealed record MetricRowDto(string Name, string Dimension1, string Dimension2, long Count);

public sealed record AiUsageRowDto(string Provider, string Status, int Count, int AverageDurationMs);

public sealed record MetricsDto(DateTime ProcessStartedAt, IReadOnlyList<MetricRowDto> Counters, IReadOnlyList<AiUsageRowDto> AiUsageLast24Hours);

/// <summary>
/// Process-local counters with a closed set of dimension values (operation names and outcomes from
/// code, never ids or free text), mirrored to System.Diagnostics.Metrics for an external collector.
/// </summary>
public sealed class OpsMetrics : IDisposable
{
    public const string MeterName = "Kantar.Operations";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _commands;
    private readonly Counter<long> _outbox;
    private readonly ConcurrentDictionary<(string Name, string A, string B), long> _counts = new();

    public OpsMetrics()
    {
        _commands = _meter.CreateCounter<long>("kantar.commands");
        _outbox = _meter.CreateCounter<long>("kantar.outbox.messages");
    }

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public void Command(string operation, string outcome)
    {
        _commands.Add(1, new TagList { { "operation", operation }, { "outcome", outcome } });
        _counts.AddOrUpdate(("command", operation, outcome), 1, (_, v) => v + 1);
    }

    public void Outbox(string type, string outcome)
    {
        _outbox.Add(1, new TagList { { "type", type }, { "outcome", outcome } });
        _counts.AddOrUpdate(("outbox", type, outcome), 1, (_, v) => v + 1);
    }

    public IReadOnlyList<MetricRowDto> Snapshot() =>
        _counts.OrderBy(c => c.Key.Name, StringComparer.Ordinal).ThenBy(c => c.Key.A, StringComparer.Ordinal).ThenBy(c => c.Key.B, StringComparer.Ordinal)
            .Select(c => new MetricRowDto(c.Key.Name, c.Key.A, c.Key.B, c.Value)).ToArray();

    public void Dispose() => _meter.Dispose();
}

public sealed class OperationsService(IKantarDb db, TimeProvider clock, OpsMetrics metrics, AuditWriter audit)
{
    public const int KeptRuns = 50;

    private static void RequireAdmin(CurrentUser user)
    {
        if (!user.Is(Roles.SystemAdmin))
        {
            throw Problems.Forbidden();
        }
    }

    // ------------------------------------------------------------------ reconciliation

    /// <summary>
    /// Compares every stock balance with its journals and with the open confirmed order quantity.
    /// Read-only with respect to business data: it writes only its own report rows. A difference is
    /// reported as a finding and is never corrected here. A journal/balance difference cannot arise
    /// from the application's own commands (both are written in one transaction), so it points at an
    /// out-of-band write or a defect and has no in-application repair: a stock adjustment changes
    /// balance and journal together and would not close it.
    /// </summary>
    public async Task<ReconciliationRunDto> RunReconciliationAsync(Guid? requestedBy, CancellationToken ct)
    {
        var started = clock.GetUtcNow().UtcDateTime;

        var balances = await db.StockBalances.AsNoTracking().Select(b => new { b.WarehouseId, b.ProductId, b.OnHand, b.Reserved }).ToListAsync(ct);
        var journal = await db.StockMovements.AsNoTracking().GroupBy(m => new { m.WarehouseId, m.ProductId })
            .Select(g => new { g.Key.WarehouseId, g.Key.ProductId, Sum = g.Sum(m => (long)m.QuantityDelta), LastId = g.Max(m => m.Id) }).ToListAsync(ct);
        var ledger = await db.ReservationEntries.AsNoTracking().GroupBy(r => new { r.WarehouseId, r.ProductId })
            .Select(g => new { g.Key.WarehouseId, g.Key.ProductId, Sum = g.Sum(r => (long)r.QuantityDelta), LastId = g.Max(r => r.Id) }).ToListAsync(ct);
        var open = await (
            from o in db.SalesOrders.AsNoTracking()
            join l in db.SalesOrderLines.AsNoTracking() on o.Id equals l.OrderId
            where o.Status == SalesOrderStatus.Confirmed || o.Status == SalesOrderStatus.PartiallyShipped
            group l by new { o.WarehouseId, l.ProductId } into g
            select new { g.Key.WarehouseId, g.Key.ProductId, Sum = g.Sum(l => (long)(l.OrderedQty - l.ShippedQty - l.CancelledQty)) }).ToListAsync(ct);

        var lastMovementIds = journal.Select(j => j.LastId).ToArray();
        var lastMovements = await db.StockMovements.AsNoTracking().Where(m => lastMovementIds.Contains(m.Id))
            .ToDictionaryAsync(m => (m.WarehouseId, m.ProductId), m => (long)m.OnHandAfter, ct);
        var lastEntryIds = ledger.Select(l => l.LastId).ToArray();
        var lastEntries = await db.ReservationEntries.AsNoTracking().Where(r => lastEntryIds.Contains(r.Id))
            .ToDictionaryAsync(r => (r.WarehouseId, r.ProductId), r => (long)r.ReservedAfter, ct);

        var onHand = balances.ToDictionary(b => (b.WarehouseId, b.ProductId), b => ((long)b.OnHand, (long)b.Reserved));
        var journalSums = journal.ToDictionary(j => (j.WarehouseId, j.ProductId), j => j.Sum);
        var ledgerSums = ledger.ToDictionary(l => (l.WarehouseId, l.ProductId), l => l.Sum);
        var openSums = open.ToDictionary(o => (o.WarehouseId, o.ProductId), o => o.Sum);

        var keys = onHand.Keys.Union(journalSums.Keys).Union(ledgerSums.Keys).Union(openSums.Keys).ToArray();
        var run = new ReconciliationRun { StartedAt = started, BalancesChecked = keys.Length, RequestedBy = requestedBy };

        foreach (var key in keys)
        {
            var (balanceOnHand, balanceReserved) = onHand.GetValueOrDefault(key);
            Check("onhand_equals_movement_sum", journalSums.GetValueOrDefault(key), balanceOnHand);
            Check("reserved_equals_reservation_sum", ledgerSums.GetValueOrDefault(key), balanceReserved);
            Check("reserved_equals_open_order_quantity", openSums.GetValueOrDefault(key), balanceReserved);
            if (lastMovements.TryGetValue(key, out var lastOnHand))
            {
                Check("onhand_equals_last_movement_after", lastOnHand, balanceOnHand);
            }

            if (lastEntries.TryGetValue(key, out var lastReserved))
            {
                Check("reserved_equals_last_reservation_after", lastReserved, balanceReserved);
            }

            void Check(string rule, long expected, long actual)
            {
                if (expected != actual)
                {
                    run.Findings.Add(new ReconciliationFinding { RunId = run.Id, WarehouseId = key.WarehouseId, ProductId = key.ProductId, Rule = rule, Expected = expected, Actual = actual });
                }
            }
        }

        run.FindingCount = run.Findings.Count;
        run.CompletedAt = clock.GetUtcNow().UtcDateTime;
        db.ReconciliationRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Old reports are trimmed; this deletes report rows only. Same order as "latest", so the newest are the ones kept.
        var obsolete = await Newest(db.ReconciliationRuns).Skip(KeptRuns).Select(r => r.Id).ToListAsync(ct);
        if (obsolete.Count > 0)
        {
            await db.ReconciliationRuns.Where(r => obsolete.Contains(r.Id)).ExecuteDeleteAsync(ct);
        }

        return await ToDtoAsync(run, ct);
    }

    /// <summary>Newest first. Two runs can complete in the same instant, so start time and id break the tie deterministically.</summary>
    private static IOrderedQueryable<ReconciliationRun> Newest(IQueryable<ReconciliationRun> runs) =>
        runs.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).ThenByDescending(r => r.Id);

    public ICommandHandler<bool, ReconciliationRunDto> RunReconciliation => new DelegateHandler<bool, ReconciliationRunDto>(
        (_, user, _) =>
        {
            RequireAdmin(user);
            return Task.CompletedTask;
        },
        async (_, user, ct) =>
        {
            audit.Write(user, "ops.reconciliation_requested", "ReconciliationRun", Guid.Empty);
            return await RunReconciliationAsync(user.Id, ct);
        });

    public async Task<ReconciliationRunDto?> LatestReconciliationAsync(CurrentUser user, CancellationToken ct)
    {
        RequireAdmin(user);
        var run = await Newest(db.ReconciliationRuns.AsNoTracking().Include(r => r.Findings)).FirstOrDefaultAsync(ct);
        return run is null ? null : await ToDtoAsync(run, ct);
    }

    /// <summary>
    /// "DriftFound" is a report, not a repair: nothing was changed and nothing is claimed to be fixed.
    /// Warehouse codes and SKUs are added for the reader with one lookup per kind, not one per finding.
    /// </summary>
    private async Task<ReconciliationRunDto> ToDtoAsync(ReconciliationRun run, CancellationToken ct)
    {
        var findings = run.Findings.Take(200).ToArray();
        var warehouseIds = findings.Select(f => f.WarehouseId).Distinct().ToArray();
        var productIds = findings.Select(f => f.ProductId).Distinct().ToArray();
        var warehouses = warehouseIds.Length == 0
            ? []
            : await db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var products = productIds.Length == 0
            ? []
            : await db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Sku, ct);

        return new ReconciliationRunDto(
            run.Id, run.StartedAt, run.CompletedAt, run.BalancesChecked, run.FindingCount, run.FindingCount == 0 ? "Consistent" : "DriftFound",
            findings.Select(f => new ReconciliationFindingDto(
                f.WarehouseId, warehouses.GetValueOrDefault(f.WarehouseId, UserDisplayNames.Unknown),
                f.ProductId, products.GetValueOrDefault(f.ProductId, UserDisplayNames.Unknown),
                f.Rule, f.Expected, f.Actual)).ToArray());
    }

    // ------------------------------------------------------------------ outbox / metrics

    public async Task<OutboxSummaryDto> OutboxSummaryAsync(CurrentUser user, CancellationToken ct)
    {
        RequireAdmin(user);
        var now = clock.GetUtcNow().UtcDateTime;
        var since = now.AddHours(-24);
        var open = db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAt == null);
        return new OutboxSummaryDto(
            await open.CountAsync(m => m.LastErrorCode != OutboxStates.Dead, ct),
            await open.CountAsync(m => m.LeaseUntil != null && m.LeaseUntil > now, ct),
            await open.CountAsync(m => m.Attempts > 0 && m.LastErrorCode != null && m.LastErrorCode != OutboxStates.Dead, ct),
            await open.CountAsync(m => m.LastErrorCode == OutboxStates.Dead, ct),
            await db.OutboxMessages.AsNoTracking().CountAsync(m => m.ProcessedAt != null && m.ProcessedAt >= since, ct),
            await open.Where(m => m.LastErrorCode != OutboxStates.Dead).MinAsync(m => (DateTime?)m.CreatedAt, ct));
    }

    public async Task<MetricsDto> MetricsAsync(CurrentUser user, CancellationToken ct)
    {
        RequireAdmin(user);
        var since = clock.GetUtcNow().UtcDateTime.AddHours(-24);
        var usage = await db.AiUsage.AsNoTracking().Where(u => u.OccurredAt >= since)
            .GroupBy(u => new { u.Provider, u.Status })
            .Select(g => new AiUsageRowDto(g.Key.Provider, g.Key.Status, g.Count(), (int)g.Average(u => u.DurationMs)))
            .ToListAsync(ct);
        return new MetricsDto(metrics.StartedAt, metrics.Snapshot(), usage);
    }

    // ------------------------------------------------------------------ notifications

    public async Task<IReadOnlyList<NotificationDto>> NotificationsAsync(CurrentUser user, CancellationToken ct) =>
        await db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id)
            .OrderByDescending(n => n.CreatedAt).Take(50)
            .Select(n => new NotificationDto(n.Id, n.Kind, n.Text, n.CreatedAt, n.ReadAt != null))
            .ToListAsync(ct);

    public async Task MarkNotificationReadAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var updated = await db.Notifications.Where(n => n.Id == id && n.UserId == user.Id && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        if (updated == 0 && !await db.Notifications.AnyAsync(n => n.Id == id && n.UserId == user.Id, ct))
        {
            throw Problems.NotFound();
        }
    }

    // ------------------------------------------------------------------ housekeeping

    /// <summary>Deletes rows whose retention has ended. Every predicate is time-based, so repeating it is harmless.</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var deleted = 0;
        deleted += await db.IdempotencyRecords.Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        deleted += await db.AiAnswers.Where(a => a.ExpiresAt < now).ExecuteDeleteAsync(ct);
        deleted += await db.Sessions.Where(s => s.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        deleted += await db.OutboxConsumptions.Where(c => c.ConsumedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        deleted += await db.OutboxMessages.Where(m => m.ProcessedAt != null && m.ProcessedAt < now.AddDays(-7)).ExecuteDeleteAsync(ct);
        deleted += await db.Notifications.Where(n => n.ReadAt != null && n.ReadAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        return deleted;
    }
}

public static class OutboxStates
{
    public const string Dead = "dead";
    public const int MaxAttempts = 8;
}
