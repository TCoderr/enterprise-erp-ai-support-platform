using Kantar.Application.Abstractions;
using Kantar.Domain.Identity;
using Kantar.Domain.Inventory;
using Kantar.Domain.MasterData;
using Kantar.Domain.Operations;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Kantar.Infrastructure.Persistence;

public sealed class KantarDbContext(DbContextOptions<KantarDbContext> options) : DbContext(options), IKantarDb
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<WarehouseGrant> WarehouseGrants => Set<WarehouseGrant>();
    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<ReservationEntry> ReservationEntries => Set<ReservationEntry>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();

    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<Shipment> Shipments => Set<Shipment>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<KbDocument> KbDocuments => Set<KbDocument>();
    public DbSet<KbDocumentVersion> KbDocumentVersions => Set<KbDocumentVersion>();
    public DbSet<KbChunk> KbChunks => Set<KbChunk>();

    public DbSet<AiAnswer> AiAnswers => Set<AiAnswer>();
    public DbSet<AiUsage> AiUsage => Set<AiUsage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxConsumption> OutboxConsumptions => Set<OutboxConsumption>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationFinding> ReconciliationFindings => Set<ReconciliationFinding>();

    public void ClearTracking() => ChangeTracker.Clear();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>().HaveColumnType("datetime2(3)");
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<string>().HaveMaxLength(256);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ModelConfiguration.Identity(modelBuilder);
        ModelConfiguration.MasterData(modelBuilder);
        ModelConfiguration.OperationsCore(modelBuilder);
        ModelConfiguration.Inventory(modelBuilder);
        ModelConfiguration.Sales(modelBuilder);
        ModelConfiguration.Purchasing(modelBuilder);
        ModelConfiguration.Support(modelBuilder);
        ModelConfiguration.Assistance(modelBuilder);
        ModelConfiguration.OperationsJobs(modelBuilder);
    }

    /// <summary>All timestamps are UTC; SQL Server returns them without a kind, so it is restored on read.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
