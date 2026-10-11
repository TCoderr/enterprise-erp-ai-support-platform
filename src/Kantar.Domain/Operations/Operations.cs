namespace Kantar.Domain.Operations;

public sealed class IdempotencyRecord
{
    public const int HashVersionCurrent = 1;

    public Guid UserId { get; set; }
    public required string Operation { get; set; }
    public required string IdempotencyKey { get; set; }
    public required byte[] RequestHash { get; set; }
    public byte HashVersion { get; set; } = HashVersionCurrent;
    public short ResponseStatus { get; set; }
    public string? ResponseContentType { get; set; }
    public byte[]? ResponseBody { get; set; }
    public string? ResponseLocation { get; set; }
    public string? ResponseETag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Append-only. DetailJson holds a closed set of non-personal fields per action.</summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public Guid? WarehouseId { get; set; }
    public required string CorrelationId { get; set; }
    public string? DetailJson { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public int Attempts { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public long LeaseVersion { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? LastErrorCode { get; set; }
}

public sealed class OutboxConsumption
{
    public Guid MessageId { get; set; }
    public required string Handler { get; set; }
    public DateTime ConsumedAt { get; set; }
}

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Kind { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public sealed class ReconciliationRun
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public int BalancesChecked { get; set; }
    public int FindingCount { get; set; }
    public Guid? RequestedBy { get; set; }
    public List<ReconciliationFinding> Findings { get; set; } = [];
}

public sealed class ReconciliationFinding
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid ProductId { get; set; }
    public required string Rule { get; set; }
    public long Expected { get; set; }
    public long Actual { get; set; }
}

public sealed class AiAnswer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public required string Status { get; set; }
    public required string Provider { get; set; }
    public string? Model { get; set; }
    public string? AnswerText { get; set; }
    public string? LimitationsText { get; set; }
    public string? CitationsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Usage row without user or content: provider, outcome, duration and source count only.</summary>
public sealed class AiUsage
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public required string Provider { get; set; }
    public string? Model { get; set; }
    public required string Status { get; set; }
    public int DurationMs { get; set; }
    public int SourceCount { get; set; }
}
