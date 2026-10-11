using System.Net;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Xunit;

namespace Kantar.IntegrationTests.Flows;

/// <summary>The product's main path end to end: PO → approval → partial receipt → stock → reservation → partial dispatch → cancel remaining.</summary>
public sealed class ProcureToShipFlowTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    [Fact]
    public async Task Purchase_receive_reserve_ship_and_cancel_keep_balances_and_journals_consistent()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync(price: 250.50m);
        var supplier = await _world.SupplierAsync();
        var customer = await _world.CustomerAsync();
        var grants = new[] { (warehouse.Id, WarehouseLevel.View) };
        var buyer = await _world.LoginAsAsync([Roles.PurchasingAgent], grants, poLimit: 0);
        var approver = await _world.LoginAsAsync([Roles.PurchasingAgent], grants, poLimit: 50_000m);
        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], grants);

        // --- purchase order: draft by a buyer without approval limit ---
        using var created = await buyer.PostAsync("/api/purchasing/orders", new
        {
            supplierId = supplier.Id,
            warehouseId = warehouse.Id,
            lines = new[] { new { productId = product.Id, quantity = 100, unitCost = 120.00m } },
        }, ApiClient.NewKey());
        var po = await created.JsonAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", po.Str("status"));
        var poLine = po["lines"]![0]!.Id();

        using var refused = await buyer.PostAsync($"/api/purchasing/orders/{po.Id()}/approve", null, ApiClient.NewKey(), created.ETag());
        await refused.ProblemAsync(HttpStatusCode.Forbidden, "approval_limit_exceeded");

        using var approved = await approver.PostAsync($"/api/purchasing/orders/{po.Id()}/approve", null, ApiClient.NewKey(), created.ETag());
        Assert.Equal("Approved", (await approved.JsonAsync(HttpStatusCode.OK)).Str("status"));
        Assert.Equal((0, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        // --- partial receipt by the warehouse ---
        using var firstReceipt = await operatorClient.PostAsync($"/api/purchasing/orders/{po.Id()}/receipts", new
        {
            externalReference = "IRS-FLOW-1-" + po.Str("number"),
            lines = new[] { new { orderLineId = poLine, quantity = 60 } },
        }, ApiClient.NewKey());
        var receipt = await firstReceipt.JsonAsync(HttpStatusCode.Created);
        Assert.Equal("PartiallyReceived", receipt.Str("purchaseOrderStatus"));
        Assert.Equal((60, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        // The operator sees quantities but no costs.
        var operatorView = await operatorClient.GetJsonAsync($"/api/purchasing/orders/{po.Id()}");
        Assert.Equal("Fulfilment", operatorView.Str("view"));
        Assert.Null(operatorView["total"]);
        Assert.Null(operatorView["lines"]![0]!["unitCost"]);
        Assert.Equal(40, operatorView["lines"]![0]!.Number("openQty"));

        // --- sales order: price comes from the server, confirmation reserves stock ---
        var (order, draftETag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 50));
        Assert.Equal(250.50m, order["lines"]![0]!["unitPrice"]!.GetValue<decimal>());
        Assert.Equal(12525.00m, order["total"]!.GetValue<decimal>());
        var (confirmed, _) = await World.ConfirmAsync(sales, order, draftETag);
        Assert.Equal("Confirmed", confirmed.Str("status"));
        Assert.Equal((60, 50), await _world.BalanceAsync(warehouse.Id, product.Id));
        var soLine = confirmed["lines"]![0]!.Id();

        // --- partial dispatch ---
        using var shipped = await operatorClient.PostAsync($"/api/sales/orders/{order.Id()}/shipments", new
        {
            dispatchReference = "SEVK-FLOW-1",
            lines = new[] { new { orderLineId = soLine, quantity = 20 } },
        }, ApiClient.NewKey());
        var shipment = await shipped.JsonAsync(HttpStatusCode.Created);
        Assert.Equal("PartiallyShipped", shipment.Str("salesOrderStatus"));
        Assert.Equal((40, 30), await _world.BalanceAsync(warehouse.Id, product.Id));

        // --- cancel the remainder: reservation released, shipped stock does not come back ---
        var current = await sales.GetAsync($"/api/sales/orders/{order.Id()}");
        using var cancelled = await sales.PostAsync($"/api/sales/orders/{order.Id()}/cancel", new { reason = "Müşteri kalan miktarı istemedi" }, ApiClient.NewKey(), current.ETag());
        var final = await cancelled.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", final.Str("status"));
        Assert.Equal(20, final["lines"]![0]!.Number("shippedQty"));
        Assert.Equal(30, final["lines"]![0]!.Number("cancelledQty"));
        Assert.Equal(0, final["lines"]![0]!.Number("openQty"));
        Assert.Equal((40, 0), await _world.BalanceAsync(warehouse.Id, product.Id));

        // --- journals: one receipt (+60), one shipment (-20); reservations +50, -20, -30 ---
        var movements = await sales.GetJsonAsync($"/api/inventory/movements?warehouseId={warehouse.Id}&productId={product.Id}");
        var deltas = movements["items"]!.AsArray().Select(m => (m!.Str("type"), m!.Number("quantityDelta"), m!.Number("onHandAfter"))).ToArray();
        Assert.Equal([("Shipment", -20, 40), ("Receipt", 60, 60)], deltas);
        var reservations = await sales.GetJsonAsync($"/api/inventory/reservations?warehouseId={warehouse.Id}&productId={product.Id}");
        var entries = reservations["items"]!.AsArray().Select(r => (r!.Str("reason"), r!.Number("quantityDelta"), r!.Number("reservedAfter"))).ToArray();
        Assert.Equal([("ReleaseOnCancel", -30, 0), ("ReleaseOnShipment", -20, 30), ("Reserve", 50, 50)], entries);
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);

        // --- the closed order is immutable ---
        using var afterClose = await sales.GetAsync($"/api/sales/orders/{order.Id()}");
        using var shipAgain = await operatorClient.PostAsync($"/api/sales/orders/{order.Id()}/shipments", new
        {
            dispatchReference = "SEVK-FLOW-2",
            lines = new[] { new { orderLineId = soLine, quantity = 1 } },
        }, ApiClient.NewKey());
        await shipAgain.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        using var editClosed = await sales.PutAsync($"/api/sales/orders/{order.Id()}", new
        {
            customerId = customer.Id,
            warehouseId = warehouse.Id,
            lines = new[] { new { productId = product.Id, quantity = 1 } },
        }, afterClose.ETag());
        await editClosed.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal((40, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
    }

    [Fact]
    public async Task Snapshots_survive_later_master_data_changes()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync(price: 80m);
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 10);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 2));
        await World.ConfirmAsync(sales, order, etag);

        using var productNow = await admin.GetAsync($"/api/products/{product.Id}");
        using var repriced = await admin.PutAsync($"/api/products/{product.Id}", new { sku = product.Sku, name = "Yeni Ad", unit = "ADET", listPrice = 999.99m, reorderLevel = 0 }, productNow.ETag());
        Assert.Equal(HttpStatusCode.OK, repriced.StatusCode);
        using var customerNow = await admin.GetAsync($"/api/customers/{customer.Id}");
        using var renamed = await admin.PutAsync($"/api/customers/{customer.Id}", new { code = customer.Code, name = "Değişmiş Unvan", taxNumber = "1234567890", address = "Yeni adres satırı" }, customerNow.ETag());
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using var deactivated = await admin.PostAsync($"/api/products/{product.Id}/deactivate");
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var after = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal(80m, after["lines"]![0]!["unitPrice"]!.GetValue<decimal>());
        Assert.Equal(160m, after["total"]!.GetValue<decimal>());
        Assert.Equal(customer.Name, after.Str("customerName"));
        Assert.Equal(customer.Address, after.Str("shippingAddress"));
        Assert.Equal(product.Name, after["lines"]![0]!.Str("productName"));

        // A passive product cannot enter a new document, but the confirmed commitment still ships.
        using var newOrder = await sales.PostAsync("/api/sales/orders", new
        {
            customerId = customer.Id,
            warehouseId = warehouse.Id,
            lines = new[] { new { productId = product.Id, quantity = 1 } },
        }, ApiClient.NewKey());
        await newOrder.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");

        var operatorClient = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        using var shipped = await operatorClient.PostAsync($"/api/sales/orders/{order.Id()}/shipments", new
        {
            dispatchReference = "SEVK-SNAP-1",
            lines = new[] { new { orderLineId = after["lines"]![0]!.Id(), quantity = 2 } },
        }, ApiClient.NewKey());
        Assert.Equal("Shipped", (await shipped.JsonAsync(HttpStatusCode.Created)).Str("salesOrderStatus"));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }
}
