using System.Text.Json;
using Kantar.Application.Abstractions;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Common;

/// <summary>Adapts two delegates to the two-phase handler contract for commands too small to deserve a class.</summary>
public sealed class DelegateHandler<TCommand, TResult>(
    Func<TCommand, CurrentUser, CancellationToken, Task> authorize,
    Func<TCommand, CurrentUser, CancellationToken, Task<TResult>> execute) : ICommandHandler<TCommand, TResult>
{
    public Task AuthorizeAsync(TCommand command, CurrentUser user, CancellationToken cancellationToken) =>
        authorize(command, user, cancellationToken);

    public Task<TResult> ExecuteAsync(TCommand command, CurrentUser user, CancellationToken cancellationToken) =>
        execute(command, user, cancellationToken);
}

/// <summary>Writes audit rows into the current transaction. Details are small, typed and free of personal data.</summary>
public sealed class AuditWriter(IKantarDb db, IRequestContext requestContext, TimeProvider clock)
{
    public void Write(CurrentUser actor, string action, string entityType, Guid entityId, Guid? warehouseId = null, object? detail = null) =>
        Write(actor.Id, action, entityType, entityId, warehouseId, detail);

    public void Write(Guid actorUserId, string action, string entityType, Guid entityId, Guid? warehouseId = null, object? detail = null)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = clock.GetUtcNow().UtcDateTime,
            ActorUserId = actorUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            WarehouseId = warehouseId,
            CorrelationId = requestContext.CorrelationId,
            DetailJson = detail is null ? null : JsonSerializer.Serialize(detail, ApiJson.Options),
        });
    }
}

/// <summary>Adds outbox rows to the current transaction; the worker delivers them after commit.</summary>
public sealed class OutboxWriter(IKantarDb db, TimeProvider clock)
{
    public void Enqueue(string type, object payload)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, ApiJson.Options),
            CreatedAt = now,
            AvailableAt = now,
        });
    }
}

/// <summary>
/// Display names for the user ids on a page of documents, read in one query for the whole page.
/// An id without a user row shows as a dash rather than failing the read.
/// </summary>
public static class UserDisplayNames
{
    public const string Unknown = "—";

    public static async Task<IReadOnlyDictionary<Guid, string>> LoadAsync(IKantarDb db, IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToArray();
        return ids.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
    }

    public static async Task<string> OfAsync(IKantarDb db, Guid userId, CancellationToken cancellationToken) =>
        (await LoadAsync(db, [userId], cancellationToken)).GetValueOrDefault(userId, Unknown);

    public static string Of(this IReadOnlyDictionary<Guid, string> names, Guid userId) => names.GetValueOrDefault(userId, Unknown);
}

public static class Guard
{
    public static void Precondition(byte[] current, byte[]? expected)
    {
        if (expected is null)
        {
            throw Problems.PreconditionRequired();
        }

        if (!current.AsSpan().SequenceEqual(expected))
        {
            throw Problems.PreconditionFailed();
        }
    }

    /// <summary>
    /// A search term is user text like any other: trimmed, NFC, bounded and free of control or invisible
    /// characters. (A NUL inside a LIKE pattern is ignored by the collation and would match every row.)
    /// Returns null when there is nothing to search for.
    /// </summary>
    public static string? Search(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        if (!Text.TryClean(search, multiline: false, out var term))
        {
            throw Problems.Validation("search", "invalid_characters", "Denetim veya görünmez karakterler kullanılamaz.");
        }

        if (term.Length > 64)
        {
            throw Problems.Validation("search", "length", "Arama en fazla 64 karakter olabilir.");
        }

        return term.Length == 0 ? null : term;
    }

    public static int Limit(int? requested)
    {
        var limit = requested ?? 25;
        if (limit is < 1 or > 100)
        {
            throw Problems.Validation("limit", "out_of_range", "limit 1 ile 100 arasında olmalıdır.");
        }

        return limit;
    }
}
