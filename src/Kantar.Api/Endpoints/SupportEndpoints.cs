using Kantar.Api.Http;
using Kantar.Application.Abstractions;
using Kantar.Application.Assistance;
using Kantar.Application.Common;
using Kantar.Application.Support;
using Kantar.Domain.Common;
using Kantar.Domain.Support;

namespace Kantar.Api.Endpoints;

public sealed record CreateTicketRequest(string Title, string Description, string Priority, string Module, string? Screen = null, string? ErrorCode = null, RelatedDocumentInput? RelatedDocument = null);

public sealed record AddCommentRequest(string Body, bool IsInternal = false);

public sealed record SaveSuggestionRequest(Guid AnswerId);

public sealed record AssignTicketRequest(Guid AssigneeUserId);

public sealed record ChangeTicketStatusRequest(string Status);

public sealed record CreateKbDocumentRequest(string Slug, string Visibility, string Module, string Title, string BodyMarkdown, Guid? WarehouseId = null, string? ErrorCode = null);

public sealed record AddKbVersionRequest(string Title, string BodyMarkdown);

public sealed record PublishKbRequest(int? VersionNo = null);

public sealed record AssistSearchRequest(string Query);

public sealed record AssistAskRequest(string Question, Guid? TicketId = null);

public static class SupportEndpoints
{
    private static string? IfMatchHeader(HttpContext context)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        return string.IsNullOrEmpty(header) ? null : header;
    }

    public static void Map(RouteGroupBuilder api)
    {
        MapTickets(api);
        MapKnowledge(api);
        MapAssist(api);
    }

    private static void MapTickets(RouteGroupBuilder api)
    {
        var tickets = api.MapGroup("/support/tickets").WithTags("Support");

        api.MapGet("/support/staff", async (HttpContext context, TicketService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.ListStaffAsync(context.Caller(), context.RequestAborted));
        }).WithTags("Support").Produces<IReadOnlyList<SupportStaffDto>>();

        tickets.MapGet("", async (HttpContext context, TicketService service) =>
        {
            context.AllowQuery("status", "assignee", "mine", "limit", "cursor");
            await context.WriteJsonAsync(await service.ListAsync(
                context.Caller(), context.QueryEnum<TicketStatus>("status"), context.QueryString("assignee"), context.QueryBool("mine") ?? false,
                context.QueryInt("limit"), context.QueryString("cursor"), context.RequestAborted));
        }).Produces<Page<TicketListItemDto>>();

        tickets.MapPost("", async (HttpContext context, TicketService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CreateTicketRequest>(context);
            var command = TicketService.Normalize(new CreateTicketCommand(body.Title, body.Description, body.Priority, body.Module, body.Screen, body.ErrorCode, body.RelatedDocument));
            var outcome = await executor.RunAsync(
                service.Create, command, context.Caller(), context.Idempotency("support.ticket.create", command),
                t => new ResponseShape(StatusCodes.Status201Created, t, $"/api/support/tickets/{t.Id}", t.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CreateTicketRequest>("application/json").Produces<TicketDto>(StatusCodes.Status201Created);

        tickets.MapGet("/{id:guid}", async (Guid id, HttpContext context, TicketService service) =>
        {
            context.AllowQuery();
            var ticket = await service.GetAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(ticket, etag: ticket.ETag);
        }).Produces<TicketDto>();

        tickets.MapPost("/{id:guid}/comments", async (Guid id, HttpContext context, TicketService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<AddCommentRequest>(context);
            var command = TicketService.Normalize(new AddCommentCommand(id, body.Body, body.IsInternal));
            var outcome = await executor.RunAsync(
                service.AddComment, command, context.Caller(), context.Idempotency("support.ticket.comment", command),
                t => new ResponseShape(StatusCodes.Status201Created, t, ETag: t.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<AddCommentRequest>("application/json").Produces<TicketDto>(StatusCodes.Status201Created);

        // A separate, explicit command: the user chooses to store an assistance answer on the ticket.
        tickets.MapPost("/{id:guid}/comments/from-assist", async (Guid id, HttpContext context, TicketService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<SaveSuggestionRequest>(context);
            var command = new SaveSuggestionCommand(id, body.AnswerId);
            var outcome = await executor.RunAsync(
                service.SaveSuggestion, command, context.Caller(), context.Idempotency("support.ticket.suggestion", command),
                t => new ResponseShape(StatusCodes.Status201Created, t, ETag: t.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<SaveSuggestionRequest>("application/json").Produces<TicketDto>(StatusCodes.Status201Created);

        tickets.MapPost("/{id:guid}/assign", async (Guid id, HttpContext context, TicketService service, IUnitOfWork unitOfWork) =>
        {
            var body = await JsonBody.ReadAsync<AssignTicketRequest>(context);
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var ticket = await unitOfWork.ExecuteAsync(ct => service.AssignAsync(caller, id, body.AssigneeUserId, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(ticket, etag: ticket.ETag);
        }).Accepts<AssignTicketRequest>("application/json").Produces<TicketDto>();

        tickets.MapPost("/{id:guid}/status", async (Guid id, HttpContext context, TicketService service, IUnitOfWork unitOfWork) =>
        {
            var body = await JsonBody.ReadAsync<ChangeTicketStatusRequest>(context);
            var (caller, expected) = (context.Caller(), context.IfMatch());
            var ticket = await unitOfWork.ExecuteAsync(ct => service.ChangeStatusAsync(caller, id, body.Status, expected, ct), context.RequestAborted);
            await context.WriteJsonAsync(ticket, etag: ticket.ETag);
        }).Accepts<ChangeTicketStatusRequest>("application/json").Produces<TicketDto>();
    }

    private static void MapKnowledge(RouteGroupBuilder api)
    {
        var kb = api.MapGroup("/kb").WithTags("KnowledgeBase");

        kb.MapGet("/articles", async (HttpContext context, KnowledgeService service) =>
        {
            context.AllowQuery("search", "module", "errorCode");
            await context.WriteJsonAsync(await service.ListArticlesAsync(
                context.Caller(), context.QueryString("search"), context.QueryEnum<AppModule>("module"), context.QueryString("errorCode"), context.RequestAborted));
        }).Produces<IReadOnlyList<KbArticleListItemDto>>();

        kb.MapGet("/articles/{slug}", async (string slug, HttpContext context, KnowledgeService service) =>
        {
            context.AllowQuery();
            if (slug.Length > 80)
            {
                throw Problems.NotFound();
            }

            await context.WriteJsonAsync(await service.GetArticleAsync(context.Caller(), slug, context.RequestAborted));
        }).Produces<KbArticleDto>();

        kb.MapGet("/manage/documents", async (HttpContext context, KnowledgeService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.ListDocumentsAsync(context.Caller(), context.RequestAborted));
        }).Produces<IReadOnlyList<KbDocumentDto>>();

        kb.MapGet("/manage/documents/{id:guid}", async (Guid id, HttpContext context, KnowledgeService service) =>
        {
            context.AllowQuery();
            var document = await service.GetDocumentAsync(context.Caller(), id, context.RequestAborted);
            await context.WriteJsonAsync(document, etag: document.ETag);
        }).Produces<KbDocumentDto>();

        kb.MapPost("/manage/documents", async (HttpContext context, KnowledgeService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<CreateKbDocumentRequest>(context);
            var command = KnowledgeService.Normalize(new CreateKbDocumentCommand(body.Slug, body.Visibility, body.Module, body.WarehouseId, body.ErrorCode, body.Title, body.BodyMarkdown));
            var outcome = await executor.RunAsync(
                service.CreateDocument, command, context.Caller(), context.Idempotency("kb.document.create", command),
                d => new ResponseShape(StatusCodes.Status201Created, d, $"/api/kb/manage/documents/{d.Id}", d.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<CreateKbDocumentRequest>("application/json").Produces<KbDocumentDto>(StatusCodes.Status201Created);

        kb.MapPost("/manage/documents/{id:guid}/versions", async (Guid id, HttpContext context, KnowledgeService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<AddKbVersionRequest>(context);
            var command = KnowledgeService.Normalize(new AddKbVersionCommand(id, body.Title, body.BodyMarkdown, IfMatchHeader(context)));
            var outcome = await executor.RunAsync(
                service.AddVersion, command, context.Caller(), context.Idempotency("kb.document.version", command),
                d => new ResponseShape(StatusCodes.Status201Created, d, ETag: d.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<AddKbVersionRequest>("application/json").Produces<KbDocumentDto>(StatusCodes.Status201Created);

        kb.MapPost("/manage/documents/{id:guid}/publish", async (Guid id, HttpContext context, KnowledgeService service, CommandExecutor executor) =>
        {
            var body = await JsonBody.ReadAsync<PublishKbRequest>(context);
            var command = new KbPublishCommand(id, body.VersionNo, IfMatchHeader(context));
            var outcome = await executor.RunAsync(
                service.Publish, command, context.Caller(), context.Idempotency("kb.document.publish", command),
                d => new ResponseShape(StatusCodes.Status200OK, d, ETag: d.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Accepts<PublishKbRequest>("application/json").Produces<KbDocumentDto>();

        kb.MapPost("/manage/documents/{id:guid}/retire", async (Guid id, HttpContext context, KnowledgeService service, CommandExecutor executor) =>
        {
            var command = new KbPublishCommand(id, null, IfMatchHeader(context));
            var outcome = await executor.RunAsync(
                service.Retire, command, context.Caller(), context.Idempotency("kb.document.retire", command),
                d => new ResponseShape(StatusCodes.Status200OK, d, ETag: d.ETag), context.RequestAborted);
            await context.WriteOutcomeAsync(outcome);
        }).Produces<KbDocumentDto>();
    }

    private static void MapAssist(RouteGroupBuilder api)
    {
        var assist = api.MapGroup("/assist").WithTags("Assistance");

        assist.MapGet("/status", (HttpContext context, AssistService service) =>
        {
            context.AllowQuery();
            return context.WriteJsonAsync(service.Provider);
        }).Produces<ProviderInfoDto>();

        // POST because the query can contain personal data and must not end up in URLs or access logs.
        assist.MapPost("/search", async (HttpContext context, AssistService service) =>
        {
            var body = await JsonBody.ReadAsync<AssistSearchRequest>(context);
            var query = AssistService.NormalizeQuestion(body.Query, "query");
            await context.WriteJsonAsync(await service.SearchAsync(context.Caller(), query, context.RequestAborted));
        }).Accepts<AssistSearchRequest>("application/json").Produces<IReadOnlyList<SourceDto>>();

        assist.MapPost("/ask", async (HttpContext context, AssistService service) =>
        {
            var body = await JsonBody.ReadAsync<AssistAskRequest>(context);
            var command = new AskCommand(AssistService.NormalizeQuestion(body.Question), body.TicketId);
            await context.WriteJsonAsync(await service.AskAsync(context.Caller(), command, context.RequestAborted));
        }).RequireRateLimiting("assist").Accepts<AssistAskRequest>("application/json").Produces<AnswerDto>();

        assist.MapGet("/answers/{id:guid}", async (Guid id, HttpContext context, AssistService service) =>
        {
            context.AllowQuery();
            await context.WriteJsonAsync(await service.GetAnswerAsync(context.Caller(), id, context.RequestAborted));
        }).Produces<AnswerDto>();
    }
}
