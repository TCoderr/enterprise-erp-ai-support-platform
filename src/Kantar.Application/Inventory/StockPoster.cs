using Kantar.Application.Abstractions;
using Kantar.Domain.Common;

namespace Kantar.Application.Inventory;

/// <summary>One balance change plus the journal row(s) it must produce once it is applied.</summary>
public sealed record StockPosting(
    Guid WarehouseId,
    Guid ProductId,
    int OnHandDelta,
    int ReservedDelta,
    string Field,
    Action<StockLevels> Journal,
    bool EnsureRow = false);

/// <summary>
/// Applies a set of balance changes in one fixed order — (warehouse, product) ascending — so two
/// commands touching the same rows can never wait on each other in opposite order. Either every
/// posting applies or the caller's transaction rolls back; nothing here commits.
/// </summary>
public sealed class StockPoster(IStockLedger ledger, IFailpoints failpoints)
{
    public async Task ApplyAsync(IEnumerable<StockPosting> postings, CancellationToken cancellationToken)
    {
        var ordered = postings.OrderBy(p => p.WarehouseId).ThenBy(p => p.ProductId).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var posting = ordered[i];
            if (posting.EnsureRow)
            {
                await ledger.EnsureRowAsync(posting.WarehouseId, posting.ProductId, cancellationToken);
            }

            var after = await ledger.TryApplyAsync(posting.WarehouseId, posting.ProductId, posting.OnHandDelta, posting.ReservedDelta, cancellationToken);
            if (after is null)
            {
                throw await InsufficientAsync(ordered.AsMemory(i), cancellationToken);
            }

            posting.Journal(after.Value);
            await failpoints.HitAsync("stock.after-posting", cancellationToken);
        }
    }

    /// <summary>
    /// Builds the rejection. The transaction is about to roll back, so the remaining postings are only
    /// inspected (not applied) to report every short line in one response.
    /// </summary>
    private async Task<DomainException> InsufficientAsync(ReadOnlyMemory<StockPosting> remaining, CancellationToken cancellationToken)
    {
        var errors = new List<FieldError>();
        for (var i = 0; i < remaining.Length; i++)
        {
            var posting = remaining.Span[i];
            var levels = await ledger.ReadAsync(posting.WarehouseId, posting.ProductId, cancellationToken);
            var fits = levels.Reserved + posting.ReservedDelta >= 0
                && levels.OnHand + posting.OnHandDelta >= levels.Reserved + posting.ReservedDelta;
            if (i == 0 || !fits)
            {
                var requested = posting.ReservedDelta > 0 ? posting.ReservedDelta : -posting.OnHandDelta;
                errors.Add(new FieldError(posting.Field, "insufficient_available", $"İstenen {requested}, kullanılabilir {levels.Available}"));
            }
        }

        return Problems.Conflict("insufficient_available", null, errors);
    }
}
