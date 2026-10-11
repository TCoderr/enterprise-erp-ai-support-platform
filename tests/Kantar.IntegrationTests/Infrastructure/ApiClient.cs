using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>
/// A browser-like API caller: keeps its own cookie jar and CSRF token, so tests can capture a cookie,
/// replay it after logout, or send requests without a token on purpose.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    public string? CsrfToken { get; set; }

    public static string NewKey() => "test-" + Guid.NewGuid().ToString("N");

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        object? body = null,
        string? idempotencyKey = null,
        string? ifMatch = null,
        bool sendCsrf = true,
        string? rawBody = null,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, url);
        if (Cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (sendCsrf && CsrfToken is not null && method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-Token", CsrfToken);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        else if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        var response = await http.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                var pair = header.Split(';', 2)[0];
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var (name, value) = (pair[..separator], pair[(separator + 1)..]);
                if (value.Length == 0 || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                {
                    Cookies.Remove(name);
                }
                else
                {
                    Cookies[name] = value;
                }
            }
        }

        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, string? key = null, string? ifMatch = null) =>
        SendAsync(HttpMethod.Post, url, body, key, ifMatch);

    public Task<HttpResponseMessage> PutAsync(string url, object body, string? ifMatch = null) =>
        SendAsync(HttpMethod.Put, url, body, null, ifMatch);

    public async Task<JsonNode> GetJsonAsync(string url)
    {
        using var response = await GetAsync(url);
        return await response.JsonAsync(HttpStatusCode.OK);
    }

    /// <summary>Anonymous CSRF bootstrap, then login. Leaves the client holding the session cookie and token.</summary>
    public async Task<HttpResponseMessage> LoginAsync(string userName, string password)
    {
        using (var csrf = await GetAsync("/api/auth/csrf"))
        {
            CsrfToken = (await csrf.JsonAsync(HttpStatusCode.OK))["token"]!.GetValue<string>();
        }

        var response = await PostAsync("/api/auth/login", new { userName, password });
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            CsrfToken = json["csrfToken"]!.GetValue<string>();
        }

        return response;
    }

    /// <summary>A second client presenting the same cookies: a "captured" session.</summary>
    public ApiClient CloneWith(HttpClient other)
    {
        var clone = new ApiClient(other) { CsrfToken = CsrfToken };
        foreach (var cookie in Cookies)
        {
            clone.Cookies[cookie.Key] = cookie.Value;
        }

        return clone;
    }
}

public static class ResponseExtensions
{
    public static async Task<JsonNode> JsonAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected} but got {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    /// <summary>Asserts a problem response with the given status and machine code.</summary>
    public static async Task<JsonNode> ProblemAsync(this HttpResponseMessage response, HttpStatusCode expected, string code)
    {
        var json = await response.JsonAsync(expected);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, json["code"]?.GetValue<string>());
        return json;
    }

    public static string ETag(this HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) ? values.Single() : throw new InvalidOperationException("Response has no ETag.");

    public static Guid Id(this JsonNode node, string property = "id") => node[property]!.GetValue<Guid>();

    public static string Str(this JsonNode node, string property) => node[property]!.GetValue<string>();

    public static int Number(this JsonNode node, string property) => node[property]!.GetValue<int>();
}
