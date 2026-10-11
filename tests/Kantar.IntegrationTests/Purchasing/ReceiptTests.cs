using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Purchasing;

/// <summary>docs/02 §6 and §8: goods receipts against the open quantity of an approved purchase order.</summary>
public sealed class ReceiptTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private Task<int> ReceiptCountAsync(Guid orderId) =>
        _world.CountAsync("SELECT COUNT(*) FROM purch.GoodsReceipts WHERE PurchaseOrderId = @o;", new SqlParameter("@o", orderId));

    private async Task<(ApiClient Buyer, ApiClient Operator, Supplier Supplier)> ActorsAsync(Warehouse warehouse)
    {
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)], poLimit: 1_000_000m);
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        return (buyer, operatorClient, await _world.SupplierAsync());
    }

    private static (int Ordered, int Received, int Cancelled, int Open) Quantities(JsonNode order, int line = 0)
    {
        var node = order["lines"]![line]!;
        return (node.Number("orderedQty"), node.Number("receivedQty"), node.Number("cancelledQty"), node.Number("openQty"));
    }

    [Fact]
    public async Task Over_receipt_is_refused_and_writes_nothing()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
        var line = po["lines"]![0]!.Id();

        using var tooMany = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 11));
        var problem = await tooMany.ProblemAsync(HttpStatusCode.Conflict, "exceeds_remaining");
        Assert.Equal("lines[0].quantity", problem["errors"]![0]!.Str("field"));
        Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await ReceiptCountAsync(po.Id()));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id));
        Assert.Equal("Approved", (await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}")).Str("status"));

        using var partial = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 6));
        var receipt = await partial.JsonAsync(HttpStatusCode.Created);
        Assert.Equal($"/api/purchasing/orders/{po.Id()}/receipts/{receipt.Id()}", partial.Headers.Location?.ToString());
        Assert.Equal("PartiallyReceived", receipt.Str("purchaseOrderStatus"));

        // Four are left: five is one too many, four closes the order.
        using var oneTooMany = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 5));
        await oneTooMany.ProblemAsync(HttpStatusCode.Conflict, "exceeds_remaining");
        Assert.Equal((6, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        using var rest = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 4));
        Assert.Equal("Received", (await rest.JsonAsync(HttpStatusCode.Created)).Str("purchaseOrderStatus"));
        using var afterClose = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 1));
        await afterClose.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");

        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(2, await _world.MovementCountAsync(warehouse.Id, product.Id, "Receipt"));
        Assert.Equal(2, await ReceiptCountAsync(po.Id()));
        var final = await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}");
        Assert.Equal((10, 10, 0, 0), Quantities(final));
        Assert.Equal([4, 6], final["receipts"]!.AsArray().Select(r => r!.Number("totalQuantity")).Order());
        var read = await operatorClient.GetJsonAsync($"/api/purchasing/orders/{po.Id()}/receipts/{receipt.Id()}");
        Assert.Equal((receipt.Str("number"), 6), (read.Str("number"), read["lines"]![0]!.Number("quantity")));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task A_line_of_another_order_or_a_repeated_line_is_a_line_mismatch()
    {
        var warehouse = await _world.WarehouseAsync();
        var (first, second) = (await _world.ProductAsync(), await _world.ProductAsync());
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (first.Id, 10), (second.Id, 10));
        var (other, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (first.Id, 10));
        var ownLine = po["lines"]![0]!.Id();
        var foreignLine = other["lines"]![0]!.Id();

        using var foreign = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (foreignLine, 1));
        var problem = await foreign.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");
        Assert.Equal("lines[0].orderLineId", problem["errors"]![0]!.Str("field"));
        using var unknown = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (Guid.NewGuid(), 1));
        await unknown.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");
        using var twice = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (ownLine, 1), (ownLine, 2));
        await twice.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");

        // The product id of a line is not a line id either.
        using var productAsLine = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (first.Id, 1));
        await productAsLine.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");

        Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, first.Id));
        Assert.Equal(0, await ReceiptCountAsync(po.Id()) + await ReceiptCountAsync(other.Id()));
        Assert.Equal((10, 0, 0, 10), Quantities(await buyer.GetJsonAsync($"/api/purchasing/orders/{other.Id()}")));
        await _world.AssertReconciledAsync(warehouse.Id, first.Id);
    }

    [Fact]
    public async Task One_bad_line_rejects_the_whole_receipt_including_the_good_line()
    {
        var warehouse = await _world.WarehouseAsync();
        var (good, bad) = (await _world.ProductAsync(), await _world.ProductAsync());
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (good.Id, 10), (bad.Id, 5));
        var (goodLine, badLine) = (po["lines"]![0]!.Id(), po["lines"]![1]!.Id());

        using var excess = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (goodLine, 4), (badLine, 6));
        var problem = await excess.ProblemAsync(HttpStatusCode.Conflict, "exceeds_remaining");
        Assert.Equal(["lines[1].quantity"], problem["errors"]!.AsArray().Select(e => e!.Str("field")));
        using var mismatch = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (goodLine, 4), (Guid.NewGuid(), 1));
        await mismatch.ProblemAsync(HttpStatusCode.UnprocessableEntity, "line_mismatch");

        foreach (var product in new[] { good, bad })
        {
            Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
            Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id));
            await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        }

        Assert.Equal(0, await ReceiptCountAsync(po.Id()));
        var after = await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}");
        Assert.Equal("Approved", after.Str("status"));
        Assert.Equal((10, 0, 0, 10), Quantities(after, 0));
        Assert.Equal((5, 0, 0, 5), Quantities(after, 1));
    }

    [Fact]
    public async Task A_failure_on_the_second_line_leaves_no_stock_no_movement_and_no_receipt_for_the_first()
    {
        var warehouse = await _world.WarehouseAsync();
        var (first, second) = (await _world.ProductAsync(), await _world.ProductAsync());
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (first.Id, 10), (second.Id, 5));
        var body = World.ReceiptBody(_world.Reference("IRS"), (po["lines"]![0]!.Id(), 10), (po["lines"]![1]!.Id(), 5));
        var key = ApiClient.NewKey();

        // Let the first balance change through and fail after the second one.
        var hits = 0;
        var injected = fixture.Failpoints.On("stock.after-posting", _ =>
            Interlocked.Increment(ref hits) == 2 ? throw new InjectedFailureException() : Task.CompletedTask);
        try
        {
            using var failed = await operatorClient.PostAsync($"/api/purchasing/orders/{po.Id()}/receipts", body, key);
            await failed.ProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        }
        finally
        {
            injected.Dispose();
        }

        Assert.Equal(2, hits);
        foreach (var product in new[] { first, second })
        {
            Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
            Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id));
        }

        Assert.Equal(0, await ReceiptCountAsync(po.Id()));
        Assert.Equal(0, await _world.IdempotencyRecordCountAsync(key));
        Assert.Equal("Approved", (await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}")).Str("status"));

        using var retried = await operatorClient.PostAsync($"/api/purchasing/orders/{po.Id()}/receipts", body, key);
        Assert.Equal("Received", (await retried.JsonAsync(HttpStatusCode.Created)).Str("purchaseOrderStatus"));
        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, first.Id));
        Assert.Equal((5, 0), await _world.BalanceAsync(warehouse.Id, second.Id));
        await _world.AssertReconciledAsync(warehouse.Id, first.Id);
        await _world.AssertReconciledAsync(warehouse.Id, second.Id);
    }

    [Fact]
    public async Task Receiving_needs_an_operator_who_operates_the_order_s_warehouse()
    {
        var warehouse = await _world.WarehouseAsync();
        var other = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, _, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
        var line = po["lines"]![0]!.Id();

        // docs/02 §2.4: an operator reads a purchase order only with Operate, so below that the order
        // is out of scope (404). A caller who does see the order but has no receiving right gets 403.
        var denied = new (string Who, string[] Roles, (Guid, WarehouseLevel)[] Grants, HttpStatusCode Status)[]
        {
            ("operator without a grant", [Roles.WarehouseOperator], [], HttpStatusCode.NotFound),
            ("operator of another warehouse", [Roles.WarehouseOperator], [(other.Id, WarehouseLevel.Supervise)], HttpStatusCode.NotFound),
            ("operator with View only", [Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.View)], HttpStatusCode.NotFound),
            ("purchasing agent in scope", [Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.Supervise)], HttpStatusCode.Forbidden),
            ("sales manager", [Roles.SalesManager], [(warehouse.Id, WarehouseLevel.Supervise)], HttpStatusCode.NotFound),
            ("system admin", [Roles.SystemAdmin], [], HttpStatusCode.NotFound),
        };

        foreach (var (who, roles, grants, status) in denied)
        {
            var client = await _world.LoginAsAsync(roles, grants);
            var reply = await Reply.ReadAsync(World.ReceiveAsync(client, po.Id(), _world.Reference("IRS"), (line, 1)));
            Assert.True(reply.Status == status, $"{who}: expected {(int)status}, got {(int)reply.Status}");
            Assert.Equal(status == HttpStatusCode.Forbidden ? "forbidden" : "not_found", reply.Problem);
        }

        // The buyer who owns the order cannot receive it either: receiving is the warehouse's act.
        using var byBuyer = await World.ReceiveAsync(buyer, po.Id(), _world.Reference("IRS"), (line, 1));
        await byBuyer.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await ReceiptCountAsync(po.Id()));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task A_draft_order_is_invisible_to_the_warehouse_and_cannot_be_received()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (draft, _) = await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));

        using var read = await operatorClient.GetAsync($"/api/purchasing/orders/{draft.Id()}");
        await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var receive = await World.ReceiveAsync(operatorClient, draft.Id(), _world.Reference("IRS"), (draft["lines"]![0]!.Id(), 1));
        await receive.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        var listed = await operatorClient.GetJsonAsync($"/api/purchasing/orders?warehouseId={warehouse.Id}");
        Assert.DoesNotContain(draft.Id(), listed["items"]!.AsArray().Select(o => o!.Id()));

        Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await ReceiptCountAsync(draft.Id()));
    }

    [Fact]
    public async Task Twenty_parallel_receipts_of_ten_units_against_an_order_of_one_hundred_receive_exactly_one_hundred()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 100));
        var line = po["lines"]![0]!.Id();
        var clients = _world.Clones(operatorClient, 20);

        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => World.ReceiveAsync(client, po.Id(), _world.Reference("IRS"), (line, 10)))));

        Assert.Equal(10, replies.Count(r => r.Status == HttpStatusCode.Created));
        var rejected = replies.Where(r => r.Status != HttpStatusCode.Created).ToArray();
        Assert.Equal(10, rejected.Length);
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Contains(r.Problem, new[] { "exceeds_remaining", "invalid_state" });
        });

        Assert.Equal((100, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(10, await _world.MovementCountAsync(warehouse.Id, product.Id, "Receipt"));
        Assert.Equal(10, await _world.MovementCountAsync(warehouse.Id, product.Id));
        Assert.Equal(10, await ReceiptCountAsync(po.Id()));
        var final = await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}");
        Assert.Equal("Received", final.Str("status"));
        Assert.Equal((100, 100, 0, 0), Quantities(final));

        // Every successful reply reports a distinct running state: 10, 20, … 100 units on hand.
        var movements = await buyer.GetJsonAsync($"/api/inventory/movements?warehouseId={warehouse.Id}&productId={product.Id}&limit=100");
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i * 10).Reverse(), movements["items"]!.AsArray().Select(m => m!.Number("onHandAfter")));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Parallel_receipts_that_do_not_divide_the_order_stop_at_the_remaining_quantity()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 100));
        var line = po["lines"]![0]!.Id();
        var clients = _world.Clones(operatorClient, 12);

        // 12 × 15 = 180 against 100: six fit (90), the other six would exceed the 10 that remain.
        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => World.ReceiveAsync(client, po.Id(), _world.Reference("IRS"), (line, 15)))));

        Assert.Equal(6, replies.Count(r => r.Status == HttpStatusCode.Created));
        Assert.All(replies.Where(r => r.Status != HttpStatusCode.Created), r => r.AssertProblem(HttpStatusCode.Conflict, "exceeds_remaining"));
        Assert.Equal((90, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        var final = await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}");
        Assert.Equal("PartiallyReceived", final.Str("status"));
        Assert.Equal((100, 90, 0, 10), Quantities(final));
        Assert.Equal(6, await ReceiptCountAsync(po.Id()));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Cancelling_after_a_receipt_closes_only_the_remainder_and_keeps_the_received_stock()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, approvedETag) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
        var line = po["lines"]![0]!.Id();
        using var received = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 6));
        await received.JsonAsync(HttpStatusCode.Created);

        // The ETag from before the receipt is stale now; the buyer has to look at the order again.
        using var stale = await World.CancelPurchaseOrderAsync(buyer, po.Id(), approvedETag);
        await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        using var missing = await World.CancelPurchaseOrderAsync(buyer, po.Id(), null);
        await missing.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");

        using var cancelled = await World.CancelPurchaseOrderAsync(buyer, po.Id(), await World.ETagAsync(buyer, $"/api/purchasing/orders/{po.Id()}"));
        var final = await cancelled.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", final.Str("status"));
        Assert.Equal((10, 6, 4, 0), Quantities(final));
        Assert.Equal((6, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));

        using var afterCancel = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 1));
        await afterCancel.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal((6, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Receiving_a_cancelled_order_is_invalid_state_and_adds_no_stock()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, etag) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
        using var cancelled = await World.CancelPurchaseOrderAsync(buyer, po.Id(), etag);
        Assert.Equal((10, 0, 10, 0), Quantities(await cancelled.JsonAsync(HttpStatusCode.OK)));

        using var receive = await World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (po["lines"]![0]!.Id(), 4));
        await receive.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");

        Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await _world.MovementCountAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await ReceiptCountAsync(po.Id()));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Receive_racing_with_cancel_ends_in_one_of_the_two_serial_outcomes()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (receiveFirst, cancelFirst, receivedTotal) = (0, 0, 0);

        for (var round = 0; round < 8; round++)
        {
            var (po, etag) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
            var line = po["lines"]![0]!.Id();

            var replies = await Race.RunAsync(
                () => World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 4)),
                () => World.CancelPurchaseOrderAsync(buyer, po.Id(), etag));
            var (receive, cancel) = (replies[0], replies[1]);

            if (receive.Status == HttpStatusCode.Created)
            {
                // Receipt first: the cancel was sent with the pre-receipt version and is told so.
                // Cancelling again with the current version closes only what is still open.
                cancel.AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
                using var again = await World.CancelPurchaseOrderAsync(buyer, po.Id(), await World.ETagAsync(buyer, $"/api/purchasing/orders/{po.Id()}"));
                Assert.Equal((10, 4, 6, 0), Quantities(await again.JsonAsync(HttpStatusCode.OK)));
                receiveFirst++;
                receivedTotal += 4;
            }
            else
            {
                // Cancel first: the receipt finds a closed order.
                Assert.Equal(HttpStatusCode.OK, cancel.Status);
                receive.AssertProblem(HttpStatusCode.Conflict, "invalid_state");
                cancelFirst++;
            }

            var final = await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}");
            var (ordered, received, cancelledQty, open) = Quantities(final);
            Assert.Equal("Cancelled", final.Str("status"));
            Assert.Equal(ordered, received + cancelledQty);
            Assert.Equal(0, open);
            Assert.Equal(receive.Status == HttpStatusCode.Created ? 1 : 0, await ReceiptCountAsync(po.Id()));
            Assert.Equal((receivedTotal, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        }

        Assert.Equal(8, receiveFirst + cancelFirst);
        Assert.Equal(receiveFirst, await _world.MovementCountAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Receipt_first_a_cancel_waiting_behind_it_closes_nothing_until_it_is_sent_against_the_new_version()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, operatorClient, supplier) = await ActorsAsync(warehouse);
        var (po, etag) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
        var line = po["lines"]![0]!.Id();

        Reply receive, cancel;
        using (var pause = new Pause(fixture.Failpoints, "stock.after-posting"))
        {
            // The receipt is parked after it raised the balance, with the order row locked and nothing committed.
            var receiving = Reply.ReadAsync(World.ReceiveAsync(operatorClient, po.Id(), _world.Reference("IRS"), (line, 4)));
            await pause.Reached;
            Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

            var cancelling = Reply.ReadAsync(World.CancelPurchaseOrderAsync(buyer, po.Id(), etag));
            await fixture.WaitForBlockedRequestAsync();
            Assert.False(cancelling.IsCompleted);

            pause.Release();
            (receive, cancel) = (await receiving, await cancelling);
        }

        Assert.Equal(HttpStatusCode.Created, receive.Status);
        cancel.AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal((10, 4, 0, 6), Quantities(await buyer.GetJsonAsync($"/api/purchasing/orders/{po.Id()}")));

        using var again = await World.CancelPurchaseOrderAsync(buyer, po.Id(), receive.ETag);
        Assert.Equal((10, 4, 6, 0), Quantities(await again.JsonAsync(HttpStatusCode.OK)));
        Assert.Equal((4, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Draft_update_needs_the_current_etag()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, _, supplier) = await ActorsAsync(warehouse);
        var (draft, etag) = await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
        object Body(int quantity) => new { supplierId = supplier.Id, warehouseId = warehouse.Id, lines = new[] { new { productId = product.Id, quantity, unitCost = 12.50m } } };
        var url = $"/api/purchasing/orders/{draft.Id()}";

        using var withoutIfMatch = await buyer.PutAsync(url, Body(20));
        await withoutIfMatch.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");

        using var updated = await buyer.PutAsync(url, Body(20), etag);
        var second = await updated.JsonAsync(HttpStatusCode.OK);
        Assert.Equal((20, 0, 0, 20), Quantities(second));
        Assert.Equal(250.00m, second["total"]!.GetValue<decimal>());
        Assert.NotEqual(etag, updated.ETag());

        // The first writer's change is kept: a second write with the version it replaced is refused.
        using var stale = await buyer.PutAsync(url, Body(30), etag);
        await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        foreach (var garbage in new[] { "W/" + updated.ETag(), "\"not-an-etag\"", "*" })
        {
            using var rejected = await buyer.PutAsync(url, Body(30), garbage);
            await rejected.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        }

        using var current = await buyer.GetAsync(url);
        Assert.Equal(updated.ETag(), current.ETag());
        Assert.Equal((20, 0, 0, 20), Quantities(await current.JsonAsync(HttpStatusCode.OK)));
    }

    [Fact]
    public async Task Two_concurrent_draft_updates_with_the_same_etag_let_exactly_one_through()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var (buyer, _, supplier) = await ActorsAsync(warehouse);
        var clients = _world.Clones(buyer, 2);

        for (var round = 0; round < 5; round++)
        {
            var (draft, etag) = await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 10));
            var url = $"/api/purchasing/orders/{draft.Id()}";
            var quantities = new[] { 21, 34 };

            var replies = await Race.RunAsync(quantities.Select((quantity, i) => (Func<Task<HttpResponseMessage>>)(() => clients[i].PutAsync(
                url,
                new { supplierId = supplier.Id, warehouseId = warehouse.Id, lines = new[] { new { productId = product.Id, quantity, unitCost = 10.00m } } },
                etag))));

            var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
            Assert.Single(replies, r => r.Status == HttpStatusCode.PreconditionFailed).AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
            var stored = await buyer.GetJsonAsync(url);
            Assert.Equal(Quantities(winner.Json), Quantities(stored));
            Assert.Equal(quantities[Array.IndexOf(replies, winner)], Quantities(stored).Ordered);
            Assert.Single(stored["lines"]!.AsArray());
        }
    }
}
