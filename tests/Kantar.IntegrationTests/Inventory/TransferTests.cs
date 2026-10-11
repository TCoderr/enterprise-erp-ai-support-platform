using System.Net;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Inventory;

/// <summary>docs/02 §4.3, §7 and §8: a transfer takes from the source and gives to the target in one transaction or not at all.</summary>
public sealed class TransferTests(KantarFixture fixture)
{
    private readonly World _world = fixture.NewWorld();

    private Task<int> DocumentCountAsync(string reference) =>
        _world.CountAsync("SELECT COUNT(*) FROM inv.StockTransfers WHERE Reference = @r;", new SqlParameter("@r", reference));

    private Task<int> MovementsOfDocumentAsync(Guid transferId, string type) => _world.CountAsync(
        "SELECT COUNT(*) FROM inv.StockMovements WHERE ReferenceType = 3 AND ReferenceId = @id AND Type = @t;",
        new SqlParameter("@id", transferId), new SqlParameter("@t", (byte)Enum.Parse<Kantar.Domain.Inventory.MovementType>(type)));

    private async Task<ApiClient> OperatorAsync(params Warehouse[] warehouses) =>
        await _world.LoginAsAsync([Roles.WarehouseOperator], warehouses.Select(w => (w.Id, WarehouseLevel.Operate)).ToArray());

    [Fact]
    public async Task A_transfer_moves_stock_with_one_out_and_one_in_movement_per_line()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var (first, second) = (await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(source, [(first, 10), (second, 6)]);
        var operatorClient = await OperatorAsync(source, target);
        var reference = _world.Reference("TR");

        using var response = await World.TransferAsync(operatorClient, reference, source.Id, target.Id, (first.Id, 4), (second.Id, 6));
        var transfer = await response.JsonAsync(HttpStatusCode.Created);

        Assert.Equal($"/api/inventory/transfers/{transfer.Id()}", response.Headers.Location?.ToString());
        Assert.StartsWith("TR-", transfer.Str("number"), StringComparison.Ordinal);
        Assert.Equal((reference, source.Id, target.Id), (transfer.Str("reference"), transfer.Id("fromWarehouseId"), transfer.Id("toWarehouseId")));
        Assert.Equal([(1, first.Id, 4), (2, second.Id, 6)], transfer["lines"]!.AsArray().Select(l => (l!.Number("lineNo"), l!.Id("productId"), l!.Number("quantity"))));

        Assert.Equal((6, 0), await _world.BalanceAsync(source.Id, first.Id));
        Assert.Equal((0, 0), await _world.BalanceAsync(source.Id, second.Id));
        Assert.Equal((4, 0), await _world.BalanceAsync(target.Id, first.Id));
        Assert.Equal((6, 0), await _world.BalanceAsync(target.Id, second.Id));

        // Two lines → exactly two TransferOut and two TransferIn rows, all pointing at this document.
        Assert.Equal(2, await MovementsOfDocumentAsync(transfer.Id(), "TransferOut"));
        Assert.Equal(2, await MovementsOfDocumentAsync(transfer.Id(), "TransferIn"));
        var outRows = await operatorClient.GetJsonAsync($"/api/inventory/movements?warehouseId={source.Id}&type=TransferOut");
        Assert.Equal(
            new[] { (first.Id, -4, 6), (second.Id, -6, 0) }.OrderBy(r => r.Item1),
            outRows["items"]!.AsArray().Select(m => (m!.Id("productId"), m!.Number("quantityDelta"), m!.Number("onHandAfter"))).OrderBy(r => r.Item1));
        var inRows = await operatorClient.GetJsonAsync($"/api/inventory/movements?warehouseId={target.Id}");
        Assert.Equal(
            new[] { (first.Id, "TransferIn", 4, 4), (second.Id, "TransferIn", 6, 6) }.OrderBy(r => r.Item1),
            inRows["items"]!.AsArray().Select(m => (m!.Id("productId"), m!.Str("type"), m!.Number("quantityDelta"), m!.Number("onHandAfter"))).OrderBy(r => r.Item1));
        Assert.All(inRows["items"]!.AsArray(), m => Assert.Equal(transfer.Str("number"), m!.Str("referenceNumber")));

        // The document is readable from either side with View on that side only.
        var targetViewer = await _world.LoginAsAsync([Roles.SalesAgent], [(target.Id, WarehouseLevel.View)]);
        Assert.Equal(reference, (await targetViewer.GetJsonAsync($"/api/inventory/transfers/{transfer.Id()}")).Str("reference"));
        var listed = await targetViewer.GetJsonAsync($"/api/inventory/transfers?warehouseId={target.Id}");
        Assert.Equal([transfer.Id()], listed["items"]!.AsArray().Select(t => t!.Id()));

        foreach (var (warehouse, product) in new[] { (source, first), (source, second), (target, first), (target, second) })
        {
            await _world.AssertReconciledAsync(warehouse.Id, product.Id);
        }
    }

