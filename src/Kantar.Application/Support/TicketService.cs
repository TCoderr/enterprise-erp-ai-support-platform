using System.Reflection;
using System.Text.Json;
using Kantar.Application.Abstractions;
using Kantar.Application.Assistance;
using Kantar.Application.Common;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Support;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Support;

public sealed record RelatedDocumentInput(string Type, string Number);

public sealed record CreateTicketCommand(string Title, string Description, string Priority, string Module, string? Screen, string? ErrorCode, RelatedDocumentInput? RelatedDocument);

public sealed record AddCommentCommand(Guid TicketId, string Body, bool IsInternal);

public sealed record SaveSuggestionCommand(Guid TicketId, Guid AnswerId);

public sealed record TicketCommentDto(
    Guid Id, Guid AuthorUserId, string AuthorDisplayName, string Body, bool IsInternal, string Source, string? AiProvider, string? AiModel,
    IReadOnlyList<CitationDto>? Citations, DateTime CreatedAt);

/// <summary>
/// The whitelisted diagnostic snapshot. There is no free-form field and nothing is taken from the client.
/// Screen and ErrorCode are optional: a snapshot is stored without them when the ticket has none, and
/// must read back from that stored form.
/// </summary>
public sealed record TicketDiagnosticDto(
    string AppVersion, string Module, string CorrelationId, IReadOnlyList<string> RequesterRoles, DateTime ServerTimeUtc, string? Screen = null, string? ErrorCode = null);

public sealed record TicketDto(
    Guid Id, string Number, string Status, string Priority, string Module, string Title, string Description, string? Screen, string? ErrorCode,
    string? RelatedDocumentType, string? RelatedDocumentNumber, Guid RequesterUserId, string RequesterDisplayName,
    Guid? AssigneeUserId, string? AssigneeDisplayName, DateTime CreatedAt, DateTime UpdatedAt,
    TicketDiagnosticDto? Diagnostic, IReadOnlyList<TicketCommentDto> Comments, string ETag);

