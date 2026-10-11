using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Kantar.IntegrationTests.Support;

/// <summary>Support tickets through the HTTP API: who sees what, who may change what, and what a ticket never gives access to.</summary>
public sealed class TicketTests(KantarFixture fixture)
{
    private static readonly string[] DiagnosticFields = ["appVersion", "correlationId", "errorCode", "module", "requesterRoles", "screen", "serverTimeUtc"];

    private static readonly string[] TicketFields =
    [
        "assigneeDisplayName", "assigneeUserId", "comments", "createdAt", "description", "diagnostic", "eTag", "errorCode", "id", "module", "number",
        "priority", "relatedDocumentNumber", "relatedDocumentType", "requesterDisplayName", "requesterUserId", "screen", "status", "title", "updatedAt",
    ];

    private readonly World _world = fixture.NewWorld();

    private static string Url(Guid id, string action = "") => $"/api/support/tickets/{id}{action}";

    private static async Task<List<JsonNode>> ListAllAsync(ApiClient client, string query = "")
    {
        var items = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var page = await client.GetJsonAsync(
                "/api/support/tickets?limit=100" + (query.Length == 0 ? string.Empty : "&" + query) + (cursor is null ? string.Empty : "&cursor=" + cursor));
            items.AddRange(page["items"]!.AsArray().Select(i => i!));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        return items;
    }

