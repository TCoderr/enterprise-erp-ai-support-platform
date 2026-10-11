using Kantar.Domain.Identity;
using Kantar.Domain.Inventory;
using Kantar.Domain.MasterData;
using Kantar.Domain.Operations;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Abstractions;

/// <summary>The single unit of persistence. One instance per request, one transaction per command.</summary>
public interface IKantarDb
{
    DbSet<User> Users { get; }
    DbSet<UserRole> UserRoles { get; }
    DbSet<WarehouseGrant> WarehouseGrants { get; }
    DbSet<Session> Sessions { get; }

    DbSet<Product> Products { get; }
    DbSet<Customer> Customers { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<Warehouse> Warehouses { get; }

    DbSet<StockBalance> StockBalances { get; }
    DbSet<StockMovement> StockMovements { get; }
    DbSet<ReservationEntry> ReservationEntries { get; }
    DbSet<StockTransfer> StockTransfers { get; }
    DbSet<StockAdjustment> StockAdjustments { get; }

    DbSet<SalesOrder> SalesOrders { get; }
    DbSet<SalesOrderLine> SalesOrderLines { get; }
    DbSet<Shipment> Shipments { get; }

    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderLine> PurchaseOrderLines { get; }
    DbSet<GoodsReceipt> GoodsReceipts { get; }

    DbSet<Ticket> Tickets { get; }
    DbSet<TicketComment> TicketComments { get; }
    DbSet<KbDocument> KbDocuments { get; }
    DbSet<KbDocumentVersion> KbDocumentVersions { get; }
    DbSet<KbChunk> KbChunks { get; }

    DbSet<AiAnswer> AiAnswers { get; }
    DbSet<AiUsage> AiUsage { get; }

    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<AuditEntry> AuditEntries { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<OutboxConsumption> OutboxConsumptions { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<ReconciliationRun> ReconciliationRuns { get; }
    DbSet<ReconciliationFinding> ReconciliationFindings { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets every tracked entity. Called before each transaction attempt.</summary>
    void ClearTracking();
}
