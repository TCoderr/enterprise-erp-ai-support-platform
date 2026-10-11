using System.Globalization;
using Kantar.Domain.Common;
using Kantar.Domain.Identity;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>
/// Text normalisation and code validation. Codes and role names are compared in binary columns and
/// ordinal sets, so nothing here may depend on the thread culture: the Turkish casing of i/ı/İ is
/// the classic way to get that wrong.
/// </summary>
public sealed class TextValidatorTests
{
    private static readonly string[] Cultures = ["tr-TR", "en-US"];

    private static void UnderEachCulture(Action<string> assertions)
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            foreach (var name in Cultures)
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                assertions(name);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    // ---- Text.TryClean -------------------------------------------------------------------------

    [Theory]
    [InlineData("  Kalem  ", "Kalem")]
    [InlineData("\tKalem\n", "Kalem")]
    [InlineData("İstanbul Şişli Çağlayan", "İstanbul Şişli Çağlayan")]
    [InlineData("iki  boşluk", "iki  boşluk")]
    public void Leading_and_trailing_white_space_is_trimmed(string input, string expected)
    {
        Assert.True(Text.TryClean(input, multiline: false, out var cleaned));
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void Text_is_normalised_to_nfc()
    {
        // "Çağrı" typed with combining marks (C + cedilla, g + breve) must equal the precomposed form.
        const string decomposed = "Çağrı";
        const string composed = "Çağrı";

        Assert.True(Text.TryClean(decomposed, multiline: false, out var cleaned));

        Assert.Equal(composed, cleaned);
        Assert.Equal(5, cleaned.Length);
        Assert.True(cleaned.IsNormalized(System.Text.NormalizationForm.FormC));
    }

    [Fact]
    public void Null_cleans_to_empty_and_windows_line_endings_become_line_feeds()
    {
        Assert.True(Text.TryClean(null, multiline: false, out var fromNull));
        Assert.Equal(string.Empty, fromNull);

        Assert.True(Text.TryClean("ilk satır\r\nikinci satır", multiline: true, out var lines));
        Assert.Equal("ilk satır\nikinci satır", lines);
    }

    [Theory]
    [InlineData("satır\nsonu")]
    [InlineData("sekme\tiçeride")]
    public void Line_feed_and_tab_are_allowed_only_in_multiline_text(string input)
    {
        Assert.True(Text.TryClean(input, multiline: true, out var cleaned));
        Assert.Equal(input, cleaned);
        Assert.False(Text.TryClean(input, multiline: false, out var rejected));
        Assert.Equal(string.Empty, rejected);
    }

    [Theory]
    [InlineData(0x0000)] // NUL
    [InlineData(0x0007)] // BEL
    [InlineData(0x0008)] // BS
    [InlineData(0x000B)] // VT
    [InlineData(0x000C)] // FF
    [InlineData(0x000D)] // a lone carriage return
    [InlineData(0x001B)] // ESC
    [InlineData(0x007F)] // DEL
    [InlineData(0x0085)] // NEL (C1)
    [InlineData(0x009F)] // APC (C1)
    public void Control_characters_are_rejected_even_in_multiline_text(int codePoint)
    {
        var input = "ön" + (char)codePoint + "arka";

        Assert.False(Text.TryClean(input, multiline: true, out var cleaned));
        Assert.False(Text.TryClean(input, multiline: false, out _));
        Assert.Equal(string.Empty, cleaned);
    }

    [Theory]
    [InlineData(0x200B)] // zero width space
    [InlineData(0x200C)] // zero width non-joiner
    [InlineData(0x200D)] // zero width joiner
    [InlineData(0x2060)] // word joiner
    [InlineData(0xFEFF)] // zero width no-break space / BOM
    [InlineData(0x202D)] // left-to-right override
    [InlineData(0x202E)] // right-to-left override
    [InlineData(0x00AD)] // soft hyphen
    [InlineData(0x061C)] // arabic letter mark
    [InlineData(0x180E)] // mongolian vowel separator
    [InlineData(0x200E)] // left-to-right mark
    [InlineData(0x200F)] // right-to-left mark
    [InlineData(0x202A)] // left-to-right embedding
    [InlineData(0x202B)] // right-to-left embedding
    [InlineData(0x202C)] // pop directional formatting
    [InlineData(0x2061)] // function application (invisible operator)
    [InlineData(0x2066)] // left-to-right isolate
    [InlineData(0x2067)] // right-to-left isolate
    [InlineData(0x2068)] // first strong isolate
    [InlineData(0x2069)] // pop directional isolate
    [InlineData(0x206F)] // nominal digit shapes
    [InlineData(0xFFF9)] // interlinear annotation anchor
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    public void Invisible_format_characters_and_unicode_line_separators_are_rejected(int codePoint)
    {
        var input = "fatura" + (char)codePoint + "no";

        Assert.False(Text.TryClean(input, multiline: true, out var cleaned));
        Assert.False(Text.TryClean(input, multiline: false, out _));
        Assert.Equal(string.Empty, cleaned);
    }

    [Theory]
    [InlineData(0xE0001)] // language tag
    [InlineData(0xE0041)] // tag latin capital letter a: text that no reader sees
    [InlineData(0xE007F)] // cancel tag
    [InlineData(0x1D173)] // musical symbol begin beam
    public void Format_characters_outside_the_basic_plane_are_rejected_too(int codePoint)
    {
        var input = "fatura" + char.ConvertFromUtf32(codePoint) + "no";

        Assert.False(Text.TryClean(input, multiline: true, out _));
        Assert.False(Text.TryClean(input, multiline: false, out _));
    }

    [Theory]
    [InlineData(0x00A0)] // no-break space
    [InlineData(0x2013)] // en dash
    [InlineData(0x20BA)] // turkish lira sign
    [InlineData(0x1F600)] // an emoji: outside the basic plane but visible
    [InlineData(0x1D11E)] // musical symbol g clef
    public void Visible_characters_next_to_the_rejected_ranges_are_still_accepted(int codePoint)
    {
        var input = "fatura" + char.ConvertFromUtf32(codePoint) + "no";

        Assert.True(Text.TryClean(input, multiline: false, out var cleaned));
        Assert.Equal(input, cleaned);
    }

    [Fact]
    public void A_format_character_right_after_a_character_outside_the_basic_plane_is_still_seen()
    {
        var input = "fatura" + char.ConvertFromUtf32(0x1F600) + (char)0x200B + char.ConvertFromUtf32(0x1F600) + "no";

        Assert.False(Text.TryClean(input, multiline: true, out _));
    }

    [Fact]
    public void Cleaning_does_not_depend_on_the_thread_culture()
    {
        UnderEachCulture(culture =>
        {
            Assert.True(Text.TryClean("  Iğdır ILIK ılık İzmir iri  ", multiline: false, out var cleaned), culture);
            Assert.Equal("Iğdır ILIK ılık İzmir iri", cleaned);
        });
    }

    // ---- Validator.Text ------------------------------------------------------------------------

    [Fact]
    public void Validator_returns_the_cleaned_text_and_measures_length_after_cleaning()
    {
        var validator = new Validator();

        var name = validator.Text("name", "  Çay  ", 3, 3);

        Assert.Equal("Çay", name);
        Assert.True(validator.IsValid);
        validator.ThrowIfInvalid();
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("ab", "length")]
    [InlineData("abcdefghijk", "length")]
    [InlineData("ab\u0000cd", "invalid_characters")]
    [InlineData("ab\ncd", "invalid_characters")]
    [InlineData("ab\u200Bcd", "invalid_characters")]
    public void Validator_reports_required_length_and_character_errors(string? value, string code)
    {
        var validator = new Validator();

        validator.Text("note", value, 3, 10);

        Assert.False(validator.IsValid);
        var ex = Assert.Throws<DomainException>(validator.ThrowIfInvalid);
        Assert.Equal(ErrorKind.Validation, ex.Kind);
        Assert.Equal("validation_failed", ex.Code);
        Assert.Equal([("note", code)], ex.Errors.Select(e => (e.Field, e.Code)));
    }

    [Fact]
    public void Optional_text_may_be_missing_but_is_still_checked_when_present()
    {
        var validator = new Validator();

        Assert.Equal(string.Empty, validator.Text("note", null, 0, 5, required: false));
        Assert.Equal(string.Empty, validator.Text("note", "   ", 0, 5, required: false));
        Assert.True(validator.IsValid);

        validator.Text("note", "çok uzun bir not", 0, 5, required: false);
        Assert.False(validator.IsValid);
    }

    [Fact]
    public void Validator_collects_every_error_before_it_throws()
    {
        var validator = new Validator();
        validator.Text("name", null, 2, 10);
        validator.Code("sku", "a b", 3, 32);
        validator.Range("quantity", 0, 1, 10);
        validator.Money("price", 1.005m);

        var ex = Assert.Throws<DomainException>(validator.ThrowIfInvalid);

        Assert.Equal(["name", "sku", "quantity", "price"], ex.Errors.Select(e => e.Field));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.01", true)]
    [InlineData("12.3", true)]
    [InlineData("9999999999.99", true)]
    [InlineData("-0.01", false)]
    [InlineData("0.001", false)]
    [InlineData("10000000000", false)]
    public void Money_is_not_negative_has_two_decimals_at_most_and_is_bounded(string amount, bool valid)
    {
        var validator = new Validator();

        validator.Money("price", decimal.Parse(amount, CultureInfo.InvariantCulture));

        Assert.Equal(valid, validator.IsValid);
    }

    // ---- codes ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("abc-123", "ABC-123")]
    [InlineData("  irs/2026.10_a  ", "IRS/2026.10_A")]
    [InlineData("iii", "III")]
    [InlineData("KILIF-1", "KILIF-1")]
    public void Codes_are_trimmed_and_upper_cased_the_same_way_under_every_culture(string input, string expected)
    {
        UnderEachCulture(culture =>
        {
            // Under tr-TR a culture-sensitive upper-casing would turn "i" into "İ" and break the code.
            Assert.True(string.Equals(expected, Text.NormalizeCode(input), StringComparison.Ordinal), culture);
            Assert.True(Text.IsCode(Text.NormalizeCode(input), 3, 32), culture);

            var validator = new Validator();
            Assert.Equal(expected, validator.Code("sku", input, 3, 32));
            Assert.True(validator.IsValid, culture);
        });
    }

    [Theory]
    [InlineData("kılıf-1")] // dotless ı
    [InlineData("İZMİR-1")] // dotted capital İ
    [InlineData("ızgara")]
    [InlineData("çay-1")]
    [InlineData("ŞİŞE")]
    public void Turkish_letters_give_the_same_code_and_the_same_verdict_under_every_culture(string input)
    {
        var results = new List<(string Normalized, bool Valid)>();
        UnderEachCulture(_ =>
        {
            var normalized = Text.NormalizeCode(input);
            results.Add((normalized, Text.IsCode(normalized, 3, 32)));
        });

        Assert.Equal(2, results.Count);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(input.Trim().ToUpperInvariant(), results[0].Normalized);

        // Whatever the invariant mapping makes of these letters, a code is accepted only when it is plain ASCII.
        Assert.Equal(results[0].Normalized.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'), results[0].Valid);
    }

    [Theory]
    [InlineData("AB", false)]
    [InlineData("ABC", true)]
    [InlineData("A-B_C/D.9", true)]
    [InlineData("abc", false)]
    [InlineData("A B", false)]
    [InlineData("A'B", false)]
    [InlineData("A;B--", false)]
    [InlineData("ÄBC", false)]
    [InlineData("ABC\n", false)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456", false)]
    public void A_code_is_three_to_thirty_two_upper_case_ascii_letters_digits_and_four_symbols(string value, bool expected)
    {
        UnderEachCulture(culture => Assert.True(expected == Text.IsCode(value, 3, 32), culture));
    }

    [Fact]
    public void Validator_code_reports_invalid_code_with_the_field_name()
    {
        var validator = new Validator();

        validator.Code("reference", "irs 2026", 3, 48);

        var ex = Assert.Throws<DomainException>(validator.ThrowIfInvalid);
        Assert.Equal([("reference", "invalid_code")], ex.Errors.Select(e => (e.Field, e.Code)));
    }

    // ---- role names ----------------------------------------------------------------------------

    [Fact]
    public void Role_names_are_matched_ordinally_under_every_culture()
    {
        UnderEachCulture(culture =>
        {
            Assert.All(Roles.All, role => Assert.True(Roles.IsKnown(role), culture));

            // Under tr-TR a case-insensitive, culture-aware comparison would treat "ı"/"i" and "I"/"İ" as pairs.
            foreach (var lookalike in new[] { "SystemAdmın", "SYSTEMADMIN", "systemadmin", "SystemAdmİn", "Systemadmin", "SalesAgent ", "salesagent", "WarehouseOperatör" })
            {
                Assert.False(Roles.IsKnown(lookalike), $"{culture}: {lookalike}");
            }

            var user = new CurrentUser(
                Guid.NewGuid(), "yonetici", "Yönetici", new HashSet<string>([Roles.SystemAdmin, Roles.SupportAgent], StringComparer.Ordinal),
                new Dictionary<Guid, WarehouseLevel>(), 0, Guid.NewGuid(), false);
            Assert.True(user.Is(Roles.SystemAdmin), culture);
            Assert.True(user.IsSupportStaff, culture);
            Assert.False(user.Is("SYSTEMADMIN"), culture);
            Assert.False(user.Is("systemadmin"), culture);
            Assert.False(user.Is("SystemAdmın"), culture);
            Assert.False(user.IsAny("SİSTEMADMİN", "supportagent"), culture);
            Assert.True(AccessPolicy.CanManageMasterData(user), culture);
        });
    }
}
