using System.Globalization;
using System.Text;

namespace Kantar.Domain.Common;

/// <summary>Normalisation applied to every free-text and code input before validation, hashing and storage.</summary>
public static class Text
{
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// <summary>Trims, normalises to NFC and rejects control and invisible characters; line feed and tab only in multi-line text.</summary>
    public static bool TryClean(string? input, bool multiline, out string cleaned)
    {
        cleaned = string.Empty;
        if (input is null)
        {
            return true;
        }

        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Normalize(NormalizationForm.FormC);
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            if (ch is '\n' or '\t')
            {
                if (!multiline)
                {
                    return false;
                }

                continue;
            }

            // Refused because they hide or reorder text for a reviewer: control characters, the Unicode
            // line and paragraph separators, and every format character (category Cf: zero-width
            // characters, bidirectional marks, embeddings, overrides and isolates, soft hyphen, tags).
            var codePoint = char.IsHighSurrogate(ch) && i + 1 < normalized.Length && char.IsLowSurrogate(normalized[i + 1])
                ? char.ConvertToUtf32(ch, normalized[++i])
                : ch;
            if (char.IsControl(ch) || ch is LineSeparator or ParagraphSeparator || CharUnicodeInfo.GetUnicodeCategory(codePoint) == UnicodeCategory.Format)
            {
                return false;
            }
        }

        cleaned = normalized;
        return true;
    }

    /// <summary>Codes (SKU, business references) live in binary-collated columns, so they are stored upper-invariant.</summary>
    public static string NormalizeCode(string input) => input.Trim().ToUpperInvariant();

    public static bool IsCode(string value, int min, int max)
    {
        if (value.Length < min || value.Length > max)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '/' or '.';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
