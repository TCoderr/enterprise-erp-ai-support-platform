using Kantar.Application.Support;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>The chunker decides what retrieval can serve, so its boundaries are pinned here.</summary>
public sealed class KbChunkerTests
{
    private const string Title = "Depolar arası transfer";

    private static string Paragraph(int index, int length)
    {
        var prefix = $"Paragraf {index}: ";
        return prefix + new string((char)('a' + (index % 26)), length - prefix.Length);
    }

    [Fact]
    public void Body_is_split_at_headings_and_sections_are_numbered_from_one()
    {
        var body = "# Amaç\nStok bir depodan diğerine taşınır.\n\n## Adımlar\n1. Kaynak depoyu seçin.\n2. Miktarı girin.\n\n### Dikkat\nRezerve stok taşınamaz.";

        var sections = KbChunker.Split(Title, body);

        Assert.Equal(
            [
                new KbSection(1, "Amaç", "Stok bir depodan diğerine taşınır."),
                new KbSection(2, "Adımlar", "1. Kaynak depoyu seçin.\n2. Miktarı girin."),
                new KbSection(3, "Dikkat", "Rezerve stok taşınamaz."),
            ],
            sections);
    }

    [Fact]
    public void Text_before_the_first_heading_is_filed_under_the_document_title()
    {
        var sections = KbChunker.Split(Title, "Bu belge transfer adımlarını anlatır.\n\n# Adımlar\nKaynak depoyu seçin.");

        Assert.Equal(2, sections.Count);
        Assert.Equal(new KbSection(1, Title, "Bu belge transfer adımlarını anlatır."), sections[0]);
        Assert.Equal(new KbSection(2, "Adımlar", "Kaynak depoyu seçin."), sections[1]);
    }