    private static IEnumerable<string> AllStrings(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => AllStrings(p.Value)),
        JsonArray array => array.SelectMany(AllStrings),
        JsonValue value when value.TryGetValue<string>(out var text) => [text],
        _ => [],
    };

    // ------------------------------------------------------------------ create and diagnostic snapshot

    [Fact]
    public async Task Create_builds_the_diagnostic_snapshot_from_a_whitelist_and_shows_it_to_support_staff_only()
    {
        var requesterUser = await _world.UserAsync([Roles.SalesAgent, Roles.WarehouseOperator]);
        var requester = await _world.LoginAsync(requesterUser);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var marker = SupportWorld.Canary();

        using var created = await requester.PostAsync("/api/support/tickets", new
        {
            title = "Sipariş onaylanmıyor",
            description = $"Onayla düğmesine basınca hata alıyorum. {marker}",
            priority = "High",
            module = "Sales",
            screen = "sales.order",
            errorCode = "stk-409",
        }, ApiClient.NewKey());
        var ticket = await created.JsonAsync(HttpStatusCode.Created);
        var correlationId = created.Headers.GetValues("X-Correlation-Id").Single();

        Assert.Equal(Url(ticket.Id()), created.Headers.Location?.ToString());
        Assert.StartsWith("TK-", ticket.Str("number"), StringComparison.Ordinal);
        Assert.Equal(("Open", "High", "Sales", "sales.order", "STK-409"), (ticket.Str("status"), ticket.Str("priority"), ticket.Str("module"), ticket.Str("screen"), ticket.Str("errorCode")));
        Assert.Equal(requesterUser.Id, ticket.Id("requesterUserId"));
        Assert.Null(ticket["assigneeUserId"]);
        Assert.Empty(ticket["comments"]!.AsArray());

        // The requester gets no snapshot, neither in the create response nor when reading the ticket back.
        Assert.Null(ticket["diagnostic"]);
        var (own, _) = await SupportWorld.GetTicketAsync(requester, ticket.Id());
        Assert.Null(own["diagnostic"]);

        var (staffView, _) = await SupportWorld.GetTicketAsync(agent, ticket.Id());
        var diagnostic = staffView["diagnostic"]!.AsObject();
        Assert.Equal(DiagnosticFields, diagnostic.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("Sales", "sales.order", "STK-409"), (diagnostic.Str("module"), diagnostic.Str("screen"), diagnostic.Str("errorCode")));
        Assert.Equal(correlationId, diagnostic.Str("correlationId"));
        Assert.Equal([Roles.SalesAgent, Roles.WarehouseOperator], diagnostic["requesterRoles"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
        Assert.Matches("^[0-9]+(\\.[0-9]+)*(-[0-9A-Za-z.]+)?$", diagnostic.Str("appVersion"));
        var serverTime = diagnostic["serverTimeUtc"]!.GetValue<DateTime>().ToUniversalTime();
        Assert.InRange(serverTime, fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(-5), fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(5));

        // What is stored is the same closed set: nothing typed by the user, nothing about the connection.
        var stored = await fixture.AppScalarAsync<string>("SELECT DiagnosticJson FROM sup.Tickets WHERE Id = @id;", new SqlParameter("@id", ticket.Id()));
        Assert.NotNull(stored);
        Assert.Equal(DiagnosticFields, JsonNode.Parse(stored)!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(marker, stored, StringComparison.Ordinal);
        Assert.DoesNotContain(requesterUser.UserName, stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnostic_snapshot_of_a_ticket_without_screen_and_error_code_has_no_field_outside_the_whitelist()
    {
        var requester = await _world.LoginAsAsync([Roles.PurchasingAgent]);
        var (ticket, _) = await SupportWorld.CreateTicketAsync(requester);

        var stored = await fixture.AppScalarAsync<string>("SELECT DiagnosticJson FROM sup.Tickets WHERE Id = @id;", new SqlParameter("@id", ticket.Id()));

        Assert.NotNull(stored);
        var keys = JsonNode.Parse(stored)!.AsObject().Select(p => p.Key).ToArray();
        Assert.All(keys, key => Assert.Contains(key, DiagnosticFields));
        Assert.Equal(["appVersion", "correlationId", "module", "requesterRoles", "serverTimeUtc"], keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Create_is_idempotent_for_the_same_key_and_body()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var body = new { title = "Aynı talep iki kez", description = "Bağlantı koptu, yeniden gönderdim.", priority = "Low", module = "General" };
        var key = ApiClient.NewKey();

        using var first = await requester.PostAsync("/api/support/tickets", body, key);
        using var second = await requester.PostAsync("/api/support/tickets", body, key);
        using var differentBody = await requester.PostAsync("/api/support/tickets", body with { priority = "Urgent" }, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsByteArrayAsync(), await second.Content.ReadAsByteArrayAsync());
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replay").Single());
        await differentBody.ProblemAsync(HttpStatusCode.Conflict, "idempotency_key_reused");
        Assert.Single(await ListAllAsync(requester));
    }

    // ------------------------------------------------------------------ validation

    private static readonly Dictionary<string, (Action<JsonObject> Mutate, string Code, string? Field)> InvalidTickets = new(StringComparer.Ordinal)
    {
        ["unknown screen code"] = (t => t["screen"] = "sales.secret", "validation_failed", "screen"),
        ["screen code in another casing"] = (t => t["screen"] = "Sales.Order", "validation_failed", "screen"),
        ["screen code with markup"] = (t => t["screen"] = "<script>alert(1)</script>", "validation_failed", "screen"),
        ["error code without a dash"] = (t => t["errorCode"] = "STK409", "validation_failed", "errorCode"),
        ["error code with two letters"] = (t => t["errorCode"] = "ST-409", "validation_failed", "errorCode"),
        ["error code followed by text"] = (t => t["errorCode"] = "STK-409; DROP TABLE", "validation_failed", "errorCode"),
        ["related document number with spaces and punctuation"] = (t => t["relatedDocument"] = new JsonObject { ["type"] = "SalesOrder", ["number"] = "SO 1; DROP TABLE" }, "validation_failed", "relatedDocument"),
        ["related document number too short"] = (t => t["relatedDocument"] = new JsonObject { ["type"] = "SalesOrder", ["number"] = "SO" }, "validation_failed", "relatedDocument"),
        ["related document number too long"] = (t => t["relatedDocument"] = new JsonObject { ["type"] = "SalesOrder", ["number"] = "SO-00000000000000001" }, "validation_failed", "relatedDocument"),
        ["related document of an unknown type"] = (t => t["relatedDocument"] = new JsonObject { ["type"] = "Invoice", ["number"] = "SO-00000001" }, "validation_failed", "relatedDocument"),
        ["related document type given as a number"] = (t => t["relatedDocument"] = new JsonObject { ["type"] = "1", ["number"] = "SO-00000001" }, "validation_failed", "relatedDocument"),
        ["title too short"] = (t => t["title"] = "abc", "validation_failed", "title"),
        ["title too long"] = (t => t["title"] = new string('a', 161), "validation_failed", "title"),
        ["description too short"] = (t => t["description"] = "kısa", "validation_failed", "description"),
        ["description too long"] = (t => t["description"] = new string('a', 4001), "validation_failed", "description"),
        ["unknown priority"] = (t => t["priority"] = "Critical", "validation_failed", "priority"),
        ["priority given as a number"] = (t => t["priority"] = "2", "validation_failed", "priority"),
        ["unknown module"] = (t => t["module"] = "Finance", "validation_failed", "module"),
        ["client supplied diagnostic"] = (t => t["diagnostic"] = new JsonObject { ["appVersion"] = "9.9", ["ip"] = "203.0.113.7" }, "unknown_field", null),
        ["client supplied status"] = (t => t["status"] = "Closed", "unknown_field", null),
        ["client supplied requester"] = (t => t["requesterUserId"] = Guid.NewGuid().ToString(), "unknown_field", null),
        ["client supplied assignee"] = (t => t["assigneeUserId"] = Guid.NewGuid().ToString(), "unknown_field", null),
        ["client supplied number"] = (t => t["number"] = "TK-00000001", "unknown_field", null),
        ["related document with an id"] = (t => t["relatedDocument"] = new JsonObject { ["type"] = "SalesOrder", ["number"] = "SO-00000001", ["id"] = Guid.NewGuid().ToString() }, "unknown_field", null),
    };

    public static TheoryData<string> InvalidTicketNames => [.. InvalidTickets.Keys];

    [Theory]
    [MemberData(nameof(InvalidTicketNames))]
    public async Task Invalid_ticket_is_rejected_and_nothing_is_stored(string name)
    {
        var (mutate, code, field) = InvalidTickets[name];
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var body = new JsonObject
        {
            ["title"] = "Geçerli bir başlık",
            ["description"] = "Geçerli ve yeterince uzun bir açıklama.",
            ["priority"] = "Normal",
            ["module"] = "General",
        };
        mutate(body);

        using var response = await requester.PostAsync("/api/support/tickets", body, ApiClient.NewKey());

        var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, code);
        if (field is not null)
        {
            Assert.Contains(problem["errors"]!.AsArray(), e => e!.Str("field") == field);
        }

        Assert.Empty(await ListAllAsync(requester));
    }

    // ------------------------------------------------------------------ visibility and list scoping

    [Fact]
    public async Task Requester_sees_the_ticket_another_user_gets_not_found_and_staff_see_it()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var outsider = await _world.LoginAsAsync([Roles.SalesManager]);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();

        using var own = await requester.GetAsync(Url(id));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(etag, own.ETag());

        // Not being the requester and not being support staff looks exactly like a ticket that does not exist.
        using var missing = await outsider.GetAsync(Url(Guid.NewGuid()));
        var missingProblem = await missing.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        foreach (var client in new[] { outsider, admin })
        {
            using var read = await client.GetAsync(Url(id));
            var problem = await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
            Assert.Equal(missingProblem.Str("title"), problem.Str("title"));
            Assert.Null(problem["detail"]);

            using var comment = await client.PostAsync(Url(id, "/comments"), new { body = "Ben de aynı sorunu yaşıyorum." }, ApiClient.NewKey());
            await comment.ProblemAsync(HttpStatusCode.NotFound, "not_found");
            using var transition = await client.PostAsync(Url(id, "/status"), new { status = "InProgress" }, ifMatch: etag);
            await transition.ProblemAsync(HttpStatusCode.NotFound, "not_found");
            using var assign = await client.PostAsync(Url(id, "/assign"), new { assigneeUserId = Guid.NewGuid() }, ifMatch: etag);
            await assign.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        foreach (var staff in new[] { agent, manager })
        {
            var (view, _) = await SupportWorld.GetTicketAsync(staff, id);
            Assert.Equal(ticket.Str("number"), view.Str("number"));
            Assert.NotNull(view["diagnostic"]);
        }

        var (unchanged, unchangedETag) = await SupportWorld.GetTicketAsync(requester, id);
        Assert.Equal(etag, unchangedETag);
        Assert.Empty(unchanged["comments"]!.AsArray());
    }

    [Fact]
    public async Task Lists_are_scoped_to_the_caller_and_the_filters_narrow_them_further()
    {
        var firstUser = await _world.UserAsync([Roles.SalesAgent]);
        var first = await _world.LoginAsync(firstUser);
        var second = await _world.LoginAsAsync([Roles.PurchasingAgent]);
        var agentUser = await _world.UserAsync([Roles.SupportAgent]);
        var agent = await _world.LoginAsync(agentUser);

        var (assigned, assignedETag) = await SupportWorld.CreateTicketAsync(first);
        var (unassigned, _) = await SupportWorld.CreateTicketAsync(first);
        var (foreign, _) = await SupportWorld.CreateTicketAsync(second);
        var (byStaff, _) = await SupportWorld.CreateTicketAsync(agent);
        using (var assign = await agent.PostAsync(Url(assigned.Id(), "/assign"), new { assigneeUserId = agentUser.Id }, ifMatch: assignedETag))
        {
            Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        }

        static Guid[] Ids(IEnumerable<JsonNode> items) => items.Select(i => i.Id()).Order().ToArray();
        Guid[] Expect(params JsonNode[] tickets) => tickets.Select(t => t.Id()).Order().ToArray();

        // A non-staff caller never sees someone else's ticket, whatever the filter says.
        Assert.Equal(Expect(assigned, unassigned), Ids(await ListAllAsync(first)));
        Assert.Equal(Expect(assigned, unassigned), Ids(await ListAllAsync(first, "mine=false")));
        Assert.Equal(Expect(assigned, unassigned), Ids(await ListAllAsync(first, "mine=true")));
        Assert.Equal(Expect(unassigned), Ids(await ListAllAsync(first, "assignee=unassigned")));
        Assert.Empty(await ListAllAsync(first, "assignee=me"));
        Assert.Equal(Expect(assigned, unassigned), Ids(await ListAllAsync(first, "status=Open")));
        Assert.Empty(await ListAllAsync(first, "status=Closed"));
        Assert.Equal(Expect(foreign), Ids(await ListAllAsync(second)));
        Assert.All(await ListAllAsync(first), item => Assert.Equal(firstUser.Id, item.Id("requesterUserId")));

        // Staff see every ticket; "mine" and "assignee" are real filters for them.
        var all = Ids(await ListAllAsync(agent));
        Assert.Superset(Expect(assigned, unassigned, foreign, byStaff).ToHashSet(), all.ToHashSet());
        Assert.Equal(Expect(byStaff), Ids(await ListAllAsync(agent, "mine=true")));
        Assert.Equal(Expect(assigned), Ids(await ListAllAsync(agent, "assignee=me")));
        var open = Ids(await ListAllAsync(agent, "assignee=unassigned&status=Open")).ToHashSet();
        Assert.Superset(Expect(unassigned, foreign, byStaff).ToHashSet(), open);
        Assert.DoesNotContain(assigned.Id(), open);

        using var badAssignee = await agent.GetAsync("/api/support/tickets?assignee=" + agentUser.Id);
        await badAssignee.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        using var badStatus = await agent.GetAsync("/api/support/tickets?status=4");
        await badStatus.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        using var unknownParameter = await first.GetAsync($"/api/support/tickets?requesterUserId={agentUser.Id}");
        await unknownParameter.ProblemAsync(HttpStatusCode.BadRequest, "unknown_parameter");
    }

    // ------------------------------------------------------------------ comments

    [Fact]
    public async Task Internal_notes_are_written_and_read_by_support_staff_only()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var agentUser = await _world.UserAsync([Roles.SupportAgent]);
        var agent = await _world.LoginAsync(agentUser);
        var (ticket, _) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();
        var internalNote = "İç not: " + SupportWorld.Canary();

        using var refused = await requester.PostAsync(Url(id, "/comments"), new { body = "Kendime iç not", isInternal = true }, ApiClient.NewKey());
        await refused.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");

        using var noted = await agent.PostAsync(Url(id, "/comments"), new { body = internalNote, isInternal = true }, ApiClient.NewKey());
        var afterNote = await noted.JsonAsync(HttpStatusCode.Created);
        Assert.True(Assert.Single(afterNote["comments"]!.AsArray())!["isInternal"]!.GetValue<bool>());

        using var reply = await agent.PostAsync(Url(id, "/comments"), new { body = "Bakiyeyi kontrol eder misiniz?" }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);

        // The requester's own comment comes back without the internal note, both in the command response and on read.
        using var answer = await requester.PostAsync(Url(id, "/comments"), new { body = "Kontrol ettim, bakiye yeterli görünüyor." }, ApiClient.NewKey());
        var answerText = await answer.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, answer.StatusCode);
        Assert.DoesNotContain(AllStrings(JsonNode.Parse(answerText)), text => text.Contains("İç not", StringComparison.Ordinal));

        var (requesterView, _) = await SupportWorld.GetTicketAsync(requester, id);
        var visible = requesterView["comments"]!.AsArray();
        Assert.Equal(
            ["Bakiyeyi kontrol eder misiniz?", "Kontrol ettim, bakiye yeterli görünüyor."],
            visible.Select(c => c!.Str("body")).Order(StringComparer.Ordinal).ToArray());
        Assert.All(visible, c => Assert.False(c!["isInternal"]!.GetValue<bool>()));
        Assert.All(visible, c => Assert.Equal("User", c!.Str("source")));
        Assert.DoesNotContain(AllStrings(requesterView), text => text.Contains(internalNote, StringComparison.Ordinal));

        var (staffView, _) = await SupportWorld.GetTicketAsync(agent, id);
        var all = staffView["comments"]!.AsArray();
        Assert.Equal(3, all.Count);
        var note = Assert.Single(all, c => c!["isInternal"]!.GetValue<bool>())!;
        Assert.Equal((internalNote, agentUser.Id, "User"), (note.Str("body"), note.Id("authorUserId"), note.Str("source")));

        // Nothing but the rejected attempt was dropped: the refused internal note left no row behind.
        Assert.Equal(3, await fixture.AppScalarAsync<int>("SELECT COUNT(*) FROM sup.TicketComments WHERE TicketId = @id;", new SqlParameter("@id", id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_comment_is_rejected(string body)
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var (ticket, _) = await SupportWorld.CreateTicketAsync(requester);

        using var empty = await requester.PostAsync(Url(ticket.Id(), "/comments"), new { body }, ApiClient.NewKey());
        using var tooLong = await requester.PostAsync(Url(ticket.Id(), "/comments"), new { body = new string('a', 4001) }, ApiClient.NewKey());
        using var withoutKey = await requester.PostAsync(Url(ticket.Id(), "/comments"), new { body = "Anahtarsız yorum" });

        await empty.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await tooLong.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        await withoutKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
    }

    [Fact]
    public async Task A_closed_ticket_accepts_no_comment_from_anyone()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "Resolved", etag);
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "Closed", etag);

        using var byRequester = await requester.PostAsync(Url(id, "/comments"), new { body = "Sorun yeniden başladı." }, ApiClient.NewKey());
        using var byStaff = await agent.PostAsync(Url(id, "/comments"), new { body = "Kapanış notu", isInternal = true }, ApiClient.NewKey());

        await byRequester.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        await byStaff.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        var (closed, closedETag) = await SupportWorld.GetTicketAsync(agent, id);
        Assert.Equal("Closed", closed.Str("status"));
        Assert.Empty(closed["comments"]!.AsArray());
        Assert.Equal(etag, closedETag);
    }

    // ------------------------------------------------------------------ status machine

    [Fact]
    public async Task Status_changes_need_a_current_if_match()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var (ticket, original) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();

        using var missing = await agent.PostAsync(Url(id, "/status"), new { status = "InProgress" });
        await missing.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        using var malformed = await agent.PostAsync(Url(id, "/status"), new { status = "InProgress" }, ifMatch: "W/\"AAAAAAAAAAA=\"");
        await malformed.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

        var (_, current) = await SupportWorld.ChangeStatusAsync(agent, id, "InProgress", original);
        Assert.NotEqual(original, current);

        // A second writer who still holds the first version must not overwrite the first writer's change.
        using var stale = await agent.PostAsync(Url(id, "/status"), new { status = "Resolved" }, ifMatch: original);
        await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        using var staleReopen = await requester.PostAsync(Url(id, "/status"), new { status = "InProgress" }, ifMatch: original);
        await staleReopen.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");

        var (after, afterETag) = await SupportWorld.GetTicketAsync(agent, id);
        Assert.Equal("InProgress", after.Str("status"));
        Assert.Equal(current, afterETag);

        using var invalidName = await agent.PostAsync(Url(id, "/status"), new { status = "Done" }, ifMatch: current);
        await invalidName.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Staff_move_a_ticket_through_the_allowed_transitions_only()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();

        async Task RefusedAsync(ApiClient client, string status, string current)
        {
            using var response = await client.PostAsync(Url(id, "/status"), new { status }, ifMatch: current);
            await response.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        }

        await RefusedAsync(agent, "Closed", etag);
        await RefusedAsync(agent, "WaitingForRequester", etag);
        await RefusedAsync(agent, "Open", etag);

        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "InProgress", etag);
        await RefusedAsync(agent, "Open", etag);
        await RefusedAsync(agent, "Closed", etag);
        await RefusedAsync(agent, "InProgress", etag);

        (_, etag) = await SupportWorld.ChangeStatusAsync(manager, id, "WaitingForRequester", etag);
        await RefusedAsync(manager, "Closed", etag);
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "InProgress", etag);
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "WaitingForRequester", etag);
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "Resolved", etag);
        await RefusedAsync(agent, "Open", etag);
        await RefusedAsync(agent, "WaitingForRequester", etag);

        // Staff may take a resolved ticket back, and close it once it is resolved again.
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "InProgress", etag);
        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "Resolved", etag);
        (_, etag) = await SupportWorld.ChangeStatusAsync(manager, id, "Closed", etag);

        // Closed is final for everyone.
        foreach (var status in new[] { "Open", "InProgress", "WaitingForRequester", "Resolved", "Closed" })
        {
            await RefusedAsync(manager, status, etag);
        }

        await RefusedAsync(requester, "InProgress", etag);
        var (final, finalETag) = await SupportWorld.GetTicketAsync(requester, id);
        Assert.Equal("Closed", final.Str("status"));
        Assert.Equal(etag, finalETag);
    }

    [Fact]
    public async Task Requester_may_only_reopen_a_resolved_ticket()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();

        async Task RequesterRefusedAsync(string status, string current, HttpStatusCode expected, string code)
        {
            using var response = await requester.PostAsync(Url(id, "/status"), new { status }, ifMatch: current);
            await response.ProblemAsync(expected, code);
        }

        // Every status other than "reopen" is a staff decision.
        foreach (var status in new[] { "Open", "WaitingForRequester", "Resolved", "Closed" })
        {
            await RequesterRefusedAsync(status, etag, HttpStatusCode.Forbidden, "forbidden");
        }

        // Reopening is only meaningful for a resolved ticket: on an open one it is refused and changes nothing.
        await RequesterRefusedAsync("InProgress", etag, HttpStatusCode.Conflict, "invalid_state");
        var (stillOpen, stillOpenETag) = await SupportWorld.GetTicketAsync(requester, id);
        Assert.Equal(("Open", etag), (stillOpen.Str("status"), stillOpenETag));

        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "InProgress", etag);
        await RequesterRefusedAsync("Resolved", etag, HttpStatusCode.Forbidden, "forbidden");
        await RequesterRefusedAsync("InProgress", etag, HttpStatusCode.Conflict, "invalid_state");

        (_, etag) = await SupportWorld.ChangeStatusAsync(agent, id, "Resolved", etag);
        await RequesterRefusedAsync("Closed", etag, HttpStatusCode.Forbidden, "forbidden");
        var (reopened, reopenedETag) = await SupportWorld.ChangeStatusAsync(requester, id, "InProgress", etag);
        Assert.Equal("InProgress", reopened.Str("status"));
        Assert.NotEqual(etag, reopenedETag);
        Assert.Null(reopened["diagnostic"]);
    }

    // ------------------------------------------------------------------ assignment

    [Fact]
    public async Task Assignment_follows_the_role_rules_and_needs_a_current_if_match()
    {
        var requesterUser = await _world.UserAsync([Roles.SalesAgent]);
        var requester = await _world.LoginAsync(requesterUser);
        var outsider = await _world.LoginAsAsync([Roles.PurchasingAgent]);
        var agentUser = await _world.UserAsync([Roles.SupportAgent]);
        var agent = await _world.LoginAsync(agentUser);
        var otherAgentUser = await _world.UserAsync([Roles.SupportAgent]);
        var managerUser = await _world.UserAsync([Roles.SupportManager]);
        var manager = await _world.LoginAsync(managerUser);
        var salesManagerUser = await _world.UserAsync([Roles.SalesManager]);
        var adminUser = await _world.UserAsync([Roles.SystemAdmin]);
        var (ticket, original) = await SupportWorld.CreateTicketAsync(requester);
        var id = ticket.Id();

        Task<HttpResponseMessage> AssignAsync(ApiClient client, Guid assignee, string? etag, Guid? ticketId = null) =>
            client.PostAsync(Url(ticketId ?? id, "/assign"), new { assigneeUserId = assignee }, ifMatch: etag);

        // The requester knows the ticket exists but may not manage it; anyone else must not learn that it exists.
        using (var byRequester = await AssignAsync(requester, agentUser.Id, original))
        {
            await byRequester.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var byOutsider = await AssignAsync(outsider, agentUser.Id, original))
        {
            await byOutsider.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        // A support agent takes tickets for themselves only.
        using (var toColleague = await AssignAsync(agent, otherAgentUser.Id, original))
        {
            await toColleague.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var toManager = await AssignAsync(agent, managerUser.Id, original))
        {
            await toManager.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var withoutIfMatch = await AssignAsync(agent, agentUser.Id, null))
        {
            await withoutIfMatch.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        }

        string current;
        using (var toSelf = await AssignAsync(agent, agentUser.Id, original))
        {
            var assigned = await toSelf.JsonAsync(HttpStatusCode.OK);
            Assert.Equal(agentUser.Id, assigned.Id("assigneeUserId"));
            Assert.Equal("Open", assigned.Str("status"));
            current = toSelf.ETag();
            Assert.NotEqual(original, current);
        }

        using (var stale = await AssignAsync(manager, otherAgentUser.Id, original))
        {
            await stale.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        }

        // The assignee must be active support staff, whoever asks.
        foreach (var notStaff in new[] { salesManagerUser.Id, adminUser.Id, requesterUser.Id, Guid.NewGuid() })
        {
            using var response = await AssignAsync(manager, notStaff, current);
            var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("assigneeUserId", problem["errors"]![0]!.Str("field"));
        }

        var (unchanged, unchangedETag) = await SupportWorld.GetTicketAsync(manager, id);
        Assert.Equal((agentUser.Id, current), (unchanged.Id("assigneeUserId"), unchangedETag));

        // A support manager assigns to any support staff, including another manager.
        using (var toOther = await AssignAsync(manager, otherAgentUser.Id, current))
        {
            Assert.Equal(otherAgentUser.Id, (await toOther.JsonAsync(HttpStatusCode.OK)).Id("assigneeUserId"));
            current = toOther.ETag();
        }

        using (var toSelfAsManager = await AssignAsync(manager, managerUser.Id, current))
        {
            Assert.Equal(managerUser.Id, (await toSelfAsManager.JsonAsync(HttpStatusCode.OK)).Id("assigneeUserId"));
            current = toSelfAsManager.ETag();
        }

        using (var unknownTicket = await AssignAsync(manager, agentUser.Id, current, Guid.NewGuid()))
        {
            await unknownTicket.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        (_, current) = await SupportWorld.ChangeStatusAsync(manager, id, "Resolved", current);
        (_, current) = await SupportWorld.ChangeStatusAsync(manager, id, "Closed", current);
        using (var closed = await AssignAsync(manager, agentUser.Id, current))
        {
            await closed.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        }
    }

    // ------------------------------------------------------------------ assignee picker

    [Fact]
    public async Task Staff_list_names_the_active_support_staff_and_is_for_support_staff_only()
    {
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var tag = Guid.NewGuid().ToString("N")[..10];

        async Task<Guid> CreateUserAsync(string displayName, params string[] roles)
        {
            using var response = await admin.PostAsync(
                "/api/admin/users", new { userName = $"dst.{Guid.NewGuid():N}"[..24], displayName, roles, poApprovalLimit = 0 });
            return (await response.JsonAsync(HttpStatusCode.Created))["user"]!.Id();
        }

        var agentId = await CreateUserAsync($"Deniz Uzman {tag}", Roles.SupportAgent);
        var managerId = await CreateUserAsync($"Çağla Yönetici {tag}", Roles.SupportManager, Roles.SalesManager);
        var bothId = await CreateUserAsync($"İlker İkisi {tag}", Roles.SupportManager, Roles.SupportAgent);
        var salesId = await CreateUserAsync($"Selin Satış {tag}", Roles.SalesAgent);
        var adminId = await CreateUserAsync($"Yasin Yönetim {tag}", Roles.SystemAdmin);
        var leftId = await CreateUserAsync($"Ayrılan Uzman {tag}", Roles.SupportAgent);
        using (var deactivated = await admin.PostAsync($"/api/admin/users/{leftId}/deactivate"))
        {
            Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        }

        foreach (var client in new[] { agent, manager })
        {
            using var response = await client.GetAsync("/api/support/staff");
            var staff = (await response.JsonAsync(HttpStatusCode.OK)).AsArray();
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            var byId = staff.ToDictionary(s => s!.Id(), s => s!);

            Assert.Equal($"Deniz Uzman {tag}", byId[agentId].Str("displayName"));
            Assert.Equal([Roles.SupportAgent], byId[agentId]["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());

            // Only the support roles are listed: that this manager also manages sales is not the picker's business.
            Assert.Equal([Roles.SupportManager], byId[managerId]["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
            Assert.Equal($"Çağla Yönetici {tag}", byId[managerId].Str("displayName"));
            Assert.Equal([Roles.SupportAgent, Roles.SupportManager], byId[bothId]["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());

            Assert.DoesNotContain(salesId, byId.Keys);
            Assert.DoesNotContain(adminId, byId.Keys);
            Assert.DoesNotContain(leftId, byId.Keys);
            Assert.All(staff, s => Assert.Equal(["displayName", "id", "roles"], s!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray()));
            Assert.All(staff, s => Assert.NotEmpty(s!["roles"]!.AsArray()));
        }

        // Everyone on the list can in fact be assigned, and nobody off it can.
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var (ticket, etag) = await SupportWorld.CreateTicketAsync(requester);
        using (var offList = await manager.PostAsync(Url(ticket.Id(), "/assign"), new { assigneeUserId = leftId }, ifMatch: etag))
        {
            await offList.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        }

        using (var onList = await manager.PostAsync(Url(ticket.Id(), "/assign"), new { assigneeUserId = agentId }, ifMatch: etag))
        {
            var assigned = await onList.JsonAsync(HttpStatusCode.OK);
            Assert.Equal((agentId, $"Deniz Uzman {tag}"), (assigned.Id("assigneeUserId"), assigned.Str("assigneeDisplayName")));
        }

        foreach (var roles in new[] { new[] { Roles.SalesAgent }, [Roles.SalesManager], [Roles.WarehouseOperator], [Roles.PurchasingAgent], [Roles.SystemAdmin] })
        {
            using var refused = await (await _world.LoginAsAsync(roles)).GetAsync("/api/support/staff");
            var text = await refused.Content.ReadAsStringAsync();
            await refused.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            Assert.DoesNotContain(tag, text, StringComparison.Ordinal);
        }

        using var byRequester = await requester.GetAsync("/api/support/staff");
        await byRequester.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        using var anonymous = await fixture.NewClient().GetAsync("/api/support/staff");
        await anonymous.ProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        using var unknownParameter = await agent.GetAsync("/api/support/staff?role=SystemAdmin");
        await unknownParameter.ProblemAsync(HttpStatusCode.BadRequest, "unknown_parameter");
    }

    // ------------------------------------------------------------------ boundaries

    [Fact]
    public async Task Naming_a_sales_order_on_a_ticket_gives_support_staff_no_access_to_that_order()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync(price: 321.45m);
        var customer = await _world.CustomerAsync();
        var sales = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var (order, _) = await World.CreateSalesOrderAsync(sales, customer.Id, warehouse.Id, (product.Id, 3));
        var orderNumber = order.Str("number");

        var (ticket, _) = await SupportWorld.CreateTicketAsync(
            sales, module: "Sales", screen: "sales.order", relatedDocument: new { type = "SalesOrder", number = orderNumber.ToLowerInvariant() });

        foreach (var staff in new[] { agent, manager })
        {
            var (view, _) = await SupportWorld.GetTicketAsync(staff, ticket.Id());
            Assert.Equal(("SalesOrder", orderNumber), (view.Str("relatedDocumentType"), view.Str("relatedDocumentNumber")));

            // The ticket carries the typed number and nothing else about the order.
            Assert.All(view.AsObject().Select(p => p.Key), key => Assert.Contains(key, TicketFields));
            var strings = AllStrings(view).ToArray();
            foreach (var forbidden in new[] { order.Id().ToString(), customer.Id.ToString(), customer.Name, customer.Code, customer.Address, product.Sku, product.Name, warehouse.Code })
            {
                Assert.DoesNotContain(strings, text => text.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
            }

            using var read = await staff.GetAsync($"/api/sales/orders/{order.Id()}");
            await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
            using var summary = await staff.GetAsync($"/api/sales/orders/{order.Id()}/summary");
            await summary.ProblemAsync(HttpStatusCode.NotFound, "not_found");
            using var list = await staff.GetAsync("/api/sales/orders");
            await list.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
            using var customers = await staff.GetAsync($"/api/customers/{customer.Id}");
            Assert.Contains(customers.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
        }

        // The owner still reads the order as before; the ticket changed nothing about it.
        var stillThere = await sales.GetJsonAsync($"/api/sales/orders/{order.Id()}");
        Assert.Equal(orderNumber, stillThere.Str("number"));
    }

    [Fact]
    public async Task A_ticket_cannot_be_used_to_find_out_whether_a_document_number_exists()
    {
        var warehouse = await _world.WarehouseAsync();
        var product = await _world.ProductAsync();
        var customer = await _world.CustomerAsync();
        var owner = await _world.LoginAsAsync([Roles.SalesAgent], [(warehouse.Id, WarehouseLevel.View)]);
        var (order, _) = await World.CreateSalesOrderAsync(owner, customer.Id, warehouse.Id, (product.Id, 1));
        var prober = await _world.LoginAsAsync([Roles.PurchasingAgent]);

        var (existing, _) = await SupportWorld.CreateTicketAsync(prober, relatedDocument: new { type = "SalesOrder", number = order.Str("number") });
        var (invented, _) = await SupportWorld.CreateTicketAsync(prober, relatedDocument: new { type = "SalesOrder", number = "SO-99999999" });

        // Same shape for a real and an invented number: the server does not look the document up.
        Assert.Equal(
            existing.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(),
            invented.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(order.Str("number"), existing.Str("relatedDocumentNumber"));
        Assert.Equal("SO-99999999", invented.Str("relatedDocumentNumber"));
        Assert.All(existing.AsObject().Select(p => p.Key), key => Assert.Contains(key, TicketFields));
    }

    [Fact]
    public async Task Markup_in_title_description_and_comment_round_trips_as_plain_json_text()
    {
        var requester = await _world.LoginAsAsync([Roles.SalesAgent]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        const string title = "<script>alert(\"x\")</script> & <b>başlık</b>";
        const string description = "Satır 1 <img src=x onerror=alert(1)>\nSatır 2 [bağlantı](javascript:alert(1)) 'tek' \"çift\" </textarea> {{7*7}} ${7*7}";
        const string comment = "<iframe src=\"javascript:alert(1)\"></iframe>\n-- '; DROP TABLE sup.Tickets; --";

        using var created = await requester.PostAsync("/api/support/tickets", new { title, description, priority = "Normal", module = "Support" }, ApiClient.NewKey());
        var raw = await created.Content.ReadAsStringAsync();
        var ticket = await created.JsonAsync(HttpStatusCode.Created);
        using var commented = await requester.PostAsync(Url(ticket.Id(), "/comments"), new { body = comment }, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, commented.StatusCode);

        Assert.Equal((title, description), (ticket.Str("title"), ticket.Str("description")));
        foreach (var reader in new[] { requester, agent })
        {
            using var response = await reader.GetAsync(Url(ticket.Id()));
            var view = await response.JsonAsync(HttpStatusCode.OK);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal((title, description), (view.Str("title"), view.Str("description")));
            Assert.Equal(comment, Assert.Single(view["comments"]!.AsArray())!.Str("body"));
        }

        var listed = Assert.Single(await ListAllAsync(requester));
        Assert.Equal(title, listed.Str("title"));

        // On the wire the angle brackets are JSON escapes, so even a client that sniffed the body as HTML finds no tag.
        Assert.DoesNotContain("<script", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await fixture.AppScalarAsync<int>("SELECT COUNT(*) FROM sup.Tickets WHERE Id = @id;", new SqlParameter("@id", ticket.Id())));
    }
}
