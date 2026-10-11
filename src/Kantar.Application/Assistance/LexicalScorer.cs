using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kantar.Application.Assistance;

/// <summary>
/// Lexical ranking for a small, terminology-heavy knowledge base. Deliberately simple: Turkish-aware
/// lower-casing and diacritic folding, a short stop list, prefix stems and BM25. No embeddings.
/// </summary>
public static partial class LexicalScorer
{
    private const int StemLength = 5;
    private const double K1 = 1.2;
    private const double B = 0.75;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ve", "veya", "ile", "bir", "bu", "su", "de", "da", "mi", "mu", "ne", "nasil", "neden", "icin", "gibi", "ama",
        "cok", "daha", "en", "ben", "sen", "biz", "siz", "var", "yok", "olarak", "olan", "ki", "ya", "hangi", "nedir",
        "demek", "lazim", "gerek", "gerekir", "nerede", "zaman", "her", "hem", "ise", "the", "is", "of", "to", "and",
    };

    [GeneratedRegex("[A-Z]{3}-[0-9]{3}", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodes();

    /// <summary>Error codes mentioned in free text, e.g. "STK-409".</summary>
    public static IReadOnlySet<string> ExtractErrorCodes(string text) =>
        ErrorCodes().Matches(text.ToUpperInvariant()).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Tokens folded and stemmed the same way for questions and documents.</summary>
    public static List<string> Tokenize(string text)
    {
        // Turkish casing first (I→ı, İ→i), then fold so that "İptal", "iptal" and "IPTAL" meet.
        var lowered = text.ToLower(Turkish);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in lowered)
        {
            var folded = Fold(ch);
            if (char.IsLetterOrDigit(folded))
            {
                current.Append(folded);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length >= 2)
            {
                var token = current.ToString();
                if (!StopWords.Contains(token))
                {
                    tokens.Add(token.Length > StemLength ? token[..StemLength] : token);
                }
            }

            current.Clear();
        }
    }

    private static char Fold(char ch) => ch switch
    {
        'ç' => 'c',
        'ğ' => 'g',
        'ı' => 'i',
        'ö' => 'o',
        'ş' => 's',
        'ü' => 'u',
        'â' => 'a',
        'î' => 'i',
        'û' => 'u',
        _ => ch,
    };

    public sealed record Ranked<T>(T Item, double Score, int MatchedTerms);

    /// <summary>
    /// Ranks candidates against the query. A candidate qualifies only when it shares at least two distinct
    /// query terms (or the single term of a one-term query), or when <paramref name="boosted"/> marks it
    /// (e.g. it documents the error code the user asked about). Everything else is "no source".
    /// </summary>
    public static List<Ranked<T>> Rank<T>(string query, IReadOnlyList<T> candidates, Func<T, string> text, Func<T, bool> boosted)
    {
        var queryTerms = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Count == 0)
        {
            return [];
        }

        var documents = candidates.Select(c => Tokenize(text(c))).ToArray();
        var averageLength = Math.Max(1.0, documents.Average(d => (double)d.Count));
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in queryTerms)
        {
            documentFrequency[term] = documents.Count(d => d.Contains(term));
        }

        var required = queryTerms.Length <= 1 ? 1 : 2;
        var ranked = new List<Ranked<T>>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var tokens = documents[i];
            var score = 0.0;
            var matched = 0;
            foreach (var term in queryTerms)
            {
                var frequency = tokens.Count(t => t == term);
                if (frequency == 0)
                {
                    continue;
                }

                matched++;
                var idf = Math.Log(1 + ((candidates.Count - documentFrequency[term] + 0.5) / (documentFrequency[term] + 0.5)));
                score += idf * (frequency * (K1 + 1)) / (frequency + (K1 * (1 - B + (B * tokens.Count / averageLength))));
            }

            var isBoosted = boosted(candidates[i]);
            if (isBoosted)
            {
                score += 100;
            }

            if (isBoosted || (queryTerms.Length > 0 && matched >= required))
            {
                ranked.Add(new Ranked<T>(candidates[i], score, matched));
            }
        }

        return ranked.OrderByDescending(r => r.Score).ToList();
    }
}
