using System.Data;
using System.Globalization;
using System.Net;
using Kantar.Domain.Identity;
using Kantar.Domain.MasterData;
using Kantar.Infrastructure.Persistence;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Kantar.IntegrationTests.Database;

/// <summary>
/// docs/03 §6: a command runs in one transaction that is retried as a whole, at most three times, on
/// deadlock (1205) or lock timeout (1222). These tests make SQL Server produce both conditions for real
/// against a second session and then count what the command left behind.
/// </summary>
public sealed class TransactionRetryTests(KantarFixture fixture)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(45);
    private readonly World _world = fixture.NewWorld();

    [Fact]
    public async Task A_command_chosen_as_deadlock_victim_is_rolled_back_retried_and_takes_effect_exactly_once()
    {
        var scene = await ArrangeAsync();
        var deadlocksBefore = await DeadlockCountAsync();
        fixture.Logs.Clear();

        // The rival session holds the balance row the transfer touches second, and will ask for the row
        // the transfer touches first: the opposite order, which is what a deadlock needs.
        await using var rival = await Rival.OpenAsync(fixture.AppConnectionString, deadlockPriority: "HIGH");
        await rival.LockBalanceAsync(scene.SecondRow, scene.Product.Id);

        var transferHoldsFirstRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hits = 0;
        Reply reply;
        using (fixture.Failpoints.On("stock.after-posting", async cancellationToken =>
        {
            if (Interlocked.Increment(ref hits) != 1)
            {
                return;
            }

            // The product's lock timeout (5 s) and the engine's deadlock search (every 5 s at most) would
            // race for which error ends the wait. For this one attempt the session waits longer, so the
            // only way out is the engine's deadlock detection. The retry runs with the product's setting.
            await RaiseLockTimeoutOfCurrentRequestAsync(cancellationToken);
            transferHoldsFirstRow.TrySetResult();
            await release.Task.WaitAsync(Patience, cancellationToken);
        }))
        {
            var pending = Reply.ReadAsync(scene.Client.PostAsync("/api/inventory/transfers", scene.Body, scene.Key));
            await transferHoldsFirstRow.Task.WaitAsync(Patience);

            var rivalWantsFirstRow = rival.LockBalanceAsync(scene.FirstRow, scene.Product.Id);
            await WaitUntilBlockedAsync(rival.SessionId);
            Assert.False(rivalWantsFirstRow.IsCompleted);

            // The transfer now asks for the second row: transfer → rival → transfer. With the rival at
            // high deadlock priority the engine must end the transfer's transaction with error 1205.
            release.SetResult();
            await rivalWantsFirstRow.WaitAsync(Patience);

            // The rival got the first row, so the transfer's first attempt is gone, and while the rival
            // holds both rows no second attempt can have posted anything.
            await AssertUntouchedAsync(scene);

            await rival.CommitAsync();
            reply = await pending.WaitAsync(Patience);
        }

        Assert.True(reply.Status == HttpStatusCode.Created, $"Expected 201 but got {(int)reply.Status}: {reply.Text}");
        Assert.False(reply.Replayed);

        // One real deadlock was counted by the server, and the unit of work logged exactly one retry for it.
        Assert.Equal(deadlocksBefore + 1, await DeadlockCountAsync());
        Assert.Equal([(1205, 1)], fixture.Logs.Of(TestLogs.RetryEvent).Select(e => (e.Number("Number"), e.Number("Attempt"))));
        Assert.Empty(fixture.Logs.Of(TestLogs.GiveUpEvent));

        // Attempt 1 reached the failpoint once (first posting); attempt 2 passed it after both postings.
        Assert.Equal(3, hits);
        await AssertPostedOnceAsync(scene, reply);
    }

    [Fact]
    public async Task A_command_that_cannot_get_its_lock_gives_up_after_three_attempts_and_leaves_nothing_behind()
    {
        var scene = await ArrangeAsync();
        fixture.Logs.Clear();

        // The rival keeps the first balance row for the whole request: every attempt runs into the
        // product's own lock timeout (error 1222).
        await using var rival = await Rival.OpenAsync(fixture.AppConnectionString, deadlockPriority: "NORMAL");
        await rival.LockBalanceAsync(scene.FirstRow, scene.Product.Id);

        var hits = 0;
        Reply refused;
        using (fixture.Failpoints.On("stock.after-posting", _ =>
        {
            Interlocked.Increment(ref hits);
            return Task.CompletedTask;
        }))
        {
            refused = await Reply.ReadAsync(scene.Client.PostAsync("/api/inventory/transfers", scene.Body, scene.Key)).WaitAsync(Patience);
        }

        refused.AssertProblem(HttpStatusCode.ServiceUnavailable, "retry_later");
        Assert.Equal(0, hits);
        Assert.Equal([(1222, 1), (1222, 2)], fixture.Logs.Of(TestLogs.RetryEvent).Select(e => (e.Number("Number"), e.Number("Attempt"))));
        Assert.Equal([(1222, 3)], fixture.Logs.Of(TestLogs.GiveUpEvent).Select(e => (e.Number("Number"), e.Number("Attempt"))));
        await AssertUntouchedAsync(scene);

        // Once the row is free the same key is a first execution, not a replay of the refusal.
        await rival.CommitAsync();
        fixture.Logs.Clear();
        var reply = await Reply.ReadAsync(scene.Client.PostAsync("/api/inventory/transfers", scene.Body, scene.Key));
        Assert.True(reply.Status == HttpStatusCode.Created, $"Expected 201 but got {(int)reply.Status}: {reply.Text}");
        Assert.False(reply.Replayed);
        Assert.Empty(fixture.Logs.Of(TestLogs.RetryEvent));
        await AssertPostedOnceAsync(scene, reply);
    }

    // ---- arrangement and assertions shared by both tests ----

    private sealed record Scene(Warehouse Source, Warehouse Target, Product Product, User Operator, ApiClient Client, string Reference, string Key, object Body, Guid FirstRow, Guid SecondRow);

    private async Task<Scene> ArrangeAsync()
    {
        var (source, target) = (await _world.WarehouseAsync(), await _world.WarehouseAsync());
        var product = await _world.ProductAsync();

        // Both balance rows exist before the transfer, so each side of the lock cycle is a plain row lock.
        await _world.StockAsync(source, product, 10);
        await _world.StockAsync(target, product, 5);
        var user = await _world.UserAsync([Roles.WarehouseOperator], [(source.Id, WarehouseLevel.Operate), (target.Id, WarehouseLevel.Operate)]);
        var client = await _world.LoginAsync(user);
        var reference = _world.Reference("TR");

        // StockPoster applies postings in (warehouse, product) order; the same ordering names the rows here.
        var ordered = new[] { source.Id, target.Id }.Order().ToArray();
        return new Scene(source, target, product, user, client, reference, ApiClient.NewKey(),
            World.TransferBody(reference, source.Id, target.Id, (product.Id, 4)), ordered[0], ordered[1]);
    }

    private async Task AssertUntouchedAsync(Scene scene)
    {
        Assert.Equal((10, 0), await _world.BalanceAsync(scene.Source.Id, scene.Product.Id));
        Assert.Equal((5, 0), await _world.BalanceAsync(scene.Target.Id, scene.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(scene.Source.Id, scene.Product.Id));
        Assert.Equal(1, await _world.MovementCountAsync(scene.Target.Id, scene.Product.Id));
        Assert.Equal(0, await DocumentCountAsync(scene.Reference));
        Assert.Equal(0, await _world.AuditCountAsync(scene.Operator.Id, "inventory.transfer_posted"));
        Assert.Equal(0, await _world.IdempotencyRecordCountAsync(scene.Key));
    }

    private async Task AssertPostedOnceAsync(Scene scene, Reply created)
    {
        async Task AssertOneEffectAsync()
        {
            Assert.Equal((6, 0), await _world.BalanceAsync(scene.Source.Id, scene.Product.Id));
            Assert.Equal((9, 0), await _world.BalanceAsync(scene.Target.Id, scene.Product.Id));
            Assert.Equal(1, await DocumentCountAsync(scene.Reference));
            Assert.Equal(1, await _world.MovementCountAsync(scene.Source.Id, scene.Product.Id, "TransferOut"));
            Assert.Equal(1, await _world.MovementCountAsync(scene.Target.Id, scene.Product.Id, "TransferIn"));
            Assert.Equal(2, await _world.MovementCountAsync(scene.Source.Id, scene.Product.Id));
            Assert.Equal(2, await _world.MovementCountAsync(scene.Target.Id, scene.Product.Id));
            Assert.Equal(1, await _world.AuditCountAsync(scene.Operator.Id, "inventory.transfer_posted"));
            Assert.Equal(1, await _world.IdempotencyRecordCountAsync(scene.Key));
            await _world.AssertReconciledAsync(scene.Source.Id, scene.Product.Id);
            await _world.AssertReconciledAsync(scene.Target.Id, scene.Product.Id);
        }

        await AssertOneEffectAsync();

        // The stored response is the one from the attempt that committed, and replaying it posts nothing.
        var replay = await Reply.ReadAsync(scene.Client.PostAsync("/api/inventory/transfers", scene.Body, scene.Key));
        Assert.Equal(HttpStatusCode.Created, replay.Status);
        Assert.True(replay.Replayed);
        Assert.Equal(created.Body, replay.Body);
        await AssertOneEffectAsync();
    }

    private Task<int> DocumentCountAsync(string reference) =>
        _world.CountAsync("SELECT COUNT(*) FROM inv.StockTransfers WHERE Reference = @r;", new SqlParameter("@r", reference));

    /// <summary>Runs on the request's own transaction connection; only ever called from inside a failpoint.</summary>
    private async Task RaiseLockTimeoutOfCurrentRequestAsync(CancellationToken cancellationToken)
    {
        var http = fixture.Services.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("The failpoint did not run inside a request.");
        var db = http.RequestServices.GetRequiredService<KantarDbContext>();
        Assert.NotNull(db.Database.CurrentTransaction);
        await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 30000;", cancellationToken);
    }

    // ---- what the server itself reports (read with the container's sysadmin login) ----

    private async Task<long> ServerScalarAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(fixture.SysadminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull
            ? throw new InvalidOperationException("The server returned no value for: " + sql)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>The engine's own cumulative count of deadlocks since it started.</summary>
    private Task<long> DeadlockCountAsync() => ServerScalarAsync(
        "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name LIKE N'Number of Deadlocks/sec%' AND instance_name = N'_Total';");

    private async Task WaitUntilBlockedAsync(short sessionId)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await ServerScalarAsync(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @s AND blocking_session_id <> 0 AND wait_type LIKE N'LCK[_]M[_]%';",
                new SqlParameter("@s", sessionId));
            if (waiting == 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Session {sessionId} never waited for a lock.");
    }

    /// <summary>
    /// A second session with the application's restricted login and its own connection (no pool, so its
    /// session settings cannot leak into the application's connections). It only takes update locks on
    /// balance rows; it changes no data.
    /// </summary>
    private sealed class Rival : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private SqlTransaction? _transaction;

        private Rival(SqlConnection connection) => _connection = connection;

        public short SessionId { get; private set; }

        public static async Task<Rival> OpenAsync(string appConnectionString, string deadlockPriority)
        {
            var builder = new SqlConnectionStringBuilder(appConnectionString) { Pooling = false, ApplicationName = "kantar-tests-rival" };
            var rival = new Rival(new SqlConnection(builder.ConnectionString));
            await rival._connection.OpenAsync();
            await using (var command = rival._connection.CreateCommand())
            {
                command.CommandText = $"SET DEADLOCK_PRIORITY {(deadlockPriority == "HIGH" ? "HIGH" : "NORMAL")}; SET LOCK_TIMEOUT -1; SELECT @@SPID;";
                rival.SessionId = Convert.ToInt16(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            rival._transaction = (SqlTransaction)await rival._connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            return rival;
        }

        /// <summary>Takes an update lock on one balance row and keeps it until the transaction ends.</summary>
        public async Task LockBalanceAsync(Guid warehouseId, Guid productId)
        {
            await using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandTimeout = 60;
            command.CommandText = "SELECT OnHand FROM inv.StockBalances WITH (UPDLOCK, ROWLOCK) WHERE WarehouseId = @w AND ProductId = @p;";
            command.Parameters.AddWithValue("@w", warehouseId);
            command.Parameters.AddWithValue("@p", productId);
            Assert.NotNull(await command.ExecuteScalarAsync());
        }

        public async Task CommitAsync()
        {
            if (_transaction is { } transaction)
            {
                _transaction = null;
                await transaction.CommitAsync();
                await transaction.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is { } transaction)
            {
                _transaction = null;
                try
                {
                    await transaction.RollbackAsync();
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    // The session may already be gone; closing the connection releases its locks either way.
                }

                await transaction.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }
    }
}
