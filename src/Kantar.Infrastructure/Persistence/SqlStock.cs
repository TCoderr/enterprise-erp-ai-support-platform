using System.Data;
using System.Data.Common;
using System.Globalization;
using Kantar.Application.Abstractions;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Kantar.Infrastructure.Persistence;

/// <summary>
/// Stock balance primitives for SQL Server. Each change is one statement, so there is no gap
/// between reading the balance and writing it in which another transaction could interleave.
/// All SQL here is parameterised and fixed; nothing is concatenated from input.
/// </summary>
internal sealed class SqlStockLedger(KantarDbContext db) : IStockLedger
{
    // UPDLOCK + HOLDLOCK takes a key-range lock on the (warehouse, product) key, so two concurrent
    // first receipts of the same product wait for each other instead of both inserting.
    private const string EnsureSql = """
        INSERT INTO inv.StockBalances (WarehouseId, ProductId, OnHand, Reserved)
        SELECT @w, @p, 0, 0
        WHERE NOT EXISTS (SELECT 1 FROM inv.StockBalances WITH (UPDLOCK, HOLDLOCK)
                          WHERE WarehouseId = @w AND ProductId = @p);
        """;

    private const string ApplySql = """
        UPDATE inv.StockBalances
        SET OnHand = OnHand + @dOnHand, Reserved = Reserved + @dReserved
        OUTPUT inserted.OnHand, inserted.Reserved
        WHERE WarehouseId = @w AND ProductId = @p
          AND Reserved + @dReserved >= 0
          AND OnHand + @dOnHand >= Reserved + @dReserved;
        """;

    private const string ReadSql = "SELECT OnHand, Reserved FROM inv.StockBalances WHERE WarehouseId = @w AND ProductId = @p;";

    public async Task EnsureRowAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken)
    {
        await using var command = await db.CreateCommandAsync(EnsureSql, cancellationToken);
        command.Parameters.Add(new SqlParameter("@w", SqlDbType.UniqueIdentifier) { Value = warehouseId });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.UniqueIdentifier) { Value = productId });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StockLevels?> TryApplyAsync(Guid warehouseId, Guid productId, int onHandDelta, int reservedDelta, CancellationToken cancellationToken)
    {
        await using var command = await db.CreateCommandAsync(ApplySql, cancellationToken);
        command.Parameters.Add(new SqlParameter("@w", SqlDbType.UniqueIdentifier) { Value = warehouseId });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.UniqueIdentifier) { Value = productId });
        command.Parameters.Add(new SqlParameter("@dOnHand", SqlDbType.Int) { Value = onHandDelta });
        command.Parameters.Add(new SqlParameter("@dReserved", SqlDbType.Int) { Value = reservedDelta });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new StockLevels(reader.GetInt32(0), reader.GetInt32(1)) : null;
    }

    public async Task<StockLevels> ReadAsync(Guid warehouseId, Guid productId, CancellationToken cancellationToken)
    {
        await using var command = await db.CreateCommandAsync(ReadSql, cancellationToken);
        command.Parameters.Add(new SqlParameter("@w", SqlDbType.UniqueIdentifier) { Value = warehouseId });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.UniqueIdentifier) { Value = productId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new StockLevels(reader.GetInt32(0), reader.GetInt32(1)) : default;
    }
}

/// <summary>
/// Header rows are read WITH (UPDLOCK, ROWLOCK): a second command on the same document waits here
/// and then sees the first one's committed state. Lock order is always document, then balances.
/// </summary>
internal sealed class SqlDocumentLocks(KantarDbContext db) : IDocumentLocks
{
    public Task<SalesOrder?> LockSalesOrderAsync(Guid id, CancellationToken cancellationToken) =>
        db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM sales.SalesOrders WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<PurchaseOrder?> LockPurchaseOrderAsync(Guid id, CancellationToken cancellationToken) =>
        db.PurchaseOrders
            .FromSqlInterpolated($"SELECT * FROM purch.PurchaseOrders WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<KbDocument?> LockKbDocumentAsync(Guid id, CancellationToken cancellationToken) =>
        db.KbDocuments
            .FromSqlInterpolated($"SELECT * FROM sup.KbDocuments WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Ticket?> LockTicketAsync(Guid id, CancellationToken cancellationToken) =>
        db.Tickets
            .FromSqlInterpolated($"SELECT * FROM sup.Tickets WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .FirstOrDefaultAsync(cancellationToken);
}

internal sealed class SqlDocumentNumbers(KantarDbContext db) : IDocumentNumbers
{
    public async Task<string> NextAsync(DocumentKind kind, CancellationToken cancellationToken)
    {
        // Sequence names come from this closed switch, never from input.
        var (prefix, sequence) = kind switch
        {
            DocumentKind.SalesOrder => ("SO", "sales.SalesOrderNumbers"),
            DocumentKind.Shipment => ("SH", "sales.ShipmentNumbers"),
            DocumentKind.PurchaseOrder => ("PO", "purch.PurchaseOrderNumbers"),
            DocumentKind.GoodsReceipt => ("GR", "purch.GoodsReceiptNumbers"),
            DocumentKind.StockTransfer => ("TR", "inv.StockTransferNumbers"),
            DocumentKind.StockAdjustment => ("AD", "inv.StockAdjustmentNumbers"),
            DocumentKind.Ticket => ("TK", "sup.TicketNumbers"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await using var command = await db.CreateCommandAsync($"SELECT NEXT VALUE FOR {sequence};", cancellationToken);
        var value = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{value:D8}");
    }
}

internal static class DbCommandExtensions
{
    /// <summary>Creates a command on the context's connection, enlisted in its current transaction.</summary>
    public static async Task<DbCommand> CreateCommandAsync(this KantarDbContext db, string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }
}
