using System.Net;
using System.Text.Json.Nodes;
using Kantar.Domain.Identity;
using Kantar.IntegrationTests.Infrastructure;
using Xunit;

namespace Kantar.IntegrationTests.Support;

/// <summary>
/// The knowledge-base read rule of docs/02 §9.3 through the reader endpoints: an article the caller
/// may not read answers 404 (never 403) and is absent from every list and search.
/// </summary>
public sealed class KnowledgeAccessTests(KantarFixture fixture)
{
    private static readonly string[] Visibilities = ["General", "SupportOnly", "ManagerOnly"];
    private static readonly string[] Modules = ["General", "Sales", "Purchasing", "Inventory", "Support"];

    private readonly World _world = fixture.NewWorld();

    private sealed record Reader(string Label, string[] Roles, bool HasGrant, ApiClient Client);

    private sealed record MatrixDocument(KbDoc Document, string Visibility, string Module, bool Scoped, string Marker);

    /// <summary>The documented rule, written out independently of the product's policy class.</summary>
    private static bool MayRead(Reader reader, MatrixDocument document)
    {
        bool Has(params string[] roles) => roles.Any(role => reader.Roles.Contains(role));
        var staff = Has(Roles.SupportAgent, Roles.SupportManager);

        var visibility = document.Visibility switch
        {
            "General" => true,
            "SupportOnly" => staff,
            "ManagerOnly" => Has(Roles.SupportManager, Roles.SalesManager),
            _ => throw new InvalidOperationException(document.Visibility),
        };
        var module = staff || document.Module switch
        {
            "General" or "Support" => true,
            "Sales" => Has(Roles.SalesAgent, Roles.SalesManager),
            "Purchasing" => Has(Roles.PurchasingAgent),
            "Inventory" => Has(Roles.WarehouseOperator, Roles.SalesAgent, Roles.SalesManager, Roles.PurchasingAgent),
            _ => throw new InvalidOperationException(document.Module),
        };
        var warehouse = !document.Scoped || staff || reader.HasGrant;
        return visibility && module && warehouse;
    }

