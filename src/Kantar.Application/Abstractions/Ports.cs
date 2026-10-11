using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;

namespace Kantar.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="work"/> in one READ COMMITTED transaction. On deadlock, lock timeout or a
    /// transient connection fault the whole transaction is rolled back and the delegate is invoked
    /// again from scratch (bounded). The delegate must therefore hold no state between attempts and
    /// must not call anything outside the database.
    /// </summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>Raised after the transaction is rolled back, so callers never query inside a failed transaction.</summary>
public sealed class UniqueConstraintException(string constraintName, Exception inner)
    : Exception($"Unique constraint violated: {constraintName}", inner)
{
    public string ConstraintName { get; } = constraintName;
}

public readonly record struct StockLevels(int OnHand, int Reserved)
{
    public int Available => OnHand - Reserved;
}

/// <summary>The only way to change a stock balance. Implemented with single-statement conditional updates.</summary>
public interface IStockLedger
{
    /// <summary>Creates the (warehouse, product) row with zeros if it does not exist yet. Safe under concurrency.</summary>
    Task EnsureRowAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies both deltas atomically if and only if the result keeps Reserved ≥ 0 and OnHand ≥ Reserved.
    /// Returns the values after the change, or null when the condition does not hold (nothing written).
    /// </summary>
    Task<StockLevels?> TryApplyAsync(Guid warehouseId, Guid productId, int onHandDelta, int reservedDelta, CancellationToken cancellationToken);

    Task<StockLevels> ReadAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken);
}

/// <summary>Loads a document header under an update lock so commands on the same document run one after another.</summary>
public interface IDocumentLocks
{
    Task<SalesOrder?> LockSalesOrderAsync(Guid id, CancellationToken cancellationToken);
    Task<PurchaseOrder?> LockPurchaseOrderAsync(Guid id, CancellationToken cancellationToken);
    Task<KbDocument?> LockKbDocumentAsync(Guid id, CancellationToken cancellationToken);
    Task<Ticket?> LockTicketAsync(Guid id, CancellationToken cancellationToken);
}

public enum DocumentKind
{
    SalesOrder,
    Shipment,
    PurchaseOrder,
    GoodsReceipt,
    StockTransfer,
    StockAdjustment,
    Ticket,
}

public interface IDocumentNumbers
{
    /// <summary>Next display number. Sequences are not transactional: a rolled-back command leaves a gap.</summary>
    Task<string> NextAsync(DocumentKind kind, CancellationToken cancellationToken);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public interface IRequestContext
{
    string CorrelationId { get; }
}

/// <summary>
/// Named points where tests can inject a failure. The production registration does nothing;
/// there is no route, header or setting that activates it.
/// </summary>
public interface IFailpoints
{
    Task HitAsync(string name, CancellationToken cancellationToken);
}

public sealed class NoFailpoints : IFailpoints
{
    public Task HitAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;
}
