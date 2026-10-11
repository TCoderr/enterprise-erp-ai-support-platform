using System.Collections.Concurrent;
using Kantar.Application.Assistance;
using Kantar.Infrastructure.Assistance;

namespace Kantar.IntegrationTests.Infrastructure;

/// <summary>
/// The answer provider seen by the application under test. By default it is the real deterministic
/// demo provider; a test can replace the next behaviour (invented citations, malformed output,
/// failures, a pause in the middle of generation). Every request the application sends is captured,
/// so tests can assert exactly which text reached "the provider" for a given caller.
/// This is a test double: nothing measured through it says anything about a real language model.
/// </summary>
public sealed class ScriptedAnswerGenerator : IAnswerGenerator
{
    private readonly DemoAnswerGenerator _demo = new();
    private Func<AnswerRequest, CancellationToken, Task<GeneratedAnswer>>? _next;

    public ConcurrentQueue<AnswerRequest> Captured { get; } = new();

    public ProviderInfoDto Info => _demo.Info;

    public int CallCount => Captured.Count;

    /// <summary>Everything that was sent to the provider so far, as one string, for canary searches.</summary>
    public string CapturedText() => string.Join(
        "\n",
        Captured.SelectMany(r => r.Sources.Select(s => $"{s.Id}|{s.Title}|{s.Heading}|{s.Text}").Append(r.Question)));

    /// <summary>Replaces the behaviour until the returned handle is disposed.</summary>
    public IDisposable Use(Func<AnswerRequest, CancellationToken, Task<GeneratedAnswer>> behaviour)
    {
        _next = behaviour;
        return new Reset(this);
    }

    public IDisposable Returns(GeneratedAnswer answer) => Use((_, _) => Task.FromResult(answer));

    public IDisposable Throws(Exception exception) => Use((_, _) => Task.FromException<GeneratedAnswer>(exception));

    /// <summary>
    /// Generation blocks until <paramref name="release"/> completes, then answers like the demo provider.
    /// <paramref name="entered"/> completes when the application has reached the provider call.
    /// </summary>
    public IDisposable PausesUntil(Task release, TaskCompletionSource entered) => Use(async (request, cancellationToken) =>
    {
        entered.TrySetResult();
        await release.WaitAsync(cancellationToken);
        return await _demo.GenerateAsync(request, cancellationToken);
    });

    public Task<GeneratedAnswer> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken)
    {
        Captured.Enqueue(request);
        var behaviour = _next;
        return behaviour is null ? _demo.GenerateAsync(request, cancellationToken) : behaviour(request, cancellationToken);
    }

    public void Clear() => Captured.Clear();

    private sealed class Reset(ScriptedAnswerGenerator owner) : IDisposable
    {
        public void Dispose() => owner._next = null;
    }
}
