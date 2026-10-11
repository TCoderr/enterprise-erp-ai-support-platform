using System.Data;
using System.Text.RegularExpressions;
using Kantar.Application.Abstractions;
using Kantar.Domain.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Kantar.Infrastructure.Persistence;

/// <summary>
/// One READ COMMITTED transaction per command with a bounded whole-transaction retry.
/// EF Core's retrying execution strategy is deliberately not used: it cannot wrap user transactions
/// transparently, and the retry unit here must be the complete business command.
/// </summary>
internal sealed partial class SqlUnitOfWork(KantarDbContext db, ILogger<SqlUnitOfWork> logger) : IUnitOfWork
{
    private const int MaxAttempts = 3;

    // 1205 deadlock victim, 1222 lock request timeout, 3960 snapshot update conflict,
    // 40197/40501/40613/49918… do not apply to a local SQL Server and are not listed.
    private static readonly HashSet<int> RetryableErrors = [1205, 1222, 3960];
    private static readonly HashSet<int> ConnectionErrors = [-2, 20, 64, 233, 10053, 10054, 10060];

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            // Already inside a unit of work (e.g. the seed composing several commands): join it.
            return await work(cancellationToken);
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                try
                {
                    await db.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 5000;", cancellationToken);
                    var result = await work(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return result;
                }
                catch
                {
                    await TryRollbackAsync(transaction);
                    db.ChangeTracker.Clear();
                    throw;
                }
            }
            catch (Exception ex) when (FindSqlException(ex) is { } sql)
            {
                if (sql.Number is 2601 or 2627)
                {
                    throw new UniqueConstraintException(ConstraintName(sql.Message), ex);
                }

                if (sql.Number == 547)
                {
                    // A CHECK or FOREIGN KEY refused the write. The database is the last guard; the
                    // request is rejected rather than retried.
                    LogIntegrity(logger, sql.Number);
                    throw Problems.Conflict("integrity_violation", "İşlem veri bütünlüğü kuralını ihlal ettiği için reddedildi.");
                }

                var retryable = RetryableErrors.Contains(sql.Number) || ConnectionErrors.Contains(sql.Number);
                if (!retryable)
                {
                    throw;
                }

                if (attempt >= MaxAttempts)
                {
                    LogGiveUp(logger, sql.Number, attempt);
                    throw Problems.Unavailable();
                }

                LogRetry(logger, sql.Number, attempt);
                await Task.Delay(Random.Shared.Next(50, 200) * attempt, cancellationToken);
            }
        }
    }

    private static async Task TryRollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The connection may already be gone; disposing the transaction is all that is left to do.
        }
    }

    internal static SqlException? FindSqlException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        return null;
    }

    /// <summary>
    /// 2627: "Violation of PRIMARY KEY/UNIQUE KEY constraint 'name'." 2601: "... with unique index 'name'."
    /// Only the constraint name is extracted; the duplicate key value in the message is never surfaced.
    /// </summary>
    internal static string ConstraintName(string message)
    {
        var index = UniqueIndexPattern().Match(message);
        if (index.Success)
        {
            return index.Groups[1].Value;
        }

        var constraint = ConstraintPattern().Match(message);
        return constraint.Success ? constraint.Groups[1].Value : "unknown";
    }

    [GeneratedRegex("unique index '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex UniqueIndexPattern();

    [GeneratedRegex("constraint '([^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ConstraintPattern();

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Transaction attempt {Attempt} failed with SQL error {Number}; retrying")]
    private static partial void LogRetry(ILogger logger, int number, int attempt);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Transaction failed with SQL error {Number} after {Attempt} attempts; giving up")]
    private static partial void LogGiveUp(ILogger logger, int number, int attempt);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "Database integrity constraint refused a write (SQL error {Number})")]
    private static partial void LogIntegrity(ILogger logger, int number);
}