    private static async Task<string[]> ListSlugsAsync(ApiClient client, string query)
    {
        var list = await client.GetJsonAsync("/api/kb/articles?" + query);
        return list.AsArray().Select(a => a!.Str("slug")).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Visibility_module_and_warehouse_scope_decide_what_each_role_reads_lists_and_finds()
    {
        var warehouse = await _world.WarehouseAsync();
        var otherWarehouse = await _world.WarehouseAsync();
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var (first, second, _) = SupportWorld.Topic();

        // Every combination, each article carrying the shared topic terms and its own marker.
        var documents = new List<MatrixDocument>();
        foreach (var visibility in Visibilities)
        {
            foreach (var module in Modules)
            {
                foreach (var scoped in new[] { false, true })
                {
                    var marker = SupportWorld.Canary();
                    var document = await SupportWorld.PublishedDocumentAsync(
                        manager, SupportWorld.Body(first, second, marker), visibility, module, scoped ? warehouse.Id : null);
                    documents.Add(new MatrixDocument(document, visibility, module, scoped, marker));
                }
            }
        }

        (Guid, WarehouseLevel)[] granted = [(warehouse.Id, WarehouseLevel.View)];
        (Guid, WarehouseLevel)[] elsewhere = [(otherWarehouse.Id, WarehouseLevel.Supervise)];
        async Task<Reader> ReaderAsync(string label, string[] roles, (Guid, WarehouseLevel)[]? grants) =>
            new(label, roles, grants == granted, await _world.LoginAsAsync(roles, grants));

        Reader[] readers =
        [
            await ReaderAsync("sales agent without grant", [Roles.SalesAgent], null),
            await ReaderAsync("sales agent with grant", [Roles.SalesAgent], granted),
            await ReaderAsync("sales manager without grant", [Roles.SalesManager], null),
            await ReaderAsync("sales manager with grant", [Roles.SalesManager], granted),
            await ReaderAsync("purchasing agent with grant", [Roles.PurchasingAgent], granted),
            await ReaderAsync("purchasing agent with a grant elsewhere", [Roles.PurchasingAgent], elsewhere),
            await ReaderAsync("warehouse operator with grant", [Roles.WarehouseOperator], granted),
            await ReaderAsync("warehouse operator without grant", [Roles.WarehouseOperator], null),
            await ReaderAsync("warehouse operator with a grant elsewhere", [Roles.WarehouseOperator], elsewhere),
            await ReaderAsync("support agent", [Roles.SupportAgent], null),
            new Reader("support manager", [Roles.SupportManager], false, manager),
            await ReaderAsync("system admin without grant", [Roles.SystemAdmin], null),
            await ReaderAsync("system admin with grant", [Roles.SystemAdmin], granted),
            await ReaderAsync("sales agent who is also support agent", [Roles.SalesAgent, Roles.SupportAgent], null),
        ];

        var mismatches = new List<string>();
        foreach (var reader in readers)
        {
            var expected = documents.Where(d => MayRead(reader, d)).ToArray();
            var expectedSlugs = expected.Select(d => d.Document.Slug).Order(StringComparer.Ordinal).ToArray();

            foreach (var document in documents)
            {
                var readable = expected.Contains(document);
                var what = $"{reader.Label} / {document.Visibility} / {document.Module} / {(document.Scoped ? "warehouse-scoped" : "unscoped")}";
                using var response = await reader.Client.GetAsync($"/api/kb/articles/{document.Document.Slug}");
                var text = await response.Content.ReadAsStringAsync();
                if (readable)
                {
                    if (response.StatusCode != HttpStatusCode.OK || !text.Contains(document.Marker, StringComparison.Ordinal))
                    {
                        mismatches.Add($"{what}: expected the article, got {(int)response.StatusCode}");
                    }
                }
                else if (response.StatusCode != HttpStatusCode.NotFound || text.Contains(document.Marker, StringComparison.Ordinal)
                    || JsonNode.Parse(text)?["code"]?.GetValue<string>() != "not_found")
                {
                    mismatches.Add($"{what}: expected 404 not_found, got {(int)response.StatusCode}");
                }
            }

            // The topic term appears in all thirty articles and nowhere else, so the search result is the readable set.
            var listed = await ListSlugsAsync(reader.Client, "search=" + first);
            if (!listed.SequenceEqual(expectedSlugs))
            {
                mismatches.Add($"{reader.Label}: list/search returned {listed.Length} articles, expected {expectedSlugs.Length}");
            }

            foreach (var module in Modules)
            {
                var byModule = await ListSlugsAsync(reader.Client, $"search={first}&module={module}");
                var expectedByModule = expected.Where(d => d.Module == module).Select(d => d.Document.Slug).Order(StringComparer.Ordinal).ToArray();
                if (!byModule.SequenceEqual(expectedByModule))
                {
                    mismatches.Add($"{reader.Label}: module filter {module} returned {byModule.Length} articles, expected {expectedByModule.Length}");
                }
            }

            var found = SupportWorld.Slugs(await SupportWorld.SearchAsync(reader.Client, $"{first} {second}"));
            if (found.Except(expectedSlugs, StringComparer.Ordinal).Any() || (found.Length == 0) != (expectedSlugs.Length == 0))
            {
                mismatches.Add($"{reader.Label}: assist search returned an article outside the readable set or missed all readable ones");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));

        // Guard against a matrix that passes because the expectation itself is degenerate.
        int Readable(string label) => documents.Count(d => MayRead(readers.Single(r => r.Label == label), d));
        Assert.Equal(30, documents.Count);
        Assert.Equal(4, Readable("sales agent without grant"));
        Assert.Equal(8, Readable("sales agent with grant"));
        Assert.Equal(16, Readable("sales manager with grant"));
        Assert.Equal(3, Readable("warehouse operator with a grant elsewhere"));
        Assert.Equal(20, Readable("support agent"));
        Assert.Equal(30, Readable("support manager"));
        Assert.Equal(2, Readable("system admin without grant"));
        Assert.Equal(4, Readable("system admin with grant"));
    }

    [Fact]
    public async Task System_admin_reads_general_articles_only_and_cannot_manage_the_knowledge_base()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var (first, second, _) = SupportWorld.Topic();
        var general = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, SupportWorld.Canary()));
        var supportOnly = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, SupportWorld.Canary()), "SupportOnly");
        var managerOnly = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, SupportWorld.Canary()), "ManagerOnly");
        var salesModule = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, SupportWorld.Canary()), module: "Sales");

        Assert.Equal(general.Slug, (await admin.GetJsonAsync($"/api/kb/articles/{general.Slug}")).Str("slug"));
        foreach (var hidden in new[] { supportOnly, managerOnly, salesModule })
        {
            using var read = await admin.GetAsync($"/api/kb/articles/{hidden.Slug}");
            await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
            using var manage = await admin.GetAsync($"/api/kb/manage/documents/{hidden.Id}");
            await manage.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        Assert.Equal([general.Slug], await ListSlugsAsync(admin, "search=" + first));
        Assert.Equal([general.Slug], SupportWorld.Slugs(await SupportWorld.SearchAsync(admin, $"{first} {second}")));
    }

    [Fact]
    public async Task Only_the_published_version_is_served_through_draft_publish_new_version_and_retire()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var reader = await _world.LoginAsAsync([Roles.SalesAgent]);
        var (first, second, _) = SupportWorld.Topic();
        var (firstMarker, secondMarker) = (SupportWorld.Canary(), SupportWorld.Canary());
        var query = $"{first} {second}";

        async Task AssertNotServedAsync(KbDoc document, params string[] markers)
        {
            foreach (var client in new[] { reader, manager })
            {
                using var read = await client.GetAsync($"/api/kb/articles/{document.Slug}");
                await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
                Assert.Empty(await ListSlugsAsync(client, "search=" + first));
                Assert.Empty(await SupportWorld.SearchAsync(client, query));
                foreach (var marker in markers)
                {
                    Assert.Empty(await ListSlugsAsync(client, "search=" + marker));
                }
            }
        }

        // --- draft: exists for management, not for readers ---
        var document = await SupportWorld.CreateDocumentAsync(manager, SupportWorld.Body(first, second, firstMarker));
        await AssertNotServedAsync(document, firstMarker);
        var draft = await manager.GetJsonAsync($"/api/kb/manage/documents/{document.Id}");
        Assert.Equal(("Draft", 1), (draft.Str("status"), draft.Number("latestVersionNo")));
        Assert.Null(draft["publishedVersionNo"]);

        // --- published: served ---
        document = await SupportWorld.PublishAsync(manager, document);
        var article = await reader.GetJsonAsync($"/api/kb/articles/{document.Slug}");
        Assert.Equal(1, article.Number("versionNo"));
        Assert.Contains(firstMarker, article.Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.Equal([document.Slug], await ListSlugsAsync(reader, "search=" + firstMarker));
        var hit = Assert.Single(await SupportWorld.SearchAsync(reader, query))!;
        Assert.Equal((document.Slug, 1, 1), (hit.Str("documentSlug"), hit.Number("versionNo"), hit.Number("sectionNo")));
        Assert.Contains(firstMarker, hit.Str("snippet"), StringComparison.Ordinal);

        // --- a newer version that is not published yet changes nothing for readers ---
        document = await SupportWorld.AddVersionAsync(manager, document, SupportWorld.Body(first, second, secondMarker));
        article = await reader.GetJsonAsync($"/api/kb/articles/{document.Slug}");
        Assert.Equal(1, article.Number("versionNo"));
        Assert.Contains(firstMarker, article.Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.DoesNotContain(secondMarker, article.Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.Empty(await ListSlugsAsync(reader, "search=" + secondMarker));
        hit = Assert.Single(await SupportWorld.SearchAsync(reader, query))!;
        Assert.Contains(firstMarker, hit.Str("snippet"), StringComparison.Ordinal);
        Assert.DoesNotContain(secondMarker, hit.Str("snippet"), StringComparison.Ordinal);

        // --- version 2 published: version 1's text is gone from reader, list search and retrieval at once ---
        document = await SupportWorld.PublishAsync(manager, document);
        article = await reader.GetJsonAsync($"/api/kb/articles/{document.Slug}");
        Assert.Equal(2, article.Number("versionNo"));
        Assert.Contains(secondMarker, article.Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.DoesNotContain(firstMarker, article.Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.Empty(await ListSlugsAsync(reader, "search=" + firstMarker));
        Assert.Equal([document.Slug], await ListSlugsAsync(reader, "search=" + secondMarker));
        hit = Assert.Single(await SupportWorld.SearchAsync(reader, query))!;
        Assert.Equal(2, hit.Number("versionNo"));
        Assert.Contains(secondMarker, hit.Str("snippet"), StringComparison.Ordinal);
        Assert.DoesNotContain(firstMarker, hit.Str("snippet"), StringComparison.Ordinal);

        // Management still has both versions; versions are never rewritten.
        var managed = await manager.GetJsonAsync($"/api/kb/manage/documents/{document.Id}");
        Assert.Equal(("Published", 2, 2), (managed.Str("status"), managed.Number("publishedVersionNo"), managed.Number("latestVersionNo")));
        var versions = managed["versions"]!.AsArray().ToDictionary(v => v!.Number("versionNo"), v => v!);
        Assert.Contains(firstMarker, versions[1].Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.Contains(secondMarker, versions[2].Str("bodyMarkdown"), StringComparison.Ordinal);
        Assert.Equal((false, true), (versions[1]["isPublished"]!.GetValue<bool>(), versions[2]["isPublished"]!.GetValue<bool>()));

        // --- retired: gone for everyone on the reader side ---
        document = await SupportWorld.RetireAsync(manager, document);
        await AssertNotServedAsync(document, firstMarker, secondMarker);
        Assert.Equal("Retired", (await manager.GetJsonAsync($"/api/kb/manage/documents/{document.Id}")).Str("status"));
    }

    [Fact]
    public async Task Publishing_an_older_version_again_serves_that_version()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var reader = await _world.LoginAsAsync([Roles.SalesAgent]);
        var (first, second, _) = SupportWorld.Topic();
        var (firstMarker, secondMarker) = (SupportWorld.Canary(), SupportWorld.Canary());
        var document = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, firstMarker));
        document = await SupportWorld.AddVersionAsync(manager, document, SupportWorld.Body(first, second, secondMarker));
        document = await SupportWorld.PublishAsync(manager, document, 2);

        document = await SupportWorld.PublishAsync(manager, document, 1);

        var article = await reader.GetJsonAsync($"/api/kb/articles/{document.Slug}");
        Assert.Equal(1, article.Number("versionNo"));
        var hit = Assert.Single(await SupportWorld.SearchAsync(reader, $"{first} {second}"))!;
        Assert.Contains(firstMarker, hit.Str("snippet"), StringComparison.Ordinal);
        Assert.DoesNotContain(secondMarker, hit.Str("snippet"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_and_retire_need_a_support_manager_and_a_current_if_match()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var reader = await _world.LoginAsAsync([Roles.SalesAgent]);
        var (first, second, _) = SupportWorld.Topic();

        // A support agent writes drafts and versions but cannot put them in front of readers.
        var document = await SupportWorld.CreateDocumentAsync(agent, SupportWorld.Body(first, second, SupportWorld.Canary()));
        var original = document.ETag;
        string PublishUrl() => $"/api/kb/manage/documents/{document.Id}/publish";
        string RetireUrl() => $"/api/kb/manage/documents/{document.Id}/retire";

        using (var byAgent = await agent.PostAsync(PublishUrl(), new { versionNo = 1 }, ApiClient.NewKey(), document.ETag))
        {
            await byAgent.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var withoutIfMatch = await manager.PostAsync(PublishUrl(), new { versionNo = 1 }, ApiClient.NewKey()))
        {
            await withoutIfMatch.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        }

        using (var withoutKey = await manager.PostAsync(PublishUrl(), new { versionNo = 1 }, ifMatch: document.ETag))
        {
            await withoutKey.ProblemAsync(HttpStatusCode.BadRequest, "idempotency_key_required");
        }

        using (var unknownVersion = await manager.PostAsync(PublishUrl(), new { versionNo = 7 }, ApiClient.NewKey(), document.ETag))
        {
            await unknownVersion.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        }

        using (var retireDraft = await manager.PostAsync(RetireUrl(), null, ApiClient.NewKey(), document.ETag))
        {
            await retireDraft.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        }

        using (var stillHidden = await reader.GetAsync($"/api/kb/articles/{document.Slug}"))
        {
            await stillHidden.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        document = await SupportWorld.PublishAsync(manager, document);
        Assert.NotEqual(original, document.ETag);

        using (var stalePublish = await manager.PostAsync(PublishUrl(), new { versionNo = 1 }, ApiClient.NewKey(), original))
        {
            await stalePublish.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        }

        using (var staleVersion = await agent.PostAsync(
            $"/api/kb/manage/documents/{document.Id}/versions", new { title = document.Title, bodyMarkdown = SupportWorld.Body(first, second, "eski") }, ApiClient.NewKey(), original))
        {
            await staleVersion.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        }

        using (var retireByAgent = await agent.PostAsync(RetireUrl(), null, ApiClient.NewKey(), document.ETag))
        {
            await retireByAgent.ProblemAsync(HttpStatusCode.Forbidden, "forbidden");
        }

        using (var retireWithoutIfMatch = await manager.PostAsync(RetireUrl(), null, ApiClient.NewKey()))
        {
            await retireWithoutIfMatch.ProblemAsync(HttpStatusCode.PreconditionRequired, "precondition_required");
        }

        using (var staleRetire = await manager.PostAsync(RetireUrl(), null, ApiClient.NewKey(), original))
        {
            await staleRetire.ProblemAsync(HttpStatusCode.PreconditionFailed, "precondition_failed");
        }

        // None of the refused commands changed what readers get.
        Assert.Equal(1, (await reader.GetJsonAsync($"/api/kb/articles/{document.Slug}")).Number("versionNo"));

        document = await SupportWorld.RetireAsync(manager, document);
        using (var republish = await manager.PostAsync(PublishUrl(), new { versionNo = 1 }, ApiClient.NewKey(), document.ETag))
        {
            await republish.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        }

        using (var versionOnRetired = await manager.PostAsync(
            $"/api/kb/manage/documents/{document.Id}/versions", new { title = document.Title, bodyMarkdown = SupportWorld.Body(first, second, "yeni") }, ApiClient.NewKey(), document.ETag))
        {
            await versionOnRetired.ProblemAsync(HttpStatusCode.Conflict, "invalid_state");
        }

        using var gone = await reader.GetAsync($"/api/kb/articles/{document.Slug}");
        await gone.ProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    public static TheoryData<string[]> NonStaffRoles => new()
    {
        new[] { Roles.SalesAgent }, new[] { Roles.SalesManager }, new[] { Roles.PurchasingAgent }, new[] { Roles.WarehouseOperator }, new[] { Roles.SystemAdmin },
    };

    [Theory]
    [MemberData(nameof(NonStaffRoles))]
    public async Task Callers_who_are_not_support_staff_cannot_use_any_management_endpoint(string[] roles)
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var caller = await _world.LoginAsAsync(roles);
        var (first, second, _) = SupportWorld.Topic();
        var marker = SupportWorld.Canary();
        var draft = await SupportWorld.CreateDocumentAsync(manager, SupportWorld.Body(first, second, marker));
        var published = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, marker), "SupportOnly");
        var content = new { title = "Yetkisiz sürüm", bodyMarkdown = SupportWorld.Body(first, second, "yetkisiz") };

        var attempts = new List<(string Name, HttpResponseMessage Response)>
        {
            ("list", await caller.GetAsync("/api/kb/manage/documents")),
            ("create", await caller.PostAsync(
                "/api/kb/manage/documents",
                new { slug = SupportWorld.Slug(), visibility = "General", module = "General", title = content.title, bodyMarkdown = content.bodyMarkdown },
                ApiClient.NewKey())),
        };
        foreach (var id in new[] { draft.Id, published.Id, Guid.NewGuid() })
        {
            var etag = id == draft.Id ? draft.ETag : published.ETag;
            attempts.Add(("read", await caller.GetAsync($"/api/kb/manage/documents/{id}")));
            attempts.Add(("version", await caller.PostAsync($"/api/kb/manage/documents/{id}/versions", content, ApiClient.NewKey(), etag)));
            attempts.Add(("publish", await caller.PostAsync($"/api/kb/manage/documents/{id}/publish", new { versionNo = 1 }, ApiClient.NewKey(), etag)));
            attempts.Add(("retire", await caller.PostAsync($"/api/kb/manage/documents/{id}/retire", null, ApiClient.NewKey(), etag)));
        }

        // 403 for an existing and for an invented id alike: the endpoint does not confirm that a document exists.
        foreach (var (name, response) in attempts)
        {
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: expected 403 but got {(int)response.StatusCode}: {text}");
                Assert.Equal("forbidden", JsonNode.Parse(text)!.Str("code"));
                Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            }
        }

        var after = await manager.GetJsonAsync($"/api/kb/manage/documents/{draft.Id}");
        Assert.Equal(("Draft", 1), (after.Str("status"), after.Number("latestVersionNo")));
        var afterPublished = await manager.GetJsonAsync($"/api/kb/manage/documents/{published.Id}");
        Assert.Equal(("Published", 1), (afterPublished.Str("status"), afterPublished.Number("latestVersionNo")));
    }

    [Fact]
    public async Task Support_staff_see_every_state_in_management_and_bodies_only_on_the_detail_view()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var agent = await _world.LoginAsAsync([Roles.SupportAgent]);
        var (first, second, _) = SupportWorld.Topic();
        var marker = SupportWorld.Canary();
        var draft = await SupportWorld.CreateDocumentAsync(agent, SupportWorld.Body(first, second, marker), "ManagerOnly");
        var retired = await SupportWorld.RetireAsync(manager, await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, marker)));

        var list = (await agent.GetJsonAsync("/api/kb/manage/documents")).AsArray();
        var states = list.Where(d => d!.Id() == draft.Id || d!.Id() == retired.Id).ToDictionary(d => d!.Id(), d => d!.Str("status"));
        Assert.Equal("Draft", states[draft.Id]);
        Assert.Equal("Retired", states[retired.Id]);
        Assert.All(
            list.Where(d => d!.Id() == draft.Id || d!.Id() == retired.Id).SelectMany(d => d!["versions"]!.AsArray()),
            version => Assert.Equal(string.Empty, version!.Str("bodyMarkdown")));

        // A support agent may read a ManagerOnly draft in management (it is their working copy) but not as a reader.
        var detail = await agent.GetJsonAsync($"/api/kb/manage/documents/{draft.Id}");
        Assert.Contains(marker, detail["versions"]![0]!.Str("bodyMarkdown"), StringComparison.Ordinal);
        using var asReader = await agent.GetAsync($"/api/kb/articles/{draft.Slug}");
        await asReader.ProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Removing_a_warehouse_grant_removes_the_warehouse_scoped_articles()
    {
        var warehouse = await _world.WarehouseAsync();
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var admin = await _world.LoginAsAsync([Roles.SystemAdmin]);
        var user = await _world.UserAsync([Roles.WarehouseOperator], [(warehouse.Id, WarehouseLevel.View)]);
        var session = await _world.LoginAsync(user);
        var (first, second, question) = SupportWorld.Topic();
        var marker = SupportWorld.Canary();
        var scoped = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, marker), module: "Inventory", warehouseId: warehouse.Id);

        Assert.Equal(warehouse.Id, (await session.GetJsonAsync($"/api/kb/articles/{scoped.Slug}")).Id("warehouseId"));
        Assert.Equal([scoped.Slug], await ListSlugsAsync(session, "search=" + first));
        Assert.Equal([scoped.Slug], SupportWorld.Slugs(await SupportWorld.SearchAsync(session, $"{first} {second}")));

        using var current = await admin.GetAsync($"/api/admin/users/{user.Id}");
        using var changed = await admin.PutAsync($"/api/admin/users/{user.Id}/warehouse-grants", new { grants = Array.Empty<object>() }, current.ETag());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // The session that held the grant gets nothing any more, whichever way the server refuses it.
        using var withOldSession = await session.GetAsync($"/api/kb/articles/{scoped.Slug}");
        Assert.Contains(withOldSession.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
        Assert.DoesNotContain(marker, await withOldSession.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var fresh = await _world.LoginAsync(user);
        using var read = await fresh.GetAsync($"/api/kb/articles/{scoped.Slug}");
        await read.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Empty(await ListSlugsAsync(fresh, "search=" + first));
        Assert.Empty(await SupportWorld.SearchAsync(fresh, $"{first} {second}"));

        fixture.Answers.Clear();
        var (answer, raw) = await SupportWorld.AnswerAsync(fresh, question);
        Assert.Equal("NoSource", answer.Str("status"));
        Assert.Equal(0, fixture.Answers.CallCount);
        Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_list_filters_by_error_code_and_rejects_undocumented_parameters()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var reader = await _world.LoginAsAsync([Roles.SalesAgent]);
        var (first, second, _) = SupportWorld.Topic();
        var code = SupportWorld.ErrorCode();
        var coded = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, "kodlu"), errorCode: code.ToLowerInvariant());
        var hiddenCoded = await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, "gizli"), "SupportOnly", errorCode: code);
        await SupportWorld.PublishedDocumentAsync(manager, SupportWorld.Body(first, second, "kodsuz"));

        Assert.Equal([coded.Slug], await ListSlugsAsync(reader, "errorCode=" + code));
        Assert.Equal([coded.Slug], await ListSlugsAsync(reader, "errorCode=" + code.ToLowerInvariant()));
        Assert.Equal(new[] { coded.Slug, hiddenCoded.Slug }.Order(StringComparer.Ordinal).ToArray(), await ListSlugsAsync(manager, "errorCode=" + code));
        Assert.Equal(code, (await reader.GetJsonAsync($"/api/kb/articles/{coded.Slug}")).Str("errorCode"));

        using var unknownParameter = await reader.GetAsync("/api/kb/articles?visibility=SupportOnly");
        await unknownParameter.ProblemAsync(HttpStatusCode.BadRequest, "unknown_parameter");
        using var unknownModule = await reader.GetAsync("/api/kb/articles?module=Finance");
        await unknownModule.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        using var longSearch = await reader.GetAsync("/api/kb/articles?search=" + new string('a', 65));
        await longSearch.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        using var unknownSlug = await reader.GetAsync("/api/kb/articles/" + SupportWorld.Slug());
        await unknownSlug.ProblemAsync(HttpStatusCode.NotFound, "not_found");
        using var longSlug = await reader.GetAsync("/api/kb/articles/" + new string('a', 81));
        await longSlug.ProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("slug", "Büyük-Harf")]
    [InlineData("slug", "ab")]
    [InlineData("slug", "iki--tire")]
    [InlineData("visibility", "Public")]
    [InlineData("visibility", "2")]
    [InlineData("module", "Finance")]
    [InlineData("errorCode", "STK409")]
    [InlineData("title", "ab")]
    [InlineData("bodyMarkdown", "çok kısa")]
    public async Task Invalid_document_is_rejected(string field, string value)
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var body = new JsonObject
        {
            ["slug"] = SupportWorld.Slug(),
            ["visibility"] = "General",
            ["module"] = "General",
            ["title"] = "Geçerli başlık",
            ["bodyMarkdown"] = "Geçerli ve yeterince uzun bir gövde metni.",
        };
        body[field] = value;

        using var response = await manager.PostAsync("/api/kb/manage/documents", body, ApiClient.NewKey());

        var problem = await response.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains(problem["errors"]!.AsArray(), e => e!.Str("field") == field);
    }

    [Fact]
    public async Task A_slug_can_be_used_once_and_an_unknown_warehouse_is_refused()
    {
        var manager = await _world.LoginAsAsync([Roles.SupportManager]);
        var existing = await SupportWorld.CreateDocumentAsync(manager, "Yeterince uzun bir gövde metni burada.");

        using var duplicate = await manager.PostAsync(
            "/api/kb/manage/documents",
            new { slug = existing.Slug, visibility = "General", module = "General", title = "İkinci belge", bodyMarkdown = "Yeterince uzun başka bir gövde metni." },
            ApiClient.NewKey());
        using var unknownWarehouse = await manager.PostAsync(
            "/api/kb/manage/documents",
            new { slug = SupportWorld.Slug(), visibility = "General", module = "Inventory", warehouseId = Guid.NewGuid(), title = "Depo belgesi", bodyMarkdown = "Yeterince uzun bir gövde metni burada." },
            ApiClient.NewKey());

        await duplicate.ProblemAsync(HttpStatusCode.Conflict, "code_taken");
        await unknownWarehouse.ProblemAsync(HttpStatusCode.BadRequest, "validation_failed");
    }
}
