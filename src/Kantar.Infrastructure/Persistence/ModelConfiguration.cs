using System.Linq.Expressions;
using Kantar.Domain.Identity;
using Kantar.Domain.Inventory;
using Kantar.Domain.MasterData;
using Kantar.Domain.Operations;
using Kantar.Domain.Purchasing;
using Kantar.Domain.Sales;
using Kantar.Domain.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kantar.Infrastructure.Persistence;

/// <summary>
/// Table mapping per module schema. Constraints stated here are the database's own guard: they hold
/// for any writer, not only for requests that came through the HTTP API.
/// </summary>
internal static class ModelConfiguration
{
    /// <summary>Codes and references are compared byte-wise, independent of the Turkish database collation.</summary>
    public const string CodeCollation = "Latin1_General_100_BIN2";

    private static PropertyBuilder<string?> Code<T>(this EntityTypeBuilder<T> e, Expression<Func<T, string?>> property, int length)
        where T : class =>
        e.Property(property).HasMaxLength(length).IsUnicode(false).UseCollation(CodeCollation);

    private static void Ref<T, TPrincipal>(this EntityTypeBuilder<T> e, Expression<Func<T, object?>> foreignKey)
        where T : class
        where TPrincipal : class =>
        e.HasOne<TPrincipal>().WithMany().HasForeignKey(foreignKey).OnDelete(DeleteBehavior.Restrict);

