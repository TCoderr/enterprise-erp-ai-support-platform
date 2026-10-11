using Kantar.Application.Common;
using Kantar.Infrastructure.Persistence;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// The unit of work turns SQL Server errors 2601 and 2627 into a constraint name; the command
/// pipeline then decides between "replay the stored response" and "duplicate business reference".
/// The message texts below are the ones SQL Server 2022 produces for this schema.
/// </summary>
public sealed class SqlErrorParsingTests
{
    [Theory]
    [InlineData(
        "Cannot insert duplicate key row in object 'purch.GoodsReceipts' with unique index 'UX_GoodsReceipts_Supplier_ExternalReference'. The duplicate key value is (0199c0de-5a1b-7c3d-9e4f-0123456789ab, IRS-2026-0001).",
        "UX_GoodsReceipts_Supplier_ExternalReference")]
    [InlineData(
        "Cannot insert duplicate key row in object 'sales.Shipments' with unique index 'UX_Shipments_Order_DispatchReference'. The duplicate key value is (0199c0de-5a1b-7c3d-9e4f-0123456789ab, SEVK-1).",
        "UX_Shipments_Order_DispatchReference")]
    [InlineData(
        "Cannot insert duplicate key row in object 'inv.StockTransfers' with unique index 'UX_StockTransfers_Reference'. The duplicate key value is (TR-REF-1).",
        "UX_StockTransfers_Reference")]
    [InlineData(
        "Cannot insert duplicate key row in object 'inv.StockAdjustments' with unique index 'UX_StockAdjustments_Reference'. The duplicate key value is (AD-REF-1).",
        "UX_StockAdjustments_Reference")]
    [InlineData(
        "Cannot insert duplicate key row in object 'inv.StockMovements' with unique index 'UX_StockMovements_ReferenceLine_Type'. The duplicate key value is (3, 0199c0de-5a1b-7c3d-9e4f-0123456789ab, 4).",
        "UX_StockMovements_ReferenceLine_Type")]
    [InlineData(
        "Cannot insert duplicate key row in object 'md.Products' with unique index 'UX_Products_Sku'. The duplicate key value is (SKU-001).\r\nThe statement has been terminated.",
        "UX_Products_Sku")]
    public void Error_2601_yields_the_unique_index_name(string message, string expected)
    {
        Assert.Equal(expected, SqlUnitOfWork.ConstraintName(message));
    }

    [Theory]
    [InlineData(
        "Violation of PRIMARY KEY constraint 'PK_IdempotencyRecords'. Cannot insert duplicate key in object 'ops.IdempotencyRecords'. The duplicate key value is (0199c0de-5a1b-7c3d-9e4f-0123456789ab, purchasing.order.receive, test-5f0c2d7e9a1b4c3d8e6f).",
        "PK_IdempotencyRecords")]
    [InlineData(
        "Violation of PRIMARY KEY constraint 'PK_StockBalances'. Cannot insert duplicate key in object 'inv.StockBalances'. The duplicate key value is (0199c0de-5a1b-7c3d-9e4f-0123456789ab, 0199c0de-5a1b-7c3d-9e4f-0123456789ac).",
        "PK_StockBalances")]
    [InlineData(
        "Violation of UNIQUE KEY constraint 'AK_Example_Code'. Cannot insert duplicate key in object 'dbo.Example'. The duplicate key value is (A-1).\r\nThe statement has been terminated.",
        "AK_Example_Code")]
    public void Error_2627_yields_the_constraint_name(string message, string expected)
    {
        Assert.Equal(expected, SqlUnitOfWork.ConstraintName(message));
    }

    [Fact]
    public void The_names_the_pipeline_switches_on_are_the_ones_the_parser_returns()
    {
        const string twin = "Violation of PRIMARY KEY constraint 'PK_IdempotencyRecords'. Cannot insert duplicate key in object 'ops.IdempotencyRecords'. The duplicate key value is (…).";

        Assert.Equal(CommandExecutor.IdempotencyPrimaryKey, SqlUnitOfWork.ConstraintName(twin));
    }

    [Fact]
    public void The_duplicate_key_value_is_never_part_of_the_result()
    {
        // The value is caller-chosen text. Only the object name that precedes it is extracted.
        const string message = "Cannot insert duplicate key row in object 'md.Customers' with unique index 'UX_Customers_Code'. The duplicate key value is (GIZLI-MUSTERI-KODU).";

        var name = SqlUnitOfWork.ConstraintName(message);

        Assert.Equal("UX_Customers_Code", name);
        Assert.DoesNotContain("GIZLI", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Transaction (Process ID 61) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.")]
    [InlineData("The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_SalesOrderLines_SalesOrders_OrderId\". The conflict occurred in database \"kantar\", table \"sales.SalesOrders\", column 'Id'.")]
    [InlineData("Lock request time out period exceeded.")]
    public void A_message_without_a_quoted_name_is_unknown(string message)
    {
        Assert.Equal("unknown", SqlUnitOfWork.ConstraintName(message));
    }

    [Fact]
    public void An_unknown_name_is_neither_the_idempotency_twin_nor_a_business_reference()
    {
        Assert.NotEqual(CommandExecutor.IdempotencyPrimaryKey, SqlUnitOfWork.ConstraintName("no name here"));
    }
}
