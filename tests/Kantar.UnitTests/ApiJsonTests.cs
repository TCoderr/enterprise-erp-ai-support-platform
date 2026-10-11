using System.Text;
using System.Text.Json;
using Kantar.Application.Common;
using Xunit;

namespace Kantar.UnitTests;

/// <summary>The API's single JSON configuration: strict reading, deterministic writing, request hash, ETag and cursor codecs.</summary>
public sealed class ApiJsonTests
{
    public enum Colour
    {
        Red,
        Green,
    }

    public sealed record Line(Guid ProductId, int Quantity);

    public sealed record Sample(string Name, int Quantity, IReadOnlyList<Line> Lines, string? Note = null);

    public sealed record Painted(Colour Colour);

    public sealed class Node
    {
        public Node? Child { get; init; }
    }

    private static readonly Guid ProductA = Guid.Parse("0199c0de-0000-7000-8000-00000000000a");
    private static readonly Guid ProductB = Guid.Parse("0199c0de-0000-7000-8000-00000000000b");

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(Encoding.UTF8.GetBytes(json), ApiJson.Options)!;

    private static string Valid(string name = "\"Kalem\"", string quantity = "3", string extra = "") =>
        $$"""{"name":{{name}},"quantity":{{quantity}},"lines":[{"productId":"{{ProductA}}","quantity":2}]{{extra}}}""";

    // ---- reading -------------------------------------------------------------------------------

    [Fact]
    public void A_well_formed_body_binds_and_an_omitted_optional_member_is_null()
    {
        var sample = Read<Sample>(Valid());

        Assert.Equal(("Kalem", 3, null), (sample.Name, sample.Quantity, sample.Note));
        Assert.Equal([new Line(ProductA, 2)], sample.Lines);
    }

