using System.Net;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Inventory;

/// <summary>docs/02 §4.3 and §7: reasoned stock adjustments by the warehouse supervisor.</summary>
public sealed class AdjustmentTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private Task<int> DocumentCountAsync(string reference) =>
        _world.CountAsync("SELECT COUNT(*) FROM inv.StockAdjustments WHERE Reference = @r;", new SqlParameter("@r", reference));

    [Fact]
    public async Task Positive_and_negative_adjustments_write_one_journal_row_each_with_the_balance_after()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 10);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var (found, damaged) = (_world.Reference("AD"), _world.Reference("AD"));

        using var plus = await World.AdjustAsync(supervisor, found, warehouse.Id, "Found", (product.Id, 5));
        var first = await plus.JsonAsync(HttpStatusCode.Created);
        Assert.Equal($"/api/inventory/adjustments/{first.Id()}", plus.Headers.Location?.ToString());
        Assert.StartsWith("AD-", first.Str("number"), StringComparison.Ordinal);
        Assert.Equal((found, "Found", warehouse.Id), (first.Str("reference"), first.Str("reasonCode"), first.Id("warehouseId")));
        Assert.Equal(5, first["lines"]![0]!.Number("quantityDelta"));
        Assert.Equal((15, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        using var minus = await World.AdjustAsync(supervisor, damaged, warehouse.Id, "Damage", (product.Id, -3));
        var second = await minus.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((12, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        var movements = await supervisor.GetJsonAsync($"/api/inventory/movements?warehouseId={warehouse.Id}&productId={product.Id}");
        var rows = movements["items"]!.AsArray()
            .Select(m => (m!.Str("type"), m!.Number("quantityDelta"), m!.Number("onHandAfter"), m!["referenceNumber"]!.GetValue<string>()))
            .ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(("Adjustment", -3, 12, second.Str("number")), rows[0]);
        Assert.Equal(("Adjustment", 5, 15, first.Str("number")), rows[1]);
        Assert.Equal(("Receipt", 10, 10), (rows[2].Item1, rows[2].Item2, rows[2].Item3));

        var read = await supervisor.GetJsonAsync($"/api/inventory/adjustments/{second.Id()}");
        Assert.Equal((damaged, "Damage"), (read.Str("reference"), read.Str("reasonCode")));
        var list = await supervisor.GetJsonAsync($"/api/inventory/adjustments?warehouseId={warehouse.Id}");
        Assert.Equal(new[] { first.Id(), second.Id() }.Order(), list["items"]!.AsArray().Select(a => a!.Id()).Order());
        Assert.Equal(2, await _world.AuditCountAsync((await supervisor.GetJsonAsync("/api/auth/me")).Id(), "inventory.adjustment_posted"));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task A_positive_adjustment_creates_the_balance_row_and_one_document_may_carry_both_signs()
    {
        var warehouse = await _world.WarehouseAsync();
        var (stocked, fresh) = (await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(warehouse, stocked, 10);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);

        using var response = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "CountDifference", (stocked.Id, -4), (fresh.Id, 7));
        var adjustment = await response.JsonAsync(HttpStatusCode.Created);

        Assert.Equal([(1, stocked.Id, -4), (2, fresh.Id, 7)], adjustment["lines"]!.AsArray().Select(l => (l!.Number("lineNo"), l!.Id("productId"), l!.Number("quantityDelta"))));
        Assert.Equal((6, 0), await _world.BalanceAsync(warehouse.Id, stocked.Id));
        Assert.Equal((7, 0), await _world.BalanceAsync(warehouse.Id, fresh.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, stocked.Id, "Adjustment"));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, fresh.Id, "Adjustment"));
        await _world.AssertReconciledAsync(warehouse.Id, stocked.Id);
        await _world.AssertReconciledAsync(warehouse.Id, fresh.Id);
    }

    [Fact]
    public async Task A_negative_adjustment_cannot_eat_into_reserved_stock_and_writes_nothing()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 10);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var supervisorUser = await _world.UserAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var supervisor = await _world.LoginAsync(supervisorUser);
        await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 8));
        Assert.Equal((10, 8), await _world.BalanceAsync(warehouse.Id, product.Id));

        // 10 on hand, 8 reserved: only 2 are free. Removing 3 would leave less than what is promised.
        var reference = _world.Reference("AD");
        var key = ApiClient.NewKey();
        using var refused = await supervisor.PostAsync("/api/inventory/adjustments", World.AdjustmentBody(reference, warehouse.Id, "Loss", (product.Id, -3)), key);
        var problem = await refused.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");
        Assert.Equal("lines[0].quantityDelta", problem["errors"]![0]!.Str("field"));

        Assert.Equal((10, 8), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await DocumentCountAsync(reference));
        Assert.Equal(0, await _world.AuditCountAsync(supervisorUser.Id, "inventory.adjustment_posted"));
        Assert.Equal(0, await _world.IdempotencyRecordCountAsync(key));

        // Exactly the free quantity can go; the reference of the refused attempt is still unused.
        using var allowed = await World.AdjustAsync(supervisor, reference, warehouse.Id, "Loss", (product.Id, -2));
        await allowed.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((8, 8), await _world.BalanceAsync(warehouse.Id, product.Id));
        using var oneMore = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Loss", (product.Id, -1));
        await oneMore.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");
        Assert.Equal((8, 8), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Of_a_confirmation_and_a_negative_adjustment_the_second_is_decided_on_the_state_the_first_committed(bool confirmFirst)
    {
        // 10 on hand. Reserving 8 and removing 5 exclude each other. The first command is parked inside
        // its transaction right after it changed the balance; the second is observed waiting on that row
        // and must lose once the first commits, whichever of the two it is.
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 10);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 8));
        var reference = _world.Reference("AD");
        Task<Reply> Confirm() => Reply.ReadAsync(sales.PostAsync($"/api/sales/orders/{order.Id()}/confirm", null, ApiClient.NewKey(), etag));
        Task<Reply> Adjust() => Reply.ReadAsync(World.AdjustAsync(supervisor, reference, warehouse.Id, "Damage", (product.Id, -5)));

        Reply first, second;
        using (var pause = new Pause(fixture.Failpoints, "stock.after-posting"))
        {
            var running = confirmFirst ? Confirm() : Adjust();
            await pause.Reached;

            // Nothing is committed yet, so nobody else sees the half-done change.
            Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

            var waiting = confirmFirst ? Adjust() : Confirm();
            await fixture.WaitForBlockedRequestAsync();
            Assert.False(waiting.IsCompleted);

            pause.Release();
            (first, second) = (await running, await waiting);
        }

        Assert.True(first.IsSuccess, first.Text);
        second.AssertProblem(HttpStatusCode.Conflict, "insufficient_available");
        var stored = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        if (confirmFirst)
        {
            Assert.Equal((10, 8), await _world.BalanceAsync(warehouse.Id, product.Id));
            Assert.Equal("Confirmed", stored.Str("status"));
            Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id, "Adjustment"));
            Assert.Equal(0, await DocumentCountAsync(reference));
        }
        else
        {
            Assert.Equal((5, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
            Assert.Equal("Draft", stored.Str("status"));
            Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, product.Id));
            Assert.Equal(1, await DocumentCountAsync(reference));
        }

        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task One_short_line_rejects_the_whole_adjustment_including_the_lines_that_would_fit()
    {
        var warehouse = await _world.WarehouseAsync();
        var (plenty, scarce, never) = (await _world.ProductAsync(), await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(warehouse, [(plenty, 10), (scarce, 2)]);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);

        // Postings are applied in a fixed (warehouse, product) order, so both orders of the short line are tried.
        var first = _world.Reference("AD");
        using var shortSecond = await World.AdjustAsync(supervisor, first, warehouse.Id, "Damage", (plenty.Id, -5), (scarce.Id, -3));
        var problem = await shortSecond.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");
        Assert.Equal(["lines[1].quantityDelta"], problem["errors"]!.AsArray().Select(e => e!.Str("field")));
        var second = _world.Reference("AD");
        using var shortFirst = await World.AdjustAsync(supervisor, second, warehouse.Id, "Damage", (scarce.Id, -3), (plenty.Id, 5));
        await shortFirst.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");

        // A product that never had stock has no balance to take from.
        var third = _world.Reference("AD");
        using var noBalance = await World.AdjustAsync(supervisor, third, warehouse.Id, "Loss", (plenty.Id, 1), (never.Id, -1));
        await noBalance.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");

        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, plenty.Id));
        Assert.Equal((2, 0), await _world.BalanceAsync(warehouse.Id, scarce.Id));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, plenty.Id, "Adjustment"));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, scarce.Id, "Adjustment"));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, never.Id));
        Assert.Equal(0, await DocumentCountAsync(first) + await DocumentCountAsync(second) + await DocumentCountAsync(third));
        await _world.AssertReconciledAsync(warehouse.Id, plenty.Id);
        await _world.AssertReconciledAsync(warehouse.Id, scarce.Id);
        await _world.AssertReconciledAsync(warehouse.Id, never.Id);
    }

    [Fact]
    public async Task Reason_code_note_reference_and_lines_are_validated_before_anything_is_written()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 10);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var reference = _world.Reference("AD");
        const string goodNote = "Sayım farkı tutanağı";
        object Line(int delta) => new { productId = product.Id, quantityDelta = delta };

        var invalid = new (string Field, object Body)[]
        {
            ("reasonCode", new { reference, warehouseId = warehouse.Id, reasonCode = "Stolen", note = goodNote, lines = new[] { Line(-1) } }),
            ("reasonCode", new { reference, warehouseId = warehouse.Id, reasonCode = "damage", note = goodNote, lines = new[] { Line(-1) } }),
            ("reasonCode", new { reference, warehouseId = warehouse.Id, reasonCode = "2", note = goodNote, lines = new[] { Line(-1) } }),
            ("reasonCode", new { reference, warehouseId = warehouse.Id, reasonCode = "", note = goodNote, lines = new[] { Line(-1) } }),
            ("note", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = "kısa not", lines = new[] { Line(-1) } }),
            ("note", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = "          ", lines = new[] { Line(-1) } }),
            ("note", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = new string('n', 501), lines = new[] { Line(-1) } }),
            ("reference", new { reference = "a b", warehouseId = warehouse.Id, reasonCode = "Damage", note = goodNote, lines = new[] { Line(-1) } }),
            ("reference", new { reference = "AB", warehouseId = warehouse.Id, reasonCode = "Damage", note = goodNote, lines = new[] { Line(-1) } }),
            ("lines[0].quantityDelta", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = goodNote, lines = new[] { Line(0) } }),
            ("lines[0].quantityDelta", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = goodNote, lines = new[] { Line(100_001) } }),
            ("lines[1].productId", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = goodNote, lines = new[] { Line(-1), Line(-2) } }),
            ("lines", new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", note = goodNote, lines = Array.Empty<object>() }),
        };

        foreach (var (field, body) in invalid)
        {
            using var response = await supervisor.PostAsync("/api/inventory/adjustments", body, ApiClient.NewKey());
            var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains(field, problem["errors"]!.AsArray().Select(e => e!.Str("field")));
        }

        // A note is not optional: leaving the member out is rejected by the request contract itself.
        using var withoutNote = await supervisor.PostAsync(
            "/api/inventory/adjustments",
            new { reference, warehouseId = warehouse.Id, reasonCode = "Damage", lines = new[] { Line(-1) } },
            ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.BadRequest, withoutNote.StatusCode);

        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await DocumentCountAsync(reference));

        // The longest allowed note, with a line break, is accepted and stored trimmed.
        var longNote = "  " + new string('n', 249) + "\n" + new string('m', 250) + "  ";
        using var accepted = await supervisor.PostAsync(
            "/api/inventory/adjustments",
            new { reference = reference.ToLowerInvariant(), warehouseId = warehouse.Id, reasonCode = "DataCorrection", note = longNote, lines = new[] { Line(-1) } },
            ApiClient.NewKey());
        var adjustment = await accepted.JsonAsync(HttpStatusCode.Created);
        Assert.Equal(longNote.Trim(), adjustment.Str("note"));
        Assert.Equal(reference, adjustment.Str("reference"));
        Assert.Equal((9, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Adjusting_needs_the_warehouse_operator_role_with_supervise_on_that_warehouse()
    {
        var warehouse = await _world.WarehouseAsync();
        var other = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 10);

        var denied = new (string Who, string[] Roles, (Guid, WarehouseLevel)[] Grants, HttpStatusCode Status, string Code)[]
        {
            ("operator with Operate", [Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)], HttpStatusCode.Forbidden, "forbidden"),
            ("operator with View", [Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.View)], HttpStatusCode.Forbidden, "forbidden"),
            ("operator without a grant", [Roles.WarehouseOperator], [], HttpStatusCode.NotFound, "not_found"),
            ("supervisor of another warehouse", [Roles.WarehouseOperator], [(other.Id, WarehouseLevel.Supervise)], HttpStatusCode.NotFound, "not_found"),
            ("sales manager with Supervise", [Roles.SalesManager], [(warehouse.Id, WarehouseLevel.Supervise)], HttpStatusCode.Forbidden, "forbidden"),
            ("purchasing agent with Supervise", [Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.Supervise)], HttpStatusCode.Forbidden, "forbidden"),
            ("system admin without a grant", [Roles.SystemAdmin], [], HttpStatusCode.NotFound, "not_found"),
        };

        foreach (var (who, roles, grants, status, code) in denied)
        {
            var client = await _world.LoginAsAsync(roles, grants);
            var reference = _world.Reference("AD");
            foreach (var delta in new[] { 1, -1 })
            {
                var reply = await Reply.ReadAsync(World.AdjustAsync(client, reference, warehouse.Id, "Found", (product.Id, delta)));
                Assert.True(reply.Status == status, $"{who}: expected {(int)status}, got {(int)reply.Status}");
                Assert.Equal(code, reply.Problem);
            }

            Assert.Equal(0, await DocumentCountAsync(reference));
        }

        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task An_adjustment_in_a_passive_warehouse_or_for_an_unknown_product_is_refused()
    {
        var warehouse = await _world.WarehouseAsync();
        var passive = await _world.WarehouseAsync(active: false);
        var product = await _world.ProductAsync();
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise), (passive.Id, WarehouseLevel.Supervise)]);

        using var inPassive = await World.AdjustAsync(supervisor, _world.Reference("AD"), passive.Id, "Found", (product.Id, 5));
        await inPassive.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
        using var unknownProduct = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Found", (Guid.NewGuid(), 5));
        await unknownProduct.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal((0, 0), await _world.BalanceAsync(passive.Id, product.Id));
        Assert.Equal(0, await _world.MovementCountAsync(passive.Id, product.Id));
    }
}
