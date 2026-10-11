using System.Diagnostics;
using System.Text.Json;
using Kantar.Application.Abstractions;
using Kantar.Application.Common;
using Kantar.Application.Identity;
using Kantar.Application.Support;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Kantar.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace Kantar.Application.Assistance;

public sealed record AskCommand(string Question, Guid? TicketId);

/// <summary>
/// Source-grounded help. The server, not the model, decides three things: which text is sent to the
/// provider (only chunks this caller may read), which citations are accepted (only labels of chunks
/// that were sent), and whether the result is delivered (only if the caller still has the same
/// access when generation ends). The model has no tools and no data access.
/// </summary>
public sealed class AssistService(
    IKantarDb db,
    KnowledgeService knowledge,
    IAnswerGenerator generator,
    AuthService auth,
    TimeProvider clock,
    AssistMetrics metrics)
{
    public const int MaxQuestionLength = 1000;
    public const int MaxSources = 5;
    public const int MaxSourceLength = 1500;
    public const int MaxTotalSourceLength = 6000;
    public const int MaxCandidates = 2000;
    public const int MaxAnswerLength = 4000;
    public const int SnippetLength = 320;
    public static readonly TimeSpan AnswerLifetime = TimeSpan.FromMinutes(30);

    public ProviderInfoDto Provider => generator.Info;

    public static string NormalizeQuestion(string? question, string field = "question")
    {
        var v = new Validator();
        var cleaned = v.Text(field, question, 3, MaxQuestionLength, multiline: true);
        v.ThrowIfInvalid();
        return cleaned;
    }

    /// <summary>Readable passages matching a query, without generating anything. Works with any provider state.</summary>
    public async Task<IReadOnlyList<SourceDto>> SearchAsync(CurrentUser user, string query, CancellationToken ct)
    {
        var retrieved = await RetrieveAsync(user, query, hint: null, ct);
        return retrieved.Select(ToSource).ToArray();
    }

    public async Task<AnswerDto> AskAsync(CurrentUser user, AskCommand command, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        string? hint = null;
        if (command.TicketId is { } ticketId)
        {
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
            if (ticket is null || !AccessPolicy.CanSeeTicket(user, ticket))
            {
                throw Problems.NotFound();
            }

            // Only these three fields of the selected ticket inform retrieval. Description, comments and
            // the related document are not read; nothing from the ticket is sent to the provider.
            hint = $"{ticket.Title} {ticket.ErrorCode} {ticket.Module}";
        }

        var retrieved = await RetrieveAsync(user, command.Question, hint, ct);
        if (retrieved.Count == 0)
        {
            return await FinishAsync(user, AnswerStatus.NoSource, null, [], [], NoSourceText, started, persist: false, ct);
        }

        var passages = retrieved.Select((chunk, index) => new SourcePassage($"S{index + 1}", chunk.Title, chunk.Heading, chunk.Text)).ToArray();
        var byLabel = passages.Zip(retrieved).ToDictionary(pair => pair.First.Id, pair => pair.Second, StringComparer.Ordinal);

        GeneratedAnswer generated;
        try
        {
            generated = await generator.GenerateAsync(new AnswerRequest(command.Question, passages), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            generated = GeneratedAnswer.Unavailable;
        }

        switch (generated.Outcome)
        {
            case GenerationOutcome.Unavailable:
                // The sources are still returned: the user can read them even though no answer was produced.
                return await FinishAsync(user, AnswerStatus.ProviderUnavailable, null, [], retrieved, "Yanıt üretici şu anda kullanılamıyor. Aşağıdaki kaynakları doğrudan okuyabilirsiniz.", started, persist: false, ct);
            case GenerationOutcome.Insufficient:
                return await FinishAsync(user, AnswerStatus.NoSource, null, [], [], generated.Limitations is { Length: > 0 } l ? PromptBuilder.SanitizeOutput(l, 500) : NoSourceText, started, persist: false, ct);
            case GenerationOutcome.Malformed:
                return await FinishAsync(user, AnswerStatus.RejectedUnsupported, null, [], [], RejectedText, started, persist: false, ct);
        }

        // Citations must be labels of passages that were actually sent. An invented or empty citation
        // list makes the whole answer unsupported; it is not partially accepted.
        var cited = generated.CitedIds.Distinct(StringComparer.Ordinal).ToArray();
        var answerText = PromptBuilder.SanitizeOutput(generated.Text ?? string.Empty, MaxAnswerLength);
        if (cited.Length == 0 || answerText.Length == 0 || cited.Any(id => !byLabel.ContainsKey(id)))
        {
            return await FinishAsync(user, AnswerStatus.RejectedUnsupported, null, [], [], RejectedText, started, persist: false, ct);
        }

        var citedChunks = cited.Select(id => byLabel[id]).ToArray();

        // Re-authorization at delivery time: the session must still be valid and every cited chunk must
        // still be readable by the caller's CURRENT roles and grants, in the same published version.
        var current = await auth.RefreshAsync(user, ct);
        if (current is null)
        {
            return await FinishAsync(user, AnswerStatus.AccessChanged, null, [], [], AccessChangedText, started, persist: false, ct);
        }

        var ids = citedChunks.Select(c => c.ChunkId).ToArray();
        var stillReadable = await knowledge.ReadableChunks(current).Where(c => ids.Contains(c.ChunkId)).Select(c => new { c.ChunkId, c.VersionId }).ToListAsync(ct);
        if (stillReadable.Count != ids.Length || citedChunks.Any(c => !stillReadable.Any(s => s.ChunkId == c.ChunkId && s.VersionId == c.VersionId)))
        {
            return await FinishAsync(user, AnswerStatus.AccessChanged, null, [], [], AccessChangedText, started, persist: false, ct);
        }

        var limitations = generated.Limitations is { Length: > 0 } text ? PromptBuilder.SanitizeOutput(text, 500) : null;
        return await FinishAsync(user, AnswerStatus.Answered, answerText, citedChunks, citedChunks, limitations, started, persist: true, ct);
    }

    public async Task<AnswerDto> GetAnswerAsync(CurrentUser user, Guid id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var answer = await db.AiAnswers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == user.Id && a.ExpiresAt > now, ct)
            ?? throw Problems.NotFound();
        var citations = JsonSerializer.Deserialize<List<StoredCitation>>(answer.CitationsJson ?? "[]", ApiJson.Options) ?? [];
        return new AnswerDto(
            answer.Id, answer.Status, answer.AnswerText, citations.Select(c => c.ToDto()).ToArray(), [], answer.LimitationsText,
            new ProviderInfoDto(answer.Provider, answer.Model, answer.Provider == "demo"), answer.ExpiresAt);
    }

    private const string NoSourceText = "Bu soruyu yanıtlamak için erişebildiğiniz yayınlı kaynaklarda yeterli bilgi yok.";
    private const string RejectedText = "Üretilen yanıt kaynaklarla doğrulanamadığı için gösterilmiyor.";
    private const string AccessChangedText = "Yanıt hazırlanırken erişim yetkiniz veya kaynağın yayın durumu değişti; yanıt gösterilmiyor.";

    // ------------------------------------------------------------------ retrieval

    private async Task<List<ReadableChunk>> RetrieveAsync(CurrentUser user, string query, string? hint, CancellationToken ct)
    {
        // Authorization is in this query's WHERE clause (see KnowledgeService.ReadableChunks): text the
        // caller may not read is never loaded into this process for this request.
        var candidates = await knowledge.ReadableChunks(user)
            .OrderBy(c => c.Slug).ThenBy(c => c.SectionNo)
            .Take(MaxCandidates)
            .ToListAsync(ct);

        var searchText = hint is null ? query : query + " " + hint;
        var codes = LexicalScorer.ExtractErrorCodes(searchText);
        var ranked = LexicalScorer.Rank(
            searchText,
            candidates,
            c => $"{c.Title} {c.Heading} {c.Text}",
            c => c.ErrorCode is not null && codes.Contains(c.ErrorCode));

        var selected = new List<ReadableChunk>();
        var budget = MaxTotalSourceLength;
        foreach (var item in ranked)
        {
            if (selected.Count == MaxSources || budget <= 0)
            {
                break;
            }

            var chunk = item.Item;
            var length = Math.Min(Math.Min(chunk.Text.Length, MaxSourceLength), budget);
            if (length < chunk.Text.Length)
            {
                chunk = Truncated(chunk, length);
            }

            selected.Add(chunk);
            budget -= length;
        }

        return selected;
    }

    private static ReadableChunk Truncated(ReadableChunk chunk, int length) => new()
    {
        ChunkId = chunk.ChunkId, DocumentId = chunk.DocumentId, VersionId = chunk.VersionId, Slug = chunk.Slug, Title = chunk.Title,
        VersionNo = chunk.VersionNo, SectionNo = chunk.SectionNo, Heading = chunk.Heading, Text = chunk.Text[..length],
        Module = chunk.Module, ErrorCode = chunk.ErrorCode,
    };

    private static SourceDto ToSource(ReadableChunk chunk) => new(
        chunk.Slug, chunk.Title, chunk.VersionNo, chunk.SectionNo, chunk.Heading,
        chunk.Text.Length <= SnippetLength ? chunk.Text : chunk.Text[..SnippetLength] + "…");

    // ------------------------------------------------------------------ result

    private async Task<AnswerDto> FinishAsync(
        CurrentUser user,
        AnswerStatus status,
        string? answerText,
        IReadOnlyList<ReadableChunk> cited,
        IReadOnlyList<ReadableChunk> shownSources,
        string? limitations,
        long started,
        bool persist,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var elapsed = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var provider = generator.Info;
        var stored = cited.Select(c => new StoredCitation(c.ChunkId, c.VersionId, c.Slug, c.Title, c.VersionNo, c.SectionNo, c.Heading)).ToArray();

        // Usage carries no user id, question or answer text: provider, outcome, duration and counts only.
        db.AiUsage.Add(new AiUsage
        {
            OccurredAt = now, Provider = provider.Kind, Model = provider.Model, Status = status.ToString(), DurationMs = elapsed, SourceCount = shownSources.Count,
        });

        AiAnswer? record = null;
        if (persist)
        {
            // The question is not stored. The answer is kept briefly so that "save to ticket" can use the
            // server's copy instead of trusting text sent back by the browser.
            record = new AiAnswer
            {
                UserId = user.Id,
                SessionId = user.SessionId,
                Status = status.ToString(),
                Provider = provider.Kind,
                Model = provider.Model,
                AnswerText = answerText,
                LimitationsText = limitations,
                CitationsJson = JsonSerializer.Serialize(stored, ApiJson.Options),
                CreatedAt = now,
                ExpiresAt = now + AnswerLifetime,
            };
            db.AiAnswers.Add(record);
        }

        await db.SaveChangesAsync(ct);
        metrics.Record(provider.Kind, status, elapsed);

        return new AnswerDto(
            record?.Id,
            status.ToString(),
            answerText,
            stored.Select(c => c.ToDto()).ToArray(),
            shownSources.Select(ToSource).ToArray(),
            limitations,
            provider,
            record?.ExpiresAt);
    }
}

/// <summary>Low-cardinality counters: provider kind and outcome only. No user, document or question labels.</summary>
public sealed class AssistMetrics : IDisposable
{
    public const string MeterName = "Kantar.Assistance";
    private readonly System.Diagnostics.Metrics.Meter _meter = new(MeterName);
    private readonly System.Diagnostics.Metrics.Counter<long> _answers;
    private readonly System.Diagnostics.Metrics.Histogram<double> _duration;

    public AssistMetrics()
    {
        _answers = _meter.CreateCounter<long>("kantar.assist.answers");
        _duration = _meter.CreateHistogram<double>("kantar.assist.duration", unit: "ms");
    }

    public void Record(string provider, AnswerStatus status, int durationMs)
    {
        var tags = new TagList { { "provider", provider }, { "status", status.ToString() } };
        _answers.Add(1, tags);
        _duration.Record(durationMs, tags);
    }

    public void Dispose() => _meter.Dispose();
}
