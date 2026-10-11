using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Xunit;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>A knowledge-base document as the tests track it: identity, slug and the ETag of its latest state.</summary>
public sealed record KbDoc(Guid Id, string Slug, string Title, string ETag);

/// <summary>
/// Request shortcuts for the support centre, the knowledge base and assistance. Documents are created
/// through the management API, exactly as a support manager would. All tests share one database, so
/// every document and question is built from random terms that no other test uses: retrieval is
/// lexical, and a shared ordinary word would let one test's document answer another test's question.
/// </summary>
public static class SupportWorld
{
    private const string Consonants = "bcdfghjklmnpqrstvwxyz";

    /// <summary>
    /// A random five-letter term without vowels. Five letters is the scorer's stem length, so the term
    /// is its own stem; without vowels it cannot collide with a Turkish or English word.
    /// </summary>
    public static string Term() => RandomNumberGenerator.GetString(Consonants, 5);

    /// <summary>A string that exists in exactly one place; finding it elsewhere proves a leak.</summary>
    public static string Canary() => "KANARYA-" + Guid.NewGuid().ToString("N");

    public static string ErrorCode() =>
        RandomNumberGenerator.GetString("BCDFGHJKLMNPQRSTVWXYZ", 3) + "-" + RandomNumberGenerator.GetString("0123456789", 3);

    public static string Slug() => "t-" + Guid.NewGuid().ToString("N");

    /// <summary>Two fresh terms and a question made of nothing but those terms and stop words.</summary>
    public static (string First, string Second, string Question) Topic()
    {
        var (first, second) = (Term(), Term());
        return (first, second, $"{first} {second} nedir?");
    }

    /// <summary>
    /// A one-section body whose first sentence carries both topic terms and the marker, so the demo
    /// provider quotes exactly that sentence when the question is <see cref="Topic"/>'s question.
    /// </summary>
    public static string Body(string first, string second, string marker) =>
        $"# Kullanım\n{first} {second} adımı {marker} koduyla izlenir. Kalan cümle yalnızca dolgudur.";

    // ------------------------------------------------------------------ knowledge base

    public static async Task<KbDoc> CreateDocumentAsync(
        ApiClient staff,
        string body,
        string visibility = "General",
        string module = "General",
        Guid? warehouseId = null,
        string? errorCode = null,
        string? title = null,
        string? slug = null)
    {
        slug ??= Slug();
        title ??= $"{Term()} {Term()}";
        using var response = await staff.PostAsync(
            "/api/kb/manage/documents",
            new { slug, visibility, module, warehouseId, errorCode, title, bodyMarkdown = body },
            ApiClient.NewKey());
        var json = await response.JsonAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", json.Str("status"));
        return new KbDoc(json.Id(), slug, title, response.ETag());
    }

    public static async Task<KbDoc> PublishAsync(ApiClient manager, KbDoc document, int? versionNo = null)
    {
        using var response = await manager.PostAsync($"/api/kb/manage/documents/{document.Id}/publish", new { versionNo }, ApiClient.NewKey(), document.ETag);
        var json = await response.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Published", json.Str("status"));
        return document with { ETag = response.ETag() };
    }

    public static async Task<KbDoc> PublishedDocumentAsync(
        ApiClient manager,
        string body,
        string visibility = "General",
        string module = "General",
        Guid? warehouseId = null,
        string? errorCode = null,
        string? title = null) =>
        await PublishAsync(manager, await CreateDocumentAsync(manager, body, visibility, module, warehouseId, errorCode, title));

    public static async Task<KbDoc> AddVersionAsync(ApiClient staff, KbDoc document, string body, string? title = null)
    {
        title ??= document.Title;
        using var response = await staff.PostAsync(
            $"/api/kb/manage/documents/{document.Id}/versions", new { title, bodyMarkdown = body }, ApiClient.NewKey(), document.ETag);
        await response.JsonAsync(HttpStatusCode.Created);
        return document with { Title = title, ETag = response.ETag() };
    }

    public static async Task<KbDoc> RetireAsync(ApiClient manager, KbDoc document)
    {
        using var response = await manager.PostAsync($"/api/kb/manage/documents/{document.Id}/retire", null, ApiClient.NewKey(), document.ETag);
        var json = await response.JsonAsync(HttpStatusCode.OK);
        Assert.Equal("Retired", json.Str("status"));
        return document with { ETag = response.ETag() };
    }

    // ------------------------------------------------------------------ assistance

    public static Task<HttpResponseMessage> AskAsync(ApiClient client, string question, Guid? ticketId = null) =>
        client.PostAsync("/api/assist/ask", ticketId is { } id ? new { question, ticketId = id } : (object)new { question });

    /// <summary>Asks and returns the parsed body together with the raw text (for canary searches over the whole response).</summary>
    public static async Task<(JsonNode Json, string Raw)> AnswerAsync(ApiClient client, string question, Guid? ticketId = null)
    {
        using var response = await AskAsync(client, question, ticketId);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 from /api/assist/ask but got {(int)response.StatusCode}: {raw}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        return (JsonNode.Parse(raw)!, raw);
    }

    public static async Task<JsonArray> SearchAsync(ApiClient client, string query)
    {
        using var response = await client.PostAsync("/api/assist/search", new { query });
        return (await response.JsonAsync(HttpStatusCode.OK)).AsArray();
    }

    public static string[] Slugs(JsonNode? array) =>
        array!.AsArray().Select(item => item!.Str("documentSlug")).Order(StringComparer.Ordinal).ToArray();

    /// <summary>An answer response that delivers nothing: no text, no stored answer, no citation.</summary>
    public static void AssertNothingDelivered(JsonNode answer)
    {
        Assert.Null(answer["answer"]);
        Assert.Null(answer["id"]);
        Assert.Null(answer["expiresAt"]);
        Assert.Empty(answer["citations"]!.AsArray());
    }

    // ------------------------------------------------------------------ tickets

    public static async Task<(JsonNode Ticket, string ETag)> CreateTicketAsync(
        ApiClient client,
        string? title = null,
        string? description = null,
        string priority = "Normal",
        string module = "General",
        string? screen = null,
        string? errorCode = null,
        object? relatedDocument = null)
    {
        title ??= $"{Term()} {Term()}";
        description ??= $"{Term()} {Term()} {Term()} sentetik açıklama";
        using var response = await client.PostAsync(
            "/api/support/tickets", new { title, description, priority, module, screen, errorCode, relatedDocument }, ApiClient.NewKey());
        var json = await response.JsonAsync(HttpStatusCode.Created);
        return (json, response.ETag());
    }

    public static async Task<(JsonNode Ticket, string ETag)> GetTicketAsync(ApiClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/support/tickets/{id}");
        var json = await response.JsonAsync(HttpStatusCode.OK);
        return (json, response.ETag());
    }

    public static async Task<(JsonNode Ticket, string ETag)> ChangeStatusAsync(ApiClient client, Guid id, string status, string etag)
    {
        using var response = await client.PostAsync($"/api/support/tickets/{id}/status", new { status }, ifMatch: etag);
        var json = await response.JsonAsync(HttpStatusCode.OK);
        Assert.Equal(status, json.Str("status"));
        return (json, response.ETag());
    }
}
