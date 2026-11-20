using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.Localization;

/// <summary>
/// Finds the English UI strings in the app's sources, for the string list and coverage report:
/// <c>{l:Tr 'text'}</c> in XAML, and in C# the literal arguments of <c>L.Tr</c>/<c>L.Format</c>/<c>L.N</c>,
/// of the dialog calls (which the dialog service translates), of toasts (AppServices.Notify), of menu
/// entries and of page titles.
/// </summary>
public static partial class UiStringScanner
{
    // method name → indexes of the arguments that are shown to the user
    private static readonly Dictionary<string, int[]> Calls = new(StringComparer.Ordinal)
    {
        ["L.Tr"] = [0],
        ["L.Format"] = [0],
        ["L.N"] = [0],
        ["ShowMessageAsync"] = [0, 1],
        ["ConfirmAsync"] = [0, 1, 2],
        ["PromptAsync"] = [0, 1],
        ["OpenFileAsync"] = [0],
        ["SaveFileAsync"] = [0],
        ["PickFolderAsync"] = [0],
        ["Notify"] = [0],
        ["Item"] = [0],
        ["FileFilter"] = [0],
        ["Page"] = [0],
        ["Header"] = [0],
        ["base"] = [0],
    };

    [GeneratedRegex(@"\{l:Tr\s+")]
    private static partial Regex XamlTr();

    [GeneratedRegex(@"(?<!\w)(L\.Tr|L\.Format|L\.N|ShowMessageAsync|ConfirmAsync|PromptAsync|OpenFileAsync|SaveFileAsync|PickFolderAsync|Notify|Item|FileFilter|Page|Header|:\s*base)\s*\(")]
    private static partial Regex CSharpCall();

    /// <summary>The texts of the <c>{l:Tr ...}</c> extensions in a XAML file.</summary>
    public static IEnumerable<string> ScanXaml(string xaml)
    {
        foreach (Match m in XamlTr().Matches(xaml))
        {
            var text = ReadMarkupArgument(xaml, m.Index + m.Length);
            if (text != null)
                yield return WebUtility.HtmlDecode(text);
        }
    }

    /// <summary>
    /// Reads the first argument of a markup extension: a quoted string ('...' with \' escapes) or the
    /// unquoted text up to ',' or '}'. A "Text=" prefix is accepted.
    /// </summary>
    public static string? ReadMarkupArgument(string s, int i)
    {
        if (s.AsSpan(i).StartsWith("Text="))
            i += 5;
        if (i >= s.Length)
            return null;
        if (s[i] == '\'')
        {
            var sb = new StringBuilder();
            for (i++; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    sb.Append(s[++i]);
                    continue;
                }

                if (s[i] == '\'')
                    return sb.ToString();
                sb.Append(s[i]);
            }

            return null;
        }

