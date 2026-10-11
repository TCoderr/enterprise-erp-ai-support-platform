using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>
/// A response read to its end: status, the headers the contract talks about and the body bytes.
/// Parallel requests are collected as replies and compared after all of them have finished.
/// </summary>
public sealed record Reply(HttpStatusCode Status, byte[] Body, string? ContentType, string? Location, string? ETag, bool Replayed)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public JsonNode Json => JsonNode.Parse(Body) ?? throw new InvalidOperationException("Response body is JSON null.");

    public bool IsProblem => ContentType?.StartsWith("application/problem+json", StringComparison.Ordinal) == true;

    /// <summary>The machine code of a problem response; null for anything else.</summary>
    public string? Problem => IsProblem ? Json["code"]?.GetValue<string>() : null;

    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public static async Task<Reply> ReadAsync(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        return new Reply(
            response.StatusCode,
            await response.Content.ReadAsByteArrayAsync(),
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.TryGetValues("Location", out var locations) ? locations.Single() : null,
            response.Headers.TryGetValues("ETag", out var etags) ? etags.Single() : null,
            response.Headers.TryGetValues("Idempotent-Replay", out var replay) && replay.Single() == "true");
    }

    /// <summary>Asserts a problem response with the given status and machine code and returns its body.</summary>
    public JsonNode AssertProblem(HttpStatusCode expected, string code)
    {
        Assert.True(Status == expected, $"Expected {(int)expected} but got {(int)Status}: {Text}");
        Assert.True(IsProblem, $"Expected application/problem+json but got {ContentType}");
        Assert.Equal(code, Problem);
        return Json;
    }
}

/// <summary>
/// Starts requests together: every task waits on one gate and the gate is opened once all of them
/// are queued. The proof of a concurrency test is never the timing; it is the counts, balances and
/// journal rows asserted afterwards.
/// </summary>
public static class Race
{
    public static async Task<Reply[]> RunAsync(IEnumerable<Func<Task<HttpResponseMessage>>> requests)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = requests.Select(request => Task.Run(async () =>
        {
            await gate.Task;
            return await Reply.ReadAsync(request());
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public static Task<Reply[]> RunAsync(params Func<Task<HttpResponseMessage>>[] requests) =>
        RunAsync((IEnumerable<Func<Task<HttpResponseMessage>>>)requests);
}