    [Fact]
    public async Task Reserved_stock_cannot_be_transferred_away()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        await _world.StockAsync(source, product, 10);
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(source.Id, WarehouseLevel.View)]);
        await World.ConfirmedSalesOrderAsync(sales, customer.Id, source.Id, (product.Id, 7));
        var operatorClient = await OperatorAsync(source, target);

        var refusedReference = _world.Reference("TR");
        using var refused = await World.TransferAsync(operatorClient, refusedReference, source.Id, target.Id, (product.Id, 4));
        var problem = await refused.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");
        Assert.Equal("lines[0].quantity", problem["errors"]![0]!.Str("field"));
        Assert.Equal((10, 7), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((0, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(0, await DocumentCountAsync(refusedReference));
        Assert.Equal(0, await _world.MovementCountAsync(target.Id, product.Id));

        using var allowed = await World.TransferAsync(operatorClient, _world.Reference("TR"), source.Id, target.Id, (product.Id, 3));
        await allowed.JsonAsync(HttpStatusCode.Created);
        Assert.Equal((7, 7), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((3, 0), await _world.BalanceAsync(target.Id, product.Id));
        await _world.AssertReconciledAsync(source.Id, product.Id);
        await _world.AssertReconciledAsync(target.Id, product.Id);
    }

    [Fact]
    public async Task Source_and_target_must_differ_and_lines_are_validated()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        await _world.StockAsync(source, product, 10);
        var operatorClient = await OperatorAsync(source, target);
        var reference = _world.Reference("TR");

        using var same = await World.TransferAsync(operatorClient, reference, source.Id, source.Id, (product.Id, 1));
        var problem = await same.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal([("toWarehouseId", "same_warehouse")], problem["errors"]!.AsArray().Select(e => (e!.Str("field"), e!.Str("code"))));

        var invalid = new (string Field, (Guid, int)[] Lines)[]
        {
            ("lines", []),
            ("lines[0].quantity", [(product.Id, 0)]),
            ("lines[0].quantity", [(product.Id, -5)]),
            ("lines[1].productId", [(product.Id, 1), (product.Id, 2)]),
        };
        foreach (var (field, lines) in invalid)
        {
            using var response = await World.TransferAsync(operatorClient, reference, source.Id, target.Id, lines);
            var rejected = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
            Assert.Contains(field, rejected["errors"]!.AsArray().Select(e => e!.Str("field")));
        }

        // A passive warehouse takes part in no new transfer, in either role; an unknown product is not a line.
        var passive = await _world.WarehouseAsync(active: false);
        var everywhere = await OperatorAsync(source, target, passive);
        using var intoPassive = await World.TransferAsync(everywhere, reference, source.Id, passive.Id, (product.Id, 1));
        await intoPassive.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
        using var outOfPassive = await World.TransferAsync(everywhere, reference, passive.Id, target.Id, (product.Id, 1));
        await outOfPassive.ProblemAsync(HttpStatusCode.Conflict, "master_data_inactive");
        using var unknownProduct = await World.TransferAsync(everywhere, reference, source.Id, target.Id, (Guid.NewGuid(), 1));
        await unknownProduct.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");

        Assert.Equal(0, await DocumentCountAsync(reference));
        Assert.Equal((10, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((0, 0), await _world.BalanceAsync(passive.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id));
        Assert.Equal(0, await _world.MovementCountAsync(passive.Id, product.Id));
        await _world.AssertReconciledAsync(source.Id, product.Id);
    }

    [Fact]
    public async Task One_short_line_moves_nothing_for_any_line()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var (plenty, scarce) = (await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(source, [(plenty, 10), (scarce, 2)]);
        var operatorClient = await OperatorAsync(source, target);

        // The same request with the lines in both orders: whichever balance is touched first, the
        // posting that already succeeded must be rolled back with the one that failed.
        var references = new[] { _world.Reference("TR"), _world.Reference("TR") };
        using var shortSecond = await World.TransferAsync(operatorClient, references[0], source.Id, target.Id, (plenty.Id, 5), (scarce.Id, 3));
        var first = await shortSecond.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");
        Assert.Equal(["lines[1].quantity"], first["errors"]!.AsArray().Select(e => e!.Str("field")));
        using var shortFirst = await World.TransferAsync(operatorClient, references[1], source.Id, target.Id, (scarce.Id, 3), (plenty.Id, 5));
        var second = await shortFirst.ProblemAsync(HttpStatusCode.Conflict, "insufficient_available");
        Assert.Equal(["lines[0].quantity"], second["errors"]!.AsArray().Select(e => e!.Str("field")));

        Assert.Equal((10, 0), await _world.BalanceAsync(source.Id, plenty.Id));
        Assert.Equal((2, 0), await _world.BalanceAsync(source.Id, scarce.Id));
        foreach (var product in new[] { plenty, scarce })
        {
            Assert.Equal((0, 0), await _world.BalanceAsync(target.Id, product.Id));
            Assert.Equal(0, await _world.MovementCountAsync(target.Id, product.Id));
            Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id));
            await _world.AssertReconciledAsync(source.Id, product.Id);
            await _world.AssertReconciledAsync(target.Id, product.Id);
        }

        Assert.Equal(0, await DocumentCountAsync(references[0]) + await DocumentCountAsync(references[1]));
    }

    [Fact]
    public async Task A_failure_after_the_first_posting_leaves_no_trace_and_the_same_key_then_succeeds()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        await _world.StockAsync(source, product, 10);
        var operatorUser = await _world.UserAsync([Roles.WarehouseOperator], [(source.Id, WarehouseLevel.Operate), (target.Id, WarehouseLevel.Operate)]);
        var operatorClient = await _world.LoginAsync(operatorUser);
        var reference = _world.Reference("TR");
        var key = ApiClient.NewKey();
        var body = World.TransferBody(reference, source.Id, target.Id, (product.Id, 4));

        // The failpoint fires once the first of the two balances (source or target) has been changed
        // and its journal row queued, i.e. in the middle of the transfer.
        var hits = 0;
        var injected = fixture.Failpoints.On("stock.after-posting", _ =>
        {
            Interlocked.Increment(ref hits);
            throw new InjectedFailureException();
        });
        try
        {
            using var failed = await operatorClient.PostAsync("/api/inventory/transfers", body, key);
            var problem = await failed.ProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
            Assert.Null(problem["detail"]);
            Assert.DoesNotContain("Injected", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            injected.Dispose();
        }

        Assert.Equal(1, hits);
        Assert.Equal((10, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((0, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id));
        Assert.Equal(0, await _world.MovementCountAsync(target.Id, product.Id));
        Assert.Equal(0, await _world.CountAsync("SELECT COUNT(*) FROM inv.StockBalances WHERE WarehouseId = @w AND ProductId = @p;", new SqlParameter("@w", target.Id), new SqlParameter("@p", product.Id)));
        Assert.Equal(0, await DocumentCountAsync(reference));
        Assert.Equal(0, await _world.AuditCountAsync(operatorUser.Id, "inventory.transfer_posted"));
        Assert.Equal(0, await _world.IdempotencyRecordCountAsync(key));

        // Nothing was recorded under the key, so the retry is a first execution, not a replay.
        var retry = await Reply.ReadAsync(operatorClient.PostAsync("/api/inventory/transfers", body, key));
        Assert.Equal(HttpStatusCode.Created, retry.Status);
        Assert.False(retry.Replayed);
        Assert.Equal((6, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((4, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(1, await DocumentCountAsync(reference));
        Assert.Equal(1, await _world.AuditCountAsync(operatorUser.Id, "inventory.transfer_posted"));
        Assert.Equal(1, await _world.IdempotencyRecordCountAsync(key));
        Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id, "TransferOut"));
        Assert.Equal(1, await _world.MovementCountAsync(target.Id, product.Id, "TransferIn"));
        await _world.AssertReconciledAsync(source.Id, product.Id);
        await _world.AssertReconciledAsync(target.Id, product.Id);
    }

    [Fact]
    public async Task Twenty_parallel_transfers_of_one_unit_from_a_stock_of_ten_move_exactly_ten()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        await _world.StockAsync(source, product, 10);
        var clients = _world.Clones(await OperatorAsync(source, target), 20);
        var references = clients.Select(_ => _world.Reference("TR")).ToArray();

        var replies = await Race.RunAsync(clients.Select((client, i) =>
            (Func<Task<HttpResponseMessage>>)(() => World.TransferAsync(client, references[i], source.Id, target.Id, (product.Id, 1)))));

        Assert.Equal(10, replies.Count(r => r.Status == HttpStatusCode.Created));
        var rejected = replies.Where(r => r.Status != HttpStatusCode.Created).ToArray();
        Assert.Equal(10, rejected.Length);
        Assert.All(rejected, r => r.AssertProblem(HttpStatusCode.Conflict, "insufficient_available"));

        Assert.Equal((0, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((10, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(10, await _world.MovementCountAsync(source.Id, product.Id, "TransferOut"));
        Assert.Equal(10, await _world.MovementCountAsync(target.Id, product.Id, "TransferIn"));
        Assert.Equal(10, await _world.CountAsync("SELECT COUNT(*) FROM inv.StockTransfers WHERE FromWarehouseId = @w;", new SqlParameter("@w", source.Id)));

        // The documents that exist are exactly the ones whose requests were answered 201.
        for (var i = 0; i < replies.Length; i++)
        {
            Assert.Equal(replies[i].Status == HttpStatusCode.Created ? 1 : 0, await DocumentCountAsync(references[i]));
        }

        await _world.AssertReconciledAsync(source.Id, product.Id);
        await _world.AssertReconciledAsync(target.Id, product.Id);
    }

    [Fact]
    public async Task Opposite_direction_transfers_in_parallel_conserve_the_totals()
    {
        var (a, b) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var (p, q) = (await _world.ProductAsync(), await _world.ProductAsync());
        await _world.StockAsync(a, [(p, 50), (q, 50)]);
        await _world.StockAsync(b, [(p, 50), (q, 50)]);
        var clients = _world.Clones(await OperatorAsync(a, b), 4);
        var expected = new Dictionary<(Guid Warehouse, Guid Product), int>
        {
            [(a.Id, p.Id)] = 50, [(a.Id, q.Id)] = 50, [(b.Id, p.Id)] = 50, [(b.Id, q.Id)] = 50,
        };
        var created = 0;

        for (var round = 0; round < 6; round++)
        {
            // Two transfers each way; the lines of the second pair name the products in the opposite order.
            var plan = new (Warehouse From, Warehouse To, (Guid ProductId, int Quantity)[] Lines)[]
            {
                (a, b, [(p.Id, 3), (q.Id, 2)]),
                (b, a, [(q.Id, 3), (p.Id, 2)]),
                (a, b, [(q.Id, 1), (p.Id, 1)]),
                (b, a, [(p.Id, 4), (q.Id, 1)]),
            };

            var replies = await Race.RunAsync(plan.Select((t, i) =>
                (Func<Task<HttpResponseMessage>>)(() => World.TransferAsync(clients[i], _world.Reference("TR"), t.From.Id, t.To.Id, t.Lines))));

            for (var i = 0; i < plan.Length; i++)
            {
                var reply = replies[i];
                if (reply.Status == HttpStatusCode.Created)
                {
                    created++;
                    foreach (var (productId, quantity) in plan[i].Lines)
                    {
                        expected[(plan[i].From.Id, productId)] -= quantity;
                        expected[(plan[i].To.Id, productId)] += quantity;
                    }

                    continue;
                }

                // Stock is ample, so a refusal can only be the bounded-retry outcome; it must be a clean problem either way.
                Assert.True(reply.IsProblem, $"Round {round}: unexpected {(int)reply.Status} {reply.ContentType}");
                Assert.Contains((reply.Status, reply.Problem), new[]
                {
                    (HttpStatusCode.Conflict, (string?)"insufficient_available"),
                    (HttpStatusCode.ServiceUnavailable, (string?)"retry_later"),
                });
            }
        }

        Assert.True(created > 0);
        foreach (var ((warehouseId, productId), onHand) in expected)
        {
            Assert.Equal((onHand, 0), await _world.BalanceAsync(warehouseId, productId));
            await _world.AssertReconciledAsync(warehouseId, productId);
        }

        foreach (var product in new[] { p, q })
        {
            var (inA, _) = await _world.BalanceAsync(a.Id, product.Id);
            var (inB, _) = await _world.BalanceAsync(b.Id, product.Id);
            Assert.Equal(100, inA + inB);
        }

        // One out and one in row per line of every transfer that was answered 201, and nothing else.
        var transferRows = await _world.CountAsync(
            "SELECT COUNT(*) FROM inv.StockMovements WHERE WarehouseId IN (@a, @b) AND Type IN (3, 4);", new SqlParameter("@a", a.Id), new SqlParameter("@b", b.Id));
        Assert.Equal(created * 2 * 2, transferRows);
    }

    [Fact]
    public async Task Transfer_needs_operate_on_both_sides()
    {
        var (source, target, elsewhere) = (await _world.WarehouseAsync(), await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();
        await _world.StockAsync(source, product, 10);

        var denied = new (string Who, string[] Roles, (Guid, WarehouseLevel)[] Grants, HttpStatusCode Status)[]
        {
            ("operate on the source, view on the target", [Roles.WarehouseOperator], [(source.Id, WarehouseLevel.Operate), (target.Id, WarehouseLevel.View)], HttpStatusCode.Forbidden),
            ("view on the source, supervise on the target", [Roles.WarehouseOperator], [(source.Id, WarehouseLevel.View), (target.Id, WarehouseLevel.Supervise)], HttpStatusCode.Forbidden),
            ("operate on the source only", [Roles.WarehouseOperator], [(source.Id, WarehouseLevel.Operate)], HttpStatusCode.NotFound),
            ("operate on the target only", [Roles.WarehouseOperator], [(target.Id, WarehouseLevel.Supervise)], HttpStatusCode.NotFound),
            ("operate on an unrelated warehouse", [Roles.WarehouseOperator], [(elsewhere.Id, WarehouseLevel.Supervise)], HttpStatusCode.NotFound),
            ("sales manager with supervise on both", [Roles.SalesManager], [(source.Id, WarehouseLevel.Supervise), (target.Id, WarehouseLevel.Supervise)], HttpStatusCode.Forbidden),
            ("system admin", [Roles.SystemAdmin], [], HttpStatusCode.NotFound),
        };

        foreach (var (who, roles, grants, status) in denied)
        {
            var client = await _world.LoginAsAsync(roles, grants);
            var reference = _world.Reference("TR");
            var reply = await Reply.ReadAsync(World.TransferAsync(client, reference, source.Id, target.Id, (product.Id, 1)));
            Assert.True(reply.Status == status, $"{who}: expected {(int)status}, got {(int)reply.Status}");
            Assert.Equal(status == HttpStatusCode.Forbidden ? "forbidden" : "not_found", reply.Problem);
            Assert.Equal(0, await DocumentCountAsync(reference));
        }

        Assert.Equal((10, 0), await _world.BalanceAsync(source.Id, product.Id));
        Assert.Equal((0, 0), await _world.BalanceAsync(target.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(source.Id, product.Id));
        await _world.AssertReconciledAsync(source.Id, product.Id);
    }
}
