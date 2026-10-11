using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Idempotency;

/// <summary>
/// docs/05 §1.2 and docs/02 §4.4: the same key and request replay the stored bytes; a key never
/// replaces authorization; and the business reference stops a second stock effect when the key cannot.
/// </summary>
public sealed class IdempotencyTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private sealed record Dock(
        Warehouse Warehouse, Product Product, Supplier Supplier, ApiClient Buyer, User OperatorUser, ApiClient Operator, JsonNode Order, Guid Line)
    {
        public string ReceiptUrl => $"/api/purchasing/orders/{Order.Id()}/receipts";
    }

    /// <summary>An approved purchase order of 100 units waiting to be received by one operator.</summary>
    private async Task<Dock> DockAsync()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var supplier = await _world.SupplierAsync();
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)], poLimit: 1_000_000m);
        var operatorUser = await _world.UserAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        var (order, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 100));
        return new Dock(warehouse, product, supplier, buyer, operatorUser, await _world.LoginAsync(operatorUser), order, order["lines"]![0]!.Id());
    }

    private Task<int> ReceiptCountAsync(Guid orderId) =>
        _world.CountAsync("SELECT COUNT(*) FROM purch.GoodsReceipts WHERE PurchaseOrderId = @o;", new SqlParameter("@o", orderId));

    private static void AssertSameResponse(Reply first, Reply replay)
    {
        Assert.Equal(first.Status, replay.Status);
        Assert.True(first.Body.AsSpan().SequenceEqual(replay.Body), "Replayed body bytes differ from the first response.");
        Assert.Equal(first.ContentType, replay.ContentType);
        Assert.Equal(first.Location, replay.Location);
        Assert.Equal(first.ETag, replay.ETag);
    }

    // ---- the key itself ------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_short_long_or_malformed_key_is_refused_before_anything_happens()
    {
        var dock = await DockAsync();
        var body = World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 10));
        string?[] keys =
        [
            null, "", "short", new string('k', 15), new string('k', 129), new string('k', 5_000),
            "has space in the key", "anahtar-ğüşiöç-0001", "semi;colon;0000000", "dots.are.not.allowed", "quote'or'1'='1'--0000",
        ];

        foreach (var key in keys)
        {
            var reply = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
            var problem = reply.AssertProblem(HttpStatusCode.BadRequest, "idempotency_key_required");
            Assert.Equal(32, problem.Str("correlationId").Length);
            if (key is { Length: > 0 })
            {
                Assert.Equal(0, await _world.IdempotencyRecordCountAsync(key));
            }
        }

        // Commands without a body need the key too.
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(dock.Warehouse, dock.Product, 5);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(dock.Warehouse.Id, WarehouseLevel.View)]);
        using var createWithoutKey = await sales.PostAsync("/api/sales/orders", new { customerId = customer.Id, warehouseId = dock.Warehouse.Id, lines = new[] { new { productId = dock.Product.Id, quantity = 1 } } });
        await createWithoutKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, dock.Warehouse.Id, (dock.Product.Id, 1));
        using var confirmWithoutKey = await sales.PostAsync($"/api/sales/orders/{order.Id()}/confirm", null, null, etag);
        await confirmWithoutKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");

        Assert.Equal(0, await ReceiptCountAsync(dock.Order.Id()));
        Assert.Equal((5, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));

        // The shortest and the longest legal keys work.
        foreach (var key in new[] { "A_b-" + new string('0', 12), new string('Z', 128) })
        {
            using var accepted = await dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 1)), key);
            await accepted.JsonAsync(HttpStatusCode.Created);
        }

        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    // ---- replay --------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_key_and_body_on_a_receipt_replay_the_first_response_byte_for_byte_with_one_stock_effect()
    {
        var dock = await DockAsync();
        var body = World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 30));
        var key = ApiClient.NewKey();

        var first = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
        var second = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
        var third = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.False(first.Replayed);
        Assert.NotNull(first.Location);
        Assert.NotNull(first.ETag);
        foreach (var replay in new[] { second, third })
        {
            AssertSameResponse(first, replay);
            Assert.True(replay.Replayed);
        }

        Assert.Equal((30, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await ReceiptCountAsync(dock.Order.Id()));
        Assert.Equal(1, await _world.AuditCountAsync(dock.OperatorUser.Id, "purchasing.goods_received"));
        Assert.Equal(1, await _world.IdempotencyRecordCountAsync(key));

        // The replay is the stored answer, not a fresh read: the order has moved on, the bytes have not.
        using var more = await World.ReceiveAsync(dock.Operator, dock.Order.Id(), _world.Reference("IRS"), (dock.Line, 70));
        Assert.Equal("Received", (await more.JsonAsync(HttpStatusCode.Created)).Str("purchaseOrderStatus"));
        var afterClose = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
        AssertSameResponse(first, afterClose);
        Assert.Equal("PartiallyReceived", afterClose.Json.Str("purchaseOrderStatus"));
        Assert.Equal((100, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    [Fact]
    public async Task The_same_key_on_a_confirmation_replays_the_first_response_and_reserves_once()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 50);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 12));
        var url = $"/api/sales/orders/{order.Id()}/confirm";
        var key = ApiClient.NewKey();

        var first = await Reply.ReadAsync(sales.PostAsync(url, null, key, etag));
        var replay = await Reply.ReadAsync(sales.PostAsync(url, null, key, etag));

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal("Confirmed", first.Json.Str("status"));
        Assert.Null(first.Location);
        AssertSameResponse(first, replay);
        Assert.True(replay.Replayed);
        Assert.Equal((50, 12), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id));

        // If-Match is part of the request: the same key with the newer version is a different request.
        var otherVersion = await Reply.ReadAsync(sales.PostAsync(url, null, key, first.ETag));
        otherVersion.AssertProblem(HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Equal((50, 12), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused_and_has_no_effect()
    {
        var dock = await DockAsync();
        var reference = _world.Reference("IRS");
        var key = ApiClient.NewKey();
        var first = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(reference, (dock.Line, 30)), key));
        Assert.Equal(HttpStatusCode.Created, first.Status);

        var changed = new[]
        {
            World.ReceiptBody(reference, (dock.Line, 31)),
            World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 30)),
            World.ReceiptBody(reference, (dock.Line, 30), (Guid.NewGuid(), 1)),
        };
        foreach (var body in changed)
        {
            var reply = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
            reply.AssertProblem(HttpStatusCode.Conflict, "idempotency_key_reused");
            Assert.False(reply.Replayed);
            Assert.DoesNotContain(first.Json.Str("number"), reply.Text, StringComparison.Ordinal);
        }

        Assert.Equal((30, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await ReceiptCountAsync(dock.Order.Id()));

        // What counts is the normalised command, not the bytes on the wire: spelling the same reference
        // in lower case with surrounding blanks is the same request and is replayed.
        var sameMeaning = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody("  " + reference.ToLowerInvariant() + " ", (dock.Line, 30)), key));
        AssertSameResponse(first, sameMeaning);
        Assert.True(sameMeaning.Replayed);
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    [Fact]
    public async Task A_key_is_scoped_to_the_user_and_the_operation()
    {
        var dock = await DockAsync();
        var second = await _world.LoginAsAsync([Roles.WarehouseOperator], [(dock.Warehouse.Id, WarehouseLevel.Operate)]);
        var key = ApiClient.NewKey();

        // Two users, one key: two independent executions.
        var mine = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 10)), key));
        var theirs = await Reply.ReadAsync(second.PostAsync(dock.ReceiptUrl, World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 5)), key));
        Assert.Equal((HttpStatusCode.Created, false), (mine.Status, mine.Replayed));
        Assert.Equal((HttpStatusCode.Created, false), (theirs.Status, theirs.Replayed));
        Assert.NotEqual(mine.Json.Id(), theirs.Json.Id());
        Assert.Equal((15, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));

        // One user, one key, two different operations: also independent.
        var target = await _world.WarehouseAsync();
        var mover = await _world.LoginAsAsync([Roles.WarehouseOperator], [(dock.Warehouse.Id, WarehouseLevel.Supervise), (target.Id, WarehouseLevel.Operate)]);
        var shared = ApiClient.NewKey();
        var transfer = await Reply.ReadAsync(mover.PostAsync("/api/inventory/transfers", World.TransferBody(_world.Reference("TR"), dock.Warehouse.Id, target.Id, (dock.Product.Id, 2)), shared));
        var adjustment = await Reply.ReadAsync(mover.PostAsync("/api/inventory/adjustments", World.AdjustmentBody(_world.Reference("AD"), dock.Warehouse.Id, "Found", (dock.Product.Id, 1)), shared));
        Assert.Equal((HttpStatusCode.Created, false), (transfer.Status, transfer.Replayed));
        Assert.Equal((HttpStatusCode.Created, false), (adjustment.Status, adjustment.Replayed));
        Assert.Equal((14, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(2, await _world.IdempotencyRecordCountAsync(key));
        Assert.Equal(2, await _world.IdempotencyRecordCountAsync(shared));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
        await _world.AssertReconciledAsync(target.Id, dock.Product.Id);
    }

    [Fact]
    public async Task Ten_parallel_requests_with_one_key_and_body_get_identical_bytes_and_cause_one_effect()
    {
        var dock = await DockAsync();
        var body = World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 40));
        var key = ApiClient.NewKey();
        var clients = _world.Clones(dock.Operator, 10);

        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => client.PostAsync(dock.ReceiptUrl, body, key))));

        Assert.All(replies, r => Assert.True(r.Status == HttpStatusCode.Created, $"{(int)r.Status}: {r.Text}"));
        Assert.All(replies, r => AssertSameResponse(replies[0], r));
        Assert.Single(replies, r => !r.Replayed);
        Assert.Equal(40, replies[0].Json["lines"]![0]!.Number("quantity"));

        Assert.Equal((40, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await ReceiptCountAsync(dock.Order.Id()));
        Assert.Equal(1, await _world.AuditCountAsync(dock.OperatorUser.Id, "purchasing.goods_received"));
        Assert.Equal(1, await _world.IdempotencyRecordCountAsync(key));
        Assert.Equal((100, 40, 0, 60), Quantities(await dock.Buyer.GetJsonAsync($"/api/purchasing/orders/{dock.Order.Id()}")));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    private static (int Ordered, int Received, int Cancelled, int Open) Quantities(JsonNode order)
    {
        var line = order["lines"]![0]!;
        return (line.Number("orderedQty"), line.Number("receivedQty"), line.Number("cancelledQty"), line.Number("openQty"));
    }

    // ---- business references -------------------------------------------------------------------

    [Fact]
    public async Task A_new_key_with_an_already_used_receipt_reference_is_a_duplicate_and_adds_no_stock()
    {
        var dock = await DockAsync();
        var (otherOrder, _) = await World.ApprovedPurchaseOrderAsync(dock.Buyer, dock.Supplier.Id, dock.Warehouse.Id, (dock.Product.Id, 50));
        var reference = _world.Reference("IRS");
        using var first = await World.ReceiveAsync(dock.Operator, dock.Order.Id(), reference, (dock.Line, 10));
        var receipt = await first.JsonAsync(HttpStatusCode.Created);

        // Same delivery note retried with a new key: on the same order, on another order of the same
        // supplier, with another quantity, in another spelling.
        var retries = new (Guid OrderId, string Reference, Guid Line, int Quantity)[]
        {
            (dock.Order.Id(), reference, dock.Line, 10),
            (dock.Order.Id(), reference, dock.Line, 3),
            (dock.Order.Id(), " " + reference.ToLowerInvariant(), dock.Line, 10),
            (otherOrder.Id(), reference, otherOrder["lines"]![0]!.Id(), 10),
        };
        foreach (var (orderId, usedReference, line, quantity) in retries)
        {
            var reply = await Reply.ReadAsync(World.ReceiveAsync(dock.Operator, orderId, usedReference, (line, quantity)));
            reply.AssertProblem(HttpStatusCode.Conflict, "duplicate_business_reference");
        }

        Assert.Equal((10, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await ReceiptCountAsync(dock.Order.Id()));
        Assert.Equal(0, await ReceiptCountAsync(otherOrder.Id()));

        // The reference is unique per supplier: another supplier's delivery note may carry the same number.
        var otherSupplier = await _world.SupplierAsync();
        var (foreignOrder, _) = await World.ApprovedPurchaseOrderAsync(dock.Buyer, otherSupplier.Id, dock.Warehouse.Id, (dock.Product.Id, 5));
        using var sameNumber = await World.ReceiveAsync(dock.Operator, foreignOrder.Id(), reference, (foreignOrder["lines"]![0]!.Id(), 5));
        Assert.NotEqual(receipt.Id(), (await sameNumber.JsonAsync(HttpStatusCode.Created)).Id());
        Assert.Equal((15, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    [Fact]
    public async Task Ten_parallel_receipts_with_different_keys_and_one_reference_receive_once()
    {
        var dock = await DockAsync();
        var reference = _world.Reference("IRS");
        var clients = _world.Clones(dock.Operator, 10);

        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => World.ReceiveAsync(client, dock.Order.Id(), reference, (dock.Line, 10)))));

        Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        Assert.All(replies.Where(r => r.Status != HttpStatusCode.Created), r => r.AssertProblem(HttpStatusCode.Conflict, "duplicate_business_reference"));
        Assert.Equal((10, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await ReceiptCountAsync(dock.Order.Id()));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    [Fact]
    public async Task A_new_key_with_an_already_used_dispatch_reference_ships_nothing_more()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 50);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var (other, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10));
        var line = order["lines"]![0]!.Id();
        var reference = _world.Reference("SEVK");
        using var first = await World.ShipAsync(operatorClient, order.Id(), reference, (line, 4));
        await first.JsonAsync(HttpStatusCode.Created);

        foreach (var quantity in new[] { 4, 1 })
        {
            using var again = await World.ShipAsync(operatorClient, order.Id(), reference, (line, quantity));
            await again.ProblemAsync(HttpStatusCode.Conflict, "duplicate_business_reference");
        }

        Assert.Equal((46, 16), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id, "Shipment"));
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id, "ReleaseOnShipment"));

        // The dispatch reference is unique within one order, so another order may use the same number.
        using var otherOrder = await World.ShipAsync(operatorClient, other.Id(), reference, (other["lines"]![0]!.Id(), 10));
        await otherOrder.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((36, 6), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task A_new_key_with_an_already_used_transfer_or_adjustment_reference_has_no_second_effect()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        await _world.StockAsync(source, product, 20);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(source.Id, WarehouseLevel.Supervise), (target.Id, WarehouseLevel.Supervise)]);

        var transferReference = _world.Reference("TR");
        using var transfer = await World.TransferAsync(supervisor, transferReference, source.Id, target.Id, (product.Id, 5));
        await transfer.JsonAsync(HttpStatusCode.Created);
        using var transferAgain = await World.TransferAsync(supervisor, transferReference, source.Id, target.Id, (product.Id, 5));
        await transferAgain.ProblemAsync(HttpStatusCode.Conflict, "duplicate_business_reference");

        // Transfer references are unique across the whole system, in either direction and spelling.
        using var reverse = await World.TransferAsync(supervisor, transferReference.ToLowerInvariant(), target.Id, source.Id, (product.Id, 1));
        await reverse.ProblemAsync(HttpStatusCode.Conflict, "duplicate_business_reference");
        Assert.Equal((15, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((5, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id, "TransferOut"));
        Assert.Equal(1, await _world.MovementCountAsync(target.Id, product.Id, "TransferIn"));

        var adjustmentReference = _world.Reference("AD");
        using var adjustment = await World.AdjustAsync(supervisor, adjustmentReference, source.Id, "Damage", (product.Id, -2));
        await adjustment.JsonAsync(HttpStatusCode.Created);
        using var adjustmentAgain = await World.AdjustAsync(supervisor, adjustmentReference, source.Id, "Damage", (product.Id, -2));
        await adjustmentAgain.ProblemAsync(HttpStatusCode.Conflict, "duplicate_business_reference");
        using var elsewhere = await World.AdjustAsync(supervisor, adjustmentReference, target.Id, "Found", (product.Id, 3));
        await elsewhere.ProblemAsync(HttpStatusCode.Conflict, "duplicate_business_reference");
        Assert.Equal((13, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((5, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id, "Adjustment"));
        Assert.Equal(0, await _world.MovementCountAsync(target.Id, product.Id, "Adjustment"));

        // In parallel as well: ten transfers and ten adjustments, one reference each.
        var raced = (Transfer: _world.Reference("TR"), Adjustment: _world.Reference("AD"));
        var clients = _world.Clones(supervisor, 20);
        var replies = await Race.RunAsync(clients.Select((client, i) => (Func<Task<HttpResponseMessage>>)(() => i % 2 == 0
            ? World.TransferAsync(client, raced.Transfer, source.Id, target.Id, (product.Id, 1))
            : World.AdjustAsync(client, raced.Adjustment, source.Id, "Loss", (product.Id, -1)))));
        Assert.Equal(2, replies.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Single(replies.Where((_, i) => i % 2 == 0), r => r.Status == HttpStatusCode.Created);
        Assert.Single(replies.Where((_, i) => i % 2 == 1), r => r.Status == HttpStatusCode.Created);
        Assert.All(replies.Where(r => r.Status != HttpStatusCode.Created), r => r.AssertProblem(HttpStatusCode.Conflict, "duplicate_business_reference"));
        Assert.Equal((11, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((6, 0), await _world.BalanceAsync(target.Id, product.Id));
        await _world.AssertReconciledAsync(source.Id, product.Id);
        await _world.AssertReconciledAsync(target.Id, product.Id);
    }

    // ---- a key is not a permission --------------------------------------------------------------

    [Fact]
    public async Task Replaying_a_receipt_after_the_warehouse_grant_was_removed_does_not_return_the_stored_response()
    {
        var dock = await DockAsync();
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var body = World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 25));
        var key = ApiClient.NewKey();
        var first = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
        Assert.Equal(HttpStatusCode.Created, first.Status);
        var whileAllowed = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
        AssertSameResponse(first, whileAllowed);

        await World.SetGrantsAsync(admin, dock.OperatorUser.Id);
        var fresh = await _world.LoginAsync(dock.OperatorUser);
        var replay = await Reply.ReadAsync(fresh.PostAsync(dock.ReceiptUrl, body, key));

        replay.AssertProblem(HttpStatusCode.NotFound, "not_found");
        Assert.False(replay.Replayed);
        Assert.Null(replay.Location);
        Assert.Null(replay.ETag);
        Assert.DoesNotContain(first.Json.Str("number"), replay.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(dock.Order.Str("number"), replay.Text, StringComparison.Ordinal);
        using var receipt = await fresh.GetAsync(first.Location!);
        await receipt.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Lowering the grant below what the command needs is refused the same way.
        await World.SetGrantsAsync(admin, dock.OperatorUser.Id, (dock.Warehouse.Id, WarehouseLevel.View));
        var viewer = await _world.LoginAsync(dock.OperatorUser);
        var asViewer = await Reply.ReadAsync(viewer.PostAsync(dock.ReceiptUrl, body, key));
        Assert.Contains(asViewer.Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.False(asViewer.Replayed);

        // With the grant back, the stored response is served again: the record was never touched.
        await World.SetGrantsAsync(admin, dock.OperatorUser.Id, (dock.Warehouse.Id, WarehouseLevel.Operate));
        var restored = await _world.LoginAsync(dock.OperatorUser);
        var again = await Reply.ReadAsync(restored.PostAsync(dock.ReceiptUrl, body, key));
        AssertSameResponse(first, again);
        Assert.True(again.Replayed);

        Assert.Equal((25, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    // ---- retention -----------------------------------------------------------------------------

    [Fact]
    public async Task After_the_retention_period_the_key_is_a_new_request_and_the_business_reference_prevents_a_second_effect()
    {
        var dock = await DockAsync();
        var body = World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 20));
        var key = ApiClient.NewKey();
        var first = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, body, key));
        Assert.Equal(HttpStatusCode.Created, first.Status);

        // Still inside the 24 hours: a replay. (Sessions last 30 idle minutes, so each step signs in again.)
        fixture.Clock.Advance(TimeSpan.FromHours(23));
        var sameDay = await _world.LoginAsync(dock.OperatorUser);
        var replay = await Reply.ReadAsync(sameDay.PostAsync(dock.ReceiptUrl, body, key));
        AssertSameResponse(first, replay);
        Assert.True(replay.Replayed);

        // 25 hours after the first request the record has expired. The request is evaluated again from
        // scratch and the delivery note number, not the key, is what stops it.
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        var nextDay = await _world.LoginAsync(dock.OperatorUser);
        var late = await Reply.ReadAsync(nextDay.PostAsync(dock.ReceiptUrl, body, key));

        late.AssertProblem(HttpStatusCode.Conflict, "duplicate_business_reference");
        Assert.False(late.Replayed);
        Assert.Equal((20, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await ReceiptCountAsync(dock.Order.Id()));

        // The expired key is free again: with a different delivery note it runs as a brand-new command.
        var reused = await Reply.ReadAsync(nextDay.PostAsync(dock.ReceiptUrl, World.ReceiptBody(_world.Reference("IRS"), (dock.Line, 5)), key));
        Assert.Equal((HttpStatusCode.Created, false), (reused.Status, reused.Replayed));
        Assert.NotEqual(first.Json.Id(), reused.Json.Id());
        Assert.Equal((25, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        Assert.Equal(1, await _world.IdempotencyRecordCountAsync(key));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }

    // ---- only successes are remembered ---------------------------------------------------------

    [Fact]
    public async Task A_rejected_command_leaves_no_record_so_the_same_key_succeeds_once_the_cause_is_fixed()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 5);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 8));
        var url = $"/api/sales/orders/{order.Id()}/confirm";
        var key = ApiClient.NewKey();

        // Not enough stock: 409, twice, and nothing is stored under the key.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var refused = await Reply.ReadAsync(sales.PostAsync(url, null, key, etag));
            refused.AssertProblem(HttpStatusCode.Conflict, "insufficient_available");
            Assert.False(refused.Replayed);
        }

        Assert.Equal(0, await _world.IdempotencyRecordCountAsync(key));
        Assert.Equal((5, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        // Stock arrives. The very same request — same key, same If-Match — is now evaluated against the new state.
        await _world.StockAsync(warehouse, product, 3);
        var accepted = await Reply.ReadAsync(sales.PostAsync(url, null, key, etag));
        Assert.Equal((HttpStatusCode.OK, false), (accepted.Status, accepted.Replayed));
        Assert.Equal("Confirmed", accepted.Json.Str("status"));
        Assert.Equal((8, 8), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.IdempotencyRecordCountAsync(key));

        // From here on it is a replay.
        var replay = await Reply.ReadAsync(sales.PostAsync(url, null, key, etag));
        AssertSameResponse(accepted, replay);
        Assert.True(replay.Replayed);
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Validation_and_precondition_failures_leave_no_record_either()
    {
        var dock = await DockAsync();
        var key = ApiClient.NewKey();
        var reference = _world.Reference("IRS");

        var tooMany = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(reference, (dock.Line, 101)), key));
        tooMany.AssertProblem(HttpStatusCode.Conflict, "exceeds_remaining");
        var mismatch = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(reference, (Guid.NewGuid(), 1)), key));
        mismatch.AssertProblem(HttpStatusCode.UnprocessableEntity, "line_mismatch");
        var invalid = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody("x", (dock.Line, 1)), key));
        invalid.AssertProblem(HttpStatusCode.BadRequest, "validation_failed");
        var stale = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(reference, (dock.Line, 1)), key, "\"AAAAAAAAAAE=\""));
        stale.AssertProblem(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal(0, await _world.IdempotencyRecordCountAsync(key));
        Assert.Equal((0, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));

        // Four different bodies were tried under the key; because none succeeded, none of them bound it.
        var accepted = await Reply.ReadAsync(dock.Operator.PostAsync(dock.ReceiptUrl, World.ReceiptBody(reference, (dock.Line, 100)), key));
        Assert.Equal((HttpStatusCode.Created, false), (accepted.Status, accepted.Replayed));
        Assert.Equal((100, 0), await _world.BalanceAsync(dock.Warehouse.Id, dock.Product.Id));
        await _world.AssertReconciledAsync(dock.Warehouse.Id, dock.Product.Id);
    }
}
