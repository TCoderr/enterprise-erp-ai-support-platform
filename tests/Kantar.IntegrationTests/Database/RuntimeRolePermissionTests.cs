using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Database;

/// <summary>
/// These tests connect with the real restricted login the application uses (kantar_app), not with the
/// migrator, and prove what that login cannot do.
/// </summary>
public sealed class RuntimeRolePermissionTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    public static TheoryData<string, string> AppendOnlyTables => new()
    {
        { "inv.StockMovements", "QuantityDelta" }, { "inv.ReservationEntries", "QuantityDelta" }, { "ops.AuditEntries", "Action" },
        { "sup.KbDocumentVersions", "Title" }, { "sup.KbChunks", "Heading" }, { "sup.TicketComments", "Body" },
        { "sales.Shipments", "DispatchReference" }, { "sales.ShipmentLines", "Quantity" },
        { "purch.GoodsReceipts", "ExternalReference" }, { "purch.GoodsReceiptLines", "Quantity" },
        { "inv.StockTransfers", "Note" }, { "inv.StockTransferLines", "Quantity" },
        { "inv.StockAdjustments", "Note" }, { "inv.StockAdjustmentLines", "QuantityDelta" },
    };

    [Fact]
    public async Task Application_login_is_the_restricted_one()
    {
        Assert.Equal("kantar_app", await fixture.AppScalarAsync<string>("SELECT USER_NAME();"));
        Assert.Equal(0, await fixture.AppScalarAsync<int>("SELECT ISNULL(IS_ROLEMEMBER(N'db_owner'), 0);"));
        Assert.Equal(0, await fixture.AppScalarAsync<int>("SELECT ISNULL(IS_ROLEMEMBER(N'db_datawriter'), 0);"));
        Assert.Equal(0, await fixture.AppScalarAsync<int>("SELECT ISNULL(IS_ROLEMEMBER(N'db_ddladmin'), 0);"));
        Assert.Equal(0, await fixture.AppScalarAsync<int>("SELECT ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0);"));
    }

    [Theory]
    [MemberData(nameof(AppendOnlyTables))]
    public async Task Append_only_tables_refuse_update_delete_and_truncate(string table, string column)
    {
        var before = await fixture.AppScalarAsync<int>($"SELECT COUNT(*) FROM {table};");

        var update = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync($"UPDATE {table} SET {column} = {column} WHERE 1 = 0;"));
        Assert.Equal(229, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync($"DELETE FROM {table} WHERE 1 = 0;"));
        Assert.Equal(229, delete.Number);
        var truncate = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync($"TRUNCATE TABLE {table};"));
        Assert.Contains(truncate.Number, new[] { 1088, 4701, 229 });

        Assert.Equal(before, await fixture.AppScalarAsync<int>($"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public async Task Journal_rows_written_by_a_real_transaction_cannot_be_changed_afterwards()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 25);
        var parameters = () => new[] { new SqlParameter("@w", warehouse.Id), new SqlParameter("@p", product.Id) };

        var update = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.AppExecuteAsync("UPDATE inv.StockMovements SET QuantityDelta = 9999 WHERE WarehouseId = @w AND ProductId = @p;", parameters()));
        Assert.Equal(229, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.AppExecuteAsync("DELETE FROM inv.StockMovements WHERE WarehouseId = @w AND ProductId = @p;", parameters()));
        Assert.Equal(229, delete.Number);

        Assert.Equal(25, await fixture.AppScalarAsync<int>("SELECT SUM(QuantityDelta) FROM inv.StockMovements WHERE WarehouseId = @w AND ProductId = @p;", parameters()));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Schema_changes_are_refused()
    {
        var create = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync("CREATE TABLE inv.Injected (Id int);"));
        Assert.Contains(create.Number, new[] { 262, 2760 });
        var alter = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync("ALTER TABLE inv.StockBalances DROP CONSTRAINT CK_StockBalances_OnHand;"));
        Assert.Contains(alter.Number, new[] { 1088, 4902, 3727 });
        var drop = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync("DROP TABLE ops.AuditEntries;"));
        Assert.Contains(drop.Number, new[] { 3701, 1088 });
    }

    [Fact]
    public async Task Check_constraints_reject_invalid_balances_even_outside_the_api()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 10);
        var parameters = () => new[] { new SqlParameter("@w", warehouse.Id), new SqlParameter("@p", product.Id) };

        // The application login may update balances (that is its job), but the database still refuses
        // a state that breaks OnHand >= Reserved >= 0.
        var overReserve = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.AppExecuteAsync("UPDATE inv.StockBalances SET Reserved = 11 WHERE WarehouseId = @w AND ProductId = @p;", parameters()));
        Assert.Equal(547, overReserve.Number);
        var negative = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.AppExecuteAsync("UPDATE inv.StockBalances SET Reserved = -1 WHERE WarehouseId = @w AND ProductId = @p;", parameters()));
        Assert.Equal(547, negative.Number);

        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
    }

    [Fact]
    public async Task Check_constraints_reject_inconsistent_line_quantities_and_journal_rows()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 10);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, _) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 4));

        var overShip = await Assert.ThrowsAsync<SqlException>(() =>
            fixture.AppExecuteAsync("UPDATE sales.SalesOrderLines SET ShippedQty = 5 WHERE OrderId = @o;", new SqlParameter("@o", order.Id())));
        Assert.Equal(547, overShip.Number);

        // A movement whose sign contradicts its type is refused by the journal's own constraint.
        var wrongSign = await Assert.ThrowsAsync<SqlException>(() => fixture.AppExecuteAsync(
            """
            INSERT INTO inv.StockMovements (WarehouseId, ProductId, Type, QuantityDelta, OnHandAfter, ReferenceType, ReferenceId, ReferenceLineId, ActorUserId, OccurredAt)
            VALUES (@w, @p, 1, -3, 7, 1, NEWID(), NEWID(), NEWID(), SYSUTCDATETIME());
            """,
            new SqlParameter("@w", warehouse.Id), new SqlParameter("@p", product.Id)));
        Assert.Equal(547, wrongSign.Number);
    }
}
