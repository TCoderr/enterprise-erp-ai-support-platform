using Kantar.Domain.Common;

namespace Kantar.Domain.Support;

public enum TicketStatus : byte
{
    Open = 1,
    InProgress = 2,
    WaitingForRequester = 3,
    Resolved = 4,
    Closed = 5,
}

public enum TicketPriority : byte
{
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

public enum AppModule : byte
{
    General = 1,
    Sales = 2,
    Purchasing = 3,
    Inventory = 4,
    Support = 5,
}

public enum RelatedDocumentType : byte
{
    SalesOrder = 1,
    PurchaseOrder = 2,
    Shipment = 3,
    GoodsReceipt = 4,
    StockTransfer = 5,
    StockAdjustment = 6,
}

public enum CommentSource : byte
{
    User = 1,
    AiSuggestion = 2,
}

public sealed class Ticket
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Number { get; set; }
    public Guid RequesterUserId { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public AppModule Module { get; set; } = AppModule.General;
    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Screen { get; set; }
    public string? ErrorCode { get; set; }

    /// <summary>Type and number as typed by the requester. It is a label, not a link that grants access.</summary>
    public RelatedDocumentType? RelatedDocumentType { get; set; }
    public string? RelatedDocumentNumber { get; set; }

    public string? DiagnosticJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<TicketComment> Comments { get; set; } = [];

    /// <summary>Screen codes a ticket may reference; anything else is rejected rather than stored.</summary>
    public static readonly IReadOnlySet<string> KnownScreens = new HashSet<string>(StringComparer.Ordinal)
    {
        "sales.orders", "sales.order", "sales.dispatch", "purchasing.orders", "purchasing.order", "purchasing.receive",
        "inventory.balances", "inventory.movements", "inventory.transfer", "inventory.adjustment",
        "support.tickets", "support.ticket", "kb.articles", "kb.manage", "assist.panel", "admin.users", "auth.login",
    };

    public void ChangeStatusByStaff(TicketStatus target, DateTime now)
    {
        var allowed = (Status, target) switch
        {
            (TicketStatus.Open, TicketStatus.InProgress or TicketStatus.Resolved) => true,
            (TicketStatus.InProgress, TicketStatus.WaitingForRequester or TicketStatus.Resolved) => true,
            (TicketStatus.WaitingForRequester, TicketStatus.InProgress or TicketStatus.Resolved) => true,
            (TicketStatus.Resolved, TicketStatus.Closed or TicketStatus.InProgress) => true,
            _ => false,
        };

        if (!allowed)
        {
            throw Problems.InvalidState($"Talep {Status} durumundan {target} durumuna geçemez.");
        }

        Status = target;
        UpdatedAt = now;
    }

    public void ReopenByRequester(DateTime now)
    {
        if (Status != TicketStatus.Resolved)
        {
            throw Problems.InvalidState("Yalnız çözüldü durumundaki talep yeniden açılabilir.");
        }

        Status = TicketStatus.InProgress;
        UpdatedAt = now;
    }
}

public sealed class TicketComment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TicketId { get; set; }
    public Guid AuthorUserId { get; set; }
    public required string Body { get; set; }
    public bool IsInternal { get; set; }
    public CommentSource Source { get; set; } = CommentSource.User;
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public string? CitationsJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public enum KbVisibility : byte
{
    General = 1,
    SupportOnly = 2,
    ManagerOnly = 3,
}

public enum KbStatus : byte
{
    Draft = 1,
    Published = 2,
    Retired = 3,
}

public sealed class KbDocument
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Slug { get; set; }
    public KbVisibility Visibility { get; set; }
    public AppModule Module { get; set; }
    public Guid? WarehouseId { get; set; }
    public string? ErrorCode { get; set; }
    public KbStatus Status { get; set; } = KbStatus.Draft;
    public Guid? PublishedVersionId { get; set; }
    public int LatestVersionNo { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public List<KbDocumentVersion> Versions { get; set; } = [];
}

/// <summary>Immutable once written; a correction is a new version.</summary>
public sealed class KbDocumentVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DocumentId { get; set; }
    public int VersionNo { get; set; }
    public required string Title { get; set; }
    public required string BodyMarkdown { get; set; }
    public Guid AuthorUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A section of one version. Served only while its version is the document's published version.</summary>
public sealed class KbChunk
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DocumentId { get; set; }
    public Guid VersionId { get; set; }
    public int SectionNo { get; set; }
    public required string Heading { get; set; }
    public required string Text { get; set; }
}
