using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Sales;

/// <summary>docs/02 §5.2 and §8: confirmation reserves every line or none, and never more than is available.</summary>
public sealed class ConfirmConcurrencyTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private static Task<HttpResponseMessage> ConfirmAsync(ApiClient client, Guid orderId, string? etag, string? key = null) =>
        client.PostAsync($"/api/sales/orders/{orderId}/confirm", null, key ?? ApiClient.NewKey(), etag);

    [Fact]
    public async Task Twenty_five_concurrent_confirmations_against_stock_for_ten_never_oversell()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 100);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var orders = new List<(JsonNode Order, string ETag)>();
        for (var i = 0; i < 25; i++)
        {
            orders.Add(await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 10)));
        }

        var clients = _world.Clones(sales, orders.Count);
        var replies = await Race.RunAsync(orders.Select((o, i) =>
            (Func<Task<HttpResponseMessage>>)(() => ConfirmAsync(clients[i], o.Order.Id(), o.ETag))));

        var confirmed = replies.Count(r => r.Status == HttpStatusCode.OK);
        Assert.Equal(10, confirmed);
        var rejected = replies.Where(r => r.Status != HttpStatusCode.OK).ToArray();
        Assert.Equal(15, rejected.Length);
        Assert.All(rejected, r =>
        {
            var problem = r.AssertProblem(HttpStatusCode.Conflict, "insufficient_available");
            Assert.Equal(["lines[0]"], problem["errors"]!.AsArray().Select(e => e!.Str("field")));
        });

        Assert.Equal((100, 100), await _world.BalanceAsync(warehouse.Id, product.Id));
        var balances = await sales.GetJsonAsync($"/api/inventory/balances?warehouseId={warehouse.Id}");
        var balance = Assert.Single(balances["items"]!.AsArray())!;
        Assert.Equal((100, 100, 0), (balance.Number("onHand"), balance.Number("reserved"), balance.Number("available")));
        Assert.Equal(10, await _world.ReservationCountAsync(warehouse.Id, product.Id, "Reserve"));
        Assert.Equal(10, await _world.ReservationCountAsync(warehouse.Id, product.Id));

        // Each reply and the stored order agree: a 200 is Confirmed, a 409 left the order a Draft with its old version.
        for (var i = 0; i < orders.Count; i++)
        {
            using var stored = await sales.GetAsync($"/api/sales/orders/{orders[i].Order.Id()}");
            var order = await stored.JsonAsync(HttpStatusCode.OK);
            if (replies[i].Status == HttpStatusCode.OK)
            {
                Assert.Equal("Confirmed", order.Str("status"));
                Assert.Equal(replies[i].ETag, stored.ETag());
            }
            else
            {
                Assert.Equal("Draft", order.Str("status"));
                Assert.Equal(orders[i].ETag, stored.ETag());
                Assert.Null(order["confirmedAt"]);
            }
        }

        // The ledger's running total climbs 10, 20, … 100: no two confirmations saw the same balance.
        var ledger = await sales.GetJsonAsync($"/api/inventory/reservations?warehouseId={warehouse.Id}&productId={product.Id}&limit=100");
        Assert.Equal(Enumerable.Range(1, 10).Select(i => i * 10).Reverse(), ledger["items"]!.AsArray().Select(e => e!.Number("reservedAfter")));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_multi_line_order_with_one_short_line_reserves_nothing(int shortLine)
    {
        var warehouse = await _world.WarehouseAsync();
        var (plenty, scarce) = (await _world.ProductAsync(), await _world.ProductAsync());
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, [(plenty, 10), (scarce, 3)]);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var lines = shortLine == 0
            ? new[] { (scarce.Id, 4), (plenty.Id, 5) }
            : new[] { (plenty.Id, 5), (scarce.Id, 4) };
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, lines);

        using var response = await ConfirmAsync(sales, order.Id(), etag);
        var problem = await response.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");

        // Only the short line is listed; the line that would fit is neither reported nor reserved.
        var error = Assert.Single(problem["errors"]!.AsArray())!;
        Assert.Equal(($"lines[{shortLine}]", "insufficient_available"), (error.Str("field"), error.Str("code")));
        Assert.Equal((10, 0), await _world.BalanceAsync(warehouse.Id, plenty.Id));
        Assert.Equal((3, 0), await _world.BalanceAsync(warehouse.Id, scarce.Id));
        Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, plenty.Id));
        Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, scarce.Id));

        using var stored = await sales.GetAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal("Draft", (await stored.JsonAsync(HttpStatusCode.OK)).Str("status"));
        Assert.Equal(etag, stored.ETag());
        await _world.AssertReconciledAsync(warehouse.Id, plenty.Id);
        await _world.AssertReconciledAsync(warehouse.Id, scarce.Id);
    }

    [Fact]
    public async Task Both_short_lines_are_listed_in_one_response()
    {
        var warehouse = await _world.WarehouseAsync();
        var (first, second, third) = (await _world.ProductAsync(), await _world.ProductAsync(), await _world.ProductAsync());
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, [(first, 2), (second, 50), (third, 1)]);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (first.Id, 3), (second.Id, 5), (third.Id, 2));

        using var response = await ConfirmAsync(sales, order.Id(), etag);
        var problem = await response.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");

        Assert.Equal(["lines[0]", "lines[2]"], problem["errors"]!.AsArray().Select(e => e!.Str("field")).Order());
        foreach (var product in new[] { first, second, third })
        {
            Assert.Equal(0, (await _world.BalanceAsync(warehouse.Id, product.Id)).Reserved);
            await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        }
    }

    [Fact]
    public async Task The_same_order_confirmed_ten_times_at_once_with_different_keys_is_reserved_once()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 100);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 7));
        var clients = _world.Clones(sales, 10);

        var replies = await Race.RunAsync(clients.Select(client =>
            (Func<Task<HttpResponseMessage>>)(() => ConfirmAsync(client, order.Id(), etag))));

        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
        Assert.False(winner.Replayed);
        Assert.All(replies.Where(r => r != winner), r =>
        {
            // The winner changed the version every other request was sent against.
            Assert.Contains((r.Status, r.Problem), new[]
            {
                (HttpStatusCode.PreconditionFailed, (string?)"precondition_failed"),
                (HttpStatusCode.Conflict, (string?)"invalid_state"),
            });
        });

        Assert.Equal((100, 7), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.CountAsync(
            "SELECT COUNT(*) FROM ops.AuditEntries WHERE EntityId = @o AND Action = 'sales.order_confirmed';", new SqlParameter("@o", order.Id())));
        Assert.Equal("Confirmed", (await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}")).Str("status"));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Confirm_requires_the_current_etag()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 100);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, firstETag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 5));

        using var edited = await sales.PutAsync(
            $"/api/sales/orders/{order.Id()}",
            new { customerId = customer.Id, warehouseId = warehouse.Id, lines = new[] { new { productId = product.Id, quantity = 9 } } },
            firstETag);
        await edited.JsonAsync(HttpStatusCode.OK);
        var currentETag = edited.ETag();
        Assert.NotEqual(firstETag, currentETag);

        // The agent would be confirming five units while the order now says nine.
        using var stale = await ConfirmAsync(sales, order.Id(), firstETag);
        await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        using var missing = await ConfirmAsync(sales, order.Id(), null);
        await missing.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        using var weak = await ConfirmAsync(sales, order.Id(), "W/" + currentETag);
        await weak.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        Assert.Equal((100, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(0, await _world.ReservationCountAsync(warehouse.Id, product.Id));

        using var confirmed = await ConfirmAsync(sales, order.Id(), currentETag);
        var result = await confirmed.JsonAsync(HttpStatusCode.OK);
        Assert.Equal(("Confirmed", 9), (result.Str("status"), result["lines"]![0]!.Number("orderedQty")));
        Assert.Equal((100, 9), await _world.BalanceAsync(warehouse.Id, product.Id));

        // A second confirmation with the now-current version is a state error, not a second reservation.
        using var again = await ConfirmAsync(sales, order.Id(), confirmed.ETag());
        await again.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        Assert.Equal((100, 9), await _world.BalanceAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task Replaying_a_confirmation_after_the_sales_role_was_removed_does_not_return_the_stored_response()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(warehouse, product, 100);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var agent = await _world.UserAsync([Roles.SalesAgent, Roles.SupportAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var session = await _world.LoginAsync(agent);
        var (order, etag) = await World.CreateSalesOrderAsync(session, customer.Id, warehouse.Id, (product.Id, 6));
        var key = ApiClient.NewKey();

        var first = await Reply.ReadAsync(ConfirmAsync(session, order.Id(), etag, key));
        Assert.Equal(HttpStatusCode.OK, first.Status);
        var replayWhileAllowed = await Reply.ReadAsync(ConfirmAsync(session, order.Id(), etag, key));
        Assert.True(replayWhileAllowed.Replayed);
        Assert.Equal(first.Body, replayWhileAllowed.Body);

        await World.SetRolesAsync(admin, agent.Id, Roles.SupportAgent);

        // The old session ended with the role change; even a fresh login must not read the result back.
        var withOldSession = await Reply.ReadAsync(ConfirmAsync(session, order.Id(), etag, key));
        withOldSession.AssertProblem(HttpStatusCode.Unauthorized, "unauthenticated");
        var fresh = await _world.LoginAsync(agent);
        var replay = await Reply.ReadAsync(ConfirmAsync(fresh, order.Id(), etag, key));

        Assert.Contains(replay.Status, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        Assert.True(replay.IsProblem);
        Assert.False(replay.Replayed);
        Assert.Null(replay.ETag);
        Assert.DoesNotContain(order.Str("number"), replay.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(customer.Name, replay.Text, StringComparison.Ordinal);
        using var read = await fresh.GetAsync($"/api/sales/orders/{order.Id()}");
        await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");

        Assert.Equal((100, 6), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.ReservationCountAsync(warehouse.Id, product.Id));
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }
}
