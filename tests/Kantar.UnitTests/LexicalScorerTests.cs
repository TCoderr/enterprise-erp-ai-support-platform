using System.Globalization;
using Kantar.Application.Assistance;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// Retrieval must give the same result whatever culture the host process runs under: a server set to
/// en-US and one set to tr-TR have to rank the same passages for the same question. Every case
/// therefore runs under both cultures, and one test compares the two runs directly.
/// </summary>
public sealed class LexicalScorerTests
{
    public static TheoryData<string> Cultures => new() { "tr-TR", "en-US" };

    private static T Under<T>(string culture, Func<T> work)
    {
        var (previous, previousUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return work();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private static List<LexicalScorer.Ranked<string>> Rank(string culture, string query, string[] candidates, Func<string, bool>? boosted = null) =>
        Under(culture, () => LexicalScorer.Rank(query, candidates, c => c, boosted ?? (_ => false)));

    // ------------------------------------------------------------------ tokenising

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Dotted_and_dotless_i_meet_in_every_casing(string culture)
    {
        var tokens = Under(culture, () => new[] { "İPTAL", "iptal", "Iptal", "IPTAL", "İptal", "ıptal" }.Select(LexicalScorer.Tokenize).ToArray());

        Assert.All(tokens, t => Assert.Equal(["iptal"], t));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Turkish_letters_are_folded_to_their_base_letters(string culture)
    {
        var tokens = Under(culture, () => LexicalScorer.Tokenize("çağrı ÇAĞRI öğüt ÖĞÜT ışık IŞIK şişe ŞİŞE üç ÜÇ kâğıt"));

        Assert.Equal(["cagri", "cagri", "ogut", "ogut", "isik", "isik", "sise", "sise", "uc", "uc", "kagit"], tokens);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Stop_words_and_single_characters_are_dropped(string culture)
    {
        var tokens = Under(culture, () => LexicalScorer.Tokenize("Bu stok NASIL ve neden a b İçin DAHA çok transfer mi?"));

        Assert.Equal(["stok", "trans"], tokens);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Long_words_are_cut_to_a_five_character_stem_so_suffixes_do_not_matter(string culture)
    {
        var tokens = Under(culture, () => LexicalScorer.Tokenize("rezervasyon rezerve REZERVASYONLARI sipariş siparişler SİPARİŞİN depo depolar"));

        Assert.Equal(["rezer", "rezer", "rezer", "sipar", "sipar", "sipar", "depo", "depol"], tokens);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Punctuation_separates_tokens_and_digits_are_kept(string culture)
    {
        var tokens = Under(culture, () => LexicalScorer.Tokenize("STK-409: \"yeterli\" stok_yok (depo/2)."));

        Assert.Equal(["stk", "409", "yeter", "stok", "depo"], tokens);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Empty_and_symbol_only_text_has_no_tokens(string culture)
    {
        Assert.Empty(Under(culture, () => LexicalScorer.Tokenize(string.Empty)));
        Assert.Empty(Under(culture, () => LexicalScorer.Tokenize("  …!? — ")));
    }

    // ------------------------------------------------------------------ error codes

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Error_codes_are_extracted_in_upper_case_regardless_of_how_they_were_typed(string culture)
    {
        var codes = Under(culture, () => LexicalScorer.ExtractErrorCodes("stk-409 hatası aldım, sonra Sip-412 ve MKB-409 çıktı; stk-409 yine geldi."));

        Assert.Equal(["MKB-409", "SIP-412", "STK-409"], codes.Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Text_without_a_code_pattern_yields_no_error_code(string culture)
    {
        var codes = Under(culture, () => LexicalScorer.ExtractErrorCodes("Stok yetersiz hatası, kod 409, ST-409, STK409, STK-40"));

        Assert.Empty(codes);
    }

    // ------------------------------------------------------------------ ranking

    private static readonly string[] Passages =
    [
        "Transfer sırasında rezerve stok taşınamaz.",
        "Transfer tek adımda tamamlanır, stok düşer.",
        "Parola sıfırlama adımları yönetici tarafından yapılır.",
        "Mal kabul işleminde yalnızca gelen miktar girilir.",
    ];

    [Theory]
    [MemberData(nameof(Cultures))]
    public void The_passage_sharing_more_query_terms_ranks_first(string culture)
    {
        var ranked = Rank(culture, "Transferde REZERVE stok taşınır mı?", Passages);

        Assert.Equal([Passages[0], Passages[1]], ranked.Select(r => r.Item));
        Assert.Equal(4, ranked[0].MatchedTerms);
        Assert.Equal(2, ranked[1].MatchedTerms);
        Assert.True(ranked[0].Score > ranked[1].Score, $"{ranked[0].Score} should exceed {ranked[1].Score}");
        Assert.All(ranked, r => Assert.True(r.Score > 0));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void A_passage_sharing_fewer_than_two_terms_is_not_a_source(string culture)
    {
        // "transfer" alone links the second passage to the question; one shared word is not evidence.
        var ranked = Rank(culture, "transfer iptal", Passages);

        Assert.Empty(ranked);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void A_one_term_question_needs_only_that_term(string culture)
    {
        var ranked = Rank(culture, "Parola nedir?", Passages);

        Assert.Equal(Passages[2], Assert.Single(ranked).Item);
        Assert.Equal(1, ranked[0].MatchedTerms);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void A_boosted_passage_qualifies_without_sharing_any_term(string culture)
    {
        var ranked = Rank(culture, "transfer iptal", Passages, boosted: p => p == Passages[3]);

        var only = Assert.Single(ranked);
        Assert.Equal(Passages[3], only.Item);
        Assert.Equal(0, only.MatchedTerms);
        Assert.True(only.Score >= 100);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void The_passage_documenting_the_asked_error_code_ranks_above_better_worded_matches(string culture)
    {
        string[] candidates =
        [
            "Hata kodları listesi: STK-409 hatası, SIP-412 hatası ve MKB-409 hatası bu belgede geçer. STK-409 hatası sık görülür.",
            "İstenen miktar depodaki kullanılabilir miktardan fazladır.",
            "Parola sıfırlama adımları.",
        ];
        const string question = "stk-409 hatası ne demek?";
        var codes = LexicalScorer.ExtractErrorCodes(question);

        // The second passage is the article whose own error code is STK-409 (kept outside its text).
        var ranked = Rank(culture, question, candidates, boosted: p => p == candidates[1] && codes.Contains("STK-409"));

        Assert.Equal([candidates[1], candidates[0]], ranked.Select(r => r.Item));
        Assert.True(ranked[0].Score >= 100);
        Assert.True(ranked[1].Score < 100);
        Assert.Equal(3, ranked[1].MatchedTerms);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void A_question_made_only_of_stop_words_matches_nothing_unless_boosted(string culture)
    {
        Assert.Empty(Rank(culture, "bu ne ve nasıl?", Passages));
        Assert.Equal(Passages[1], Assert.Single(Rank(culture, "bu ne ve nasıl?", Passages, boosted: p => p == Passages[1])).Item);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void An_empty_candidate_list_ranks_to_an_empty_list(string culture)
    {
        Assert.Empty(Rank(culture, "transfer rezerve stok", []));
        Assert.Empty(Rank(culture, string.Empty, []));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Repeating_a_term_in_the_question_does_not_count_twice(string culture)
    {
        var once = Rank(culture, "transfer stok", Passages);
        var repeated = Rank(culture, "transfer TRANSFER transferler stok stok", Passages);

        Assert.Equal(once.Select(r => (r.Item, r.Score, r.MatchedTerms)), repeated.Select(r => (r.Item, r.Score, r.MatchedTerms)));
    }

    // ------------------------------------------------------------------ culture independence

    [Fact]
    public void Tokens_error_codes_and_ranking_are_identical_under_turkish_and_english_cultures()
    {
        string[] texts =
        [
            "İPTAL iptal Iptal IPTAL", "SİPARİŞ sipariş SIPARIS siparis", "Işık ışık IŞIK isik", "TITLE title Tıtle İİİ III ııı iii",
            "stk-409 Sip-412", "Çağrı ÖĞÜT şişe ÜÇ kâğıt",
        ];
        string[] questions = ["Transferde REZERVE stok taşınır mı?", "SİPARİŞ İPTALİ nasıl yapılır?", "siparis iptali", "Sip-412 hatası", "IŞIK ışık"];
        string[] candidates = [.. Passages, "Sipariş iptali için sipariş ekranını açın.", "SIPARIS IPTALI yalnız taslak ve onaylı siparişte yapılır.", "Işık ve ışıklandırma."];

        (string[][] Tokens, string[][] Codes, (string Item, double Score, int Matched)[][] Rankings) Run(string culture) => Under(culture, () => (
            texts.Select(t => LexicalScorer.Tokenize(t).ToArray()).ToArray(),
            texts.Select(t => LexicalScorer.ExtractErrorCodes(t).Order(StringComparer.Ordinal).ToArray()).ToArray(),
            questions.Select(q => LexicalScorer.Rank(q, candidates, c => c, _ => false).Select(r => (r.Item, r.Score, r.MatchedTerms)).ToArray()).ToArray()));

        var turkish = Run("tr-TR");
        var english = Run("en-US");

        Assert.Equal(turkish.Tokens, english.Tokens);
        Assert.Equal(turkish.Codes, english.Codes);
        Assert.Equal(turkish.Rankings, english.Rankings);

        // The comparison above would also pass if both cultures were equally wrong; pin the content too.
        Assert.Equal(["iptal", "iptal", "iptal", "iptal"], turkish.Tokens[0]);
        Assert.Equal(["sipar", "sipar", "sipar", "sipar"], turkish.Tokens[1]);
        Assert.Equal(["SIP-412", "STK-409"], turkish.Codes[4]);
        Assert.Equal(2, turkish.Rankings[1].Length);
        Assert.Equal(
            turkish.Rankings[1].Select(r => r.Item).Order(StringComparer.Ordinal).ToArray(),
            turkish.Rankings[2].Select(r => r.Item).Order(StringComparer.Ordinal).ToArray());
    }
}
