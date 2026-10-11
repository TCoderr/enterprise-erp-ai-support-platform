using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.Infrastructure.Persistence;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kantar.IntegrationTests.Flows;

/// <summary>
/// Documents name the people behind their user ids: the owner of an order and whoever posted a
/// shipment, receipt, transfer or adjustment. The name is looked up when the document is read, both
/// for the single document and (in one query per page) for lists.
/// </summary>
public sealed class DisplayNameTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    private async Task<(User User, ApiClient Client, string Name)> PersonAsync(
        string name, string[] roles, (Guid WarehouseId, WarehouseLevel Level)[] grants, decimal poLimit = 0)
    {
        var user = await _world.UserAsync(roles, grants, poLimit);
        var displayName = $"{name} {_tag}";
        await RenameAsync(user.Id, displayName);
        return (user, await _world.LoginAsync(user), displayName);
    }

    private async Task RenameAsync(Guid userId, string displayName)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KantarDbContext>();
        Assert.Equal(1, await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, displayName)));
    }

    private static async Task<JsonNode> ListItemAsync(ApiClient client, string url, Guid id)
    {
        var page = await client.GetJsonAsync(url);
        return page["items"]!.AsArray().Single(i => i!.Id() == id)!;
    }

    [Fact]
    public async Task Purchase_orders_and_receipts_name_their_owner_and_receiver_in_every_view()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var supplier = await _world.SupplierAsync();
        var buyer = await PersonAsync("Ayşe Satınalma", [Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var approver = await PersonAsync("Burak Onay", [Roles.PurchasingAgent], [(warehouse.Id, WarehouseLevel.View)], poLimit: 1_000_000m);
        var keeper = await PersonAsync("Mehmet Depo", [Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);

        var (order, etag) = await World.CreatePurchaseOrderAsync(buyer.Client, supplier.Id, warehouse.Id, (product.Id, 10));
        Assert.Equal((buyer.User.Id, buyer.Name), (order.Id("ownerUserId"), order.Str("ownerDisplayName")));

        // Approved by someone else: the owner, and the owner's name, stay the buyer's.
        using var approved = await approver.Client.PostAsync($"/api/purchasing/orders/{order.Id()}/approve", null, ApiClient.NewKey(), etag);
        Assert.Equal(buyer.Name, (await approved.JsonAsync(HttpStatusCode.OK)).Str("ownerDisplayName"));
        Assert.Equal(buyer.Name, (await ListItemAsync(approver.Client, $"/api/purchasing/orders?warehouseId={warehouse.Id}", order.Id())).Str("ownerDisplayName"));

        // The fulfilment view hides costs, not who placed the order.
        var fulfilment = await keeper.Client.GetJsonAsync($"/api/purchasing/orders/{order.Id()}");
        Assert.Equal(("Fulfilment", buyer.Name), (fulfilment.Str("view"), fulfilment.Str("ownerDisplayName")));
        Assert.Null(fulfilment["total"]);
        var listed = await ListItemAsync(keeper.Client, $"/api/purchasing/orders?warehouseId={warehouse.Id}", order.Id());
        Assert.Equal(buyer.Name, listed.Str("ownerDisplayName"));
        Assert.Null(listed["total"]);

        var lineId = fulfilment["lines"]![0]!.Id();
        var key = ApiClient.NewKey();
        var body = World.ReceiptBody(_world.Reference("IRS"), (lineId, 4));
        using var received = await keeper.Client.PostAsync($"/api/purchasing/orders/{order.Id()}/receipts", body, key);
        var receipt = await received.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((keeper.User.Id, keeper.Name), (receipt.Id("createdBy"), receipt.Str("createdByDisplayName")));

        var read = await buyer.Client.GetJsonAsync($"/api/purchasing/orders/{order.Id()}/receipts/{receipt.Id()}");
        Assert.Equal((keeper.User.Id, keeper.Name), (read.Id("createdBy"), read.Str("createdByDisplayName")));

        // A replay is the stored response byte for byte, new field included.
        using var replayed = await keeper.Client.PostAsync($"/api/purchasing/orders/{order.Id()}/receipts", body, key);
        Assert.Equal("true", replayed.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(await received.Content.ReadAsByteArrayAsync(), await replayed.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Sales_orders_and_shipments_name_their_owner_and_dispatcher_in_every_view()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 20);
        var seller = await PersonAsync("Elif Satış", [Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var lead = await PersonAsync("Can Müdür", [Roles.SalesManager], [(warehouse.Id, WarehouseLevel.View)]);
        var keeper = await PersonAsync("Mehmet Depo", [Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);

        var (order, etag) = await World.CreateSalesOrderAsync(seller.Client, customer.Id, warehouse.Id, (product.Id, 6));
        Assert.Equal((seller.User.Id, seller.Name), (order.Id("ownerUserId"), order.Str("ownerDisplayName")));

        // Confirmed by the manager: still the seller's order.
        var (confirmed, _) = await World.ConfirmAsync(lead.Client, order, etag);
        Assert.Equal((seller.User.Id, seller.Name), (confirmed.Id("ownerUserId"), confirmed.Str("ownerDisplayName")));
        foreach (var client in new[] { seller.Client, lead.Client, keeper.Client })
        {
            Assert.Equal(seller.Name, (await client.GetJsonAsync($"/api/sales/orders/{order.Id()}")).Str("ownerDisplayName"));
            Assert.Equal(seller.Name, (await ListItemAsync(client, $"/api/sales/orders?warehouseId={warehouse.Id}", order.Id())).Str("ownerDisplayName"));
        }

        var fulfilment = await keeper.Client.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal("Fulfilment", fulfilment.Str("view"));
        Assert.Null(fulfilment["total"]);

        using var shipped = await World.ShipAsync(keeper.Client, order.Id(), _world.Reference("SEVK"), (confirmed["lines"]![0]!.Id(), 2));
        var shipment = await shipped.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((keeper.User.Id, keeper.Name), (shipment.Id("createdBy"), shipment.Str("createdByDisplayName")));
        var read = await seller.Client.GetJsonAsync($"/api/sales/orders/{order.Id()}/shipments/{shipment.Id()}");
        Assert.Equal((keeper.User.Id, keeper.Name), (read.Id("createdBy"), read.Str("createdByDisplayName")));

        // The name is read with the document, not frozen into it.
        await RenameAsync(seller.User.Id, seller.Name + " (evlendi)");
        Assert.Equal(seller.Name + " (evlendi)", (await lead.Client.GetJsonAsync($"/api/sales/orders/{order.Id()}")).Str("ownerDisplayName"));
        var summary = await lead.Client.GetJsonAsync($"/api/sales/orders/{order.Id()}/summary");
        Assert.Equal(seller.Name + " (evlendi)", summary["order"]!.Str("ownerDisplayName"));
    }

    [Fact]
    public async Task Transfers_and_adjustments_name_who_posted_them()
    {
        var source = await _world.WarehouseAsync();
        var target = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(source, product, 30);
        (Guid, WarehouseLevel)[] both = [(source.Id, WarehouseLevel.Supervise), (target.Id, WarehouseLevel.Operate)];
        var mover = await PersonAsync("Mehmet Depo", [Roles.WarehouseOperator], both);
        var counter = await PersonAsync("Zehra Sayım", [Roles.WarehouseOperator], both);
        var reader = await PersonAsync("Elif Satış", [Roles.SalesAgent], [(source.Id, WarehouseLevel.View)]);

        using var moved = await World.TransferAsync(mover.Client, _world.Reference("TRF"), source.Id, target.Id, (product.Id, 5));
        var transfer = await moved.JsonAsync(HttpStatusCode.Created);
        using var movedAgain = await World.TransferAsync(counter.Client, _world.Reference("TRF"), source.Id, target.Id, (product.Id, 1));
        var secondTransfer = await movedAgain.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((mover.User.Id, mover.Name), (transfer.Id("createdBy"), transfer.Str("createdByDisplayName")));
        Assert.Equal(counter.Name, secondTransfer.Str("createdByDisplayName"));
        Assert.Equal(mover.Name, (await reader.Client.GetJsonAsync($"/api/inventory/transfers/{transfer.Id()}")).Str("createdByDisplayName"));

        // A page with documents of two people: each row carries its own poster's name.
        var transfers = (await reader.Client.GetJsonAsync($"/api/inventory/transfers?warehouseId={source.Id}"))["items"]!.AsArray();
        Assert.Equal(
            new[] { (transfer.Id(), mover.Name), (secondTransfer.Id(), counter.Name) }.OrderBy(x => x.Item1).ToArray(),
            transfers.Select(t => (t!.Id(), t!.Str("createdByDisplayName"))).OrderBy(x => x.Item1).ToArray());

        var key = ApiClient.NewKey();
        var body = World.AdjustmentBody(_world.Reference("DZL"), source.Id, "Damage", (product.Id, -2));
        using var adjusted = await mover.Client.PostAsync("/api/inventory/adjustments", body, key);
        var adjustment = await adjusted.JsonAsync(HttpStatusCode.Created);
        using var adjustedAgain = await World.AdjustAsync(counter.Client, _world.Reference("DZL"), source.Id, "Found", (product.Id, 3));
        var secondAdjustment = await adjustedAgain.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((mover.User.Id, mover.Name), (adjustment.Id("createdBy"), adjustment.Str("createdByDisplayName")));
        Assert.Equal(mover.Name, (await reader.Client.GetJsonAsync($"/api/inventory/adjustments/{adjustment.Id()}")).Str("createdByDisplayName"));
        var adjustments = (await reader.Client.GetJsonAsync($"/api/inventory/adjustments?warehouseId={source.Id}"))["items"]!.AsArray();
        Assert.Equal(
            new[] { (adjustment.Id(), mover.Name), (secondAdjustment.Id(), counter.Name) }.OrderBy(x => x.Item1).ToArray(),
            adjustments.Select(a => (a!.Id(), a!.Str("createdByDisplayName"))).OrderBy(x => x.Item1).ToArray());

        using var replayed = await mover.Client.PostAsync("/api/inventory/adjustments", body, key);
        Assert.Equal("true", replayed.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal(await adjusted.Content.ReadAsByteArrayAsync(), await replayed.Content.ReadAsByteArrayAsync());
        await _world.AssertReconciledAsync(source.Id, product.Id);
    }
}
