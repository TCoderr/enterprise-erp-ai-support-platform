using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.IntegrationTests.Infrastructure;
using Xunit;

namespace Kantar.IntegrationTests.Security;

/// <summary>
/// docs/02 §2.4 over HTTP: role, warehouse grant and ownership decide together. A document outside
/// the caller's scope answers exactly like one that does not exist.
/// </summary>
public sealed class AuthorizationScopeTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private sealed record Site(
        Warehouse Warehouse, Product Product, Customer Customer, Supplier Supplier,
        JsonNode DraftOrder, JsonNode ConfirmedOrder, JsonNode DraftPurchase, JsonNode ApprovedPurchase,
        JsonNode Shipment, JsonNode Receipt, JsonNode Adjustment, JsonNode Transfer, Warehouse TransferTarget);

    /// <summary>One warehouse with stock and one document of every kind, created by its own staff.</summary>
    private async Task<Site> SiteAsync()
    {
        var warehouse = await _world.WarehouseAsync();
        var target = await _world.WarehouseAsync();
        var product = await _world.ProductAsync(price: 250.50m);
        var customer = await _world.CustomerAsync();
        var supplier = await _world.SupplierAsync();
        await _world.StockAsync(warehouse, product, 50);

        var manager = await _world.LoginAsAsync([Roles.SalesManager], [(warehouse.Id, WarehouseLevel.View)]);
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)], poLimit: 1_000_000m);
        var staff = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise), (target.Id, WarehouseLevel.Operate)]);

        var (draftOrder, _) = await World.CreateSalesOrderAsync(manager, customer.Id, warehouse.Id, (product.Id, 2));
        var (confirmedOrder, _) = await World.ConfirmedSalesOrderAsync(manager, customer.Id, warehouse.Id, (product.Id, 10));
        var (draftPurchase, _) = await World.CreatePurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 5));
        var (approvedPurchase, _) = await World.ApprovedPurchaseOrderAsync(buyer, supplier.Id, warehouse.Id, (product.Id, 20));

        using var shipped = await World.ShipAsync(staff, confirmedOrder.Id(), _world.Reference("SEVK"), (confirmedOrder["lines"]![0]!.Id(), 1));
        using var received = await World.ReceiveAsync(staff, approvedPurchase.Id(), _world.Reference("IRS"), (approvedPurchase["lines"]![0]!.Id(), 1));
        using var adjusted = await World.AdjustAsync(staff, _world.Reference("AD"), warehouse.Id, "Found", (product.Id, 1));
        using var transferred = await World.TransferAsync(staff, _world.Reference("TR"), warehouse.Id, target.Id, (product.Id, 1));

        return new Site(
            warehouse, product, customer, supplier, draftOrder, confirmedOrder, draftPurchase, approvedPurchase,
            await shipped.JsonAsync(HttpStatusCode.Created), await received.JsonAsync(HttpStatusCode.Created),
            await adjusted.JsonAsync(HttpStatusCode.Created), await transferred.JsonAsync(HttpStatusCode.Created), target);
    }

    private static string[] SalesUrls(Site s) =>
    [
        $"/api/sales/orders/{s.ConfirmedOrder.Id()}",
        $"/api/sales/orders/{s.DraftOrder.Id()}",
        $"/api/sales/orders/{s.ConfirmedOrder.Id()}/summary",
        $"/api/sales/orders/{s.ConfirmedOrder.Id()}/shipments/{s.Shipment.Id()}",
        $"/api/sales/orders?warehouseId={s.Warehouse.Id}",
    ];

    private static string[] PurchasingUrls(Site s) =>
    [
        $"/api/purchasing/orders/{s.ApprovedPurchase.Id()}",
        $"/api/purchasing/orders/{s.DraftPurchase.Id()}",
        $"/api/purchasing/orders/{s.ApprovedPurchase.Id()}/receipts/{s.Receipt.Id()}",
        $"/api/purchasing/orders?warehouseId={s.Warehouse.Id}",
    ];

    private static string[] InventoryUrls(Site s) =>
    [
        $"/api/inventory/balances?warehouseId={s.Warehouse.Id}",
        $"/api/inventory/movements?warehouseId={s.Warehouse.Id}",
        $"/api/inventory/reservations?warehouseId={s.Warehouse.Id}",
        $"/api/inventory/adjustments?warehouseId={s.Warehouse.Id}",
        $"/api/inventory/adjustments/{s.Adjustment.Id()}",
        $"/api/inventory/transfers?warehouseId={s.Warehouse.Id}",
        $"/api/inventory/transfers/{s.Transfer.Id()}",
    ];

    private static string[] ReadUrls(Site s) => [.. SalesUrls(s), .. PurchasingUrls(s), .. InventoryUrls(s)];

    /// <summary>Every write a caller could try against the site's documents, with a valid body and key.</summary>
    private (string Name, Func<ApiClient, Task<HttpResponseMessage>> Send)[] Writes(Site s)
    {
        var salesLine = s.ConfirmedOrder["lines"]![0]!.Id();
        var purchaseLine = s.ApprovedPurchase["lines"]![0]!.Id();
        var anyETag = "\"AAAAAAAAB9E=\"";
        return
        [
            ("edit sales draft", c => c.PutAsync($"/api/sales/orders/{s.DraftOrder.Id()}", new { customerId = s.Customer.Id, warehouseId = s.Warehouse.Id, lines = new[] { new { productId = s.Product.Id, quantity = 3 } } }, anyETag)),
            ("confirm sales draft", c => c.PostAsync($"/api/sales/orders/{s.DraftOrder.Id()}/confirm", null, ApiClient.NewKey(), anyETag)),
            ("cancel sales order", c => World.CancelSalesOrderAsync(c, s.ConfirmedOrder.Id(), anyETag)),
            ("ship", c => World.ShipAsync(c, s.ConfirmedOrder.Id(), _world.Reference("SEVK"), (salesLine, 1))),
            ("edit purchase draft", c => c.PutAsync($"/api/purchasing/orders/{s.DraftPurchase.Id()}", new { supplierId = s.Supplier.Id, warehouseId = s.Warehouse.Id, lines = new[] { new { productId = s.Product.Id, quantity = 3, unitCost = 1.00m } } }, anyETag)),
            ("approve purchase draft", c => c.PostAsync($"/api/purchasing/orders/{s.DraftPurchase.Id()}/approve", null, ApiClient.NewKey(), anyETag)),
            ("cancel purchase order", c => World.CancelPurchaseOrderAsync(c, s.ApprovedPurchase.Id(), anyETag)),
            ("receive", c => World.ReceiveAsync(c, s.ApprovedPurchase.Id(), _world.Reference("IRS"), (purchaseLine, 1))),
            ("adjust", c => World.AdjustAsync(c, _world.Reference("AD"), s.Warehouse.Id, "Found", (s.Product.Id, 1))),
            ("transfer out", c => World.TransferAsync(c, _world.Reference("TR"), s.Warehouse.Id, s.TransferTarget.Id, (s.Product.Id, 1))),
        ];
    }

    private async Task<(int OnHand, int Reserved, int Movements, int Reservations)> StockStateAsync(Site s)
    {
        var (onHand, reserved) = await _world.BalanceAsync(s.Warehouse.Id, s.Product.Id);
        return (onHand, reserved, await _world.MovementCountAsync(s.Warehouse.Id, s.Product.Id), await _world.ReservationCountAsync(s.Warehouse.Id, s.Product.Id));
    }

    private static string WithoutCorrelation(Reply reply)
    {
        var json = reply.Json.AsObject();
        Assert.Equal(32, json["correlationId"]!.GetValue<string>().Length);
        json.Remove("correlationId");
        return json.ToJsonString();
    }

    [Fact]
    public async Task Documents_and_stock_of_another_warehouse_answer_exactly_like_missing_ones()
    {
        var site = await SiteAsync();
        var elsewhere = await _world.WarehouseAsync();
        var before = await StockStateAsync(site);

        // The same roles that work in the site's warehouse, with the highest grant, but on a different
        // warehouse. Each is asked for the kinds of document its role does read elsewhere.
        var sales = await _world.LoginAsAsync([Roles.SalesManager, Roles.SalesAgent], [(elsewhere.Id, WarehouseLevel.Supervise)]);
        var purchasing = await _world.LoginAsAsync([Roles.PurchasingAgent], [(elsewhere.Id, WarehouseLevel.Supervise)], poLimit: 1_000_000m);
        var warehouse = await _world.LoginAsAsync([Roles.WarehouseOperator], [(elsewhere.Id, WarehouseLevel.Supervise)]);
        var outsiders = new[] { sales, purchasing, warehouse };
        var reads = new (ApiClient Client, string[] Urls)[]
        {
            (sales, [.. SalesUrls(site), .. InventoryUrls(site)]),
            (purchasing, [.. PurchasingUrls(site), .. InventoryUrls(site)]),
            (warehouse, ReadUrls(site)),
        };

        foreach (var (client, urls) in reads)
        {
            foreach (var url in urls)
            {
                var reply = await Reply.ReadAsync(client.GetAsync(url));
                Assert.True(reply.Status == HttpStatusCode.NotFound, $"GET {url} returned {(int)reply.Status}");
                Assert.Equal("not_found", reply.Problem);
            }
        }

        foreach (var outsider in outsiders)
        {
            foreach (var (name, send) in Writes(site))
            {
                var reply = await Reply.ReadAsync(send(outsider));
                Assert.True(reply.Status == HttpStatusCode.NotFound, $"{name} returned {(int)reply.Status}: {reply.Text}");
                Assert.Equal("not_found", reply.Problem);
            }
        }

        // Existing-but-out-of-scope and truly missing are indistinguishable apart from the correlation id.
        var salesOutsider = outsiders[0];
        var hidden = await Reply.ReadAsync(salesOutsider.GetAsync($"/api/sales/orders/{site.ConfirmedOrder.Id()}"));
        var missing = await Reply.ReadAsync(salesOutsider.GetAsync($"/api/sales/orders/{Guid.NewGuid()}"));
        Assert.Equal(WithoutCorrelation(missing), WithoutCorrelation(hidden));
        Assert.Equal(missing.ContentType, hidden.ContentType);
        var hiddenStock = await Reply.ReadAsync(salesOutsider.GetAsync($"/api/inventory/balances?warehouseId={site.Warehouse.Id}"));
        var missingStock = await Reply.ReadAsync(salesOutsider.GetAsync($"/api/inventory/balances?warehouseId={Guid.NewGuid()}"));
        Assert.Equal(WithoutCorrelation(missingStock), WithoutCorrelation(hiddenStock));

        Assert.Equal(before, await StockStateAsync(site));
        await _world.AssertReconciledAsync(site.Warehouse.Id, site.Product.Id);
    }

    [Fact]
    public async Task Another_agents_order_is_hidden_from_an_agent_and_open_to_the_sales_manager()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 20);
        var grants = new[] { (warehouse.Id, WarehouseLevel.View) };
        var owner = await _world.LoginAsAsync([Roles.SalesAgent], grants);
        var colleague = await _world.LoginAsAsync([Roles.SalesAgent], grants);
        var manager = await _world.LoginAsAsync([Roles.SalesManager], grants);
        var (order, etag) = await World.CreateSalesOrderAsync(owner, customer.Id, warehouse.Id, (product.Id, 5));
        var url = $"/api/sales/orders/{order.Id()}";

        // Same warehouse, same role, even the right ETag: not the owner, so the order does not exist for them.
        using var read = await colleague.GetAsync(url);
        await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var summary = await colleague.GetAsync(url + "/summary");
        await summary.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var edit = await colleague.PutAsync(url, new { customerId = customer.Id, warehouseId = warehouse.Id, lines = new[] { new { productId = product.Id, quantity = 1 } } }, etag);
        await edit.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var confirm = await colleague.PostAsync(url + "/confirm", null, ApiClient.NewKey(), etag);
        await confirm.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var cancel = await World.CancelSalesOrderAsync(colleague, order.Id(), etag);
        await cancel.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        var colleagueList = await colleague.GetJsonAsync($"/api/sales/orders?warehouseId={warehouse.Id}");
        Assert.Empty(colleagueList["items"]!.AsArray());

        // The manager of the warehouse sees it in full and may take the commercial decision.
        var seen = await manager.GetJsonAsync(url);
        Assert.Equal(("Full", "Draft", 500m), (seen.Str("view"), seen.Str("status"), seen["total"]!.GetValue<decimal>()));
        var managerList = await manager.GetJsonAsync($"/api/sales/orders?warehouseId={warehouse.Id}");
        Assert.Equal([order.Id()], managerList["items"]!.AsArray().Select(o => o!.Id()));
        Assert.Empty((await manager.GetJsonAsync($"/api/sales/orders?warehouseId={warehouse.Id}&mine=true"))["items"]!.AsArray());
        var (confirmed, _) = await World.ConfirmAsync(manager, order, etag);
        Assert.Equal("Confirmed", confirmed.Str("status"));

        // Still the owner's order: the owner reads the confirmed state, the colleague still sees nothing.
        Assert.Equal("Confirmed", (await owner.GetJsonAsync(url)).Str("status"));
        using var stillHidden = await colleague.GetAsync(url);
        await stillHidden.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal((20, 5), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task System_admin_never_receives_commercial_or_stock_data(bool withSuperviseGrant)
    {
        var site = await SiteAsync();
        var before = await StockStateAsync(site);
        var grants = withSuperviseGrant
            ? new[] { (site.Warehouse.Id, WarehouseLevel.Supervise), (site.TransferTarget.Id, WarehouseLevel.Supervise) }
            : [];
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin], grants, poLimit: 1_000_000m);
        var denied = new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound };
        string[] secrets = [site.ConfirmedOrder.Str("number"), site.ApprovedPurchase.Str("number"), site.Adjustment.Str("number"), site.Transfer.Str("number"), "250.5"];

        var requests = ReadUrls(site).Select(url => (Name: "GET " + url, Send: (Func<ApiClient, Task<HttpResponseMessage>>)(c => c.GetAsync(url))))
            .Append(("GET all sales orders", c => c.GetAsync("/api/sales/orders")))
            .Append(("GET all purchase orders", c => c.GetAsync("/api/purchasing/orders")))
            .Concat(Writes(site))
            .Append(("create sales order", c => c.PostAsync("/api/sales/orders", new { customerId = site.Customer.Id, warehouseId = site.Warehouse.Id, lines = new[] { new { productId = site.Product.Id, quantity = 1 } } }, ApiClient.NewKey())))
            .Append(("create purchase order", c => c.PostAsync("/api/purchasing/orders", new { supplierId = site.Supplier.Id, warehouseId = site.Warehouse.Id, lines = new[] { new { productId = site.Product.Id, quantity = 1, unitCost = 1.00m } } }, ApiClient.NewKey())));

        foreach (var (name, send) in requests)
        {
            var reply = await Reply.ReadAsync(send(admin));
            Assert.True(denied.Contains(reply.Status), $"{name} returned {(int)reply.Status}: {reply.Text}");
            Assert.True(reply.IsProblem, name);
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, reply.Text, StringComparison.Ordinal));
        }

        // What the admin is for still works: master data with prices, and user administration.
        var products = await admin.GetJsonAsync($"/api/products?search={site.Product.Sku}");
        Assert.Equal(250.50m, Assert.Single(products["items"]!.AsArray())!["listPrice"]!.GetValue<decimal>());
        using var users = await admin.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);

        Assert.Equal(before, await StockStateAsync(site));
        await _world.AssertReconciledAsync(site.Warehouse.Id, site.Product.Id);
    }

    [Fact]
    public async Task Warehouse_operator_gets_the_fulfilment_view_of_confirmed_documents_and_no_drafts()
    {
        var site = await SiteAsync();
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(site.Warehouse.Id, WarehouseLevel.Operate)]);
        string[] commercial = ["unitPrice", "lineTotal", "total", "customerTaxNumber", "unitCost", "note", "cancelReason"];

        // ---- sales order ----
        using var salesResponse = await operatorClient.GetAsync($"/api/sales/orders/{site.ConfirmedOrder.Id()}");
        var sales = await salesResponse.JsonAsync(HttpStatusCode.OK);
        var salesText = await salesResponse.Content.ReadAsStringAsync();
        Assert.Equal(("Fulfilment", "PartiallyShipped"), (sales.Str("view"), sales.Str("status")));
        Assert.All(commercial, field => Assert.DoesNotContain($"\"{field}\"", salesText, StringComparison.Ordinal));
        Assert.DoesNotContain("250.5", salesText, StringComparison.Ordinal);
        Assert.DoesNotContain(site.Customer.TaxNumber, salesText, StringComparison.Ordinal);
        var line = sales["lines"]![0]!;
        Assert.Equal((site.Product.Sku, 10, 1, 9), (line.Str("sku"), line.Number("orderedQty"), line.Number("shippedQty"), line.Number("openQty")));
        Assert.Equal(site.Customer.Address, sales.Str("shippingAddress"));

        using var draft = await operatorClient.GetAsync($"/api/sales/orders/{site.DraftOrder.Id()}");
        await draft.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var salesListResponse = await operatorClient.GetAsync($"/api/sales/orders?warehouseId={site.Warehouse.Id}");
        var salesList = await salesListResponse.JsonAsync(HttpStatusCode.OK);
        Assert.Equal([site.ConfirmedOrder.Id()], salesList["items"]!.AsArray().Select(o => o!.Id()));
        Assert.DoesNotContain("\"total\"", await salesListResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty((await operatorClient.GetJsonAsync($"/api/sales/orders?warehouseId={site.Warehouse.Id}&status=Draft"))["items"]!.AsArray());

        // ---- purchase order ----
        using var purchaseResponse = await operatorClient.GetAsync($"/api/purchasing/orders/{site.ApprovedPurchase.Id()}");
        var purchase = await purchaseResponse.JsonAsync(HttpStatusCode.OK);
        var purchaseText = await purchaseResponse.Content.ReadAsStringAsync();
        Assert.Equal(("Fulfilment", "PartiallyReceived"), (purchase.Str("view"), purchase.Str("status")));
        Assert.All(commercial, field => Assert.DoesNotContain($"\"{field}\"", purchaseText, StringComparison.Ordinal));
        Assert.Equal((20, 1, 19), (purchase["lines"]![0]!.Number("orderedQty"), purchase["lines"]![0]!.Number("receivedQty"), purchase["lines"]![0]!.Number("openQty")));

        using var draftPurchase = await operatorClient.GetAsync($"/api/purchasing/orders/{site.DraftPurchase.Id()}");
        await draftPurchase.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var purchaseListResponse = await operatorClient.GetAsync($"/api/purchasing/orders?warehouseId={site.Warehouse.Id}");
        var purchaseList = await purchaseListResponse.JsonAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(site.DraftPurchase.Id(), purchaseList["items"]!.AsArray().Select(o => o!.Id()));
        Assert.Contains(site.ApprovedPurchase.Id(), purchaseList["items"]!.AsArray().Select(o => o!.Id()));
        Assert.DoesNotContain("\"total\"", await purchaseListResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The operator fulfils but takes no commercial decision, and cannot open documents.
        using var cancel = await World.CancelSalesOrderAsync(operatorClient, site.ConfirmedOrder.Id(), salesResponse.ETag());
        await cancel.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var cancelPurchase = await World.CancelPurchaseOrderAsync(operatorClient, site.ApprovedPurchase.Id(), purchaseResponse.ETag());
        await cancelPurchase.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var create = await operatorClient.PostAsync("/api/sales/orders", new { customerId = site.Customer.Id, warehouseId = site.Warehouse.Id, lines = new[] { new { productId = site.Product.Id, quantity = 1 } } }, ApiClient.NewKey());
        await create.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        // With View only the operator reads stock but no documents.
        var viewer = await _world.LoginAsAsync([Roles.WarehouseOperator], [(site.Warehouse.Id, WarehouseLevel.View)]);
        using var viewerSales = await viewer.GetAsync($"/api/sales/orders/{site.ConfirmedOrder.Id()}");
        await viewerSales.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var viewerPurchase = await viewer.GetAsync($"/api/purchasing/orders/{site.ApprovedPurchase.Id()}");
        await viewerPurchase.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var viewerStock = await viewer.GetAsync($"/api/inventory/balances?warehouseId={site.Warehouse.Id}");
        Assert.Equal(HttpStatusCode.OK, viewerStock.StatusCode);
    }

    [Fact]
    public async Task Customers_suppliers_and_list_prices_are_readable_only_by_the_roles_that_need_them()
    {
        var product = await _world.ProductAsync(price: 321.09m);
        var customer = await _world.CustomerAsync();
        await _world.SupplierAsync();

        var expectations = new (string Role, bool Customers, bool Suppliers, bool Prices)[]
        {
            (Roles.SalesAgent, true, false, true),
            (Roles.SalesManager, true, false, true),
            (Roles.PurchasingAgent, false, true, false),
            (Roles.WarehouseOperator, false, false, false),
            (Roles.SupportAgent, false, false, false),
            (Roles.SupportManager, false, false, false),
            (Roles.SystemAdmin, true, true, true),
        };

        foreach (var (role, customers, suppliers, prices) in expectations)
        {
            var client = await _world.LoginAsAsync([role]);

            var customerList = await Reply.ReadAsync(client.GetAsync("/api/customers"));
            var customerDetail = await Reply.ReadAsync(client.GetAsync($"/api/customers/{customer.Id}"));
            var supplierList = await Reply.ReadAsync(client.GetAsync("/api/suppliers"));
            Assert.True(customerList.Status == (customers ? HttpStatusCode.OK : HttpStatusCode.Forbidden), $"{role}: customers {(int)customerList.Status}");
            Assert.True(customerDetail.Status == (customers ? HttpStatusCode.OK : HttpStatusCode.Forbidden), $"{role}: customer {(int)customerDetail.Status}");
            Assert.True(supplierList.Status == (suppliers ? HttpStatusCode.OK : HttpStatusCode.Forbidden), $"{role}: suppliers {(int)supplierList.Status}");
            if (!customers)
            {
                Assert.Equal("forbidden", customerList.Problem);
                Assert.Equal("forbidden", customerDetail.Problem);
                Assert.DoesNotContain(customer.TaxNumber, customerDetail.Text, StringComparison.Ordinal);
            }

            if (!suppliers)
            {
                Assert.Equal("forbidden", supplierList.Problem);
            }

            // Everyone reads the product and warehouse lists; the price member exists only for those who may see it.
            var productList = await Reply.ReadAsync(client.GetAsync($"/api/products?search={product.Sku}"));
            var productDetail = await Reply.ReadAsync(client.GetAsync($"/api/products/{product.Id}"));
            Assert.Equal(HttpStatusCode.OK, productList.Status);
            Assert.Equal(product.Sku, Assert.Single(productList.Json["items"]!.AsArray())!.Str("sku"));
            foreach (var reply in new[] { productList, productDetail })
            {
                Assert.True(prices == reply.Text.Contains("\"listPrice\"", StringComparison.Ordinal), $"{role}: listPrice member");
                Assert.True(prices == reply.Text.Contains("321.09", StringComparison.Ordinal), $"{role}: price value");
            }

            using var warehouses = await client.GetAsync("/api/warehouses");
            Assert.Equal(HttpStatusCode.OK, warehouses.StatusCode);
        }
    }

    [Fact]
    public async Task Acting_needs_the_acting_role_and_level_visible_gives_403_invisible_gives_404()
    {
        var site = await SiteAsync();
        var before = await StockStateAsync(site);
        var here = site.Warehouse.Id;
        var there = site.TransferTarget.Id;
        var writes = Writes(site).ToDictionary(w => w.Name, w => w.Send);
        const HttpStatusCode Forbidden = HttpStatusCode.Forbidden;
        const HttpStatusCode NotFound = HttpStatusCode.NotFound;

        var cases = new (string Who, string[] Roles, (Guid, WarehouseLevel)[] Grants, decimal Limit, (string Write, HttpStatusCode Status)[] Expected)[]
        {
            // Sees the sales order in full, but shipping is the warehouse's act; sees stock, but may not move or correct it.
            ("sales manager", [Roles.SalesManager], [(here, WarehouseLevel.Supervise), (there, WarehouseLevel.Supervise)], 0,
                [("ship", Forbidden), ("adjust", Forbidden), ("transfer out", Forbidden), ("receive", NotFound), ("approve purchase draft", NotFound)]),

            // Sees the purchase order in full, but receiving is the warehouse's act; sales documents do not exist for them.
            ("purchasing agent", [Roles.PurchasingAgent], [(here, WarehouseLevel.Supervise), (there, WarehouseLevel.Supervise)], 1_000_000m,
                [("receive", Forbidden), ("adjust", Forbidden), ("transfer out", Forbidden), ("ship", NotFound), ("confirm sales draft", NotFound)]),

            // View: the operator reads stock but not documents, so ship and receive find nothing; the warehouse itself is visible.
            ("operator with View", [Roles.WarehouseOperator], [(here, WarehouseLevel.View), (there, WarehouseLevel.View)], 0,
                [("ship", NotFound), ("receive", NotFound), ("adjust", Forbidden), ("transfer out", Forbidden)]),

            // Operate: everything except the supervisor's correction, and a transfer needs Operate on the far side too.
            ("operator with Operate here, View there", [Roles.WarehouseOperator], [(here, WarehouseLevel.Operate), (there, WarehouseLevel.View)], 0,
                [("adjust", Forbidden), ("transfer out", Forbidden), ("cancel sales order", Forbidden), ("cancel purchase order", Forbidden)]),
            ("operator with Operate here only", [Roles.WarehouseOperator], [(here, WarehouseLevel.Operate)], 0,
                [("transfer out", NotFound)]),
            ("operator with Supervise there only", [Roles.WarehouseOperator], [(there, WarehouseLevel.Supervise)], 0,
                [("ship", NotFound), ("receive", NotFound), ("adjust", NotFound), ("transfer out", NotFound)]),

            // Support roles have no stock or document rights whatever grant they hold.
            ("support manager", [Roles.SupportManager], [(here, WarehouseLevel.Supervise), (there, WarehouseLevel.Supervise)], 0,
                [("ship", NotFound), ("receive", NotFound), ("adjust", Forbidden), ("transfer out", Forbidden)]),
        };

        foreach (var (who, roles, grants, limit, expected) in cases)
        {
            var client = await _world.LoginAsAsync(roles, grants, limit);
            foreach (var (write, status) in expected)
            {
                var reply = await Reply.ReadAsync(writes[write](client));
                Assert.True(reply.Status == status, $"{who} / {write}: expected {(int)status}, got {(int)reply.Status}: {reply.Text}");
                Assert.Equal(status == Forbidden ? "forbidden" : "not_found", reply.Problem);
            }
        }

        // Creating documents: the wrong role is forbidden; the right role without the warehouse finds no warehouse.
        var salesBody = new { customerId = site.Customer.Id, warehouseId = here, lines = new[] { new { productId = site.Product.Id, quantity = 1 } } };
        var purchaseBody = new { supplierId = site.Supplier.Id, warehouseId = here, lines = new[] { new { productId = site.Product.Id, quantity = 1, unitCost = 1.00m } } };
        var buyerHere = await _world.LoginAsAsync([Roles.PurchasingAgent], [(here, WarehouseLevel.View)]);
        var agentHere = await _world.LoginAsAsync([Roles.SalesAgent], [(here, WarehouseLevel.View)]);
        var agentThere = await _world.LoginAsAsync([Roles.SalesAgent], [(there, WarehouseLevel.Supervise)]);
        var buyerThere = await _world.LoginAsAsync([Roles.PurchasingAgent], [(there, WarehouseLevel.Supervise)]);
        using var salesByBuyer = await buyerHere.PostAsync("/api/sales/orders", salesBody, ApiClient.NewKey());
        await salesByBuyer.ProblemAsync(Forbidden, "forbidden");
        using var purchaseByAgent = await agentHere.PostAsync("/api/purchasing/orders", purchaseBody, ApiClient.NewKey());
        await purchaseByAgent.ProblemAsync(Forbidden, "forbidden");
        using var salesElsewhere = await agentThere.PostAsync("/api/sales/orders", salesBody, ApiClient.NewKey());
        await salesElsewhere.ProblemAsync(NotFound, "not_found");
        using var purchaseElsewhere = await buyerThere.PostAsync("/api/purchasing/orders", purchaseBody, ApiClient.NewKey());
        await purchaseElsewhere.ProblemAsync(NotFound, "not_found");

        Assert.Equal(before, await StockStateAsync(site));
        Assert.Equal((1, 0), await _world.BalanceAsync(there, site.Product.Id));

        // The positive side of each boundary, so the refusals above are not an accident of the fixture.
        var fullOperator = await _world.LoginAsAsync([Roles.WarehouseOperator], [(here, WarehouseLevel.Supervise), (there, WarehouseLevel.Operate)]);
        foreach (var write in new[] { "ship", "receive", "adjust", "transfer out" })
        {
            var reply = await Reply.ReadAsync(writes[write](fullOperator));
            Assert.True(reply.Status == HttpStatusCode.Created, $"{write}: {(int)reply.Status} {reply.Text}");
        }

        await _world.AssertReconciledAsync(here, site.Product.Id);
        await _world.AssertReconciledAsync(there, site.Product.Id);
    }

    [Fact]
    public async Task A_removed_or_lowered_warehouse_grant_is_gone_for_the_next_session()
    {
        var site = await SiteAsync();
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var user = await _world.UserAsync([Roles.WarehouseOperator], [(site.Warehouse.Id, WarehouseLevel.Operate)]);
        var session = await _world.LoginAsync(user);
        var orderUrl = $"/api/sales/orders/{site.ConfirmedOrder.Id()}";
        var purchaseUrl = $"/api/purchasing/orders/{site.ApprovedPurchase.Id()}";
        var stockUrl = $"/api/inventory/balances?warehouseId={site.Warehouse.Id}";
        foreach (var url in new[] { orderUrl, purchaseUrl, stockUrl })
        {
            using var allowed = await session.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // ---- lowered to View: stock stays readable, documents and fulfilment are gone ----
        await World.SetGrantsAsync(admin, user.Id, (site.Warehouse.Id, WarehouseLevel.View));
        using var oldSession = await session.GetAsync(orderUrl);
        Assert.Contains(oldSession.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });

        var lowered = await _world.LoginAsync(user);
        Assert.Equal("View", Assert.Single((await lowered.GetJsonAsync("/api/auth/me"))["warehouses"]!.AsArray())!.Str("level"));
        using var loweredOrder = await lowered.GetAsync(orderUrl);
        await loweredOrder.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var loweredPurchase = await lowered.GetAsync(purchaseUrl);
        await loweredPurchase.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var loweredStock = await lowered.GetAsync(stockUrl);
        Assert.Equal(HttpStatusCode.OK, loweredStock.StatusCode);
        using var loweredShip = await World.ShipAsync(lowered, site.ConfirmedOrder.Id(), _world.Reference("SEVK"), (site.ConfirmedOrder["lines"]![0]!.Id(), 1));
        await loweredShip.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        // ---- removed: the warehouse and everything in it no longer exist for this user ----
        await World.SetGrantsAsync(admin, user.Id);
        using var afterRemoval = await lowered.GetAsync(stockUrl);
        Assert.Contains(afterRemoval.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });

        var fresh = await _world.LoginAsync(user);
        Assert.Empty((await fresh.GetJsonAsync("/api/auth/me"))["warehouses"]!.AsArray());
        foreach (var url in new[] { orderUrl, purchaseUrl, stockUrl, $"/api/inventory/movements?warehouseId={site.Warehouse.Id}" })
        {
            using var gone = await fresh.GetAsync(url);
            await gone.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        using var receive = await World.ReceiveAsync(fresh, site.ApprovedPurchase.Id(), _world.Reference("IRS"), (site.ApprovedPurchase["lines"]![0]!.Id(), 1));
        await receive.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        // The change is on the audit trail.
        Assert.Equal(2, await _world.CountAsync(
            "SELECT COUNT(*) FROM ops.AuditEntries WHERE EntityId = @u AND Action = 'admin.grants_changed';", new Microsoft.Data.SqlClient.SqlParameter("@u", user.Id)));
    }
}
