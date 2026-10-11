using System.Text.Json;
using Kantar.Application.Common;
using Kantar.Domain.Common;

namespace Kantar.Api.Http;

/// <summary>
/// Strict request body reader. Bodies are bounded, must be application/json, and are bound to a
/// request record that lists exactly the fields a caller may send: there is no way to set a price,
/// status, owner or number by adding a property (mass assignment), because unknown members fail.
/// </summary>
public static class JsonBody
{
    public const int MaxBytes = 64 * 1024;

    /// <summary>True for "application/json" with or without parameters such as charset.</summary>
    public static bool IsJson(string? contentType) =>
        string.Equals(contentType?.Split(';', 2)[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase);

    public static async Task<T> ReadAsync<T>(HttpContext context) where T : class
    {
        var request = context.Request;
        if (!IsJson(request.ContentType))
        {
            throw new DomainException(ErrorKind.Validation, "unsupported_media_type");
        }

        if (request.ContentLength > MaxBytes)
        {
            throw new DomainException(ErrorKind.Validation, "payload_too_large");
        }

        var buffer = new byte[MaxBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        if (read > MaxBytes)
        {
            throw new DomainException(ErrorKind.Validation, "payload_too_large");
        }

        try
        {
            RejectNullElements(buffer.AsSpan(0, read));
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, read), ApiJson.Options)
                ?? throw new DomainException(ErrorKind.Validation, "invalid_json");
        }
        catch (JsonException ex)
        {
            throw Classify(ex);
        }
    }

    /// <summary>
    /// The serializer enforces nullability on members but not on array elements: "lines":[null] would
    /// bind to a list holding a null line and fail later as a server error. No request of this API has
    /// nullable elements, so a null directly inside an array is refused here, before binding.
    /// </summary>
    private static void RejectNullElements(ReadOnlySpan<byte> json)
    {
        var limit = ApiJson.Options.MaxDepth;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = limit });
        Span<bool> inArray = stackalloc bool[limit + 2];
        Span<int> index = stackalloc int[limit + 2];
        var names = new string?[limit + 2];
        inArray.Clear();

        while (reader.Read())
        {
            var depth = reader.CurrentDepth;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    // Only used to name the offending element; an escaped name is not decoded for that.
                    names[depth] = reader.ValueIsEscaped ? "?" : System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
                    continue;
                case JsonTokenType.EndArray or JsonTokenType.EndObject:
                    continue;
            }

            if (inArray[depth])
            {
                index[depth]++;
                if (reader.TokenType == JsonTokenType.Null)
                {
                    var path = new System.Text.StringBuilder("$");
                    for (var level = 1; level <= depth; level++)
                    {
                        if (inArray[level])
                        {
                            path.Append('[').Append(index[level].ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                        }
                        else
                        {
                            path.Append('.').Append(names[level]);
                        }
                    }

                    throw new DomainException(
                        ErrorKind.Validation, "validation_failed", null,
                        [new FieldError(Bounded(path.ToString()), "validation_failed", "Alan eksik veya türü hatalı.")]);
                }
            }

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                inArray[depth + 1] = reader.TokenType == JsonTokenType.StartArray;
                index[depth + 1] = -1;
            }
        }
    }

    private static string Bounded(string path) => path.Length > 120 ? path[..120] : path;

    /// <summary>
    /// Maps serializer failures to stable codes. The serializer's message is inspected only to tell
    /// the cases apart; the path (not the message) is returned to the client.
    /// </summary>
    private static DomainException Classify(JsonException exception)
    {
        var message = exception.Message;
        var path = Bounded(string.IsNullOrEmpty(exception.Path) ? "$" : exception.Path);

        var code = message.Contains("could not be mapped to any .NET member", StringComparison.Ordinal) ? "unknown_field"
            : message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) ? "duplicate_field"
            : message.Contains("missing required properties", StringComparison.Ordinal) ? "validation_failed"

            // An explicit null for a member that is not optional is the same mistake as leaving it out
            // (docs/05 §1.1), not malformed JSON.
            : message.Contains("null values", StringComparison.Ordinal) ? "validation_failed"
            : message.Contains("could not be converted", StringComparison.Ordinal) ? "validation_failed"
            : "invalid_json";

        var detail = code switch
        {
            "unknown_field" => "Bu alan desteklenmiyor.",
            "duplicate_field" => "Alan birden çok kez verildi.",
            "validation_failed" => "Alan eksik veya türü hatalı.",
            _ => "Gövde ayrıştırılamadı.",
        };

        return new DomainException(ErrorKind.Validation, code, null, [new FieldError(path, code, detail)]);
    }
}