        var end = s.IndexOfAny([',', '}'], i);
        return end < 0 ? null : s[i..end].Trim();
    }

    [GeneratedRegex(@"\{l:TrBind\s+\{Binding\s+(?:[\w.]+\.)?(\w+)\s*\}\s*\}")]
    private static partial Regex XamlTrBind();

    /// <summary>The view-model properties a XAML file shows through <c>{l:TrBind {Binding X}}</c>.</summary>
    public static IEnumerable<string> ScanXamlTranslatedBindings(string xaml) =>
        XamlTrBind().Matches(xaml).Select(m => m.Groups[1].Value);

    /// <summary>
    /// The plain string literals a C# file assigns to the given properties (or to their
    /// [ObservableProperty] fields): <c>StatusText = ok ? "Done" : "Failed";</c>, <c>Title => "Compass";</c>.
    /// Literals that are concatenated or passed to calls are left out: they are not shown whole.
    /// </summary>
    public static IEnumerable<string> ScanAssignments(string code, IReadOnlySet<string> properties)
    {
        if (properties.Count == 0)
            yield break;
        var names = string.Join("|", properties.SelectMany(p => new[] { p, "_" + char.ToLowerInvariant(p[0]) + p[1..] }).Select(Regex.Escape));
        var assign = new Regex(@"(?<![\w.])(?:this\.)?(?:" + names + @")\s*(=(?![=>])|=>)\s*");
        foreach (Match m in assign.Matches(code))
        {
            foreach (var literal in TopLevelLiterals(code, m.Index + m.Length))
                yield return literal;
        }
    }

    // plain literals of an expression up to its ';' or the closing bracket of the enclosing initializer, outside calls and concatenation
    private static IEnumerable<string> TopLevelLiterals(string code, int i)
    {
        var depth = 0;
        var previous = '\0';
        for (; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"')
            {
                var interpolated = i > 0 && (code[i - 1] == '$' || (i > 1 && code[i - 2] == '$'));
                var end = SkipLiteral(code, i);
                var next = NextSignificant(code, end + 1);
                if (depth == 0 && !interpolated && previous != '+' && next != '+' && next != '.')
                {
                    var start = i > 0 && code[i - 1] == '@' ? i - 1 : i;
                    if (ParseLiteral(code[start..(end + 1)]) is { Length: > 0 } text)
                        yield return text;
                }

                i = end;
                previous = '"';
                continue;
            }

            if (c == '\'')
            {
                i = SkipLiteral(code, i);
                previous = '\'';
                continue;
            }

            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                    yield break;
                depth--;
            }
            else if ((c == ';' || c == ',') && depth == 0)
                yield break;

            if (!char.IsWhiteSpace(c))
                previous = c;
        }
    }

    private static char NextSignificant(string code, int i)
    {
        for (; i < code.Length; i++)
        {
            if (!char.IsWhiteSpace(code[i]))
                return code[i];
        }

        return '\0';
    }

    /// <summary>The user-visible string literals in a C# file.</summary>
    public static IEnumerable<string> ScanCSharp(string code)
    {
        foreach (Match m in CSharpCall().Matches(code))
        {
            var name = m.Groups[1].Value;
            if (name.StartsWith(':'))
                name = "base";
            var args = SplitArguments(code, m.Index + m.Length);
            foreach (var index in Calls[name])
            {
                if (index < args.Count && ParseLiteral(args[index]) is { Length: > 0 } text)
                    yield return text;
            }
        }
    }

    /// <summary>Splits the argument list that starts after '(' at the top level.</summary>
    public static List<string> SplitArguments(string code, int i)
    {
        var args = new List<string>();
        var depth = 0;
        var start = i;
        for (; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    args.Add(code[start..i].Trim());
                    return args;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(code[start..i].Trim());
                start = i + 1;
            }
        }

        return args;
    }

    // returns the index of the closing quote of the literal starting at i (handles the @, $ and raw """ forms)
    private static int SkipLiteral(string code, int i)
    {
        var quote = code[i];
        if (quote == '"' && code.AsSpan(i).StartsWith("\"\"\""))
        {
            var n = 3;
            while (i + n < code.Length && code[i + n] == '"')
                n++;
            var close = code.IndexOf(new string('"', n), i + n, StringComparison.Ordinal);
            return close < 0 ? code.Length - 1 : close + n - 1;
        }

        var prefix = i > 1 ? code.Substring(i - 2, 2) : i > 0 ? code[(i - 1)..i] : "";
        var verbatim = quote == '"' && (prefix.EndsWith('@') || prefix == "@$");
        var interpolated = quote == '"' && (prefix.EndsWith('$') || prefix == "$@");
        for (i++; i < code.Length; i++)
        {
            if (interpolated && code[i] == '{')
            {
                if (i + 1 < code.Length && code[i + 1] == '{')
                {
                    i++;
                    continue;
                }

                i = SkipHole(code, i);
                continue;
            }

            if (verbatim)
            {
                if (code[i] == '"')
                {
                    if (i + 1 < code.Length && code[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }

                    return i;
                }

                continue;
            }

            if (code[i] == '\\')
            {
                i++;
                continue;
            }

            if (code[i] == quote || code[i] == '\n')
                return i;
        }

        return i;
    }

    // from the '{' of an interpolation hole to its matching '}'
    private static int SkipHole(string code, int i)
    {
        var depth = 0;
        for (; i < code.Length; i++)
        {
            var c = code[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(code, i);
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}' && --depth == 0)
                return i;
        }

        return i;
    }

    /// <summary>The value of a plain C# string literal ("..." or @"..."), or null for anything else.</summary>
    public static string? ParseLiteral(string arg)
    {
        arg = arg.Trim();
        if (arg.StartsWith("@\"", StringComparison.Ordinal) && arg.EndsWith('"') && arg.Length >= 3)
            return arg[2..^1].Replace("\"\"", "\"");
        if (!arg.StartsWith('"') || !arg.EndsWith('"') || arg.Length < 2)
            return null;
        if (SkipLiteral(arg, 0) != arg.Length - 1)
            return null; // "a" + b
        var sb = new StringBuilder();
        for (var i = 1; i < arg.Length - 1; i++)
        {
            var c = arg[i];
            if (c != '\\' || i + 1 >= arg.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            var e = arg[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'u' when i + 4 < arg.Length:
                    sb.Append((char)Convert.ToInt32(arg.Substring(i + 1, 4), 16));
                    i += 4;
                    break;
                default: sb.Append(e); break;
            }
        }

        return sb.ToString();
    }
}
