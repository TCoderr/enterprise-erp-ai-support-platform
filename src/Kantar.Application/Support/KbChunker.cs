using System.Text;

namespace Kantar.Application.Support;

public sealed record KbSection(int SectionNo, string Heading, string Text);

/// <summary>
/// Splits a Markdown body into sections by heading; long sections are split at paragraph boundaries.
/// The result is what retrieval serves, so every piece keeps the heading it belongs to.
/// </summary>
public static class KbChunker
{
    public const int TargetLength = 1200;
    public const int HardLimit = 1500;
    public const int MaxSections = 200;

    public static IReadOnlyList<KbSection> Split(string title, string bodyMarkdown)
    {
        var sections = new List<KbSection>();
        var heading = title;
        var buffer = new StringBuilder();

        void Flush()
        {
            var text = buffer.ToString().Trim();
            buffer.Clear();
            if (text.Length == 0)
            {
                return;
            }

            foreach (var piece in SplitLong(text))
            {
                if (sections.Count < MaxSections)
                {
                    sections.Add(new KbSection(sections.Count + 1, Truncate(heading, 160), piece));
                }
            }
        }

        foreach (var rawLine in bodyMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 4 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    Flush();
                    heading = trimmed[(level + 1)..].Trim();
                    if (heading.Length == 0)
                    {
                        heading = title;
                    }

                    continue;
                }
            }

            buffer.Append(line).Append('\n');
        }

        Flush();
        return sections;
    }

    private static IEnumerable<string> SplitLong(string text)
    {
        if (text.Length <= TargetLength)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var part = paragraph.Trim();
            if (current.Length > 0 && current.Length + part.Length + 2 > TargetLength)
            {
                yield return current.ToString();
                current.Clear();
            }

            // A single paragraph longer than the hard limit is cut; it would not fit the prompt budget otherwise.
            while (part.Length > HardLimit)
            {
                yield return part[..HardLimit];
                part = part[HardLimit..];
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(part);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
