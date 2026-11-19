using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.Localization;

/// <summary>
/// A UI string split into the words that get translated (<see cref="Core"/>) and the decoration
/// around them: surrounding whitespace and a trailing ':' / '...' / '…'.
/// </summary>
public readonly record struct TextParts(string Leading, string Core, string Suffix, string Trailing);

/// <summary>
/// The normalisation shared by the harvest tool (Mission Planner's .resx files) and the runtime
/// lookup, so "&amp;Load File...", "Load file:" and "LOAD FILE" all find the same entry.
/// </summary>
public static partial class LocalizationText
{
    [GeneratedRegex(@"\s*[(（]&[\p{L}\p{N}][)）]")]
    private static partial Regex CjkMnemonic();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^{}]*)?\}")]
    private static partial Regex FormatItem();

    /// <summary>
    /// Splits text into leading whitespace, the core (mnemonics removed, whitespace collapsed,
    /// trailing ':' / '...' / '…' removed), that suffix (with the spaces before it), and trailing whitespace.
    /// </summary>
    public static TextParts Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new TextParts("", "", "", "");

        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        var end = text.Length;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        var leading = text[..start];
        var trailing = text[end..];
        var body = CollapseWhitespace(StripMnemonics(text[start..end]));

        var suffix = "";
        if (body.EndsWith("...", StringComparison.Ordinal))
            suffix = "...";
        else if (body.EndsWith('…'))
            suffix = "…";
        else if (body.EndsWith(':') || body.EndsWith('：'))
            suffix = body[^1..];

        if (suffix.Length > 0)
        {
            body = body[..^suffix.Length];
            var core = body.TrimEnd();
            suffix = body[core.Length..] + suffix;
            body = core;
        }

        return new TextParts(leading, body, suffix, trailing);
    }

    /// <summary>The lookup key: the core in lower case.</summary>
    public static string Key(string? text) => Split(text).Core.ToLowerInvariant();

    /// <summary>Removes WinForms access-key markers: "&amp;File" → "File", "&amp;&amp;" → "&amp;", "文件(&amp;F)" → "文件".</summary>
    public static string StripMnemonics(string text)
    {
        if (text.IndexOf('&') < 0)
            return text;
        text = CjkMnemonic().Replace(text, "");
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '&' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                if (next == '&')
                {
                    sb.Append('&');
                    i++;
                    continue;
                }

                if (char.IsLetterOrDigit(next))
                    continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Runs of whitespace (line breaks included) become one space; the ends are trimmed.</summary>
    public static string CollapseWhitespace(string text) => Whitespace().Replace(text, " ").Trim();

    public static bool HasLetters(string text) => text.Any(char.IsLetter);

    /// <summary>True for text like "CONNECT" or "ARM / DISARM": two or more letters, none lower case.</summary>
    public static bool IsAllCaps(string text)
    {
        var letters = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
                continue;
            if (char.IsLower(c))
                return false;
            if (char.IsUpper(c))
                letters++;
        }

        return letters >= 2;
    }

    /// <summary>The composite-format items ({0}, {1:F2}...) a string uses, as a sorted set of indexes.</summary>
    public static SortedSet<int> FormatItems(string text)
    {
        var set = new SortedSet<int>();
        foreach (Match m in FormatItem().Matches(text))
            set.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return set;
    }

    /// <summary>
    /// Whether a translation can stand in for the English text in string.Format: the same format
    /// items, and no brace that would make formatting throw.
    /// </summary>
    public static bool SameFormatItems(string english, string translation)
    {
        var items = FormatItems(english);
        if (!items.SetEquals(FormatItems(translation)))
            return false;
        if (items.Count == 0)
            return true;
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, translation, new object[items.Max + 1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