    private static string InList<TEnum>() where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetValues<TEnum>().Select(v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)));

    public static void Identity(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users", "iam", t => t.HasCheckConstraint("CK_Users_PoApprovalLimit", "[PoApprovalLimit] >= 0"));
            e.Code(x => x.UserName, 64);
            e.HasIndex(x => x.UserName).IsUnique().HasDatabaseName("UX_Users_UserName");
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsUnicode(false);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Grants).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserRole>(e =>
        {
            var roles = string.Join(", ", Roles.All.Select(r => $"'{r}'"));
            e.ToTable("UserRoles", "iam", t => t.HasCheckConstraint("CK_UserRoles_Role", $"[Role] IN ({roles})"));
            e.HasKey(x => new { x.UserId, x.Role });
            e.Code(x => x.Role, 32);
        });

        b.Entity<WarehouseGrant>(e =>
        {
            e.ToTable("WarehouseGrants", "iam", t => t.HasCheckConstraint("CK_WarehouseGrants_Level", "[Level] IN (1, 2, 3)"));
            e.HasKey(x => new { x.UserId, x.WarehouseId });
            e.Ref<WarehouseGrant, Warehouse>(x => x.WarehouseId);
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("Sessions", "iam");
            e.Property(x => x.TokenHash).HasColumnType("binary(32)");
            e.Property(x => x.CsrfSecret).HasColumnType("binary(32)");
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("UX_Sessions_TokenHash");
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public static void MasterData(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.ToTable("Products", "md", t =>
            {
                t.HasCheckConstraint("CK_Products_ListPrice", "[ListPrice] >= 0");
                t.HasCheckConstraint("CK_Products_ReorderLevel", "[ReorderLevel] >= 0");
            });
            e.Code(x => x.Sku, 32);
            e.Code(x => x.Unit, 8);
            e.Property(x => x.Name).HasMaxLength(160);
            e.HasIndex(x => x.Sku).IsUnique().HasDatabaseName("UX_Products_Sku");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("Customers", "md");
            e.Code(x => x.Code, 32);
            e.Property(x => x.Name).HasMaxLength(160);
            e.Property(x => x.TaxNumber).HasMaxLength(11).IsUnicode(false);
            e.Property(x => x.Address).HasMaxLength(400);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UX_Customers_Code");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Supplier>(e =>
        {
            e.ToTable("Suppliers", "md");
            e.Code(x => x.Code, 32);
            e.Property(x => x.Name).HasMaxLength(160);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UX_Suppliers_Code");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Warehouse>(e =>
        {
            e.ToTable("Warehouses", "md");
            e.Code(x => x.Code, 16);
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UX_Warehouses_Code");
            e.Property(x => x.RowVersion).IsRowVersion();
        });
    }

    public static void OperationsCore(ModelBuilder b)
    {
        b.Entity<IdempotencyRecord>(e =>
        {
            e.ToTable("IdempotencyRecords", "ops");
            e.HasKey(x => new { x.UserId, x.Operation, x.IdempotencyKey });
            e.Code(x => x.Operation, 64);
            e.Code(x => x.IdempotencyKey, 128);
            e.Property(x => x.RequestHash).HasColumnType("binary(32)");
            e.Property(x => x.ResponseContentType).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.ResponseBody).HasColumnType("varbinary(max)");
            e.Property(x => x.ResponseLocation).HasMaxLength(256).IsUnicode(false);
            e.Property(x => x.ResponseETag).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries", "ops");
            e.Property(x => x.Action).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.EntityType).HasMaxLength(48).IsUnicode(false);
            e.Property(x => x.CorrelationId).HasMaxLength(40).IsUnicode(false);
            e.Property(x => x.DetailJson).HasMaxLength(2000);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.HasIndex(x => x.Action);
        });
    }

    public static void Inventory(ModelBuilder b)
    {
        b.Entity<StockBalance>(e =>
        {
            e.ToTable("StockBalances", "inv", t =>
            {
                t.HasCheckConstraint("CK_StockBalances_Reserved", "[Reserved] >= 0");
                t.HasCheckConstraint("CK_StockBalances_OnHand", "[OnHand] >= [Reserved]");
            });
            e.HasKey(x => new { x.WarehouseId, x.ProductId });
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ref<StockBalance, Warehouse>(x => x.WarehouseId);
            e.Ref<StockBalance, Product>(x => x.ProductId);
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("StockMovements", "inv", t =>
            {
                t.HasCheckConstraint("CK_StockMovements_Quantity", "[QuantityDelta] <> 0");
                t.HasCheckConstraint("CK_StockMovements_OnHandAfter", "[OnHandAfter] >= 0");
                t.HasCheckConstraint(
                    "CK_StockMovements_TypeSign",
                    "([Type] IN (1, 4) AND [QuantityDelta] > 0) OR ([Type] IN (2, 3) AND [QuantityDelta] < 0) OR [Type] = 5");
                t.HasCheckConstraint(
                    "CK_StockMovements_TypeReference",
                    "([Type] = 1 AND [ReferenceType] = 1) OR ([Type] = 2 AND [ReferenceType] = 2) OR ([Type] IN (3, 4) AND [ReferenceType] = 3) OR ([Type] = 5 AND [ReferenceType] = 4)");
            });
            e.HasIndex(x => new { x.ReferenceType, x.ReferenceLineId, x.Type }).IsUnique().HasDatabaseName("UX_StockMovements_ReferenceLine_Type");
            e.HasIndex(x => new { x.WarehouseId, x.ProductId, x.Id });
            e.Ref<StockMovement, Warehouse>(x => x.WarehouseId);
            e.Ref<StockMovement, Product>(x => x.ProductId);
        });

        b.Entity<ReservationEntry>(e =>
        {
            e.ToTable("ReservationEntries", "inv", t =>
            {
                t.HasCheckConstraint("CK_ReservationEntries_Quantity", "[QuantityDelta] <> 0");
                t.HasCheckConstraint("CK_ReservationEntries_ReservedAfter", "[ReservedAfter] >= 0");
                t.HasCheckConstraint(
                    "CK_ReservationEntries_ReasonSign",
                    "([Reason] = 1 AND [QuantityDelta] > 0) OR ([Reason] IN (2, 3) AND [QuantityDelta] < 0)");
            });
            e.HasIndex(x => new { x.WarehouseId, x.ProductId, x.Id });
            e.HasIndex(x => x.SalesOrderLineId);
            e.Ref<ReservationEntry, Warehouse>(x => x.WarehouseId);
            e.Ref<ReservationEntry, Product>(x => x.ProductId);
        });

        b.Entity<StockTransfer>(e =>
        {
            e.ToTable("StockTransfers", "inv", t => t.HasCheckConstraint("CK_StockTransfers_Warehouses", "[FromWarehouseId] <> [ToWarehouseId]"));
            e.Code(x => x.Number, 16);
            e.Code(x => x.Reference, 48);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_StockTransfers_Number");
            e.HasIndex(x => x.Reference).IsUnique().HasDatabaseName("UX_StockTransfers_Reference");
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.Ref<StockTransfer, Warehouse>(x => x.FromWarehouseId);
            e.Ref<StockTransfer, Warehouse>(x => x.ToWarehouseId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockTransferLine>(e =>
        {
            e.ToTable("StockTransferLines", "inv", t => t.HasCheckConstraint("CK_StockTransferLines_Quantity", "[Quantity] > 0"));
            e.HasIndex(x => new { x.TransferId, x.ProductId }).IsUnique().HasDatabaseName("UX_StockTransferLines_Product");
            e.Ref<StockTransferLine, Product>(x => x.ProductId);
        });

        b.Entity<StockAdjustment>(e =>
        {
            e.ToTable("StockAdjustments", "inv", t =>
                t.HasCheckConstraint("CK_StockAdjustments_Reason", $"[ReasonCode] IN ({InList<AdjustmentReason>()})"));
            e.Code(x => x.Number, 16);
            e.Code(x => x.Reference, 48);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_StockAdjustments_Number");
            e.HasIndex(x => x.Reference).IsUnique().HasDatabaseName("UX_StockAdjustments_Reference");
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.Ref<StockAdjustment, Warehouse>(x => x.WarehouseId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.AdjustmentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<StockAdjustmentLine>(e =>
        {
            e.ToTable("StockAdjustmentLines", "inv", t => t.HasCheckConstraint("CK_StockAdjustmentLines_Quantity", "[QuantityDelta] <> 0"));
            e.HasIndex(x => new { x.AdjustmentId, x.ProductId }).IsUnique().HasDatabaseName("UX_StockAdjustmentLines_Product");
            e.Ref<StockAdjustmentLine, Product>(x => x.ProductId);
        });

        b.HasSequence<long>("StockTransferNumbers", "inv");
        b.HasSequence<long>("StockAdjustmentNumbers", "inv");
    }

    public static void Sales(ModelBuilder b)
    {
        b.Entity<SalesOrder>(e =>
        {
            e.ToTable("SalesOrders", "sales", t =>
                t.HasCheckConstraint("CK_SalesOrders_Status", $"[Status] IN ({InList<SalesOrderStatus>()})"));
            e.Code(x => x.Number, 16);
            e.Code(x => x.CustomerCode, 32);
            e.Property(x => x.CustomerName).HasMaxLength(160);
            e.Property(x => x.CustomerTaxNumber).HasMaxLength(11).IsUnicode(false);
            e.Property(x => x.ShippingAddress).HasMaxLength(400);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.Total);
            e.Ignore(x => x.HasShipments);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_SalesOrders_Number");
            e.HasIndex(x => new { x.WarehouseId, x.CreatedAt, x.Id });
            e.HasIndex(x => x.OwnerUserId);
            e.Ref<SalesOrder, Customer>(x => x.CustomerId);
            e.Ref<SalesOrder, Warehouse>(x => x.WarehouseId);
            e.Ref<SalesOrder, User>(x => x.OwnerUserId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SalesOrderLine>(e =>
        {
            e.ToTable("SalesOrderLines", "sales", t =>
            {
                t.HasCheckConstraint("CK_SalesOrderLines_Ordered", $"[OrderedQty] BETWEEN 1 AND {SalesOrder.MaxLineQuantity}");
                t.HasCheckConstraint(
                    "CK_SalesOrderLines_Quantities",
                    "[ShippedQty] >= 0 AND [CancelledQty] >= 0 AND [ShippedQty] + [CancelledQty] <= [OrderedQty]");
                t.HasCheckConstraint("CK_SalesOrderLines_UnitPrice", "[UnitPrice] >= 0");
            });
            e.Code(x => x.Sku, 32);
            e.Code(x => x.Unit, 8);
            e.Property(x => x.ProductName).HasMaxLength(160);
            e.Ignore(x => x.OpenQty);
            e.HasIndex(x => new { x.OrderId, x.ProductId }).IsUnique().HasDatabaseName("UX_SalesOrderLines_Product");
            e.HasIndex(x => x.ProductId);
            e.Ref<SalesOrderLine, Product>(x => x.ProductId);
        });

        b.Entity<Shipment>(e =>
        {
            e.ToTable("Shipments", "sales");
            e.Code(x => x.Number, 16);
            e.Code(x => x.DispatchReference, 48);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_Shipments_Number");
            e.HasIndex(x => new { x.SalesOrderId, x.DispatchReference }).IsUnique().HasDatabaseName("UX_Shipments_Order_DispatchReference");
            e.Ref<Shipment, SalesOrder>(x => x.SalesOrderId);
            e.Ref<Shipment, Warehouse>(x => x.WarehouseId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ShipmentLine>(e =>
        {
            e.ToTable("ShipmentLines", "sales", t => t.HasCheckConstraint("CK_ShipmentLines_Quantity", "[Quantity] > 0"));
            e.HasIndex(x => new { x.ShipmentId, x.SalesOrderLineId }).IsUnique().HasDatabaseName("UX_ShipmentLines_OrderLine");
            e.Ref<ShipmentLine, SalesOrderLine>(x => x.SalesOrderLineId);
            e.Ref<ShipmentLine, Product>(x => x.ProductId);
        });

        b.HasSequence<long>("SalesOrderNumbers", "sales");
        b.HasSequence<long>("ShipmentNumbers", "sales");
    }

    public static void Purchasing(ModelBuilder b)
    {
        b.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("PurchaseOrders", "purch", t =>
                t.HasCheckConstraint("CK_PurchaseOrders_Status", $"[Status] IN ({InList<PurchaseOrderStatus>()})"));
            e.Code(x => x.Number, 16);
            e.Code(x => x.SupplierCode, 32);
            e.Property(x => x.SupplierName).HasMaxLength(160);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.Ignore(x => x.Total);
            e.Ignore(x => x.HasReceipts);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_PurchaseOrders_Number");
            e.HasIndex(x => new { x.WarehouseId, x.CreatedAt, x.Id });
            e.Ref<PurchaseOrder, Supplier>(x => x.SupplierId);
            e.Ref<PurchaseOrder, Warehouse>(x => x.WarehouseId);
            e.Ref<PurchaseOrder, User>(x => x.OwnerUserId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PurchaseOrderLine>(e =>
        {
            e.ToTable("PurchaseOrderLines", "purch", t =>
            {
                t.HasCheckConstraint("CK_PurchaseOrderLines_Ordered", $"[OrderedQty] BETWEEN 1 AND {PurchaseOrder.MaxLineQuantity}");
                t.HasCheckConstraint(
                    "CK_PurchaseOrderLines_Quantities",
                    "[ReceivedQty] >= 0 AND [CancelledQty] >= 0 AND [ReceivedQty] + [CancelledQty] <= [OrderedQty]");
                t.HasCheckConstraint("CK_PurchaseOrderLines_UnitCost", "[UnitCost] >= 0");
            });
            e.Code(x => x.Sku, 32);
            e.Code(x => x.Unit, 8);
            e.Property(x => x.ProductName).HasMaxLength(160);
            e.Ignore(x => x.OpenQty);
            e.HasIndex(x => new { x.OrderId, x.ProductId }).IsUnique().HasDatabaseName("UX_PurchaseOrderLines_Product");
            e.HasIndex(x => x.ProductId);
            e.Ref<PurchaseOrderLine, Product>(x => x.ProductId);
        });

        b.Entity<GoodsReceipt>(e =>
        {
            e.ToTable("GoodsReceipts", "purch");
            e.Code(x => x.Number, 16);
            e.Code(x => x.ExternalReference, 48);
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_GoodsReceipts_Number");
            e.HasIndex(x => new { x.SupplierId, x.ExternalReference }).IsUnique().HasDatabaseName("UX_GoodsReceipts_Supplier_ExternalReference");
            e.HasIndex(x => x.PurchaseOrderId);
            e.Ref<GoodsReceipt, PurchaseOrder>(x => x.PurchaseOrderId);
            e.Ref<GoodsReceipt, Supplier>(x => x.SupplierId);
            e.Ref<GoodsReceipt, Warehouse>(x => x.WarehouseId);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GoodsReceiptLine>(e =>
        {
            e.ToTable("GoodsReceiptLines", "purch", t => t.HasCheckConstraint("CK_GoodsReceiptLines_Quantity", "[Quantity] > 0"));
            e.HasIndex(x => new { x.ReceiptId, x.PurchaseOrderLineId }).IsUnique().HasDatabaseName("UX_GoodsReceiptLines_OrderLine");
            e.Ref<GoodsReceiptLine, PurchaseOrderLine>(x => x.PurchaseOrderLineId);
            e.Ref<GoodsReceiptLine, Product>(x => x.ProductId);
        });

        b.HasSequence<long>("PurchaseOrderNumbers", "purch");
        b.HasSequence<long>("GoodsReceiptNumbers", "purch");
    }

    public static void Support(ModelBuilder b)
    {
        b.Entity<Ticket>(e =>
        {
            e.ToTable("Tickets", "sup", t =>
            {
                t.HasCheckConstraint("CK_Tickets_Status", $"[Status] IN ({InList<TicketStatus>()})");
                t.HasCheckConstraint("CK_Tickets_Priority", $"[Priority] IN ({InList<TicketPriority>()})");
                t.HasCheckConstraint("CK_Tickets_Module", $"[Module] IN ({InList<AppModule>()})");
            });
            e.Code(x => x.Number, 16);
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.Screen).HasMaxLength(48).IsUnicode(false);
            e.Code(x => x.ErrorCode, 8);
            e.Code(x => x.RelatedDocumentNumber, 16);
            e.Property(x => x.DiagnosticJson).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.Number).IsUnique().HasDatabaseName("UX_Tickets_Number");
            e.HasIndex(x => new { x.CreatedAt, x.Id });
            e.HasIndex(x => x.RequesterUserId);
            e.HasIndex(x => x.AssigneeUserId);
            e.Ref<Ticket, User>(x => x.RequesterUserId);
            e.Ref<Ticket, User>(x => x.AssigneeUserId);
            e.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TicketComment>(e =>
        {
            e.ToTable("TicketComments", "sup");
            e.Property(x => x.Body).HasMaxLength(8000);
            e.Property(x => x.AiProvider).HasMaxLength(32).IsUnicode(false);
            e.Property(x => x.AiModel).HasMaxLength(96);
            e.Property(x => x.CitationsJson).HasMaxLength(4000);
            e.HasIndex(x => new { x.TicketId, x.CreatedAt });
            e.Ref<TicketComment, User>(x => x.AuthorUserId);
        });

        b.Entity<KbDocument>(e =>
        {
            e.ToTable("KbDocuments", "sup", t =>
            {
                t.HasCheckConstraint("CK_KbDocuments_Status", $"[Status] IN ({InList<KbStatus>()})");
                t.HasCheckConstraint("CK_KbDocuments_Visibility", $"[Visibility] IN ({InList<KbVisibility>()})");
                t.HasCheckConstraint("CK_KbDocuments_Module", $"[Module] IN ({InList<AppModule>()})");
                t.HasCheckConstraint("CK_KbDocuments_Published", "[Status] <> 2 OR [PublishedVersionId] IS NOT NULL");
            });
            e.Property(x => x.Slug).HasMaxLength(80).IsUnicode(false).UseCollation(CodeCollation);
            e.Code(x => x.ErrorCode, 8);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("UX_KbDocuments_Slug");
            e.HasIndex(x => x.Status);
            e.Ref<KbDocument, Warehouse>(x => x.WarehouseId);
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<KbDocumentVersion>(e =>
        {
            e.ToTable("KbDocumentVersions", "sup");
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.BodyMarkdown).HasColumnType("nvarchar(max)");
            e.HasIndex(x => new { x.DocumentId, x.VersionNo }).IsUnique().HasDatabaseName("UX_KbDocumentVersions_No");
        });

        b.Entity<KbChunk>(e =>
        {
            e.ToTable("KbChunks", "sup");
            e.Property(x => x.Heading).HasMaxLength(160);
            e.Property(x => x.Text).HasMaxLength(2000);
            e.HasIndex(x => new { x.VersionId, x.SectionNo }).IsUnique().HasDatabaseName("UX_KbChunks_Section");
            e.HasIndex(x => x.DocumentId);
            e.Ref<KbChunk, KbDocument>(x => x.DocumentId);
            e.Ref<KbChunk, KbDocumentVersion>(x => x.VersionId);
        });

        b.HasSequence<long>("TicketNumbers", "sup");
    }

    public static void Assistance(ModelBuilder b)
    {
        b.Entity<AiAnswer>(e =>
        {
            e.ToTable("Answers", "ai");
            e.Property(x => x.Status).HasMaxLength(24).IsUnicode(false);
            e.Property(x => x.Provider).HasMaxLength(32).IsUnicode(false);
            e.Property(x => x.Model).HasMaxLength(96);
            e.Property(x => x.AnswerText).HasMaxLength(6000);
            e.Property(x => x.LimitationsText).HasMaxLength(1000);
            e.Property(x => x.CitationsJson).HasMaxLength(4000);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AiUsage>(e =>
        {
            e.ToTable("Usage", "ai");
            e.Property(x => x.Status).HasMaxLength(24).IsUnicode(false);
            e.Property(x => x.Provider).HasMaxLength(32).IsUnicode(false);
            e.Property(x => x.Model).HasMaxLength(96);
        });
    }

    public static void OperationsJobs(ModelBuilder b)
    {
        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages", "ops");
            e.Property(x => x.Type).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.PayloadJson).HasMaxLength(2000);
            e.Property(x => x.LeaseOwner).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.LastErrorCode).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => new { x.ProcessedAt, x.AvailableAt, x.CreatedAt });
        });

        b.Entity<OutboxConsumption>(e =>
        {
            e.ToTable("OutboxConsumptions", "ops");
            e.HasKey(x => new { x.MessageId, x.Handler });
            e.Property(x => x.Handler).HasMaxLength(64).IsUnicode(false);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("Notifications", "ops");
            e.Property(x => x.Kind).HasMaxLength(32).IsUnicode(false);
            e.Property(x => x.Text).HasMaxLength(300);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReconciliationRun>(e =>
        {
            e.ToTable("ReconciliationRuns", "ops");
            e.HasIndex(x => x.CompletedAt);
            e.HasMany(x => x.Findings).WithOne().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReconciliationFinding>(e =>
        {
            e.ToTable("ReconciliationFindings", "ops");
            e.Property(x => x.Rule).HasMaxLength(48).IsUnicode(false);
        });
    }
}