public sealed record TicketListItemDto(
    Guid Id, string Number, string Status, string Priority, string Module, string Title, string? ErrorCode,
    Guid RequesterUserId, Guid? AssigneeUserId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>One entry of the assignee picker. Roles lists the support roles only, not whatever else the user may do.</summary>
public sealed record SupportStaffDto(Guid Id, string DisplayName, IReadOnlyList<string> Roles);

public sealed class TicketService(
    IKantarDb db,
    IDocumentLocks locks,
    IDocumentNumbers numbers,
    KnowledgeService knowledge,
    IRequestContext requestContext,
    TimeProvider clock,
    AuditWriter audit,
    OutboxWriter outbox)
{
    public const string TicketAssignedEvent = "support.ticket_assigned";
    private static readonly string AppVersion = typeof(TicketService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ queries

    public async Task<Page<TicketListItemDto>> ListAsync(CurrentUser user, TicketStatus? status, string? assignee, bool mine, int? limit, string? cursor, CancellationToken ct)
    {
        var take = Guard.Limit(limit);
        IQueryable<Ticket> query = db.Tickets.AsNoTracking();

        // Non-staff see their own tickets only; the filter is not optional for them.
        if (!user.IsSupportStaff || mine)
        {
            query = query.Where(t => t.RequesterUserId == user.Id);
        }

        if (status is { } s)
        {
            query = query.Where(t => t.Status == s);
        }

        switch (assignee)
        {
            case null:
                break;
            case "me":
                query = query.Where(t => t.AssigneeUserId == user.Id);
                break;
            case "unassigned":
                query = query.Where(t => t.AssigneeUserId == null);
                break;
            default:
                throw Problems.Validation("assignee", "invalid", "me veya unassigned olmalıdır.");
        }

        if (cursor is not null)
        {
            if (!Cursor.TryDecode(cursor, out var at, out var lastId))
            {
                throw Problems.Validation("cursor", "invalid", "Geçersiz imleç.");
            }

            query = query.Where(t => t.CreatedAt < at || (t.CreatedAt == at && t.Id.CompareTo(lastId) < 0));
        }

        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        return new Page<TicketListItemDto>(
            page.Select(t => new TicketListItemDto(
                t.Id, t.Number, t.Status.ToString(), t.Priority.ToString(), t.Module.ToString(), t.Title, t.ErrorCode,
                t.RequesterUserId, t.AssigneeUserId, t.CreatedAt, t.UpdatedAt)).ToArray(),
            rows.Count > take ? Cursor.Encode(page[^1].CreatedAt, page[^1].Id) : null);
    }

    public async Task<TicketDto> GetAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Comments).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket is null || !AccessPolicy.CanSeeTicket(user, ticket))
        {
            throw Problems.NotFound();
        }

        return await ToDtoAsync(ticket, user, ct);
    }

    /// <summary>
    /// Active support staff, for choosing an assignee. Support staff only: who works in support is not
    /// something a requester needs, and the assignment rules are enforced on the command, not by this list.
    /// </summary>
    public async Task<IReadOnlyList<SupportStaffDto>> ListStaffAsync(CurrentUser user, CancellationToken ct)
    {
        if (!user.IsSupportStaff)
        {
            throw Problems.Forbidden();
        }

        var staff = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.Roles.Any(r => r.Role == Roles.SupportAgent || r.Role == Roles.SupportManager))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.UserName)
            .Take(1000)
            .Select(u => new { u.Id, u.DisplayName, Roles = u.Roles.Select(r => r.Role).ToList() })
            .ToListAsync(ct);
        return staff.Select(u => new SupportStaffDto(
            u.Id, u.DisplayName,
            u.Roles.Where(r => r is Roles.SupportAgent or Roles.SupportManager).Order(StringComparer.Ordinal).ToArray())).ToArray();
    }

    // ------------------------------------------------------------------ create

    public static CreateTicketCommand Normalize(CreateTicketCommand command)
    {
        var v = new Validator();
        var title = v.Text("title", command.Title, 5, 160);
        var description = v.Text("description", command.Description, 10, 4000, multiline: true);
        if (!IsEnumName<TicketPriority>(command.Priority))
        {
            v.Add("priority", "invalid", "Low, Normal, High veya Urgent.");
        }

        if (!IsEnumName<AppModule>(command.Module))
        {
            v.Add("module", "invalid", "General, Sales, Purchasing, Inventory veya Support.");
        }

        string? screen = null;
        if (!string.IsNullOrWhiteSpace(command.Screen))
        {
            screen = command.Screen.Trim();
            if (!Ticket.KnownScreens.Contains(screen))
            {
                v.Add("screen", "unknown", "Bilinmeyen ekran kodu.");
            }
        }

        string? errorCode = null;
        if (!string.IsNullOrWhiteSpace(command.ErrorCode))
        {
            errorCode = command.ErrorCode.Trim().ToUpperInvariant();
            if (!KnowledgeService.ErrorCodePattern().IsMatch(errorCode))
            {
                v.Add("errorCode", "invalid", "Hata kodu AAA-000 biçiminde olmalıdır.");
            }
        }

        RelatedDocumentInput? related = null;
        if (command.RelatedDocument is { } input)
        {
            var number = Text.NormalizeCode(input.Number ?? string.Empty);
            if (!IsEnumName<RelatedDocumentType>(input.Type) || !Text.IsCode(number, 4, 16))
            {
                v.Add("relatedDocument", "invalid", "Belge türü veya numarası geçersiz.");
            }

            related = new RelatedDocumentInput(input.Type ?? string.Empty, number);
        }

        v.ThrowIfInvalid();
        return new CreateTicketCommand(title, description, command.Priority, command.Module, screen, errorCode, related);
    }

    private static bool IsEnumName<TEnum>(string? value) where TEnum : struct, Enum =>
        !string.IsNullOrEmpty(value) && !char.IsAsciiDigit(value[0]) && value[0] != '-' && Enum.TryParse<TEnum>(value, out var parsed) && Enum.IsDefined(parsed);

    public ICommandHandler<CreateTicketCommand, TicketDto> Create => new DelegateHandler<CreateTicketCommand, TicketDto>(
        (_, _, _) => Task.CompletedTask,
        async (command, user, ct) =>
        {
            var now = Now;

            // The related document is stored as typed text only. Its existence is deliberately not
            // checked: a lookup here would let any user probe which document numbers exist.
            var ticket = new Ticket
            {
                Number = await numbers.NextAsync(DocumentKind.Ticket, ct),
                RequesterUserId = user.Id,
                Title = command.Title,
                Description = command.Description,
                Priority = Enum.Parse<TicketPriority>(command.Priority),
                Module = Enum.Parse<AppModule>(command.Module),
                Screen = command.Screen,
                ErrorCode = command.ErrorCode,
                RelatedDocumentType = command.RelatedDocument is { } related ? Enum.Parse<RelatedDocumentType>(related.Type) : null,
                RelatedDocumentNumber = command.RelatedDocument?.Number,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ticket.DiagnosticJson = JsonSerializer.Serialize(
                new TicketDiagnosticDto(AppVersion, command.Module, requestContext.CorrelationId, user.Roles.Order(StringComparer.Ordinal).ToArray(), now, command.Screen, command.ErrorCode),
                ApiJson.Options);

            db.Tickets.Add(ticket);
            audit.Write(user, "support.ticket_created", "Ticket", ticket.Id);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(ticket, user, ct);
        });

    // ------------------------------------------------------------------ comments

    public static AddCommentCommand Normalize(AddCommentCommand command)
    {
        var v = new Validator();
        var body = v.Text("body", command.Body, 1, 4000, multiline: true);
        v.ThrowIfInvalid();
        return command with { Body = body };
    }

    private async Task AuthorizeCommentAsync(Guid ticketId, bool isInternal, CurrentUser user, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null || !AccessPolicy.CanSeeTicket(user, ticket))
        {
            throw Problems.NotFound();
        }

        if (isInternal && !user.IsSupportStaff)
        {
            throw Problems.Forbidden();
        }
    }

    public ICommandHandler<AddCommentCommand, TicketDto> AddComment => new DelegateHandler<AddCommentCommand, TicketDto>(
        (command, user, ct) => AuthorizeCommentAsync(command.TicketId, command.IsInternal, user, ct),
        async (command, user, ct) =>
        {
            var ticket = await LockOpenTicketAsync(command.TicketId, ct);
            var now = Now;
            db.TicketComments.Add(new TicketComment
            {
                TicketId = ticket.Id, AuthorUserId = user.Id, Body = command.Body, IsInternal = command.IsInternal, CreatedAt = now,
            });
            ticket.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return await ReloadAsync(ticket.Id, user, ct);
        });

    /// <summary>
    /// Stores an assistance answer on a ticket as a clearly labelled suggestion. This is an ordinary,
    /// separately authorised command the user triggers explicitly; answer generation never writes here.
    /// The text comes from the server-side answer record, not from the request, and every cited source
    /// must still be readable by the caller in its cited version at this moment.
    /// </summary>
    public ICommandHandler<SaveSuggestionCommand, TicketDto> SaveSuggestion => new DelegateHandler<SaveSuggestionCommand, TicketDto>(
        (command, user, ct) => AuthorizeCommentAsync(command.TicketId, isInternal: false, user, ct),
        async (command, user, ct) =>
        {
            var now = Now;
            var ticket = await LockOpenTicketAsync(command.TicketId, ct);
            var answer = await db.AiAnswers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == command.AnswerId && a.UserId == user.Id && a.ExpiresAt > now, ct)
                ?? throw Problems.NotFound();
            if (answer.Status != nameof(AnswerStatus.Answered) || string.IsNullOrEmpty(answer.AnswerText))
            {
                throw Problems.InvalidState("Yalnız kaynaklı bir yanıt talebe kaydedilebilir.");
            }

            var citations = JsonSerializer.Deserialize<List<StoredCitation>>(answer.CitationsJson ?? "[]", ApiJson.Options) ?? [];
            var chunkIds = citations.Select(c => c.ChunkId).ToArray();
            var stillReadable = await knowledge.ReadableChunks(user).Where(c => chunkIds.Contains(c.ChunkId)).Select(c => c.ChunkId).ToListAsync(ct);
            if (citations.Count == 0 || stillReadable.Count != chunkIds.Length)
            {
                throw Problems.Conflict("source_no_longer_available", "Yanıtın dayandığı kaynaklardan en az biri artık yayında değil veya erişiminiz kalmadı.");
            }

            db.TicketComments.Add(new TicketComment
            {
                TicketId = ticket.Id,
                AuthorUserId = user.Id,
                Body = answer.AnswerText,
                IsInternal = false,
                Source = CommentSource.AiSuggestion,
                AiProvider = answer.Provider,
                AiModel = answer.Model,
                CitationsJson = JsonSerializer.Serialize(citations.Select(c => c.ToDto()).ToArray(), ApiJson.Options),
                CreatedAt = now,
            });
            ticket.UpdatedAt = now;
            audit.Write(user, "support.suggestion_saved", "Ticket", ticket.Id, detail: new { provider = answer.Provider, sources = citations.Count });
            await db.SaveChangesAsync(ct);
            return await ReloadAsync(ticket.Id, user, ct);
        });

    private async Task<Ticket> LockOpenTicketAsync(Guid id, CancellationToken ct)
    {
        var ticket = await locks.LockTicketAsync(id, ct) ?? throw Problems.NotFound();
        return ticket.Status == TicketStatus.Closed ? throw Problems.InvalidState("Kapalı talebe yorum eklenemez.") : ticket;
    }

    // ------------------------------------------------------------------ assign / status

    public async Task<TicketDto> AssignAsync(CurrentUser user, Guid id, Guid assigneeUserId, byte[]? expected, CancellationToken ct)
    {
        if (!user.IsSupportStaff)
        {
            // A requester may know the ticket exists but may not manage it; anyone else must not learn it exists.
            var visible = await db.Tickets.AsNoTracking().AnyAsync(t => t.Id == id && t.RequesterUserId == user.Id, ct);
            throw visible ? Problems.Forbidden() : Problems.NotFound();
        }

        if (!AccessPolicy.CanAssignTicket(user, assigneeUserId))
        {
            throw Problems.Forbidden();
        }

        var ticket = await locks.LockTicketAsync(id, ct) ?? throw Problems.NotFound();
        Guard.Precondition(ticket.RowVersion, expected);
        if (ticket.Status == TicketStatus.Closed)
        {
            throw Problems.InvalidState("Kapalı talep atanamaz.");
        }

        var assigneeIsStaff = await db.Users.AsNoTracking().AnyAsync(
            u => u.Id == assigneeUserId && u.IsActive && u.Roles.Any(r => r.Role == Roles.SupportAgent || r.Role == Roles.SupportManager), ct);
        if (!assigneeIsStaff)
        {
            throw Problems.Validation("assigneeUserId", "not_support_staff", "Talep yalnız aktif destek personeline atanabilir.");
        }

        ticket.AssigneeUserId = assigneeUserId;
        ticket.UpdatedAt = Now;
        audit.Write(user, "support.ticket_assigned", "Ticket", ticket.Id);
        outbox.Enqueue(TicketAssignedEvent, new TicketAssignedPayload(ticket.Id, ticket.Number, assigneeUserId));
        await db.SaveChangesAsync(ct);
        return await ReloadAsync(ticket.Id, user, ct);
    }

    public async Task<TicketDto> ChangeStatusAsync(CurrentUser user, Guid id, string status, byte[]? expected, CancellationToken ct)
    {
        if (!IsEnumName<TicketStatus>(status))
        {
            throw Problems.Validation("status", "invalid", "Geçersiz durum.");
        }

        var target = Enum.Parse<TicketStatus>(status);
        var ticket = await locks.LockTicketAsync(id, ct);
        if (ticket is null || !AccessPolicy.CanSeeTicket(user, ticket))
        {
            throw Problems.NotFound();
        }

        Guard.Precondition(ticket.RowVersion, expected);
        if (user.IsSupportStaff)
        {
            ticket.ChangeStatusByStaff(target, Now);
        }
        else if (target == TicketStatus.InProgress)
        {
            ticket.ReopenByRequester(Now);
        }
        else
        {
            throw Problems.Forbidden();
        }

        audit.Write(user, "support.ticket_status_changed", "Ticket", ticket.Id, detail: new { status = ticket.Status.ToString() });
        await db.SaveChangesAsync(ct);
        return await ReloadAsync(ticket.Id, user, ct);
    }

    // ------------------------------------------------------------------ mapping

    private async Task<TicketDto> ReloadAsync(Guid id, CurrentUser user, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Comments).FirstAsync(t => t.Id == id, ct);
        return await ToDtoAsync(ticket, user, ct);
    }

    private async Task<TicketDto> ToDtoAsync(Ticket ticket, CurrentUser viewer, CancellationToken ct)
    {
        var staff = viewer.IsSupportStaff;
        var comments = ticket.Comments.Where(c => staff || !c.IsInternal).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList();
        var userIds = comments.Select(c => c.AuthorUserId).Append(ticket.RequesterUserId).Concat(ticket.AssigneeUserId is { } a ? [a] : []).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        return new TicketDto(
            ticket.Id, ticket.Number, ticket.Status.ToString(), ticket.Priority.ToString(), ticket.Module.ToString(), ticket.Title, ticket.Description,
            ticket.Screen, ticket.ErrorCode, ticket.RelatedDocumentType?.ToString(), ticket.RelatedDocumentNumber,
            ticket.RequesterUserId, names.GetValueOrDefault(ticket.RequesterUserId, "—"),
            ticket.AssigneeUserId, ticket.AssigneeUserId is { } assignee ? names.GetValueOrDefault(assignee, "—") : null,
            ticket.CreatedAt, ticket.UpdatedAt,

            // The diagnostic snapshot is for support staff; the requester already knows what they entered.
            staff && ticket.DiagnosticJson is not null ? JsonSerializer.Deserialize<TicketDiagnosticDto>(ticket.DiagnosticJson, ApiJson.Options) : null,
            comments.Select(c => new TicketCommentDto(
                c.Id, c.AuthorUserId, names.GetValueOrDefault(c.AuthorUserId, "—"), c.Body, c.IsInternal, c.Source.ToString(), c.AiProvider, c.AiModel,
                c.CitationsJson is null ? null : JsonSerializer.Deserialize<List<CitationDto>>(c.CitationsJson, ApiJson.Options),
                c.CreatedAt)).ToArray(),
            ETags.From(ticket.RowVersion));
    }
}

public sealed record TicketAssignedPayload(Guid TicketId, string TicketNumber, Guid AssigneeUserId);
