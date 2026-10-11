using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Xunit;

namespace Kantar.IntegrationTests.Inventory;

/// <summary>docs/04 §3.7 and docs/05 §1.1: keyset pagination, closed filters and warehouse-scoped lists.</summary>
public sealed class QueryTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    /// <summary>Follows nextCursor to the end. <paramref name="betweenPages"/> runs after every page that has a successor.</summary>
    private static async Task<List<JsonNode>> WalkAsync(ApiClient client, string url, int limit, Func<int, Task>? betweenPages = null)
    {
        var items = new List<JsonNode>();
        string? cursor = null;
        for (var page = 0; page < 200; page++)
        {
            var json = await client.GetJsonAsync($"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}limit={limit}{(cursor is null ? string.Empty : "&cursor=" + cursor)}");
            var batch = json["items"]!.AsArray();
            Assert.True(batch.Count <= limit, $"Page {page} has {batch.Count} items for limit {limit}.");
            items.AddRange(batch.Select(i => i!.DeepClone()));
            cursor = json["nextCursor"]?.GetValue<string>();
            if (cursor is null)
            {
                return items;
            }

            Assert.Equal(limit, batch.Count);
            Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
            if (betweenPages is not null)
            {
                await betweenPages(page);
            }
        }

        throw new InvalidOperationException("Pagination did not terminate.");
    }

    [Fact]
    public async Task Movement_pages_cover_every_existing_row_exactly_once_while_new_rows_keep_arriving()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        for (var i = 0; i < 7; i++)
        {
            await _world.StockAsync(warehouse, product, 10 + i);
        }

        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var url = $"/api/inventory/movements?warehouseId={warehouse.Id}";
        var existing = (await supervisor.GetJsonAsync(url + "&limit=100"))["items"]!.AsArray().Select(m => m!["id"]!.GetValue<long>()).ToArray();
        Assert.Equal(7, existing.Length);
        Assert.Equal(existing.OrderDescending(), existing);

        // A new movement is written after every page: it has a higher id than anything already listed.
        var inserted = 0;
        var walked = await WalkAsync(supervisor, url, 2, async _ =>
        {
            using var response = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Found", (product.Id, 1));
            await response.JsonAsync(HttpStatusCode.Created);
            inserted++;
        });

        var ids = walked.Select(m => m["id"]!.GetValue<long>()).ToArray();
        Assert.Equal(existing, ids);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(3, inserted);
        Assert.All(walked, m => Assert.Equal("Receipt", m.Str("type")));

        // A fresh walk sees old and new rows, newest first, again without gaps or repeats.
        var all = (await WalkAsync(supervisor, url, 3)).Select(m => m["id"]!.GetValue<long>()).ToArray();
        Assert.Equal(10, all.Length);
        Assert.Equal(all.OrderDescending(), all);
        Assert.Equal(existing, all[3..]);
        Assert.Equal(10, await _world.MovementCountAsync(warehouse.Id, product.Id));

        // The reservation ledger pages the same way.
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var customer = await _world.CustomerAsync();
        for (var i = 0; i < 5; i++)
        {
            await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 1));
        }

        var ledger = await WalkAsync(sales, $"/api/inventory/reservations?warehouseId={warehouse.Id}&productId={product.Id}", 2);
        Assert.Equal([5, 4, 3, 2, 1], ledger.Select(e => e.Number("reservedAfter")));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Movements_are_filtered_by_product_type_and_time()
    {
        var warehouse = await _world.WarehouseAsync();
        var (first, second) = (await _world.ProductAsync(), await _world.ProductAsync());
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, [(first, 20), (second, 30)]);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var (order, _) = await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (first.Id, 5));
        using var shipped = await World.ShipAsync(supervisor, order.Id(), _world.Reference("SEVK"), (order["lines"]![0]!.Id(), 5));
        await shipped.JsonAsync(HttpStatusCode.Created);
        using var adjusted = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Damage", (second.Id, -2));
        await adjusted.JsonAsync(HttpStatusCode.Created);
        var url = $"/api/inventory/movements?warehouseId={warehouse.Id}";

        async Task<(Guid Product, string Type, int Delta)[]> RowsAsync(string filter) =>
            (await sales.GetJsonAsync(url + filter))["items"]!.AsArray()
                .Select(m => (m!.Id("productId"), m!.Str("type"), m!.Number("quantityDelta"))).ToArray();

        Assert.Equal(4, (await RowsAsync(string.Empty)).Length);
        Assert.Equal([(first.Id, "Shipment", -5), (first.Id, "Receipt", 20)], await RowsAsync($"&productId={first.Id}"));
        Assert.Equal([(second.Id, "Adjustment", -2), (second.Id, "Receipt", 30)], await RowsAsync($"&productId={second.Id}"));
        Assert.Equal(2, (await RowsAsync("&type=Receipt")).Length);
        Assert.All(await RowsAsync("&type=Receipt"), r => Assert.Equal("Receipt", r.Type));
        Assert.Equal([(first.Id, "Shipment", -5)], await RowsAsync("&type=Shipment"));
        Assert.Equal([(second.Id, "Adjustment", -2)], await RowsAsync("&type=Adjustment"));
        Assert.Empty(await RowsAsync("&type=TransferIn"));
        Assert.Empty(await RowsAsync($"&type=Shipment&productId={second.Id}"));
        Assert.Empty(await RowsAsync($"&productId={Guid.NewGuid()}"));

        // "from" is inclusive and "to" exclusive, on the server's clock.
        var now = fixture.Clock.GetUtcNow().UtcDateTime;
        static string Iso(DateTime value) => Uri.EscapeDataString(value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        Assert.Equal(4, (await RowsAsync($"&from={Iso(now.AddHours(-1))}&to={Iso(now.AddHours(1))}")).Length);
        Assert.Empty(await RowsAsync($"&to={Iso(now.AddHours(-1))}"));
        Assert.Empty(await RowsAsync($"&from={Iso(now.AddHours(1))}"));
        Assert.Equal([(first.Id, "Shipment", -5)], await RowsAsync($"&from={Iso(now.AddHours(-1))}&type=Shipment&productId={first.Id}"));

        // Each row names its source document.
        var shipment = Assert.Single((await sales.GetJsonAsync(url + "&type=Shipment"))["items"]!.AsArray())!;
        Assert.Equal(("Shipment", (await shipped.JsonAsync(HttpStatusCode.Created)).Str("number")), (shipment.Str("referenceType"), shipment.Str("referenceNumber")));
        await _world.AssertReconciledAsync(warehouse.Id, first.Id);
        await _world.AssertReconciledAsync(warehouse.Id, second.Id);
    }

    [Fact]
    public async Task Sales_order_pages_are_stable_when_every_order_has_the_same_creation_time()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var created = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            var (order, _) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, i + 1));
            created.Add(order.Id());
        }

        var url = $"/api/sales/orders?warehouseId={warehouse.Id}";
        var single = (await sales.GetJsonAsync(url + "&limit=100"))["items"]!.AsArray();

        // The test clock does not move on its own, so all nine share one timestamp and only the id breaks the tie.
        Assert.Single(single.Select(o => o!.Str("createdAt")).Distinct());
        Assert.Equal(created.Order(), single.Select(o => o!.Id()).Order());

        foreach (var limit in new[] { 1, 2, 4, 9 })
        {
            var walked = (await WalkAsync(sales, url, limit)).Select(o => o.Id()).ToArray();
            Assert.Equal(single.Select(o => o!.Id()), walked);
        }

        // Orders that arrive mid-walk carry the same timestamp and may sort anywhere among their peers;
        // the nine that were there are still returned once each, in their original relative order.
        var late = new List<Guid>();
        var walkedWithInserts = (await WalkAsync(sales, url, 2, async _ =>
        {
            var (order, _) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 1));
            late.Add(order.Id());
        })).Select(o => o.Id()).ToArray();
        Assert.Equal(walkedWithInserts.Length, walkedWithInserts.Distinct().Count());
        Assert.Equal(single.Select(o => o!.Id()), walkedWithInserts.Where(created.Contains));
        Assert.All(walkedWithInserts.Except(created), id => Assert.Contains(id, late));

        // Filters combine with the cursor.
        var (toConfirm, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 1));
        await _world.StockAsync(warehouse, product, 5);
        await World.ConfirmAsync(sales, toConfirm, etag);
        var drafts = await WalkAsync(sales, url + "&status=Draft", 4);
        Assert.Equal(created.Count + late.Count, drafts.Count);
        Assert.All(drafts, o => Assert.Equal("Draft", o.Str("status")));
        Assert.Equal([toConfirm.Id()], (await WalkAsync(sales, url + "&status=Confirmed&mine=true", 4)).Select(o => o.Id()));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Purchase_order_transfer_and_adjustment_pages_are_stable_under_identical_timestamps()
    {
        var (warehouse, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        var supplier = await _world.SupplierAsync();
        await _world.StockAsync(warehouse, product, 50);
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise), (target.Id, WarehouseLevel.Operate)]);
        for (var i = 0; i < 5; i++)
        {
            await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 1));
            using var transfer = await World.TransferAsync(supervisor, _world.Reference("TR"), warehouse.Id, target.Id, (product.Id, 1));
            await transfer.JsonAsync(HttpStatusCode.Created);
            using var adjustment = await World.AdjustAsync(supervisor, _world.Reference("AD"), warehouse.Id, "Found", (product.Id, 1));
            await adjustment.JsonAsync(HttpStatusCode.Created);
        }

        var lists = new (ApiClient Client, string Url, int Expected)[]
        {
            (buyer, $"/api/purchasing/orders?warehouseId={warehouse.Id}", 6), // five drafts and the order that brought the stock
            (supervisor, $"/api/inventory/transfers?warehouseId={warehouse.Id}", 5),
            (supervisor, $"/api/inventory/transfers?warehouseId={target.Id}", 5),
            (supervisor, $"/api/inventory/adjustments?warehouseId={warehouse.Id}", 5),
        };
        foreach (var (client, url, expected) in lists)
        {
            var single = (await client.GetJsonAsync(url + "&limit=100"))["items"]!.AsArray().Select(d => d!.Id()).ToArray();
            Assert.Equal(expected, single.Length);
            foreach (var limit in new[] { 1, 2, 3 })
            {
                Assert.Equal(single, (await WalkAsync(client, url, limit)).Select(d => d.Id()));
            }
        }

        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        await _world.AssertReconciledAsync(target.Id, product.Id);
    }

    [Fact]
    public async Task Lists_return_only_rows_of_warehouses_the_caller_may_see()
    {
        var (mine, theirs) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        var supplier = await _world.SupplierAsync();
        await _world.StockAsync(mine, product, 20);
        await _world.StockAsync(theirs, product, 20);

        var agent = await _world.LoginAsAsync([Roles.SalesAgent], [(mine.Id, WarehouseLevel.View)]);
        var colleague = await _world.LoginAsAsync([Roles.SalesAgent], [(mine.Id, WarehouseLevel.View)]);
        var manager = await _world.LoginAsAsync([Roles.SalesManager], [(mine.Id, WarehouseLevel.View)]);
        var foreignManager = await _world.LoginAsAsync([Roles.SalesManager], [(theirs.Id, WarehouseLevel.View)]);
        var both = await _world.LoginAsAsync([Roles.SalesManager], [(mine.Id, WarehouseLevel.View), (theirs.Id, WarehouseLevel.View)]);
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(mine.Id, WarehouseLevel.Operate), (theirs.Id, WarehouseLevel.View)]);

        var (agentDraft, _) = await World.CreateSalesOrderAsync(agent, customer.Id, mine.Id, (product.Id, 1));
        var (agentConfirmed, _) = await World.ConfirmedSalesOrderAsync(agent, customer.Id, mine.Id, (product.Id, 2));
        var (colleagueDraft, _) = await World.CreateSalesOrderAsync(colleague, customer.Id, mine.Id, (product.Id, 3));
        var (managerDraft, _) = await World.CreateSalesOrderAsync(manager, customer.Id, mine.Id, (product.Id, 4));
        var (foreignConfirmed, _) = await World.ConfirmedSalesOrderAsync(foreignManager, customer.Id, theirs.Id, (product.Id, 5));

        async Task<Guid[]> SalesIdsAsync(ApiClient client, string query = "") =>
            (await client.GetJsonAsync("/api/sales/orders?limit=100" + query))["items"]!.AsArray().Select(o => o!.Id()).Order().ToArray();
        static Guid[] Ids(params JsonNode[] orders) => orders.Select(o => o.Id()).Order().ToArray();

        // Without a warehouse filter the list is the union of what the caller may see, never more.
        Assert.Equal(Ids(agentDraft, agentConfirmed), await SalesIdsAsync(agent));
        Assert.Equal(Ids(colleagueDraft), await SalesIdsAsync(colleague));
        Assert.Equal(Ids(agentDraft, agentConfirmed, colleagueDraft, managerDraft), await SalesIdsAsync(manager));
        Assert.Equal(Ids(managerDraft), await SalesIdsAsync(manager, "&mine=true"));
        Assert.Equal(Ids(foreignConfirmed), await SalesIdsAsync(foreignManager));
        Assert.Equal(Ids(agentDraft, agentConfirmed, colleagueDraft, managerDraft, foreignConfirmed), await SalesIdsAsync(both));
        Assert.Equal(Ids(foreignConfirmed), await SalesIdsAsync(both, $"&warehouseId={theirs.Id}"));
        Assert.Equal(Ids(agentConfirmed), await SalesIdsAsync(both, $"&warehouseId={mine.Id}&status=Confirmed"));

        // The operator operates one warehouse and only views the other: confirmed orders of the first, nothing of the second.
        Assert.Equal(Ids(agentConfirmed), await SalesIdsAsync(operatorClient));
        using var operatorElsewhere = await operatorClient.GetAsync($"/api/sales/orders?warehouseId={theirs.Id}");
        await operatorElsewhere.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var agentElsewhere = await agent.GetAsync($"/api/sales/orders?warehouseId={theirs.Id}");
        await agentElsewhere.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        // Purchase orders: the two stocking orders exist, one per warehouse; each buyer lists only their side.
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(mine.Id, WarehouseLevel.View)]);
        var (purchase, _) = await World.CreatePurchaseOrderAsync(buyer, supplier.Id, mine.Id, (product.Id, 1));
        var purchases = (await buyer.GetJsonAsync("/api/purchasing/orders?limit=100"))["items"]!.AsArray();
        Assert.Equal(2, purchases.Count);
        Assert.Contains(purchase.Id(), purchases.Select(o => o!.Id()));
        Assert.All(purchases, o => Assert.Equal(mine.Id, o!.Id("warehouseId")));
        using var buyerElsewhere = await buyer.GetAsync($"/api/purchasing/orders?warehouseId={theirs.Id}");
        await buyerElsewhere.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        var operatorPurchases = (await operatorClient.GetJsonAsync("/api/purchasing/orders?limit=100"))["items"]!.AsArray();
        Assert.All(operatorPurchases, o => Assert.Equal((mine.Id, "Received"), (o!.Id("warehouseId"), o!.Str("status"))));
        Assert.Single(operatorPurchases);

        // A role with no document rights at all is told so instead of receiving an empty list.
        var support = await _world.LoginAsAsync([Roles.SupportAgent], [(mine.Id, WarehouseLevel.Supervise)]);
        using var supportSales = await support.GetAsync("/api/sales/orders");
        await supportSales.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var supportPurchases = await support.GetAsync("/api/purchasing/orders");
        await supportPurchases.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var supportStock = await support.GetAsync($"/api/inventory/balances?warehouseId={mine.Id}");
        await supportStock.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        await _world.AssertReconciledAsync(mine.Id, product.Id);
        await _world.AssertReconciledAsync(theirs.Id, product.Id);
    }

    [Fact]
    public async Task Low_stock_filter_lists_active_products_whose_available_quantity_is_at_or_below_the_reorder_level()
    {
        var warehouse = await _world.WarehouseAsync();
        var customer = await _world.CustomerAsync();
        var reservedDown = await _world.ProductAsync(reorderLevel: 5);   // 10 on hand, 6 reserved → 4 available → low
        var atLevel = await _world.ProductAsync(reorderLevel: 5);        // 5 available → low (boundary)
        var aboveLevel = await _world.ProductAsync(reorderLevel: 5);     // 6 available → not low
        var noLevel = await _world.ProductAsync(reorderLevel: 0);        // 3 available, level 0 → not low
        var passive = await _world.ProductAsync(reorderLevel: 50);       // far below its level, but passive → not listed
        await _world.StockAsync(warehouse, [(reservedDown, 10), (atLevel, 5), (aboveLevel, 6), (noLevel, 3), (passive, 1)]);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var url = $"/api/inventory/balances?warehouseId={warehouse.Id}";

        async Task<Guid[]> LowAsync() =>
            (await sales.GetJsonAsync(url + "&lowStockOnly=true"))["items"]!.AsArray().Select(b => b!.Id("productId")).Order().ToArray();

        Assert.Equal(new[] { atLevel.Id, passive.Id }.Order(), await LowAsync());

        await World.ConfirmedSalesOrderAsync(sales, customer.Id, warehouse.Id, (reservedDown.Id, 6));
        using var deactivated = await admin.PostAsync($"/api/products/{passive.Id}/deactivate");
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(new[] { reservedDown.Id, atLevel.Id }.Order(), await LowAsync());

        // The unfiltered list has all five, sorted by SKU, each with its own flag and derived quantity.
        var all = (await sales.GetJsonAsync(url))["items"]!.AsArray();
        Assert.Equal(new[] { reservedDown, atLevel, aboveLevel, noLevel, passive }.Select(p => p.Sku).Order(StringComparer.Ordinal), all.Select(b => b!.Str("sku")));
        var byProduct = all.ToDictionary(b => b!.Id("productId"), b => b!);
        Assert.Equal((10, 6, 4, 5, true), Row(byProduct[reservedDown.Id]));
        Assert.Equal((5, 0, 5, 5, true), Row(byProduct[atLevel.Id]));
        Assert.Equal((6, 0, 6, 5, false), Row(byProduct[aboveLevel.Id]));
        Assert.Equal((3, 0, 3, 0, false), Row(byProduct[noLevel.Id]));
        Assert.Equal((1, 0, 1, 50, false), Row(byProduct[passive.Id]));

        // Search by SKU prefix (any case) or by a part of the name; paging walks the same order.
        Assert.Equal([atLevel.Id], (await sales.GetJsonAsync(url + "&search=" + atLevel.Sku.ToLowerInvariant()))["items"]!.AsArray().Select(b => b!.Id("productId")));
        Assert.Empty((await sales.GetJsonAsync(url + "&search=YOK-BOYLE-BIR-SKU"))["items"]!.AsArray());
        Assert.Equal(all.Select(b => b!.Id("productId")), (await WalkAsync(sales, url, 2)).Select(b => b.Id("productId")));
        Assert.Equal(new[] { reservedDown.Id, atLevel.Id }.Order(), (await WalkAsync(sales, url + "&lowStockOnly=true", 1)).Select(b => b.Id("productId")).Order());

        foreach (var product in new[] { reservedDown, atLevel, aboveLevel, noLevel, passive })
        {
            await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        }

        static (int OnHand, int Reserved, int Available, int ReorderLevel, bool IsLow) Row(JsonNode b) =>
            (b.Number("onHand"), b.Number("reserved"), b.Number("available"), b.Number("reorderLevel"), b["isLow"]!.GetValue<bool>());
    }
}
