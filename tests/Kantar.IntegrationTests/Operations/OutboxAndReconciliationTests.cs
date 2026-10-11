using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kantar.Application.Inventory;
using Kantar.Application.Operations;
using Kantar.Application.Support;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.Domain.Operations;
using Kantar.Infrastructure.Operations;
using Kantar.Infrastructure.Persistence;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kantar.IntegrationTests.Operations;

/// <summary>
/// Outbox delivery, the read-only stock reconciliation and housekeeping. The web host under test runs
/// no worker, so the dispatcher (and, for the shutdown test, a worker instance) is driven directly
/// from a DI scope with the same restricted database login the worker process uses.
/// </summary>
public sealed partial class OutboxAndReconciliationTests(KantarFixture fixture)
{
    private const string LowStockHandler = "low-stock-notifier";
    private const string AssigneeHandler = "assignee-notifier";

    private readonly World _world = fixture.NewWorld();

    private DateTime Now => fixture.Clock.GetUtcNow().UtcDateTime;

    private static string NewOwner() => "test-" + Guid.NewGuid().ToString("N");

    // ------------------------------------------------------------------ plumbing

    private async Task<T> ScopedAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private Task<T> DbAsync<T>(Func<KantarDbContext, Task<T>> work) => ScopedAsync(services => work(services.GetRequiredService<KantarDbContext>()));

    private Task<IReadOnlyList<LeasedMessage>> LeaseAsync(string owner) =>
        ScopedAsync(services => services.GetRequiredService<OutboxDispatcher>().LeaseAsync(owner, 200, CancellationToken.None));

    private Task<bool> ProcessAsync(string owner, LeasedMessage message) =>
        ScopedAsync(services => services.GetRequiredService<OutboxDispatcher>().ProcessAsync(owner, message, CancellationToken.None));

    /// <summary>
    /// Delivers whatever earlier tests left in the shared queue (they run no worker), so that the
    /// messages a test leases afterwards are the ones it produced itself.
    /// </summary>
    private async Task DrainAsync()
    {
        var owner = NewOwner();
        for (var round = 0; round < 200; round++)
        {
            var leased = await LeaseAsync(owner);
            if (leased.Count == 0)
            {
                return;
            }

            foreach (var message in leased)
            {
                await ProcessAsync(owner, message);
            }
        }

        Assert.Fail("The outbox still had due messages after 200 rounds.");
    }

    /// <summary>Leases what is due. This test's message is returned; anything else that was due is delivered right away.</summary>
    private async Task<LeasedMessage?> TryLeaseAsync(string owner, Guid messageId)
    {
        var leased = await LeaseAsync(owner);
        foreach (var other in leased.Where(m => m.Id != messageId))
        {
            await ProcessAsync(owner, other);
        }

        return leased.SingleOrDefault(m => m.Id == messageId);
    }

    private async Task<LeasedMessage> LeaseOneAsync(string owner, Guid messageId)
    {
        var leased = await TryLeaseAsync(owner, messageId);
        Assert.True(leased is not null, "The message was expected to be due but was not leased.");
        return leased;
    }

    private Task<List<OutboxMessage>> MessagesAsync(string payloadFragment) => DbAsync(db =>
        db.OutboxMessages.AsNoTracking().Where(m => m.PayloadJson.Contains(payloadFragment)).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync());