    [Fact]
    public void Body_without_any_heading_is_one_section_under_the_title()
    {
        var sections = KbChunker.Split(Title, "Tek paragraflık kısa bir açıklama.");

        Assert.Equal([new KbSection(1, Title, "Tek paragraflık kısa bir açıklama.")], sections);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n\r\n\t\n")]
    [InlineData("# Yalnız başlık")]
    [InlineData("# Bir\n## İki\n### Üç")]
    public void Empty_body_or_headings_without_text_produce_no_section(string body)
    {
        Assert.Empty(KbChunker.Split(Title, body));
    }

    [Fact]
    public void A_heading_without_text_does_not_create_a_section_and_the_next_heading_wins()
    {
        var sections = KbChunker.Split(Title, "# Boş bölüm\n# Dolu bölüm\nMetin burada.");

        Assert.Equal([new KbSection(1, "Dolu bölüm", "Metin burada.")], sections);
    }

    [Fact]
    public void Only_markdown_headings_of_level_one_to_four_split_the_body()
    {
        var body = "# Başlık\n#etiket bir başlık değildir\n##### beşinci düzey başlık değildir\nMetin # işareti içerebilir.\n    ## Girintili başlık\nSon metin.";

        var sections = KbChunker.Split(Title, body);

        Assert.Equal(2, sections.Count);
        Assert.Equal("Başlık", sections[0].Heading);
        Assert.Equal("#etiket bir başlık değildir\n##### beşinci düzey başlık değildir\nMetin # işareti içerebilir.", sections[0].Text);
        Assert.Equal(new KbSection(2, "Girintili başlık", "Son metin."), sections[1]);
    }

    [Fact]
    public void A_bare_heading_marker_is_text_and_does_not_start_a_nameless_section()
    {
        var sections = KbChunker.Split(Title, "# İlk\nBirinci metin.\n#    \nİkinci metin.");

        Assert.Equal([new KbSection(1, "İlk", "Birinci metin.\n#\nİkinci metin.")], sections);
    }

    [Fact]
    public void Windows_line_endings_and_trailing_spaces_do_not_leak_into_the_text()
    {
        var sections = KbChunker.Split(Title, "# Adımlar   \r\nBirinci satır.   \r\nİkinci satır.\r\n\r\n");

        Assert.Equal([new KbSection(1, "Adımlar", "Birinci satır.\nİkinci satır.")], sections);
    }

    [Fact]
    public void A_long_section_is_split_at_paragraph_boundaries_and_every_piece_keeps_its_heading()
    {
        var paragraphs = Enumerable.Range(1, 6).Select(i => Paragraph(i, 400)).ToArray();
        var body = "# Kısa\nÖnceki bölüm.\n\n## Uzun bölüm\n" + string.Join("\n\n", paragraphs) + "\n\n## Sonraki\nSon bölüm.";

        var sections = KbChunker.Split(Title, body);

        Assert.Equal(Enumerable.Range(1, sections.Count), sections.Select(s => s.SectionNo));
        var pieces = sections.Where(s => s.Heading == "Uzun bölüm").ToArray();
        Assert.Equal(3, pieces.Length);
        Assert.All(pieces, piece => Assert.InRange(piece.Text.Length, 1, KbChunker.TargetLength));

        // Nothing is cut inside a paragraph and nothing is lost or reordered.
        var rejoined = pieces.SelectMany(p => p.Text.Split("\n\n")).ToArray();
        Assert.Equal(paragraphs, rejoined);

        Assert.Equal("Kısa", sections[0].Heading);
        Assert.Equal(new KbSection(sections.Count, "Sonraki", "Son bölüm."), sections[^1]);
    }

    [Fact]
    public void A_section_just_over_the_target_stays_whole_when_it_is_one_paragraph_within_the_hard_limit()
    {
        var paragraph = Paragraph(1, 1400);

        var sections = KbChunker.Split(Title, "# Tek paragraf\n" + paragraph);

        Assert.Equal([new KbSection(1, "Tek paragraf", paragraph)], sections);
    }

    [Fact]
    public void A_single_paragraph_longer_than_the_hard_limit_is_cut_so_no_piece_exceeds_it()
    {
        var paragraph = Paragraph(2, (KbChunker.HardLimit * 2) + 200);

        var sections = KbChunker.Split(Title, "# Dev paragraf\nKısa giriş.\n\n" + paragraph + "\n\nKısa kapanış.");

        Assert.All(sections, s => Assert.InRange(s.Text.Length, 1, KbChunker.HardLimit));
        Assert.All(sections, s => Assert.Equal("Dev paragraf", s.Heading));
        Assert.Equal(Enumerable.Range(1, sections.Count), sections.Select(s => s.SectionNo));
        Assert.Equal("Kısa giriş.", sections[0].Text);
        Assert.Equal(KbChunker.HardLimit, sections[1].Text.Length);
        Assert.EndsWith("\n\nKısa kapanış.", sections[^1].Text, StringComparison.Ordinal);
        Assert.Equal(
            "Kısa giriş." + paragraph + "Kısa kapanış.",
            string.Concat(sections.Select(s => s.Text.Replace("\n\n", string.Empty, StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData(1201)]
    [InlineData(2400)]
    [InlineData(5000)]
    [InlineData(19_000)]
    public void No_piece_ever_exceeds_the_hard_limit_whatever_the_paragraph_sizes(int total)
    {
        var sizes = new[] { 90, 700, 1499, 1500, 1501, 300, 40, 2900, 1199, 1201 };
        var paragraphs = new List<string>();
        for (var i = 0; paragraphs.Sum(p => p.Length) < total; i++)
        {
            paragraphs.Add(Paragraph(i, sizes[i % sizes.Length]));
        }

        var sections = KbChunker.Split(Title, "# Karışık\n" + string.Join("\n\n", paragraphs));

        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.InRange(s.Text.Length, 1, KbChunker.HardLimit));
        Assert.Equal(string.Concat(paragraphs), string.Concat(sections.Select(s => s.Text.Replace("\n\n", string.Empty, StringComparison.Ordinal))));
    }

    [Fact]
    public void A_very_long_heading_is_truncated_to_the_column_width()
    {
        var heading = new string('B', 400);

        var sections = KbChunker.Split(Title, $"# {heading}\nMetin.");

        Assert.Equal(new string('B', 160), Assert.Single(sections).Heading);
    }

    [Fact]
    public void The_number_of_sections_is_capped()
    {
        var body = string.Join("\n", Enumerable.Range(1, KbChunker.MaxSections + 50).Select(i => $"# Bölüm {i}\nMetin {i}."));

        var sections = KbChunker.Split(Title, body);

        Assert.Equal(KbChunker.MaxSections, sections.Count);
        Assert.Equal(Enumerable.Range(1, KbChunker.MaxSections), sections.Select(s => s.SectionNo));
        Assert.Equal($"Bölüm {KbChunker.MaxSections}", sections[^1].Heading);
    }
}
