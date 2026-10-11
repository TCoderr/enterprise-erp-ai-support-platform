using System.Text;
using Kantar.Application.Assistance;

namespace Kantar.Infrastructure.Assistance;

/// <summary>
/// Deterministic, rule-based stand-in for a language model: it copies the sentences of the retrieved
/// passages that share terms with the question and cites the passages it copied from. It is NOT a
/// language model and says so in every answer. It exists so that retrieval, authorization, citation
/// validation and failure handling can be tested and demonstrated without any model; it says nothing
/// about answer quality of a real model.
/// </summary>
public sealed class DemoAnswerGenerator : IAnswerGenerator
{
    public const string Kind = "demo";
    public const string Notice = "Bu yanıt demo sağlayıcı tarafından, kaynak cümleleri seçilerek oluşturuldu; bir dil modeli çıktısı değildir.";
    private const int MaxSources = 3;
    private const int MaxSentencesPerSource = 2;

    public ProviderInfoDto Info { get; } = new(Kind, "kural-tabanli-alinti-v1", IsDemo: true);

    public Task<GeneratedAnswer> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken)
    {
        var queryTerms = LexicalScorer.Tokenize(request.Question).ToHashSet(StringComparer.Ordinal);
        var answer = new StringBuilder();
        var cited = new List<string>();

        foreach (var source in request.Sources.Take(MaxSources))
        {
            var picked = SplitSentences(source.Text)
                .Select((sentence, index) => (sentence, index, score: LexicalScorer.Tokenize(sentence).Distinct(StringComparer.Ordinal).Count(queryTerms.Contains)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.index)
                .Take(MaxSentencesPerSource)
                .OrderBy(x => x.index)
                .Select(x => x.sentence)
                .ToArray();

            // A passage retrieved through its error code may share no words with the question;
            // its opening sentence is then the most useful thing to show.
            if (picked.Length == 0 && cited.Count == 0 && request.Sources.Count == 1)
            {
                picked = SplitSentences(source.Text).Take(1).ToArray();
            }

            if (picked.Length == 0)
            {
                continue;
            }

            if (answer.Length > 0)
            {
                answer.Append("\n\n");
            }

            answer.Append(source.Heading).Append(": ").Append(string.Join(" ", picked));
            cited.Add(source.Id);
        }

        return Task.FromResult(cited.Count == 0
            ? GeneratedAnswer.Insufficient("Kaynaklarda soruyla örtüşen bir ifade bulunamadı.")
            : new GeneratedAnswer(GenerationOutcome.Answered, answer.ToString(), cited, Notice));
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
            if (ch is '.' or '!' or '?' && current.Length > 20)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
        }

        if (current.ToString().Trim() is { Length: > 0 } rest)
        {
            yield return rest;
        }
    }
}

/// <summary>Used when assistance is switched off: every request reports the provider as unavailable.</summary>
public sealed class DisabledAnswerGenerator : IAnswerGenerator
{
    public ProviderInfoDto Info { get; } = new("disabled", null, IsDemo: false);

    public Task<GeneratedAnswer> GenerateAsync(AnswerRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(GeneratedAnswer.Unavailable);
}
