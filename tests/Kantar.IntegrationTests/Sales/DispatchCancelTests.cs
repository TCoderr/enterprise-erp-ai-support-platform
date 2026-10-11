using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Sales;

/// <summary>docs/02 §5.2 and §8: partial shipments, cancelling the remainder, and the races between them.</summary>
public sealed class DispatchCancelTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private Task<int> ShipmentCountAsync(Guid orderId) =>
        _world.CountAsync("SELECT COUNT(*) FROM sales.Shipments WHERE SalesOrderId = @o;", new SqlParameter("@o", orderId));

    private async Task<(ApiClient Sales, ApiClient Operator, Customer Customer)> ActorsAsync(Warehouse warehouse)
    {
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        return (sales, operatorClient, await _world.CustomerAsync());
    }

    private static (int Ordered, int Shipped, int Cancelled, int Open) Quantities(JsonNode order, int line = 0)
    {
        var node = order["lines"]![line]!;
        return (node.Number("orderedQty"), node.Number("shippedQty"), node.Number("cancelledQty"), node.Number("openQty"));
    }

    [Fact]
    public async Task Cancelling_after_a_shipment_releases_only_the_remainder()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 20);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, confirmedETag) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var line = order["lines"]![0]!.Id();

        using var shipped = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (line, 4));
        var shipment = await shipped.JsonAsync(HttpStatusCode.Created);
        Assert.Equal($"/api/sales/orders/{order.Id()}/shipments/{shipment.Id()}", shipped.Headers.Location?.ToString());
        Assert.Equal("PartiallyShipped", shipment.Str("salesOrderStatus"));
        Assert.Equal((16, 6), await _world.BalanceAsync(warehouse.Id, product.Id));

        // The version the agent confirmed with is stale after the shipment.
        using var stale = await World.CancelSalesOrderAsync(sales, order.Id(), confirmedETag);
        await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        using var missing = await World.CancelSalesOrderAsync(sales, order.Id(), null);
        await missing.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        Assert.Equal((16, 6), await _world.BalanceAsync(warehouse.Id, product.Id));

        // The shipment response already carries the order's new version.
        using var cancelled = await World.CancelSalesOrderAsync(sales, order.Id(), shipped.ETag());
        var final = await cancelled.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", final.Str("status"));
        Assert.Equal((10, 4, 6, 0), Quantities(final));
        Assert.Equal((16, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
        var ledger = await sales.GetJsonAsync($"/api/inventory/reservations?warehouseId={warehouse.Id}&productId={product.Id}");
        Assert.Equal(
            [("ReleaseOnCancel", -6, 0), ("ReleaseOnShipment", -4, 6), ("Reserve", 10, 10)],
            ledger["items"]!.AsArray().Select(e => (e!.Str("reason"), e!.Number("quantityDelta"), e!.Number("reservedAfter"))));
        var read = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}/shipments/{shipment.Id()}");
        Assert.Equal((shipment.Str("number"), 4), (read.Str("number"), read["lines"]![0]!.Number("quantity")));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Shipping_a_cancelled_order_is_invalid_state_and_moves_nothing()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 20);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, etag) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));

        using var cancelled = await World.CancelSalesOrderAsync(sales, order.Id(), etag);
        Assert.Equal((10, 0, 10, 0), Quantities(await cancelled.JsonAsync(HttpStatusCode.OK)));
        Assert.Equal((20, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        using var ship = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (order["lines"]![0]!.Id(), 4));
        await ship.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        using var cancelAgain = await World.CancelSalesOrderAsync(sales, order.Id(), cancelled.ETag());
        await cancelAgain.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");

        Assert.Equal((20, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
        Assert.Equal(0, await ShipmentCountAsync(order.Id()));
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id, "ReleaseOnCancel"));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Shipping_more_than_open_or_a_foreign_line_is_refused_as_a_whole()
    {
        var warehouse = await _world.WarehouseAsync();
        var (first, second) = (await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(warehouse, [(first, 50), (second, 50)]);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (first.Id, 10), (second.Id, 5));
        var (other, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (first.Id, 10));
        var (lineA, lineB) = (order["lines"]![0]!.Id(), order["lines"]![1]!.Id());

        // Plenty of stock on the shelf does not help: the ceiling is the order's open quantity.
        using var tooMany = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (lineA, 4), (lineB, 6));
        var excess = await tooMany.ProblemAsync(HttpStatusCode.Conflict, "exceeds_remaining");
        Assert.Equal(["lines[1].quantity"], excess["errors"]!.AsArray().Select(e => e!.Str("field")));

        using var foreign = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (lineA, 4), (other["lines"]![0]!.Id(), 1));
        var mismatch = await foreign.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");
        Assert.Equal(["lines[1].orderLineId"], mismatch["errors"]!.AsArray().Select(e => e!.Str("field")));
        using var twice = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (lineA, 1), (lineA, 1));
        await twice.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");
        using var zero = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (lineA, 0));
        await zero.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal((50, 20), await _world.BalanceAsync(warehouse.Id, first.Id));
        Assert.Equal((50, 5), await _world.BalanceAsync(warehouse.Id, second.Id));
        Assert.Equal(0, await ShipmentCountAsync(order.Id()) + await ShipmentCountAsync(other.Id()));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, first.Id, "Shipment"));
        var stored = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal("Confirmed", stored.Str("status"));
        Assert.Equal((10, 0, 0, 10), Quantities(stored, 0));
        Assert.Equal((5, 0, 0, 5), Quantities(stored, 1));
        await _world.AssertReconciledAsync(warehouse.Id, first.Id);
        await _world.AssertReconciledAsync(warehouse.Id, second.Id);
    }

    [Fact]
    public async Task Partial_shipments_that_add_up_to_the_order_end_in_shipped()
    {
        var warehouse = await _world.WarehouseAsync();
        var (first, second) = (await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(warehouse, [(first, 25), (second, 8)]);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (first.Id, 10), (second.Id, 8));
        var (lineA, lineB) = (order["lines"]![0]!.Id(), order["lines"]![1]!.Id());
        Assert.Equal((25, 10), await _world.BalanceAsync(warehouse.Id, first.Id));

        var steps = new (string Status, (Guid, int)[] Lines, (int, int) First, (int, int) Second)[]
        {
            ("PartiallyShipped", [(lineA, 3)], (22, 7), (8, 8)),
            ("PartiallyShipped", [(lineB, 8), (lineA, 3)], (19, 4), (0, 0)),
            ("Shipped", [(lineA, 4)], (15, 0), (0, 0)),
        };
        foreach (var (status, lines, firstBalance, secondBalance) in steps)
        {
            using var response = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), lines);
            Assert.Equal(status, (await response.JsonAsync(HttpStatusCode.Created)).Str("salesOrderStatus"));
            Assert.Equal(firstBalance, await _world.BalanceAsync(warehouse.Id, first.Id));
            Assert.Equal(secondBalance, await _world.BalanceAsync(warehouse.Id, second.Id));
        }

        // On hand fell by exactly the ordered quantity and nothing is left reserved.
        var final = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal("Shipped", final.Str("status"));
        Assert.NotNull(final["closedAt"]);
        Assert.Equal((10, 10, 0, 0), Quantities(final, 0));
        Assert.Equal((8, 8, 0, 0), Quantities(final, 1));
        Assert.Equal([3, 4, 11], final["shipments"]!.AsArray().Select(s => s!.Number("totalQuantity")).Order());
        Assert.Equal(3, await _world.MovementCountAsync(warehouse.Id, first.Id, "Shipment"));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, second.Id, "Shipment"));
        Assert.Equal(3, await _world.ReservationCountAsync(warehouse.Id, first.Id, "ReleaseOnShipment"));

        // A shipped order is final: nothing more ships and there is nothing to cancel.
        using var extra = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (lineA, 1));
        await extra.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        using var cancel = await World.CancelSalesOrderAsync(sales, order.Id(), await World.ETagAsync(sales, $"/api/sales/orders/{order.Id()}"));
        await cancel.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal((15, 0), await _world.BalanceAsync(warehouse.Id, first.Id));
        await _world.AssertReconciledAsync(warehouse.Id, first.Id);
        await _world.AssertReconciledAsync(warehouse.Id, second.Id);
    }

    [Fact]
    public async Task Ship_racing_with_cancel_on_ten_orders_never_loses_or_doubles_a_unit()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 100);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (shipFirst, cancelFirst, shippedTotal) = (0, 0, 0);

        for (var round = 0; round < 10; round++)
        {
            var (order, etag) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
            var line = order["lines"]![0]!.Id();
            Assert.Equal((100 - shippedTotal, 10), await _world.BalanceAsync(warehouse.Id, product.Id));

            var replies = await Race.RunAsync(
                () => World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (line, 4)),
                () => World.CancelSalesOrderAsync(sales, order.Id(), etag));
            var (ship, cancel) = (replies[0], replies[1]);

            if (ship.Status == HttpStatusCode.Created)
            {
                // Shipment first: the cancel carried the pre-shipment version. With the current
                // version it releases only the six units that did not ship.
                cancel.AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
                Assert.Equal((100 - shippedTotal - 4, 6), await _world.BalanceAsync(warehouse.Id, product.Id));
                using var again = await World.CancelSalesOrderAsync(sales, order.Id(), ship.ETag);
                Assert.Equal((10, 4, 6, 0), Quantities(await again.JsonAsync(HttpStatusCode.OK)));
                shipFirst++;
                shippedTotal += 4;
            }
            else
            {
                // Cancel first: the whole reservation is released and the shipment finds a closed order.
                Assert.Equal(HttpStatusCode.OK, cancel.Status);
                Assert.Equal((10, 0, 10, 0), Quantities(cancel.Json));
                ship.AssertProblem(HttpStatusCode.Conflict, "invalid_state");
                cancelFirst++;
            }

            var final = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
            var (ordered, shipped, cancelled, open) = Quantities(final);
            Assert.Equal("Cancelled", final.Str("status"));
            Assert.Equal(ordered, shipped + cancelled);
            Assert.Equal(0, open);
            Assert.Equal(ship.Status == HttpStatusCode.Created ? 4 : 0, shipped);
            Assert.Equal(ship.Status == HttpStatusCode.Created ? 1 : 0, await ShipmentCountAsync(order.Id()));

            // This order holds no reservation any more and on hand fell by exactly what it shipped.
            Assert.Equal((100 - shippedTotal, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        }

        Assert.Equal(10, shipFirst + cancelFirst);
        Assert.Equal(shipFirst, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
        Assert.Equal(10, await _world.ReservationCountAsync(warehouse.Id, product.Id, "Reserve"));
        Assert.Equal(shipFirst, await _world.ReservationCountAsync(warehouse.Id, product.Id, "ReleaseOnShipment"));
        Assert.Equal(10, await _world.ReservationCountAsync(warehouse.Id, product.Id, "ReleaseOnCancel"));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Shipment_first_a_cancel_waiting_behind_it_releases_nothing_until_it_is_sent_against_the_new_version()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 20);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, etag) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var line = order["lines"]![0]!.Id();

        Reply ship, cancel;
        using (var pause = new Pause(fixture.Failpoints, "stock.after-posting"))
        {
            // The shipment is parked after it changed the balance, with the order row locked and nothing committed.
            var shipping = Reply.ReadAsync(World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (line, 4)));
            await pause.Reached;
            Assert.Equal((20, 10), await _world.BalanceAsync(warehouse.Id, product.Id));

            var cancelling = Reply.ReadAsync(World.CancelSalesOrderAsync(sales, order.Id(), etag));
            await fixture.WaitForBlockedRequestAsync();
            Assert.False(cancelling.IsCompleted);

            pause.Release();
            (ship, cancel) = (await shipping, await cancelling);
        }

        Assert.Equal(HttpStatusCode.Created, ship.Status);
        cancel.AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal((16, 6), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, product.Id, "ReleaseOnCancel"));

        using var again = await World.CancelSalesOrderAsync(sales, order.Id(), ship.ETag);
        Assert.Equal((10, 4, 6, 0), Quantities(await again.JsonAsync(HttpStatusCode.OK)));
        Assert.Equal((16, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Cancel_first_a_shipment_waiting_behind_it_is_refused_and_moves_nothing()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 20);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, etag) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var line = order["lines"]![0]!.Id();

        Reply ship, cancel;
        using (var pause = new Pause(fixture.Failpoints, "stock.after-posting"))
        {
            // The cancel is parked after it released the reservation, before it commits.
            var cancelling = Reply.ReadAsync(World.CancelSalesOrderAsync(sales, order.Id(), etag));
            await pause.Reached;
            Assert.Equal((20, 10), await _world.BalanceAsync(warehouse.Id, product.Id));

            var shipping = Reply.ReadAsync(World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (line, 4)));
            await fixture.WaitForBlockedRequestAsync();
            Assert.False(shipping.IsCompleted);

            pause.Release();
            (cancel, ship) = (await cancelling, await shipping);
        }

        Assert.Equal(HttpStatusCode.OK, cancel.Status);
        Assert.Equal((10, 0, 10, 0), Quantities(cancel.Json));
        ship.AssertProblem(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal((20, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
        Assert.Equal(0, await ShipmentCountAsync(order.Id()));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Ten_parallel_shipments_of_the_full_open_quantity_ship_it_once()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 100);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var line = order["lines"]![0]!.Id();
        var clients = _world.Clones(operatorClient, 10);

        // Ten different dispatch references: the business-reference rule cannot be what stops the other nine.
        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => World.ShipAsync(client, order.Id(), _world.Reference("SEVK"), (line, 10)))));

        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        Assert.Equal("Shipped", winner.Json.Str("salesOrderStatus"));
        Assert.All(replies.Where(r => r != winner), r => r.AssertProblem(HttpStatusCode.Conflict, "invalid_state"));

        Assert.Equal((90, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id, "ReleaseOnShipment"));
        Assert.Equal(1, await ShipmentCountAsync(order.Id()));
        Assert.Equal((10, 10, 0, 0), Quantities(await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}")));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Parallel_partial_shipments_stop_at_the_open_quantity()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 100);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var line = order["lines"]![0]!.Id();
        var clients = _world.Clones(operatorClient, 8);

        // 8 × 3 = 24 against an open quantity of 10: three fit (9), the rest would exceed the one unit left.
        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => World.ShipAsync(client, order.Id(), _world.Reference("SEVK"), (line, 3)))));

        Assert.Equal(3, replies.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(replies.Where(r => r.Status != HttpStatusCode.Created), r => r.AssertProblem(HttpStatusCode.Conflict, "exceeds_remaining"));
        Assert.Equal((91, 1), await _world.BalanceAsync(warehouse.Id, product.Id));
        var stored = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal("PartiallyShipped", stored.Str("status"));
        Assert.Equal((10, 9, 0, 1), Quantities(stored));
        Assert.Equal(3, await ShipmentCountAsync(order.Id()));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task A_negative_adjustment_racing_with_confirm_cannot_take_what_the_confirmation_reserves()
    {
        var warehouse = await _world.WarehouseAsync();
        var (sales, _, customer) = await ActorsAsync(warehouse);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var (confirmWon, adjustmentWon) = (0, 0);

        for (var round = 0; round < 8; round++)
        {
            // 10 on hand. Reserving 8 and removing 5 cannot both hold: exactly one of them must lose.
            var product = await _world.ProductAsync();
            await _world.StockAsync(warehouse, product, 10);
            var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 8));

            var replies = await Race.RunAsync(
                () => sales.PostAsync($"/api/sales/orders/{order.Id()}/confirm", null, ApiClient.NewKey(), etag),
                () => World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Damage", (product.Id, -5)));
            var (confirm, adjust) = (replies[0], replies[1]);

            Assert.Single(replies, r => r.IsSuccess);
            Assert.Single(replies, r => !r.IsSuccess).AssertProblem(HttpStatusCode.Conflict, "insufficient_available");
            var balance = await _world.BalanceAsync(warehouse.Id, product.Id);
            if (confirm.IsSuccess)
            {
                Assert.Equal((10, 8), balance);
                Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id, "Adjustment"));
                confirmWon++;
            }
            else
            {
                Assert.Equal(HttpStatusCode.Created, adjust.Status);
                Assert.Equal((5, 0), balance);
                Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, product.Id));
                Assert.Equal("Draft", (await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}")).Str("status"));
                adjustmentWon++;
            }

            Assert.True(balance.Reserved >= 0 && balance.OnHand >= balance.Reserved);
            await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        }

        Assert.Equal(8, confirmWon + adjustmentWon);
    }

    [Theory]
    [InlineData(-2, true, 0)]
    [InlineData(-3, false, 2)]
    public async Task A_negative_adjustment_racing_with_a_shipment_sees_only_the_free_quantity(int delta, bool adjustmentFits, int onHandLeft)
    {
        var warehouse = await _world.WarehouseAsync();
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);

        for (var round = 0; round < 5; round++)
        {
            // 10 on hand, 8 reserved, 2 free. Shipping the 8 lowers on hand and reserved together, so the
            // free quantity is 2 before and after it: the adjustment's fate must not depend on who is first.
            var product = await _world.ProductAsync();
            await _world.StockAsync(warehouse, product, 10);
            var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 8));
            var line = order["lines"]![0]!.Id();

            var replies = await Race.RunAsync(
                () => World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (line, 8)),
                () => World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Loss", (product.Id, delta)));
            var (ship, adjust) = (replies[0], replies[1]);

            Assert.Equal(HttpStatusCode.Created, ship.Status);
            if (adjustmentFits)
            {
                Assert.Equal(HttpStatusCode.Created, adjust.Status);
            }
            else
            {
                adjust.AssertProblem(HttpStatusCode.Conflict, "insufficient_available");
            }

            Assert.Equal((onHandLeft, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
            Assert.Equal(adjustmentFits ? 1 : 0, await _world.MovementCountAsync(warehouse.Id, product.Id, "Adjustment"));
            Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
            await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        }
    }

    [Fact]
    public async Task Cancelling_a_draft_has_no_stock_or_ledger_effect()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 10);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 4));

        using var withoutReason = await sales.PostAsync($"/api/sales/orders/{order.Id()}/cancel", new { reason = "  " }, ApiClient.NewKey(), etag);
        await withoutReason.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        using var cancelled = await World.CancelSalesOrderAsync(sales, order.Id(), etag);
        var final = await cancelled.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", final.Str("status"));
        Assert.Equal((4, 0, 0, 4), Quantities(final));
        Assert.Equal("Müşteri siparişten vazgeçti", final.Str("cancelReason"));

        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));

        // A cancelled draft cannot be confirmed back to life.
        using var confirm = await sales.PostAsync($"/api/sales/orders/{order.Id()}/confirm", null, ApiClient.NewKey(), cancelled.ETag());
        await confirm.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");

        // It was never released to the warehouse, so for the operator it does not exist (docs/02 §2.4:
        // the warehouse sees orders only from confirmation onwards).
        using var ship = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (order["lines"]![0]!.Id(), 1));
        await ship.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var read = await operatorClient.GetAsync($"/api/sales/orders/{order.Id()}");
        await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Order_summary_carries_the_legal_notice_and_nothing_beyond_the_order()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync(price: 40m);
        await _world.StockAsync(warehouse, product, 10);
        var (sales, operatorClient, customer) = await ActorsAsync(warehouse);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 3));
        using var shipped = await World.ShipAsync(operatorClient, order.Id(), _world.Reference("SEVK"), (order["lines"]![0]!.Id(), 1));
        await shipped.JsonAsync(HttpStatusCode.Created);

        var summary = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}/summary");

        // The notice must say that this is neither an invoice nor a dispatch note.
        var notice = summary.Str("legalNotice");
        Assert.Contains("fatura", notice, StringComparison.Ordinal);
        Assert.Contains("irsaliye değildir", notice, StringComparison.Ordinal);
        Assert.Equal(["generatedAt", "legalNotice", "order", "warehouseCode", "warehouseName"], summary.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal((warehouse.Code, warehouse.Name), (summary.Str("warehouseCode"), summary.Str("warehouseName")));

        // The embedded order is exactly what the order endpoint returns to this caller.
        var direct = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.True(JsonNode.DeepEquals(direct, summary["order"]), "Summary order differs from the order endpoint.");
        Assert.Equal(120m, summary["order"]!["total"]!.GetValue<decimal>());
        Assert.Equal([1], summary["order"]!["shipments"]!.AsArray().Select(s => s!.Number("totalQuantity")));

        // The warehouse gets the same summary without commercial fields; an outsider gets nothing.
        var forWarehouse = await operatorClient.GetJsonAsync($"/api/sales/orders/{order.Id()}/summary");
        Assert.Equal(notice, forWarehouse.Str("legalNotice"));
        Assert.Equal("Fulfilment", forWarehouse["order"]!.Str("view"));
        Assert.Null(forWarehouse["order"]!["total"]);
        Assert.Null(forWarehouse["order"]!["customerTaxNumber"]);
        Assert.Null(forWarehouse["order"]!["lines"]![0]!["unitPrice"]);
        var outsider = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        using var denied = await outsider.GetAsync($"/api/sales/orders/{order.Id()}/summary");
        await denied.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var withQuery = await sales.GetAsync($"/api/sales/orders/{order.Id()}/summary?format=pdf");
        await withQuery.ProblemAsync(HttpStatusCode.BadRequest, "unknown_parameter");
    }
}