    private Task<OutboxMessage> MessageAsync(Guid id) => DbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id));

    private Task<List<string>> ConsumptionsAsync(Guid messageId) => DbAsync(db =>
        db.OutboxConsumptions.AsNoTracking().Where(c => c.MessageId == messageId).Select(c => c.Handler).ToListAsync());

    private Task<int> NotificationRowsAsync(Guid userId) => DbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId));

    private Task<int> MakeDeliverableAgainAsync(Guid messageId) => DbAsync(db =>
        db.OutboxMessages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, (DateTime?)null)));

    private static async Task<JsonArray> NotificationsAsync(ApiClient client) => (await client.GetJsonAsync("/api/notifications")).AsArray();

    private sealed record LowStock(Warehouse Warehouse, Product Product, User SupervisorUser, ApiClient Supervisor, Guid MessageId);

    /// <summary>A warehouse whose only product is at its reorder level after one receipt, a supervisor for it, and the receipt's pending message.</summary>
    private async Task<LowStock> ArrangeLowStockAsync()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync(reorderLevel: 10);
        var supervisorUser = await _world.UserAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var supervisor = await _world.LoginAsync(supervisorUser);
        await DrainAsync();
        await _world.StockAsync(warehouse, product, 5);
        var message = Assert.Single(await MessagesAsync(warehouse.Id.ToString()));
        Assert.Equal((InventoryService.StockChangedEvent, 0, 0L), (message.Type, message.Attempts, message.LeaseVersion));
        Assert.Null(message.ProcessedAt);
        return new LowStock(warehouse, product, supervisorUser, supervisor, message.Id);
    }

    private IDisposable Failing(string failpoint) => fixture.Failpoints.On(failpoint, _ => throw new InjectedFailureException());

    // ------------------------------------------------------------------ outbox: written with the business effect

    [Fact]
    public async Task A_stock_command_writes_its_outbox_and_audit_rows_in_the_same_transaction_as_the_stock_effect()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var reference = "DZL-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var key = ApiClient.NewKey();
        var body = new
        {
            reference,
            warehouseId = warehouse.Id,
            reasonCode = "Found",
            note = "Sayımda bulunan fazla ürün",
            lines = new[] { new { productId = product.Id, quantityDelta = 9 } },
        };

        async Task<(int Outbox, int Audit, int Movements, int Documents, int Balances, int Idempotency)> RowsAsync() => (
            (await MessagesAsync(warehouse.Id.ToString())).Count,
            await DbAsync(db => db.AuditEntries.CountAsync(a => a.WarehouseId == warehouse.Id)),
            await _world.MovementCountAsync(warehouse.Id, product.Id),
            await DbAsync(db => db.StockAdjustments.CountAsync(a => a.Reference == reference)),
            await DbAsync(db => db.StockBalances.CountAsync(b => b.WarehouseId == warehouse.Id)),
            await DbAsync(db => db.IdempotencyRecords.CountAsync(r => r.IdempotencyKey == key)));

        // The command fails after the balance was changed and before it commits: nothing at all may remain.
        using (Failing("stock.after-posting"))
        {
            using var failed = await supervisor.PostAsync("/api/inventory/adjustments", body, key);
            await failed.ProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        }

        Assert.Equal((0, 0, 0, 0, 0, 0), await RowsAsync());

        // The same request with the same key is then simply executed: the failed attempt left no claim behind.
        using var posted = await supervisor.PostAsync("/api/inventory/adjustments", body, key);
        await posted.JsonAsync(HttpStatusCode.Created);
        Assert.False(posted.Headers.Contains("Idempotent-Replay"));

        Assert.Equal((1, 1, 1, 1, 1, 1), await RowsAsync());
        Assert.Equal((9, 0), await _world.BalanceAsync(warehouse.Id, product.Id));
        var message = Assert.Single(await MessagesAsync(warehouse.Id.ToString()));
        Assert.Equal(InventoryService.StockChangedEvent, message.Type);
        Assert.Contains(product.Id.ToString(), message.PayloadJson, StringComparison.Ordinal);
        Assert.Equal((0, 0L), (message.Attempts, message.LeaseVersion));
        Assert.Null(message.ProcessedAt);
        Assert.Null(message.LeaseOwner);
        Assert.Equal(Now, message.CreatedAt, TimeSpan.FromMilliseconds(2));
        Assert.Equal(message.CreatedAt, message.AvailableAt);

        // A replay returns the stored response and enqueues nothing new.
        using var replayed = await supervisor.PostAsync("/api/inventory/adjustments", body, key);
        Assert.Equal("true", replayed.Headers.GetValues("Idempotent-Replay").Single());
        Assert.Equal((1, 1, 1, 1, 1, 1), await RowsAsync());
    }

    // ------------------------------------------------------------------ outbox: delivery

    [Fact]
    public async Task Low_stock_is_notified_to_the_supervisors_of_that_warehouse_only_and_each_user_handles_their_own_notifications()
    {
        var warehouse = await _world.WarehouseAsync();
        var otherWarehouse = await _world.WarehouseAsync();
        var low = await _world.ProductAsync(reorderLevel: 10);
        var comfortable = await _world.ProductAsync(reorderLevel: 0);
        var supervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var secondSupervisor = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Supervise), (otherWarehouse.Id, WarehouseLevel.View)]);
        var operatorOnly = await _world.LoginAsAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.Operate)]);
        var viewer = await _world.LoginAsAsync([Roles.SalesManager], [(warehouse.Id, WarehouseLevel.View)]);
        var supervisorElsewhere = await _world.LoginAsAsync([Roles.WarehouseOperator], [(otherWarehouse.Id, WarehouseLevel.Supervise)]);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        await DrainAsync();

        await _world.StockAsync(warehouse, [(low, 5), (comfortable, 5)]);
        var pending = Assert.Single(await MessagesAsync(warehouse.Id.ToString()));

        // Nothing is delivered by the command itself.
        Assert.Empty(await NotificationsAsync(supervisor));

        var owner = NewOwner();
        var leased = await LeaseOneAsync(owner, pending.Id);
        Assert.Equal((InventoryService.StockChangedEvent, 1, 1L), (leased.Type, leased.Attempts, leased.LeaseVersion));
        var held = await MessageAsync(pending.Id);
        Assert.Equal(owner, held.LeaseOwner);
        Assert.Equal(Now + OutboxDispatcher.LeaseDuration, held.LeaseUntil!.Value, TimeSpan.FromMilliseconds(2));
        Assert.True(await ProcessAsync(owner, leased));

        var done = await MessageAsync(pending.Id);
        Assert.Equal(Now, done.ProcessedAt!.Value, TimeSpan.FromMilliseconds(2));
        Assert.Null(done.LeaseOwner);
        Assert.Null(done.LeaseUntil);
        Assert.Null(done.LastErrorCode);
        Assert.Equal([LowStockHandler], await ConsumptionsAsync(pending.Id));
        Assert.Null(await TryLeaseAsync(NewOwner(), pending.Id));

        foreach (var client in new[] { supervisor, secondSupervisor })
        {
            var notification = Assert.Single(await NotificationsAsync(client))!;
            Assert.Equal(("low_stock", false), (notification.Str("kind"), notification["isRead"]!.GetValue<bool>()));
            Assert.Contains(low.Sku, notification.Str("text"), StringComparison.Ordinal);
            Assert.Contains(warehouse.Code, notification.Str("text"), StringComparison.Ordinal);
            Assert.DoesNotContain(comfortable.Sku, notification.Str("text"), StringComparison.Ordinal);
        }

        foreach (var client in new[] { operatorOnly, viewer, supervisorElsewhere, admin })
        {
            Assert.Empty(await NotificationsAsync(client));
        }

        // A notification can be marked read by its recipient only; for anyone else it does not exist.
        var id = (await NotificationsAsync(supervisor))[0]!.Id();
        foreach (var client in new[] { secondSupervisor, supervisorElsewhere, admin })
        {
            using var foreign = await client.PostAsync($"/api/notifications/{id}/read");
            await foreign.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        using var unknown = await supervisor.PostAsync($"/api/notifications/{Guid.NewGuid()}/read");
        await unknown.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.False((await NotificationsAsync(supervisor))[0]!["isRead"]!.GetValue<bool>());

        using var read = await supervisor.PostAsync($"/api/notifications/{id}/read");
        Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        using var readAgain = await supervisor.PostAsync($"/api/notifications/{id}/read");
        Assert.Equal(HttpStatusCode.NoContent, readAgain.StatusCode);
        Assert.True(Assert.Single(await NotificationsAsync(supervisor))!["isRead"]!.GetValue<bool>());
        Assert.False(Assert.Single(await NotificationsAsync(secondSupervisor))!["isRead"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Redelivery_of_a_message_that_was_already_handled_has_no_second_effect()
    {
        var arranged = await ArrangeLowStockAsync();
        var first = NewOwner();
        Assert.True(await ProcessAsync(first, await LeaseOneAsync(first, arranged.MessageId)));
        var notification = Assert.Single(await NotificationsAsync(arranged.Supervisor))!;

        // Read it first: the handler never stacks open reminders, and that rule must not be what prevents the duplicate here.
        using (var read = await arranged.Supervisor.PostAsync($"/api/notifications/{notification.Id()}/read"))
        {
            Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        }

        // The completion mark is lost (as if the worker died right after its transaction) and the message comes round again.
        Assert.Equal(1, await MakeDeliverableAgainAsync(arranged.MessageId));
        var second = NewOwner();
        var again = await LeaseOneAsync(second, arranged.MessageId);
        Assert.Equal((2, 2L), (again.Attempts, again.LeaseVersion));
        Assert.True(await ProcessAsync(second, again));

        var after = Assert.Single(await NotificationsAsync(arranged.Supervisor))!;
        Assert.Equal((notification.Id(), true), (after.Id(), after["isRead"]!.GetValue<bool>()));
        Assert.Equal(1, await NotificationRowsAsync(arranged.SupervisorUser.Id));
        Assert.Equal([LowStockHandler], await ConsumptionsAsync(arranged.MessageId));
        Assert.NotNull((await MessageAsync(arranged.MessageId)).ProcessedAt);
    }

    [Fact]
    public async Task A_crash_before_completion_rolls_the_effect_back_and_the_retry_applies_it_exactly_once()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var arranged = await ArrangeLowStockAsync();
        var before = await admin.GetJsonAsync("/api/ops/outbox");
        var owner = NewOwner();
        var leased = await LeaseOneAsync(owner, arranged.MessageId);

        bool completed;
        using (Failing("outbox.before-complete"))
        {
            completed = await ProcessAsync(owner, leased);
        }

        // The handler had written its notification and its consumption mark; both went with the transaction.
        Assert.False(completed);
        Assert.Empty(await NotificationsAsync(arranged.Supervisor));
        Assert.Empty(await ConsumptionsAsync(arranged.MessageId));
        var failed = await MessageAsync(arranged.MessageId);
        Assert.Null(failed.ProcessedAt);
        Assert.Equal((1, nameof(InjectedFailureException)), (failed.Attempts, failed.LastErrorCode));
        Assert.Null(failed.LeaseOwner);
        Assert.Null(failed.LeaseUntil);
        Assert.Equal(Now.AddSeconds(2), failed.AvailableAt, TimeSpan.FromMilliseconds(2));

        var retrying = await admin.GetJsonAsync("/api/ops/outbox");
        Assert.Equal(before.Number("retrying") + 1, retrying.Number("retrying"));
        Assert.Equal(before.Number("pending"), retrying.Number("pending"));
        Assert.Equal(before.Number("dead"), retrying.Number("dead"));

        // Not before its back-off has passed.
        Assert.Null(await TryLeaseAsync(NewOwner(), arranged.MessageId));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));

        var retryOwner = NewOwner();
        var retry = await LeaseOneAsync(retryOwner, arranged.MessageId);
        Assert.Equal((2, 2L), (retry.Attempts, retry.LeaseVersion));
        Assert.True(await ProcessAsync(retryOwner, retry));

        Assert.Single(await NotificationsAsync(arranged.Supervisor));
        Assert.Equal([LowStockHandler], await ConsumptionsAsync(arranged.MessageId));
        var done = await MessageAsync(arranged.MessageId);
        Assert.NotNull(done.ProcessedAt);
        Assert.Null(done.LastErrorCode);
        Assert.Null(await TryLeaseAsync(NewOwner(), arranged.MessageId));
        Assert.Equal(1, await NotificationRowsAsync(arranged.SupervisorUser.Id));
        Assert.Equal(before.Number("retrying"), (await admin.GetJsonAsync("/api/ops/outbox")).Number("retrying"));
    }

    [Fact]
    public async Task A_worker_that_outlived_its_lease_cannot_complete_a_message_another_worker_has_finished()
    {
        var arranged = await ArrangeLowStockAsync();
        var (stalled, takeover) = (NewOwner(), NewOwner());
        var stalledLease = await LeaseOneAsync(stalled, arranged.MessageId);

        // While the lease is current nobody else gets the message.
        fixture.Clock.Advance(OutboxDispatcher.LeaseDuration - TimeSpan.FromSeconds(1));
        Assert.Null(await TryLeaseAsync(takeover, arranged.MessageId));

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var takeoverLease = await LeaseOneAsync(takeover, arranged.MessageId);
        Assert.True(takeoverLease.LeaseVersion > stalledLease.LeaseVersion);
        Assert.Equal((2, 2L), (takeoverLease.Attempts, takeoverLease.LeaseVersion));
        Assert.True(await ProcessAsync(takeover, takeoverLease));
        var completedAt = (await MessageAsync(arranged.MessageId)).ProcessedAt;

        // The first worker wakes up late with its old fencing token.
        Assert.False(await ProcessAsync(stalled, stalledLease));

        Assert.Single(await NotificationsAsync(arranged.Supervisor));
        Assert.Equal(1, await NotificationRowsAsync(arranged.SupervisorUser.Id));
        Assert.Equal([LowStockHandler], await ConsumptionsAsync(arranged.MessageId));
        var row = await MessageAsync(arranged.MessageId);
        Assert.Equal((completedAt, 2, 2L), (row.ProcessedAt, row.Attempts, row.LeaseVersion));
        Assert.Null(row.LastErrorCode);
    }

    [Fact]
    public async Task A_worker_that_outlived_its_lease_cannot_apply_the_effect_while_the_new_owner_still_holds_the_message()
    {
        var arranged = await ArrangeLowStockAsync();
        var (stalled, takeover) = (NewOwner(), NewOwner());
        var stalledLease = await LeaseOneAsync(stalled, arranged.MessageId);
        fixture.Clock.Advance(OutboxDispatcher.LeaseDuration + TimeSpan.FromSeconds(1));
        var takeoverLease = await LeaseOneAsync(takeover, arranged.MessageId);

        // The stalled worker finishes first. It does the work, finds that the lease is no longer its own and rolls back.
        Assert.False(await ProcessAsync(stalled, stalledLease));

        Assert.Empty(await NotificationsAsync(arranged.Supervisor));
        Assert.Empty(await ConsumptionsAsync(arranged.MessageId));
        var held = await MessageAsync(arranged.MessageId);
        Assert.Equal((takeover, 2L), (held.LeaseOwner, held.LeaseVersion));
        Assert.Null(held.ProcessedAt);
        Assert.Null(held.LastErrorCode);

        // The message is still the new owner's to complete, and it is applied once.
        Assert.True(await ProcessAsync(takeover, takeoverLease));
        Assert.Single(await NotificationsAsync(arranged.Supervisor));
        Assert.Equal([LowStockHandler], await ConsumptionsAsync(arranged.MessageId));
        Assert.NotNull((await MessageAsync(arranged.MessageId)).ProcessedAt);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_parked_as_dead_and_reported_instead_of_being_dropped()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var arranged = await ArrangeLowStockAsync();
        var before = await admin.GetJsonAsync("/api/ops/outbox");

        for (var attempt = 1; attempt <= OutboxStates.MaxAttempts; attempt++)
        {
            var owner = NewOwner();
            var leased = await LeaseOneAsync(owner, arranged.MessageId);
            Assert.Equal(attempt, leased.Attempts);
            using (Failing("outbox.before-complete"))
            {
                Assert.False(await ProcessAsync(owner, leased));
            }

            var row = await MessageAsync(arranged.MessageId);
            Assert.Null(row.ProcessedAt);
            Assert.Null(row.LeaseOwner);
            if (attempt < OutboxStates.MaxAttempts)
            {
                // Exponential back-off: 2, 4, 8 … seconds, never more than five minutes.
                var delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));
                Assert.Equal(nameof(InjectedFailureException), row.LastErrorCode);
                Assert.Equal(Now + delay, row.AvailableAt, TimeSpan.FromMilliseconds(2));
                fixture.Clock.Advance(delay - TimeSpan.FromSeconds(1));
                Assert.Null(await TryLeaseAsync(NewOwner(), arranged.MessageId));
                fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            }
            else
            {
                Assert.Equal(OutboxStates.Dead, row.LastErrorCode);
                Assert.True(row.AvailableAt > Now.AddYears(100));
            }
        }

        // However long anyone waits, a dead message is not picked up again; it stays in the table for an operator to see.
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(await TryLeaseAsync(NewOwner(), arranged.MessageId));
        var parked = await MessageAsync(arranged.MessageId);
        Assert.Equal((OutboxStates.MaxAttempts, OutboxStates.Dead), (parked.Attempts, parked.LastErrorCode));
        Assert.Null(parked.ProcessedAt);
        Assert.Equal(0, await NotificationRowsAsync(arranged.SupervisorUser.Id));
        Assert.Empty(await ConsumptionsAsync(arranged.MessageId));

        // The first admin session has idled out while the clock was moved; the report is read with a new one.
        var after = await (await _world.LoginAsAsync([Roles.SystemAdmin])).GetJsonAsync("/api/ops/outbox");
        Assert.Equal(before.Number("dead") + 1, after.Number("dead"));
    }

    [Fact]
    public async Task Assigning_a_ticket_enqueues_one_message_and_its_delivery_notifies_the_assignee_once()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var agentUser = await _world.UserAsync([Roles.SupportAgent]);
        var agent = await _world.LoginAsync(agentUser);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        var fragment = ticket.Id().ToString();
        await DrainAsync();

        // A refused assignment leaves nothing in the outbox.
        using (var stale = await manager.PostAsync($"/api/support/tickets/{ticket.Id()}/assign", new { assigneeUserId = agentUser.Id }, ifMatch: "\"AAAAAAAAAAA=\""))
        {
            await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        }

        Assert.Empty(await MessagesAsync(fragment));

        using (var assigned = await manager.PostAsync($"/api/support/tickets/{ticket.Id()}/assign", new { assigneeUserId = agentUser.Id }, ifMatch: etag))
        {
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        }

        var message = Assert.Single(await MessagesAsync(fragment));
        Assert.Equal(TicketService.TicketAssignedEvent, message.Type);
        Assert.Contains(agentUser.Id.ToString(), message.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(ticket.Str("number"), message.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Str("title"), message.PayloadJson, StringComparison.Ordinal);
        Assert.Empty(await NotificationsAsync(agent));

        var owner = NewOwner();
        Assert.True(await ProcessAsync(owner, await LeaseOneAsync(owner, message.Id)));

        var notification = Assert.Single(await NotificationsAsync(agent))!;
        Assert.Equal("ticket_assigned", notification.Str("kind"));
        Assert.Contains(ticket.Str("number"), notification.Str("text"), StringComparison.Ordinal);
        Assert.Empty(await NotificationsAsync(requester));
        Assert.Empty(await NotificationsAsync(manager));
        Assert.Equal([AssigneeHandler], await ConsumptionsAsync(message.Id));

        // This handler has no rule of its own against duplicates: only the consumption record stops a redelivery.
        Assert.Equal(1, await MakeDeliverableAgainAsync(message.Id));
        var second = NewOwner();
        Assert.True(await ProcessAsync(second, await LeaseOneAsync(second, message.Id)));
        Assert.Single(await NotificationsAsync(agent));
        Assert.Equal(1, await NotificationRowsAsync(agentUser.Id));
    }

    [Fact]
    public async Task A_stopping_worker_lets_the_message_in_hand_commit_before_it_stops()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var agentUser = await _world.UserAsync([Roles.SupportAgent]);
        var agent = await _world.LoginAsync(agentUser);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        await DrainAsync();
        using (var assigned = await manager.PostAsync($"/api/support/tickets/{ticket.Id()}/assign", new { assigneeUserId = agentUser.Id }, ifMatch: etag))
        {
            Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        }

        var message = Assert.Single(await MessagesAsync(ticket.Id().ToString()));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new WorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 10,
            HeartbeatFile = Path.Combine(Path.GetTempPath(), $"kantar-test-{Guid.NewGuid():N}.heartbeat"),
        };
        using var worker = new OutboxWorker(fixture.Services.GetRequiredService<IServiceScopeFactory>(), options, NullLogger<OutboxWorker>.Instance);
        try
        {
            // The message is parked inside its transaction: effect written, completion not yet.
            using var parked = fixture.Failpoints.On("outbox.before-complete", async cancellationToken =>
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            });
            await worker.StartAsync(CancellationToken.None);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Empty(await NotificationsAsync(agent));

            var stopping = worker.StopAsync(CancellationToken.None);

            // Stop was requested, the message is still in flight: the worker must be waiting for it, not abandoning it.
            Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromMilliseconds(500))));
            Assert.Null((await MessageAsync(message.Id)).ProcessedAt);

            release.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
            File.Delete(options.HeartbeatFile);
        }

        var done = await MessageAsync(message.Id);
        Assert.NotNull(done.ProcessedAt);
        Assert.Null(done.LeaseOwner);
        Assert.Equal(1, done.Attempts);
        Assert.Equal([AssigneeHandler], await ConsumptionsAsync(message.Id));
        Assert.Single(await NotificationsAsync(agent));
    }

    // ------------------------------------------------------------------ reconciliation

    /// <summary>Counts and checksums of the stock tables. Any insert, delete or update (the row versions are included) changes it.</summary>
    private async Task<string> StockFingerprintAsync()
    {
        const string sql =
            """
            SELECT
              (SELECT COUNT_BIG(*) FROM inv.StockBalances),
              (SELECT ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) FROM inv.StockBalances),
              (SELECT COUNT_BIG(*) FROM inv.StockMovements),
              (SELECT ISNULL(MAX(Id), 0) FROM inv.StockMovements),
              (SELECT ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) FROM inv.StockMovements),
              (SELECT COUNT_BIG(*) FROM inv.ReservationEntries),
              (SELECT ISNULL(MAX(Id), 0) FROM inv.ReservationEntries),
              (SELECT ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(*)), 0) FROM inv.ReservationEntries),
              (SELECT COUNT_BIG(*) FROM inv.StockAdjustments),
              (SELECT COUNT_BIG(*) FROM inv.StockTransfers),
              (SELECT ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Id, OrderedQty, ShippedQty, CancelledQty)), 0) FROM sales.SalesOrderLines),
              (SELECT ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Id, Status, RowVersion)), 0) FROM sales.SalesOrders);
            """;
        await using var connection = new SqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static (string Rule, long Expected, long Actual)[] FindingsFor(JsonNode run, Guid warehouseId, Guid productId) =>
        run["findings"]!.AsArray()
            .Where(f => f!.Id("warehouseId") == warehouseId && f!.Id("productId") == productId)
            .Select(f => (f!.Str("rule"), f!["expected"]!.GetValue<long>(), f!["actual"]!.GetValue<long>()))
            .OrderBy(f => f.Item1, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task Reconciliation_reports_out_of_band_drift_with_expected_and_actual_values_and_changes_no_stock_data()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        await _world.StockAsync(warehouse, product, 20);
        var (order, etag) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 5));
        await World.ConfirmAsync(sales, order, etag);
        Assert.Equal((20, 5), await _world.BalanceAsync(warehouse.Id, product.Id));
        SqlParameter[] Key() => [new("@w", warehouse.Id), new("@p", product.Id)];

        async Task<JsonNode> RunAsync()
        {
            using var response = await admin.PostAsync("/api/ops/reconciliation/run", null, ApiClient.NewKey());
            return await response.JsonAsync(HttpStatusCode.OK);
        }

        // --- consistent data ---
        var clean = await RunAsync();
        Assert.Empty(FindingsFor(clean, warehouse.Id, product.Id));
        Assert.True(
            clean.Str("outcome") == "Consistent" && clean.Number("findingCount") == 0,
            "The shared test database is expected to be consistent before drift is introduced, but reconciliation found: " + clean["findings"]!.ToJsonString());
        Assert.Empty(clean["findings"]!.AsArray());
        Assert.InRange(clean.Number("balancesChecked"), 1, int.MaxValue);

        // --- somebody changes a balance behind the application's back ---
        Assert.Equal(1, await fixture.AppExecuteAsync("UPDATE inv.StockBalances SET OnHand = OnHand + 7 WHERE WarehouseId = @w AND ProductId = @p;", Key()));
        var fingerprint = await StockFingerprintAsync();

        // "Latest" is decided by completion time and the test clock stands still, so this run gets an instant of its own
        // (between runs of the same instant the order is fixed by start time and id, which is stable but not chronological).
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var drift = await RunAsync();

        Assert.Equal("DriftFound", drift.Str("outcome"));
        Assert.Equal(2, drift.Number("findingCount"));

        // A finding names the warehouse and the product the way a person knows them, next to the ids.
        Assert.All(
            drift["findings"]!.AsArray().Where(f => f!.Id("productId") == product.Id),
            f => Assert.Equal((warehouse.Code, product.Sku), (f!.Str("warehouseCode"), f!.Str("sku"))));
        Assert.All(
            drift["findings"]!.AsArray(),
            f => Assert.Equal(["actual", "expected", "productId", "rule", "sku", "warehouseCode", "warehouseId"], f!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray()));
        Assert.Equal(
            [("onhand_equals_last_movement_after", 20L, 27L), ("onhand_equals_movement_sum", 20L, 27L)],
            FindingsFor(drift, warehouse.Id, product.Id));

        // The run is a report. It did not touch a balance, a journal row or an order, and it did not "repair" the difference.
        Assert.Equal(fingerprint, await StockFingerprintAsync());
        Assert.Equal((27, 5), await _world.BalanceAsync(warehouse.Id, product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(warehouse.Id, product.Id));

        using (var latest = await admin.GetAsync("/api/ops/reconciliation/latest"))
        {
            var report = await latest.JsonAsync(HttpStatusCode.OK);
            Assert.Equal((drift.Id(), "DriftFound", 2), (report.Id(), report.Str("outcome"), report.Number("findingCount")));
            Assert.Equal(FindingsFor(drift, warehouse.Id, product.Id), FindingsFor(report, warehouse.Id, product.Id));
        }

        // Asking again reports the same difference again; reporting it once does not make it go away.
        var second = await RunAsync();
        Assert.NotEqual(drift.Id(), second.Id());
        Assert.Equal(FindingsFor(drift, warehouse.Id, product.Id), FindingsFor(second, warehouse.Id, product.Id));

        // --- the reserved side drifts as well: every rule that involves it speaks up ---
        Assert.Equal(1, await fixture.AppExecuteAsync("UPDATE inv.StockBalances SET Reserved = Reserved + 1 WHERE WarehouseId = @w AND ProductId = @p;", Key()));
        var both = await RunAsync();
        Assert.Equal(
            [
                ("onhand_equals_last_movement_after", 20L, 27L),
                ("onhand_equals_movement_sum", 20L, 27L),
                ("reserved_equals_last_reservation_after", 5L, 6L),
                ("reserved_equals_open_order_quantity", 5L, 6L),
                ("reserved_equals_reservation_sum", 5L, 6L),
            ],
            FindingsFor(both, warehouse.Id, product.Id));
        Assert.Equal((27, 6), await _world.BalanceAsync(warehouse.Id, product.Id));

        // --- the values are put back by hand: consistent again ---
        Assert.Equal(1, await fixture.AppExecuteAsync(
            "UPDATE inv.StockBalances SET OnHand = OnHand - 7, Reserved = Reserved - 1 WHERE WarehouseId = @w AND ProductId = @p;", Key()));
        var restored = await RunAsync();
        Assert.Equal(("Consistent", 0), (restored.Str("outcome"), restored.Number("findingCount")));
        Assert.Empty(restored["findings"]!.AsArray());
        await _world.AssertReconciledAsync(warehouse.Id, product.Id);
    }

    [Fact]
    public async Task The_service_run_used_by_the_worker_gives_the_same_verdict_and_keeps_only_its_own_report_rows()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        await _world.StockAsync(warehouse, product, 8);
        var fingerprint = await StockFingerprintAsync();

        var run = await ScopedAsync(services => services.GetRequiredService<OperationsService>().RunReconciliationAsync(null, CancellationToken.None));

        Assert.DoesNotContain(run.Findings, f => f.WarehouseId == warehouse.Id && f.ProductId == product.Id);
        Assert.Equal(run.FindingCount == 0 ? "Consistent" : "DriftFound", run.Outcome);
        Assert.InRange(run.BalancesChecked, 1, int.MaxValue);
        Assert.Equal(fingerprint, await StockFingerprintAsync());
        Assert.InRange(await DbAsync(db => db.ReconciliationRuns.CountAsync()), 1, OperationsService.KeptRuns);
        Assert.Null(await DbAsync(db => db.ReconciliationRuns.Where(r => r.Id == run.Id).Select(r => r.RequestedBy).SingleAsync()));
    }

    // ------------------------------------------------------------------ operations endpoints

    public static TheoryData<string> NonAdminRoles => [Roles.SupportManager, Roles.SupportAgent, Roles.SalesManager, Roles.SalesAgent, Roles.PurchasingAgent, Roles.WarehouseOperator];

    [Theory]
    [MemberData(nameof(NonAdminRoles))]
    public async Task Operations_endpoints_are_for_the_system_admin_only(string role)
    {
        var warehouse = await _world.WarehouseAsync();
        var client = await _world.LoginAsAsync([role], [(warehouse.Id, WarehouseLevel.Supervise)]);
        var runsBefore = await DbAsync(db => db.ReconciliationRuns.CountAsync());

        foreach (var url in new[] { "/api/ops/outbox", "/api/ops/reconciliation/latest", "/api/ops/metrics" })
        {
            using var response = await client.GetAsync(url);
            await response.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using var run = await client.PostAsync("/api/ops/reconciliation/run", null, ApiClient.NewKey());
        await run.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(runsBefore, await DbAsync(db => db.ReconciliationRuns.CountAsync()));

        // Notifications are personal, not an operations screen: every signed-in user has their own (empty) list.
        Assert.Empty(await NotificationsAsync(client));
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}|[0-9a-fA-F]{32}|@", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierLike();

    [Fact]
    public async Task System_admin_reads_the_operations_summaries_and_a_reconciliation_run_needs_an_idempotency_key()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);

        using (var anonymous = await fixture.NewClient().GetAsync("/api/ops/outbox"))
        {
            await anonymous.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        }

        var outbox = await admin.GetJsonAsync("/api/ops/outbox");
        Assert.All(new[] { "pending", "leased", "retrying", "dead", "processedLast24Hours" }, field => Assert.InRange(outbox.Number(field), 0, int.MaxValue));

        using (var withoutKey = await admin.PostAsync("/api/ops/reconciliation/run"))
        {
            await withoutKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        }

        using (var shortKey = await admin.PostAsync("/api/ops/reconciliation/run", null, "kisa"))
        {
            await shortKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        }

        var runsBefore = await DbAsync(db => db.ReconciliationRuns.CountAsync());
        var key = ApiClient.NewKey();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        using var first = await admin.PostAsync("/api/ops/reconciliation/run", null, key);
        var run = await first.JsonAsync(HttpStatusCode.OK);
        using var replay = await admin.PostAsync("/api/ops/reconciliation/run", null, key);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await replay.Content.ReadAsByteArrayAsync());
        Assert.Equal("true", replay.Headers.GetValues("Idempotent-Replay").Single());
        Assert.InRange(await DbAsync(db => db.ReconciliationRuns.CountAsync()), runsBefore, runsBefore + 1);
        Assert.Contains(run.Str("outcome"), new[] { "Consistent", "DriftFound" });

        using var latest = await admin.GetAsync("/api/ops/reconciliation/latest");
        Assert.Equal(run.Id(), (await latest.JsonAsync(HttpStatusCode.OK)).Id());

        // Runs that complete in the same instant (the test clock stands still) are ordered by start time, then id:
        // "latest" is the same run however often it is asked for.
        var sameInstant = new List<Guid> { run.Id() };
        for (var i = 0; i < 3; i++)
        {
            using var again = await admin.PostAsync("/api/ops/reconciliation/run", null, ApiClient.NewKey());
            sameInstant.Add((await again.JsonAsync(HttpStatusCode.OK)).Id());
        }

        var expectedLatest = await DbAsync(db => db.ReconciliationRuns
            .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Select(r => r.Id).FirstAsync());
        Assert.Contains(expectedLatest, sameInstant);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(expectedLatest, (await admin.GetJsonAsync("/api/ops/reconciliation/latest")).Id());
        }

        // Counters carry operation names and outcomes from code, never an identifier or a user name.
        var metrics = await admin.GetJsonAsync("/api/ops/metrics");
        var counters = metrics["counters"]!.AsArray();
        Assert.Contains(counters, c => c!.Str("name") == "command" && c!.Str("dimension1") == "ops.reconciliation.run");
        Assert.All(counters, c =>
        {
            Assert.Contains(c!.Str("name"), new[] { "command", "outbox" });
            Assert.DoesNotMatch(IdentifierLike(), c!.Str("dimension1"));
            Assert.DoesNotMatch(IdentifierLike(), c!.Str("dimension2"));
            Assert.InRange(c!["count"]!.GetValue<long>(), 1, long.MaxValue);
        });
        Assert.All(metrics["aiUsageLast24Hours"]!.AsArray(), row =>
            Assert.Equal(["averageDurationMs", "count", "provider", "status"], row!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray()));
    }

    // ------------------------------------------------------------------ housekeeping

    [Fact]
    public async Task Cleanup_deletes_rows_whose_retention_has_ended_and_keeps_everything_else()
    {
        var user = await _world.UserAsync([Roles.SalesAgent]);
        var now = Now;
        static byte[] Random32() => RandomNumberGenerator.GetBytes(32);

        IdempotencyRecord Idempotency(DateTime expiresAt) => new()
        {
            UserId = user.Id, Operation = "test.cleanup", IdempotencyKey = ApiClient.NewKey(), RequestHash = Random32(), CreatedAt = expiresAt.AddHours(-24), ExpiresAt = expiresAt,
        };
        AiAnswer Answer(DateTime expiresAt) => new()
        {
            UserId = user.Id, SessionId = Guid.NewGuid(), Status = "Answered", Provider = "demo", AnswerText = "Sentetik yanıt", CreatedAt = expiresAt.AddMinutes(-30), ExpiresAt = expiresAt,
        };
        Session SessionRow(DateTime expiresAt) => new()
        {
            UserId = user.Id, TokenHash = Random32(), CsrfSecret = Random32(), SecurityStamp = Guid.NewGuid(), CreatedAt = expiresAt.AddHours(-8), LastSeenAt = expiresAt.AddHours(-1), ExpiresAt = expiresAt,
        };
        OutboxMessage Message(DateTime createdAt, DateTime? processedAt) => new()
        {
            Type = "test.cleanup", PayloadJson = "{}", CreatedAt = createdAt, AvailableAt = createdAt.AddYears(50), ProcessedAt = processedAt,
        };
        OutboxConsumption Consumption(DateTime consumedAt) => new() { MessageId = Guid.NewGuid(), Handler = "test-cleanup", ConsumedAt = consumedAt };
        Notification Note(DateTime createdAt, DateTime? readAt) => new() { UserId = user.Id, Kind = "test", Text = "Sentetik bildirim", CreatedAt = createdAt, ReadAt = readAt };

        var (expiredRecord, currentRecord) = (Idempotency(now.AddMinutes(-1)), Idempotency(now.AddHours(1)));
        var (expiredAnswer, currentAnswer) = (Answer(now.AddMinutes(-1)), Answer(now.AddMinutes(10)));
        var (oldSession, recentlyExpiredSession, liveSession) = (SessionRow(now.AddDays(-2)), SessionRow(now.AddHours(-1)), SessionRow(now.AddHours(7)));
        var (oldProcessed, recentProcessed, oldUnprocessed) =
            (Message(now.AddDays(-9), now.AddDays(-8)), Message(now.AddDays(-2), now.AddDays(-1)), Message(now.AddDays(-30), null));
        var (oldConsumption, recentConsumption) = (Consumption(now.AddDays(-8)), Consumption(now.AddDays(-1)));
        var (oldRead, recentRead, oldUnread) = (Note(now.AddDays(-40), now.AddDays(-31)), Note(now.AddDays(-40), now.AddDays(-2)), Note(now.AddDays(-90), null));

        await DbAsync(async db =>
        {
            db.AddRange(expiredRecord, currentRecord, expiredAnswer, currentAnswer, oldSession, recentlyExpiredSession, liveSession);
            db.AddRange(oldProcessed, recentProcessed, oldUnprocessed, oldConsumption, recentConsumption, oldRead, recentRead, oldUnread);
            return await db.SaveChangesAsync();
        });

        async Task<bool[]> ExistAsync() => await DbAsync(async db => new[]
        {
            await db.IdempotencyRecords.AnyAsync(r => r.IdempotencyKey == expiredRecord.IdempotencyKey),
            await db.IdempotencyRecords.AnyAsync(r => r.IdempotencyKey == currentRecord.IdempotencyKey),
            await db.AiAnswers.AnyAsync(a => a.Id == expiredAnswer.Id),
            await db.AiAnswers.AnyAsync(a => a.Id == currentAnswer.Id),
            await db.Sessions.AnyAsync(s => s.Id == oldSession.Id),
            await db.Sessions.AnyAsync(s => s.Id == recentlyExpiredSession.Id),
            await db.Sessions.AnyAsync(s => s.Id == liveSession.Id),
            await db.OutboxMessages.AnyAsync(m => m.Id == oldProcessed.Id),
            await db.OutboxMessages.AnyAsync(m => m.Id == recentProcessed.Id),
            await db.OutboxMessages.AnyAsync(m => m.Id == oldUnprocessed.Id),
            await db.OutboxConsumptions.AnyAsync(c => c.MessageId == oldConsumption.MessageId),
            await db.OutboxConsumptions.AnyAsync(c => c.MessageId == recentConsumption.MessageId),
            await db.Notifications.AnyAsync(n => n.Id == oldRead.Id),
            await db.Notifications.AnyAsync(n => n.Id == recentRead.Id),
            await db.Notifications.AnyAsync(n => n.Id == oldUnread.Id),
        });

        string[] names =
        [
            "expired idempotency record", "current idempotency record", "expired answer", "current answer",
            "session expired two days ago", "session expired an hour ago", "live session",
            "message processed eight days ago", "message processed yesterday", "old message never processed",
            "consumption eight days old", "consumption one day old",
            "notification read 31 days ago", "notification read two days ago", "old notification never read",
        ];
        string[] expectedToRemain =
        [
            "current idempotency record", "current answer", "session expired an hour ago", "live session",
            "message processed yesterday", "old message never processed", "consumption one day old",
            "notification read two days ago", "old notification never read",
        ];

        async Task<string[]> RemainingAsync() => names.Zip(await ExistAsync()).Where(x => x.Second).Select(x => x.First).ToArray();

        Assert.Equal(names, await RemainingAsync());
        try
        {
            var deleted = await ScopedAsync(services => services.GetRequiredService<OperationsService>().CleanupAsync(CancellationToken.None));

            Assert.Equal(expectedToRemain, await RemainingAsync());
            Assert.InRange(deleted, names.Length - expectedToRemain.Length, int.MaxValue);

            // Every predicate is about time, so running it again removes nothing more of these rows.
            await ScopedAsync(services => services.GetRequiredService<OperationsService>().CleanupAsync(CancellationToken.None));
            Assert.Equal(expectedToRemain, await RemainingAsync());
        }
        finally
        {
            // The synthetic queue rows are not real work; they are taken out so later tests do not meet them.
            await DbAsync(db => db.OutboxMessages.Where(m => m.Type == "test.cleanup").ExecuteDeleteAsync());
        }
    }
}
