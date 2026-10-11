using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kantar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ops");

            migrationBuilder.EnsureSchema(
                name: "md");

            migrationBuilder.EnsureSchema(
                name: "purch");

            migrationBuilder.EnsureSchema(
                name: "inv");

            migrationBuilder.EnsureSchema(
                name: "sales");

            migrationBuilder.EnsureSchema(
                name: "iam");

            migrationBuilder.CreateSequence(
                name: "GoodsReceiptNumbers",
                schema: "purch");

            migrationBuilder.CreateSequence(
                name: "PurchaseOrderNumbers",
                schema: "purch");

            migrationBuilder.CreateSequence(
                name: "SalesOrderNumbers",
                schema: "sales");

            migrationBuilder.CreateSequence(
                name: "ShipmentNumbers",
                schema: "sales");

            migrationBuilder.CreateSequence(
                name: "StockAdjustmentNumbers",
                schema: "inv");

            migrationBuilder.CreateSequence(
                name: "StockTransferNumbers",
                schema: "inv");

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                schema: "ops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    DetailJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                schema: "md",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TaxNumber = table.Column<string>(type: "varchar(11)", unicode: false, maxLength: 11, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                schema: "ops",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    HashVersion = table.Column<byte>(type: "tinyint", nullable: false),
                    ResponseStatus = table.Column<short>(type: "smallint", nullable: false),
                    ResponseContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ResponseBody = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    ResponseLocation = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                    ResponseETag = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => new { x.UserId, x.Operation, x.IdempotencyKey });
                });

            migrationBuilder.CreateTable(
                name: "Products",
                schema: "md",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Unit = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ListPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReorderLevel = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_ListPrice", "[ListPrice] >= 0");
                    table.CheckConstraint("CK_Products_ReorderLevel", "[ReorderLevel] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                schema: "md",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserName = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                    SecurityStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    PoApprovalLimit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_PoApprovalLimit", "[PoApprovalLimit] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                schema: "md",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                schema: "iam",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    CsrfSecret = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    SecurityStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "iam",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.Role });
                    table.CheckConstraint("CK_UserRoles_Role", "[Role] IN ('SalesAgent', 'SalesManager', 'PurchasingAgent', 'WarehouseOperator', 'SupportAgent', 'SupportManager', 'SystemAdmin')");
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                schema: "purch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SupplierCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SupplierName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrders_Status", "[Status] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "md",
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReservationEntries",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityDelta = table.Column<int>(type: "int", nullable: false),
                    ReservedAfter = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<byte>(type: "tinyint", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservationEntries", x => x.Id);
                    table.CheckConstraint("CK_ReservationEntries_Quantity", "[QuantityDelta] <> 0");
                    table.CheckConstraint("CK_ReservationEntries_ReasonSign", "([Reason] = 1 AND [QuantityDelta] > 0) OR ([Reason] IN (2, 3) AND [QuantityDelta] < 0)");
                    table.CheckConstraint("CK_ReservationEntries_ReservedAfter", "[ReservedAfter] >= 0");
                    table.ForeignKey(
                        name: "FK_ReservationEntries_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReservationEntries_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CustomerCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CustomerName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CustomerTaxNumber = table.Column<string>(type: "varchar(11)", unicode: false, maxLength: 11, nullable: false),
                    ShippingAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.CheckConstraint("CK_SalesOrders_Status", "[Status] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "md",
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustments",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reference = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReasonCode = table.Column<byte>(type: "tinyint", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustments", x => x.Id);
                    table.CheckConstraint("CK_StockAdjustments_Reason", "[ReasonCode] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_StockAdjustments_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockBalances",
                schema: "inv",
                columns: table => new
                {
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnHand = table.Column<int>(type: "int", nullable: false),
                    Reserved = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockBalances", x => new { x.WarehouseId, x.ProductId });
                    table.CheckConstraint("CK_StockBalances_OnHand", "[OnHand] >= [Reserved]");
                    table.CheckConstraint("CK_StockBalances_Reserved", "[Reserved] >= 0");
                    table.ForeignKey(
                        name: "FK_StockBalances_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockBalances_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    QuantityDelta = table.Column<int>(type: "int", nullable: false),
                    OnHandAfter = table.Column<int>(type: "int", nullable: false),
                    ReferenceType = table.Column<byte>(type: "tinyint", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.CheckConstraint("CK_StockMovements_OnHandAfter", "[OnHandAfter] >= 0");
                    table.CheckConstraint("CK_StockMovements_Quantity", "[QuantityDelta] <> 0");
                    table.CheckConstraint("CK_StockMovements_TypeReference", "([Type] = 1 AND [ReferenceType] = 1) OR ([Type] = 2 AND [ReferenceType] = 2) OR ([Type] IN (3, 4) AND [ReferenceType] = 3) OR ([Type] = 5 AND [ReferenceType] = 4)");
                    table.CheckConstraint("CK_StockMovements_TypeSign", "([Type] IN (1, 4) AND [QuantityDelta] > 0) OR ([Type] IN (2, 3) AND [QuantityDelta] < 0) OR [Type] = 5");
                    table.ForeignKey(
                        name: "FK_StockMovements_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockMovements_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Reference = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    FromWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.Id);
                    table.CheckConstraint("CK_StockTransfers_Warehouses", "[FromWarehouseId] <> [ToWarehouseId]");
                    table.ForeignKey(
                        name: "FK_StockTransfers_Warehouses_FromWarehouseId",
                        column: x => x.FromWarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransfers_Warehouses_ToWarehouseId",
                        column: x => x.ToWarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WarehouseGrants",
                schema: "iam",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<byte>(type: "tinyint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WarehouseGrants", x => new { x.UserId, x.WarehouseId });
                    table.CheckConstraint("CK_WarehouseGrants_Level", "[Level] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_WarehouseGrants_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "iam",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WarehouseGrants_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceipts",
                schema: "purch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalReference = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoodsReceipts_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalSchema: "purch",
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceipts_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "md",
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceipts_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                schema: "purch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProductName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Unit = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false, collation: "Latin1_General_100_BIN2"),
                    UnitCost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OrderedQty = table.Column<int>(type: "int", nullable: false),
                    ReceivedQty = table.Column<int>(type: "int", nullable: false),
                    CancelledQty = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => x.Id);
                    table.CheckConstraint("CK_PurchaseOrderLines_Ordered", "[OrderedQty] BETWEEN 1 AND 100000");
                    table.CheckConstraint("CK_PurchaseOrderLines_Quantities", "[ReceivedQty] >= 0 AND [CancelledQty] >= 0 AND [ReceivedQty] + [CancelledQty] <= [OrderedQty]");
                    table.CheckConstraint("CK_PurchaseOrderLines_UnitCost", "[UnitCost] >= 0");
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "purch",
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderLines",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProductName = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Unit = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false, collation: "Latin1_General_100_BIN2"),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OrderedQty = table.Column<int>(type: "int", nullable: false),
                    ShippedQty = table.Column<int>(type: "int", nullable: false),
                    CancelledQty = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.CheckConstraint("CK_SalesOrderLines_Ordered", "[OrderedQty] BETWEEN 1 AND 100000");
                    table.CheckConstraint("CK_SalesOrderLines_Quantities", "[ShippedQty] >= 0 AND [CancelledQty] >= 0 AND [ShippedQty] + [CancelledQty] <= [OrderedQty]");
                    table.CheckConstraint("CK_SalesOrderLines_UnitPrice", "[UnitPrice] >= 0");
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "sales",
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SalesOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DispatchReference = table.Column<string>(type: "varchar(48)", unicode: false, maxLength: 48, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shipments_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalSchema: "sales",
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "md",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockAdjustmentLines",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdjustmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityDelta = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockAdjustmentLines", x => x.Id);
                    table.CheckConstraint("CK_StockAdjustmentLines_Quantity", "[QuantityDelta] <> 0");
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockAdjustmentLines_StockAdjustments_AdjustmentId",
                        column: x => x.AdjustmentId,
                        principalSchema: "inv",
                        principalTable: "StockAdjustments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferLines",
                schema: "inv",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransferId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineNo = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferLines", x => x.Id);
                    table.CheckConstraint("CK_StockTransferLines_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_StockTransfers_TransferId",
                        column: x => x.TransferId,
                        principalSchema: "inv",
                        principalTable: "StockTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceiptLines",
                schema: "purch",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceiptLines", x => x.Id);
                    table.CheckConstraint("CK_GoodsReceiptLines_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLines_GoodsReceipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalSchema: "purch",
                        principalTable: "GoodsReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLines_PurchaseOrderLines_PurchaseOrderLineId",
                        column: x => x.PurchaseOrderLineId,
                        principalSchema: "purch",
                        principalTable: "PurchaseOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentLines",
                schema: "sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentLines", x => x.Id);
                    table.CheckConstraint("CK_ShipmentLines_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_ShipmentLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "md",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLines_SalesOrderLines_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalSchema: "sales",
                        principalTable: "SalesOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShipmentLines_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalSchema: "sales",
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Action",
                schema: "ops",
                table: "AuditEntries",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EntityType_EntityId",
                schema: "ops",
                table: "AuditEntries",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_Customers_Code",
                schema: "md",
                table: "Customers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptLines_ProductId",
                schema: "purch",
                table: "GoodsReceiptLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceiptLines_PurchaseOrderLineId",
                schema: "purch",
                table: "GoodsReceiptLines",
                column: "PurchaseOrderLineId");

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceiptLines_OrderLine",
                schema: "purch",
                table: "GoodsReceiptLines",
                columns: new[] { "ReceiptId", "PurchaseOrderLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_PurchaseOrderId",
                schema: "purch",
                table: "GoodsReceipts",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipts_WarehouseId",
                schema: "purch",
                table: "GoodsReceipts",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceipts_Number",
                schema: "purch",
                table: "GoodsReceipts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceipts_Supplier_ExternalReference",
                schema: "purch",
                table: "GoodsReceipts",
                columns: new[] { "SupplierId", "ExternalReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_ExpiresAt",
                schema: "ops",
                table: "IdempotencyRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "UX_Products_Sku",
                schema: "md",
                table: "Products",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_ProductId",
                schema: "purch",
                table: "PurchaseOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrderLines_Product",
                schema: "purch",
                table: "PurchaseOrderLines",
                columns: new[] { "OrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_OwnerUserId",
                schema: "purch",
                table: "PurchaseOrders",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_SupplierId",
                schema: "purch",
                table: "PurchaseOrders",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_WarehouseId_CreatedAt_Id",
                schema: "purch",
                table: "PurchaseOrders",
                columns: new[] { "WarehouseId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrders_Number",
                schema: "purch",
                table: "PurchaseOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReservationEntries_ProductId",
                schema: "inv",
                table: "ReservationEntries",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ReservationEntries_SalesOrderLineId",
                schema: "inv",
                table: "ReservationEntries",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ReservationEntries_WarehouseId_ProductId_Id",
                schema: "inv",
                table: "ReservationEntries",
                columns: new[] { "WarehouseId", "ProductId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_ProductId",
                schema: "sales",
                table: "SalesOrderLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "UX_SalesOrderLines_Product",
                schema: "sales",
                table: "SalesOrderLines",
                columns: new[] { "OrderId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                schema: "sales",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_OwnerUserId",
                schema: "sales",
                table: "SalesOrders",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_WarehouseId_CreatedAt_Id",
                schema: "sales",
                table: "SalesOrders",
                columns: new[] { "WarehouseId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_SalesOrders_Number",
                schema: "sales",
                table: "SalesOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ExpiresAt",
                schema: "iam",
                table: "Sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UserId",
                schema: "iam",
                table: "Sessions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_Sessions_TokenHash",
                schema: "iam",
                table: "Sessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLines_ProductId",
                schema: "sales",
                table: "ShipmentLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentLines_SalesOrderLineId",
                schema: "sales",
                table: "ShipmentLines",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "UX_ShipmentLines_OrderLine",
                schema: "sales",
                table: "ShipmentLines",
                columns: new[] { "ShipmentId", "SalesOrderLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_WarehouseId",
                schema: "sales",
                table: "Shipments",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "UX_Shipments_Number",
                schema: "sales",
                table: "Shipments",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Shipments_Order_DispatchReference",
                schema: "sales",
                table: "Shipments",
                columns: new[] { "SalesOrderId", "DispatchReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustmentLines_ProductId",
                schema: "inv",
                table: "StockAdjustmentLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "UX_StockAdjustmentLines_Product",
                schema: "inv",
                table: "StockAdjustmentLines",
                columns: new[] { "AdjustmentId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_CreatedAt_Id",
                schema: "inv",
                table: "StockAdjustments",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StockAdjustments_WarehouseId",
                schema: "inv",
                table: "StockAdjustments",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "UX_StockAdjustments_Number",
                schema: "inv",
                table: "StockAdjustments",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_StockAdjustments_Reference",
                schema: "inv",
                table: "StockAdjustments",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_ProductId",
                schema: "inv",
                table: "StockBalances",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ProductId",
                schema: "inv",
                table: "StockMovements",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_WarehouseId_ProductId_Id",
                schema: "inv",
                table: "StockMovements",
                columns: new[] { "WarehouseId", "ProductId", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_StockMovements_ReferenceLine_Type",
                schema: "inv",
                table: "StockMovements",
                columns: new[] { "ReferenceType", "ReferenceLineId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_ProductId",
                schema: "inv",
                table: "StockTransferLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "UX_StockTransferLines_Product",
                schema: "inv",
                table: "StockTransferLines",
                columns: new[] { "TransferId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_CreatedAt_Id",
                schema: "inv",
                table: "StockTransfers",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_FromWarehouseId",
                schema: "inv",
                table: "StockTransfers",
                column: "FromWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransfers_ToWarehouseId",
                schema: "inv",
                table: "StockTransfers",
                column: "ToWarehouseId");

            migrationBuilder.CreateIndex(
                name: "UX_StockTransfers_Number",
                schema: "inv",
                table: "StockTransfers",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_StockTransfers_Reference",
                schema: "inv",
                table: "StockTransfers",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Suppliers_Code",
                schema: "md",
                table: "Suppliers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Users_UserName",
                schema: "iam",
                table: "Users",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WarehouseGrants_WarehouseId",
                schema: "iam",
                table: "WarehouseGrants",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "UX_Warehouses_Code",
                schema: "md",
                table: "Warehouses",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "GoodsReceiptLines",
                schema: "purch");

            migrationBuilder.DropTable(
                name: "IdempotencyRecords",
                schema: "ops");

            migrationBuilder.DropTable(
                name: "ReservationEntries",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "Sessions",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "ShipmentLines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "StockAdjustmentLines",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "StockBalances",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "StockMovements",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "StockTransferLines",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "WarehouseGrants",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "GoodsReceipts",
                schema: "purch");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLines",
                schema: "purch");

            migrationBuilder.DropTable(
                name: "SalesOrderLines",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Shipments",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "StockAdjustments",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "StockTransfers",
                schema: "inv");

            migrationBuilder.DropTable(
                name: "PurchaseOrders",
                schema: "purch");

            migrationBuilder.DropTable(
                name: "Products",
                schema: "md");

            migrationBuilder.DropTable(
                name: "SalesOrders",
                schema: "sales");

            migrationBuilder.DropTable(
                name: "Suppliers",
                schema: "md");

            migrationBuilder.DropTable(
                name: "Customers",
                schema: "md");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "iam");

            migrationBuilder.DropTable(
                name: "Warehouses",
                schema: "md");

            migrationBuilder.DropSequence(
                name: "GoodsReceiptNumbers",
                schema: "purch");

            migrationBuilder.DropSequence(
                name: "PurchaseOrderNumbers",
                schema: "purch");

            migrationBuilder.DropSequence(
                name: "SalesOrderNumbers",
                schema: "sales");

            migrationBuilder.DropSequence(
                name: "ShipmentNumbers",
                schema: "sales");

            migrationBuilder.DropSequence(
                name: "StockAdjustmentNumbers",
                schema: "inv");

            migrationBuilder.DropSequence(
                name: "StockTransferNumbers",
                schema: "inv");
        }
    }
}
