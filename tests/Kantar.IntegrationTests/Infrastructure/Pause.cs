using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>
/// Holds the first command that reaches a failpoint inside its open transaction until the test
/// releases it. Used to build a known order between two commands on the same document: the first
/// is parked while it holds its locks, the second is observed waiting, then the first is let go.
/// </summary>
public sealed class Pause : IDisposable
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable _registration;
    private int _hits;

    public Pause(TestFailpoints failpoints, string name)
    {
        _registration = failpoints.On(name, async _ =>
        {
            if (Interlocked.Increment(ref _hits) == 1)
            {
                _reached.TrySetResult();
                await _release.Task;
            }
        });
    }

    /// <summary>Completes when the first command is parked at the failpoint.</summary>
    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(20));

    public void Release() => _release.TrySetResult();

    public void Dispose()
    {
        Release();
        _registration.Dispose();
    }
}

public static class LockObservation
{
    /// <summary>
    /// Polls the server until a request in the test database is waiting on a lock held by another
    /// session. This observes a condition with an upper bound; it is not a timing assumption.
    /// </summary>
    public static async Task WaitForBlockedRequestAsync(this KantarFixture fixture)
    {
        const string sql = """
            SELECT COUNT(*) FROM sys.dm_exec_requests
            WHERE database_id = DB_ID(@db) AND blocking_session_id <> 0 AND wait_type LIKE N'LCK%';
            """;
        await using var connection = new SqlConnection(fixture.SysadminConnectionString);
        await connection.OpenAsync();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(4))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@db", fixture.DatabaseName);
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("No request was observed waiting on a lock.");
    }
}
