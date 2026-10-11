using Kantar.Application.Abstractions;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Common;

public interface ICommandHandler<in TCommand, TResult>
{
    /// <summary>
    /// Read-only check of the caller's current permissions and resource scope. It also runs before a
    /// stored response is replayed, so a revoked role cannot read an old result back.
    /// </summary>
    Task AuthorizeAsync(TCommand command, CurrentUser user, CancellationToken cancellationToken);

    /// <summary>Runs inside the transaction. May be invoked again on retry; must not call external systems.</summary>
    Task<TResult> ExecuteAsync(TCommand command, CurrentUser user, CancellationToken cancellationToken);
}

public sealed record IdempotencyRequest(string Operation, string Key, byte[] RequestHash);

public sealed record ResponseShape(int Status, object? Body, string? Location = null, string? ETag = null);

public sealed record CommandOutcome(int Status, byte[] Body, string ContentType, string? Location, string? ETag, bool Replayed);

/// <summary>
/// Authorize → (replay?) → one transaction: idempotency claim + business effect + stored response → commit.
/// The claim and the effect commit together, so there is no observable "claimed but not done" state.
/// </summary>
public sealed class CommandExecutor(IKantarDb db, IUnitOfWork unitOfWork, TimeProvider clock, Operations.OpsMetrics metrics)
{
    public const string JsonContentType = "application/json; charset=utf-8";
    public const string IdempotencyPrimaryKey = "PK_IdempotencyRecords";
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>Unique constraints that represent a stable business reference (docs/02 §4.4).</summary>
    private static readonly HashSet<string> BusinessReferenceConstraints = new(StringComparer.Ordinal)
    {
        "UX_GoodsReceipts_Supplier_ExternalReference",
        "UX_Shipments_Order_DispatchReference",
        "UX_StockTransfers_Reference",
        "UX_StockAdjustments_Reference",
    };

    /// <summary>Runs the command and records its outcome under the operation name (a closed set of names from code).</summary>
    public async Task<CommandOutcome> RunAsync<TCommand, TResult>(
        ICommandHandler<TCommand, TResult> handler,
        TCommand command,
        CurrentUser user,
        IdempotencyRequest? idempotency,
        Func<TResult, ResponseShape> shape,
        CancellationToken cancellationToken)
    {
        var operation = idempotency?.Operation ?? typeof(TCommand).Name;
        try
        {
            var outcome = await RunCoreAsync(handler, command, user, idempotency, shape, cancellationToken);
            metrics.Command(operation, outcome.Replayed ? "replayed" : "ok");
            return outcome;
        }
        catch (DomainException)
        {
            metrics.Command(operation, "rejected");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            metrics.Command(operation, "error");
            throw;
        }
    }

    private async Task<CommandOutcome> RunCoreAsync<TCommand, TResult>(
        ICommandHandler<TCommand, TResult> handler,
        TCommand command,
        CurrentUser user,
        IdempotencyRequest? idempotency,
        Func<TResult, ResponseShape> shape,
        CancellationToken cancellationToken)
    {
        await handler.AuthorizeAsync(command, user, cancellationToken);

        if (idempotency is not null && await FindAsync(user, idempotency, cancellationToken) is { } stored)
        {
            return Replay(stored, idempotency);
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    IdempotencyRecord? record = null;
                    if (idempotency is not null)
                    {
                        record = await ClaimAsync(user, idempotency, ct);
                    }

                    var result = await handler.ExecuteAsync(command, user, ct);
                    var response = shape(result);
                    var body = response.Body is null ? [] : ApiJson.Serialize(response.Body);

                    if (record is not null)
                    {
                        record.ResponseStatus = (short)response.Status;
                        record.ResponseContentType = JsonContentType;
                        record.ResponseBody = body;
                        record.ResponseLocation = response.Location;
                        record.ResponseETag = response.ETag;
                    }

                    await db.SaveChangesAsync(ct);
                    return new CommandOutcome(response.Status, body, JsonContentType, response.Location, response.ETag, Replayed: false);
                },
                cancellationToken);
        }
        catch (UniqueConstraintException ex) when (ex.ConstraintName == IdempotencyPrimaryKey && idempotency is not null)
        {
            // A concurrent request with the same key committed first. Its transaction is finished and
            // ours is rolled back, so this read sees the completed record.
            db.ClearTracking();
            var winner = await FindAsync(user, idempotency, cancellationToken)
                ?? throw Problems.Unavailable();
            return Replay(winner, idempotency);
        }
        catch (UniqueConstraintException ex) when (BusinessReferenceConstraints.Contains(ex.ConstraintName))
        {
            throw Problems.Conflict("duplicate_business_reference", "Bu iş referansı daha önce kullanıldı; ikinci bir stok etkisi oluşturulmadı.");
        }
    }

    private async Task<IdempotencyRecord?> FindAsync(CurrentUser user, IdempotencyRequest request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(
            r => r.UserId == user.Id && r.Operation == request.Operation && r.IdempotencyKey == request.Key && r.ExpiresAt > now,
            cancellationToken);
    }

    private static CommandOutcome Replay(IdempotencyRecord stored, IdempotencyRequest request)
    {
        if (stored.HashVersion != IdempotencyRecord.HashVersionCurrent
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(stored.RequestHash, request.RequestHash))
        {
            throw Problems.Conflict("idempotency_key_reused", "Bu Idempotency-Key farklı bir istekle kullanılmış.");
        }

        return new CommandOutcome(
            stored.ResponseStatus,
            stored.ResponseBody ?? [],
            stored.ResponseContentType ?? JsonContentType,
            stored.ResponseLocation,
            stored.ResponseETag,
            Replayed: true);
    }

    private async Task<IdempotencyRecord> ClaimAsync(CurrentUser user, IdempotencyRequest request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // An expired record under the same key would block the insert; it is no longer a valid replay source.
        await db.IdempotencyRecords
            .Where(r => r.UserId == user.Id && r.Operation == request.Operation && r.IdempotencyKey == request.Key && r.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        var record = new IdempotencyRecord
        {
            UserId = user.Id,
            Operation = request.Operation,
            IdempotencyKey = request.Key,
            RequestHash = request.RequestHash,
            CreatedAt = now,
            ExpiresAt = now + Retention,
        };
        db.IdempotencyRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }
}