    [Theory]
    [InlineData(""","status":"Confirmed" """)]
    [InlineData(""","unitPrice":1.00 """)]
    [InlineData(""","Name":"Büyük harfli ad" """)]
    public void An_unknown_member_is_an_error(string extra)
    {
        var ex = Assert.Throws<JsonException>(() => Read<Sample>(Valid(extra: extra)));

        Assert.Contains("could not be mapped", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_member_inside_a_line_is_an_error()
    {
        var json = $$"""{"name":"Kalem","quantity":3,"lines":[{"productId":"{{ProductA}}","quantity":2,"unitPrice":0.01}]}""";

        Assert.Throws<JsonException>(() => Read<Sample>(json));
    }

    [Theory]
    [InlineData(""","quantity":4 """)]
    [InlineData(""","name":"İkinci" """)]
    [InlineData(""","note":"a","note":"b" """)]
    public void A_duplicate_member_is_an_error(string extra)
    {
        var ex = Assert.Throws<JsonException>(() => Read<Sample>(Valid(extra: extra)));

        Assert.Contains("Duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("\"1\"")]
    [InlineData("1e0")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("2147483648")]
    public void An_integer_member_accepts_only_a_json_integer(string quantity)
    {
        Assert.Throws<JsonException>(() => Read<Sample>(Valid(quantity: quantity)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("2147483647")]
    public void Json_integers_bind_as_written(string quantity)
    {
        Assert.Equal(int.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), Read<Sample>(Valid(quantity: quantity)).Quantity);
    }

    [Theory]
    [InlineData("""{"quantity":3,"lines":[]}""")]
    [InlineData("""{"name":"Kalem","lines":[]}""")]
    [InlineData("""{"name":"Kalem","quantity":3}""")]
    [InlineData("""{}""")]
    public void A_missing_required_constructor_parameter_is_an_error(string json)
    {
        Assert.Throws<JsonException>(() => Read<Sample>(json));
    }

    [Theory]
    [InlineData("""{"name":null,"quantity":3,"lines":[]}""")]
    [InlineData("""{"name":"Kalem","quantity":null,"lines":[]}""")]
    [InlineData("""{"name":"Kalem","quantity":3,"lines":null}""")]
    public void An_explicit_null_for_a_non_nullable_member_is_an_error(string json)
    {
        Assert.Throws<JsonException>(() => Read<Sample>(json));
    }

    [Fact]
    public void An_explicit_null_for_an_optional_member_equals_leaving_it_out()
    {
        var withNull = Read<Sample>(Valid(extra: ""","note":null """));
        var without = Read<Sample>(Valid());

        Assert.Null(withNull.Note);
        Assert.Equal(ApiJson.HashCommand("op", without), ApiJson.HashCommand("op", withNull));
    }

    [Fact]
    public void Nesting_deeper_than_the_limit_is_an_error()
    {
        static string Nested(int levels) => string.Concat(Enumerable.Repeat("""{"child":""", levels)) + "{}" + new string('}', levels);

        Assert.NotNull(Read<Node>(Nested(3)));
        Assert.Equal(8, ApiJson.Options.MaxDepth);
        Assert.Throws<JsonException>(() => Read<Node>(Nested(8)));
        Assert.Throws<JsonException>(() => Read<Node>(Nested(64)));
    }

    [Theory]
    [InlineData("""{"name":"Kalem","quantity":3,"lines":[],}""")]
    [InlineData("""{"name":"Kalem",/* yorum */"quantity":3,"lines":[]}""")]
    [InlineData("""{"name":"Kalem","quantity":3,"lines":[]""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""kalem""")]
    public void Malformed_or_lenient_json_is_an_error(string json)
    {
        Assert.Throws<JsonException>(() => Read<Sample>(json));
    }

    [Fact]
    public void Enums_are_read_from_their_names_only()
    {
        Assert.Equal(Colour.Green, Read<Painted>("""{"colour":"Green"}""").Colour);
        Assert.Throws<JsonException>(() => Read<Painted>("""{"colour":1}"""));
        Assert.Throws<JsonException>(() => Read<Painted>("""{"colour":"Purple"}"""));
    }

    // ---- writing -------------------------------------------------------------------------------

    [Fact]
    public void Writing_is_camel_case_compact_and_leaves_null_members_out()
    {
        var json = Encoding.UTF8.GetString(ApiJson.Serialize(new Sample("Kalem", 3, [new Line(ProductA, 2)])));

        Assert.Equal($$"""{"name":"Kalem","quantity":3,"lines":[{"productId":"{{ProductA}}","quantity":2}]}""", json);
        Assert.Equal("""{"colour":"Green"}""", Encoding.UTF8.GetString(ApiJson.Serialize(new Painted(Colour.Green))));
    }

    [Fact]
    public void Writing_the_same_value_twice_gives_the_same_bytes()
    {
        var value = new Sample("Çıktı", 12, [new Line(ProductA, 1), new Line(ProductB, 5)], "not");

        Assert.Equal(ApiJson.Serialize(value), ApiJson.Serialize(value with { }));
    }

    // ---- request hash --------------------------------------------------------------------------

    private static Sample Command() => new("Kalem", 3, [new Line(ProductA, 2), new Line(ProductB, 5)], "not");

    [Fact]
    public void Hash_is_stable_for_equal_commands_built_separately()
    {
        var first = ApiJson.HashCommand("sales.order.create", Command());
        var second = ApiJson.HashCommand("sales.order.create", Command());

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_changes_with_the_operation_name()
    {
        var command = Command();

        Assert.NotEqual(ApiJson.HashCommand("sales.order.create", command), ApiJson.HashCommand("sales.order.confirm", command));
        Assert.NotEqual(ApiJson.HashCommand("a", command), ApiJson.HashCommand("A", command));
    }

    public static TheoryData<string, Sample> ChangedCommands() => new()
    {
        { "name", Command() with { Name = "Kalem " } },
        { "name case", Command() with { Name = "kalem" } },
        { "quantity", Command() with { Quantity = 4 } },
        { "note", Command() with { Note = "Not" } },
        { "note removed", Command() with { Note = null } },
        { "line quantity", Command() with { Lines = [new Line(ProductA, 2), new Line(ProductB, 6)] } },
        { "line product", Command() with { Lines = [new Line(ProductA, 2), new Line(ProductA, 5)] } },
        { "line order", Command() with { Lines = [new Line(ProductB, 5), new Line(ProductA, 2)] } },
        { "line removed", Command() with { Lines = [new Line(ProductA, 2)] } },
        { "line added", Command() with { Lines = [new Line(ProductA, 2), new Line(ProductB, 5), new Line(Guid.Empty, 1)] } },
    };

    [Theory]
    [MemberData(nameof(ChangedCommands))]
    public void Hash_changes_with_any_field(string what, Sample changed)
    {
        Assert.False(
            ApiJson.HashCommand("sales.order.create", Command()).AsSpan().SequenceEqual(ApiJson.HashCommand("sales.order.create", changed)),
            $"Hash did not change with: {what}");
    }

    [Fact]
    public void Operation_and_body_cannot_be_shifted_into_each_other()
    {
        // Without the separator both would hash the bytes "op123".
        Assert.NotEqual(ApiJson.HashCommand("op1", 23), ApiJson.HashCommand("op12", 3));
        Assert.NotEqual(ApiJson.HashCommand("op", 123), ApiJson.HashCommand("op123", 0));
    }

    // ---- ETags ---------------------------------------------------------------------------------

    [Fact]
    public void ETag_round_trips_a_row_version()
    {
        byte[] rowVersion = [0, 0, 0, 0, 0, 0, 0x1F, 0xA4];

        var tag = ETags.From(rowVersion);

        Assert.Equal("\"AAAAAAAAH6Q=\"", tag);
        Assert.True(ETags.TryParse(tag, out var parsed));
        Assert.Equal(rowVersion, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\"\"")]
    [InlineData("AAAAAAAAH6Q=")]
    [InlineData("W/\"AAAAAAAAH6Q=\"")]
    [InlineData("\"AAAAAAAAH6Q=\", \"AAAAAAAAH6U=\"")]
    [InlineData("\"AAAAAAAAH6Q=")]
    [InlineData("*")]
    [InlineData("\"not base64 !!\"")]
    [InlineData("\"AAAAAAAAHw==\"")]
    [InlineData("\"AAAAAAAAH6QA\"")]
    [InlineData("\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    public void ETag_parser_rejects_weak_list_and_garbage_values(string? header)
    {
        Assert.False(ETags.TryParse(header, out var parsed));
        Assert.Empty(parsed);
    }

    // ---- cursors -------------------------------------------------------------------------------

    [Fact]
    public void Document_cursor_round_trips_time_and_id()
    {
        var at = new DateTime(2026, 10, 11, 17, 45, 51, 123, DateTimeKind.Utc);
        var id = Guid.CreateVersion7();

        var cursor = Cursor.Encode(at, id);

        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
        Assert.True(Cursor.TryDecode(cursor, out var decodedAt, out var decodedId));
        Assert.Equal((at, id, DateTimeKind.Utc), (decodedAt, decodedId, decodedAt.Kind));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(255L)]
    [InlineData(long.MaxValue)]
    public void Journal_cursor_round_trips_an_id(long id)
    {
        Assert.True(Cursor.TryDecode(Cursor.Encode(id), out long decoded));
        Assert.Equal(id, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("abc")]
    [InlineData("bm90LWEtY3Vyc29y")]
    [InlineData("%00%00")]
    [InlineData("../../etc/passwd")]
    public void Cursor_parsers_reject_garbage(string? cursor)
    {
        Assert.False(Cursor.TryDecode(cursor, out DateTime _, out Guid _));
        Assert.False(Cursor.TryDecode(cursor, out long _));
    }

    [Fact]
    public void Cursor_parsers_reject_oversized_input()
    {
        var oversized = new string('A', 129);
        var huge = new string('A', 100_000);

        Assert.False(Cursor.TryDecode(oversized, out DateTime _, out Guid _));
        Assert.False(Cursor.TryDecode(oversized, out long _));
        Assert.False(Cursor.TryDecode(huge, out DateTime _, out Guid _));
        Assert.False(Cursor.TryDecode(huge, out long _));
    }

    [Fact]
    public void A_cursor_of_one_kind_is_not_accepted_as_the_other()
    {
        var document = Cursor.Encode(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Guid.NewGuid());
        var journal = Cursor.Encode(42L);

        Assert.False(Cursor.TryDecode(document, out long _));
        Assert.False(Cursor.TryDecode(journal, out DateTime _, out Guid _));
    }

    [Theory]
    [InlineData("7fffffffffffffff.00000000000000000000000000000000")]
    [InlineData("-1.00000000000000000000000000000000")]
    [InlineData("zz.00000000000000000000000000000000")]
    [InlineData("8dc0a1b2c3d4e5f.not-a-guid")]
    [InlineData("8dc0a1b2c3d4e5f.00000000-0000-0000-0000-000000000000")]
    [InlineData("8dc0a1b2c3d4e5f")]
    [InlineData("8dc0a1b2c3d4e5f.00000000000000000000000000000000.extra")]
    public void Document_cursor_with_an_impossible_time_or_id_is_rejected(string raw)
    {
        var cursor = Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(Cursor.TryDecode(cursor, out DateTime _, out Guid _));
    }
}
