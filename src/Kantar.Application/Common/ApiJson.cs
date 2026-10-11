using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kantar.Application.Common;

/// <summary>
/// The one JSON configuration of the API. Reading is strict (unknown and duplicate members are
/// errors, numbers are never read from strings); writing is deterministic for a given type, which
/// is what the stored idempotency response and the request hash rely on.
/// </summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 8,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>
    /// Hash of a validated, normalised command. The canonical form is this API's own serialisation of
    /// the typed command (fixed member order, invariant numbers); it is not a general RFC 8785 implementation.
    /// </summary>
    public static byte[] HashCommand<T>(string operation, T command)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData("v1\u001f"u8);
        sha.AppendData(Encoding.UTF8.GetBytes(operation));
        sha.AppendData("\u001f"u8);
        sha.AppendData(Serialize(command));
        return sha.GetHashAndReset();
    }
}

public static class ETags
{
    public static string From(byte[] rowVersion) => "\"" + Convert.ToBase64String(rowVersion) + "\"";

    /// <summary>Parses a strong ETag. Weak tags and lists are not accepted for preconditions.</summary>
    public static bool TryParse(string? header, out byte[] rowVersion)
    {
        rowVersion = [];
        if (string.IsNullOrEmpty(header) || header.Length < 3 || header[0] != '"' || header[^1] != '"')
        {
            return false;
        }

        var buffer = new byte[16];
        if (!Convert.TryFromBase64String(header[1..^1], buffer, out var written) || written != 8)
        {
            return false;
        }

        rowVersion = buffer[..written];
        return true;
    }
}

/// <summary>Opaque keyset cursor: position of the last row of the previous page.</summary>
public static class Cursor
{
    public static string Encode(DateTime createdAt, Guid id) =>
        Base64Url($"{createdAt.Ticks:x}.{id:N}");

    public static string Encode(long id) => Base64Url($"n.{id:x}");

    public static bool TryDecode(string? cursor, out DateTime createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        var text = FromBase64Url(cursor);
        var parts = text?.Split('.');
        return parts is { Length: 2 }
            && long.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            && Guid.TryParseExact(parts[1], "N", out id)
            && Assign(out createdAt, new DateTime(ticks, DateTimeKind.Utc));
    }

    public static bool TryDecode(string? cursor, out long id)
    {
        id = 0;
        var text = FromBase64Url(cursor);
        var parts = text?.Split('.');
        return parts is ["n", var hex]
            && long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id);
    }

    private static bool Assign(out DateTime target, DateTime value)
    {
        target = value;
        return true;
    }

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.ASCII.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? FromBase64Url(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return null;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        var buffer = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? Encoding.ASCII.GetString(buffer, 0, written) : null;
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
