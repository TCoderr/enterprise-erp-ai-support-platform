using Kantar.Application.Assistance;
using Kantar.Infrastructure.Assistance;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// The demo provider is a rule-based stand-in, not a language model. These tests pin that it is
/// deterministic, that it only cites what it copied from and that it labels itself honestly;
/// they say nothing about the quality of answers a real model would give.
/// </summary>
public sealed class DemoAnswerGeneratorTests
{
    private const string Question = "Transferde rezerve stok taşınır mı?";

    private static readonly SourcePassage Transfer = new(
        "S1", "Depolar arası transfer", "Dikkat edilecekler",
        "Yalnızca kullanılabilir stok taşınabilir. Rezerve miktar transfer edilemez. Transfer tek adımda tamamlanır.");

    private static readonly SourcePassage Password = new(
        "S2", "Parola işlemleri", "Sıfırlama", "Parola sıfırlama yönetici tarafından yapılır. Geçici parola bir kez gösterilir.");

    private static readonly SourcePassage Shortage = new(
        "S3", "STK-409", "Anlamı", "Stok yetersizse transfer reddedilir.\nBakiye ekranından kullanılabilir miktarı kontrol edin.");

    private static Task<GeneratedAnswer> AskAsync(string question, params SourcePassage[] sources) =>
        new DemoAnswerGenerator().GenerateAsync(new AnswerRequest(question, sources), CancellationToken.None);

    [Fact]
    public async Task Answer_is_built_from_the_overlapping_sentences_of_the_passages_and_cites_exactly_those_passages()
    {
        var answer = await AskAsync(Question, Transfer, Password, Shortage);

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal(["S1", "S3"], answer.CitedIds);
        Assert.Equal(
            "Dikkat edilecekler: Yalnızca kullanılabilir stok taşınabilir. Rezerve miktar transfer edilemez.\n\n"
            + "Anlamı: Stok yetersizse transfer reddedilir.",
            answer.Text);
    }

    [Fact]
    public async Task A_passage_that_shares_nothing_with_the_question_is_neither_quoted_nor_cited()
    {
        var answer = await AskAsync(Question, Password, Transfer);

        Assert.Equal(["S1"], answer.CitedIds);
        Assert.DoesNotContain("Parola", answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password.Heading, answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_quoted_sentence_comes_verbatim_from_the_passage_it_is_attributed_to()
    {
        SourcePassage[] sources = [Transfer, Password, Shortage];

        var answer = await AskAsync(Question, sources);

        var blocks = answer.Text!.Split("\n\n");
        Assert.Equal(answer.CitedIds.Count, blocks.Length);
        foreach (var (block, id) in blocks.Zip(answer.CitedIds))
        {
            var source = sources.Single(s => s.Id == id);
            Assert.StartsWith(source.Heading + ": ", block, StringComparison.Ordinal);
            var quoted = block[(source.Heading.Length + 2)..];
            var sentences = quoted.Split(". ", StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('.'));
            Assert.All(sentences, sentence => Assert.Contains(sentence, source.Text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Citations_are_always_labels_that_were_supplied()
    {
        SourcePassage[] sources =
        [
            Transfer with { Id = "S1" }, Shortage with { Id = "S2" }, Transfer with { Id = "S3", Heading = "Üçüncü" },
            Shortage with { Id = "S4", Heading = "Dördüncü" }, Transfer with { Id = "S5", Heading = "Beşinci" },
        ];

        var answer = await AskAsync(Question, sources);

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.NotEmpty(answer.CitedIds);
        Assert.Subset(sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal), answer.CitedIds.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(answer.CitedIds.Distinct(StringComparer.Ordinal), answer.CitedIds);

        // It quotes from the best-ranked passages only; the order of citations follows the order of the passages.
        Assert.Equal(["S1", "S2", "S3"], answer.CitedIds);
        Assert.DoesNotContain("Dördüncü", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_input_gives_the_same_output_every_time()
    {
        var shared = new DemoAnswerGenerator();
        var request = new AnswerRequest(Question, [Transfer, Password, Shortage]);

        var answers = new List<GeneratedAnswer>();
        for (var i = 0; i < 5; i++)
        {
            answers.Add(await shared.GenerateAsync(request, CancellationToken.None));
            answers.Add(await new DemoAnswerGenerator().GenerateAsync(new AnswerRequest(Question, [Transfer, Password, Shortage]), CancellationToken.None));
        }

        Assert.All(answers, a =>
        {
            Assert.Equal(answers[0].Outcome, a.Outcome);
            Assert.Equal(answers[0].Text, a.Text);
            Assert.Equal(answers[0].CitedIds, a.CitedIds);
            Assert.Equal(answers[0].Limitations, a.Limitations);
        });
    }

    [Fact]
    public async Task Nothing_overlapping_in_several_passages_is_an_abstention()
    {
        var answer = await AskAsync("Kargo firması entegrasyonu nasıl kurulur?", Transfer, Password, Shortage);

        Assert.Equal(GenerationOutcome.Insufficient, answer.Outcome);
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
        Assert.False(string.IsNullOrWhiteSpace(answer.Limitations));
    }

    [Fact]
    public async Task No_passage_at_all_is_an_abstention()
    {
        var answer = await AskAsync(Question);

        Assert.Equal(GenerationOutcome.Insufficient, answer.Outcome);
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
    }

    [Fact]
    public async Task A_single_passage_retrieved_without_shared_words_is_answered_with_its_opening_sentence()
    {
        // This is the error-code case: the passage was selected by its code, not by the question's words.
        var answer = await AskAsync("MKB-409 nedir?", Shortage with { Id = "S1" });

        Assert.Equal(GenerationOutcome.Answered, answer.Outcome);
        Assert.Equal(["S1"], answer.CitedIds);
        Assert.Equal("Anlamı: Stok yetersizse transfer reddedilir.", answer.Text);
    }

    [Fact]
    public async Task Every_answer_says_that_it_is_not_the_output_of_a_language_model()
    {
        var generator = new DemoAnswerGenerator();

        var answer = await generator.GenerateAsync(new AnswerRequest(Question, [Transfer]), CancellationToken.None);

        Assert.Equal(new ProviderInfoDto("demo", "kural-tabanli-alinti-v1", IsDemo: true), generator.Info);
        Assert.True(generator.Info.IsDemo);
        Assert.Equal(DemoAnswerGenerator.Notice, answer.Limitations);
        Assert.Contains("demo sağlayıcı", answer.Limitations, StringComparison.Ordinal);
        Assert.Contains("dil modeli çıktısı değildir", answer.Limitations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_disabled_provider_never_answers_and_does_not_claim_to_be_a_demo()
    {
        var generator = new DisabledAnswerGenerator();

        var answer = await generator.GenerateAsync(new AnswerRequest(Question, [Transfer]), CancellationToken.None);

        Assert.Equal(GenerationOutcome.Unavailable, answer.Outcome);
        Assert.Null(answer.Text);
        Assert.Empty(answer.CitedIds);
        Assert.Equal(new ProviderInfoDto("disabled", null, IsDemo: false), generator.Info);
    }
}
