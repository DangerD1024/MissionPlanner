using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MissionPlanner.App.Core.Speech;

/// <summary>
/// Text preparation for the neural voices: numbers become words ("10.5" → "ten point five", "3rd" →
/// "third"), units are spelled out ("10.2V" → "ten point two volts"), GCS words no dictionary has are
/// respelled ("PosHold" → "Position Hold") and CamelCase or letter-digit runs are split ("EKF3" → "EKF
/// three"). The <see cref="Phonemizer"/> then turns the words into phonemes.
/// </summary>
public static partial class SpeechText
{
    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>Words of vehicle messages no lexicon has, as the GCS says them.</summary>
    private static readonly (Regex Word, string Said)[] Respell =
    [
        .. new (string, string)[]
        {
            ("PosHold", "Position Hold"),
            ("AltHold", "Altitude Hold"),
            ("AltCtl", "Altitude Control"),
            ("PosCtl", "Position Control"),
            ("ArduPilot", "R do Pilot"),
            ("ArduCopter", "R do Copter"),
            ("ArduPlane", "R do Plane"),
            ("ArduRover", "R do Rover"),
            ("ArduSub", "R do Sub"),
            ("MAVLink", "Mav Link"),
            ("u-blox", "U blocks"),
            ("ublox", "U blocks"),
            ("baudrate", "bawd rate"),
            ("baud", "bawd"),
        }.Select(p => (new Regex(@"(?<![\w-])" + Regex.Escape(p.Item1) + @"(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), p.Item2)),
    ];

    private static readonly Dictionary<string, string> Units = new(StringComparer.Ordinal)
    {
        ["%"] = "percent",
        ["°"] = "degrees",
        ["V"] = "volts",
        ["A"] = "amps",
        ["mA"] = "milliamps",
        ["mAh"] = "milliamp hours",
        ["Ah"] = "amp hours",
        ["m/s"] = "meters per second",
        ["km/h"] = "kilometers per hour",
        ["kph"] = "kilometers per hour",
        ["mph"] = "miles per hour",
        ["kn"] = "knots",
        ["km"] = "kilometers",
        ["cm"] = "centimeters",
        ["mm"] = "millimeters",
        ["Hz"] = "hertz",
        ["kHz"] = "kilohertz",
        ["MHz"] = "megahertz",
        ["GHz"] = "gigahertz",
        ["ms"] = "milliseconds",
        ["s"] = "seconds",
        ["sec"] = "seconds",
        ["min"] = "minutes",
        ["h"] = "hours",
        ["dB"] = "decibels",
        ["dBm"] = "decibel milliwatts",
    };

    /// <summary>Splits text into sentences (at . ! ? ; : followed by a space or the end, never inside "10.5").</summary>
    public static IReadOnlyList<string> Sentences(string text)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var end = c is '\n' or '\r' ||
                      (c is '.' or '!' or '?' or ';' or ':' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])));
            if (!end)
                continue;
            Add(text[start..(i + 1)]);
            start = i + 1;
        }

        Add(text[start..]);
        return list;

        void Add(string s)
        {
            s = s.Trim();
            if (s.Any(char.IsLetterOrDigit))
                list.Add(s);
        }
    }

    /// <summary>
    /// True when the text is in the Latin alphabet: the neural voices speak English, so a message in another
    /// script (a translated speech text, a vehicle message) goes to the system voice instead.
    /// </summary>
    public static bool IsLatinScript(string text)
    {
        foreach (var c in text)
        {
            if (c > 'ɏ' && char.IsLetter(c))
                return false;
        }

        return true;
    }

    /// <summary>Numbers, units, respellings and word splits (see the class summary).</summary>
    public static string Normalize(string sentence)
    {
        var text = sentence;
        foreach (var (word, said) in Respell)
            text = word.Replace(text, said);

        text = SpeakNumbers(text);
        text = text.Replace("&", " and ").Replace('_', ' ').Replace('/', ' ').Replace('|', ' ');
        // CamelCase and acronym-word runs: "ZigZag" → "Zig Zag", "GPSFix" → "GPS Fix"
        text = CamelCase().Replace(text, "$1 $2");
        text = AcronymWord().Replace(text, "$1 $2");
        return SpaceBeforePunctuation().Replace(Spaces().Replace(text, " "), "$1").Trim();
    }

    /// <summary>"-3.5" → "minus three point five", "1,500" → "one thousand five hundred", "50%" → "fifty percent", "3rd" → "third".</summary>
    public static string SpeakNumbers(string text)
    {
        text = NumberWithUnit().Replace(text, m =>
        {
            var unit = m.Groups["unit"].Value;
            return Units.TryGetValue(unit, out var said) ? m.Groups["num"].Value + " " + said : m.Value;
        });
        text = Ordinal().Replace(text, m => long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? OrdinalWords(n)
            : m.Value);
        return Number().Replace(text, m =>
        {
            var s = m.Value.Replace(",", "");
            var negative = s.StartsWith('-');
            if (negative)
                s = s[1..];
            var dot = s.IndexOf('.');
            var whole = dot < 0 ? s : s[..dot];
            var words = whole.Length == 0 ? "zero"
                : whole.Length > 15 || (whole.Length > 1 && whole[0] == '0') ? Digits(whole)
                : Words(long.Parse(whole, CultureInfo.InvariantCulture));
            if (dot >= 0 && dot + 1 < s.Length)
                words += " point " + Digits(s[(dot + 1)..]);
            return " " + (negative ? "minus " : "") + words + " ";
        });
    }

    private static string Digits(string digits) => string.Join(" ", digits.Select(c => Ones[c - '0']));

    public static string Words(long n)
    {
        if (n < 0)
            return "minus " + Words(-n);
        if (n < 20)
            return Ones[n];
        if (n < 100)
            return Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");
        if (n < 1000)
            return Ones[n / 100] + " hundred" + (n % 100 > 0 ? " " + Words(n % 100) : "");
        foreach (var (scale, name) in new[] { (1_000_000_000_000L, "trillion"), (1_000_000_000L, "billion"), (1_000_000L, "million"), (1000L, "thousand") })
        {
            if (n >= scale)
                return Words(n / scale) + " " + name + (n % scale > 0 ? " " + Words(n % scale) : "");
        }

        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string OrdinalWords(long n)
    {
        var words = Words(n);
        var space = words.LastIndexOf(' ');
        var head = space < 0 ? "" : words[..(space + 1)];
        var last = words[(space + 1)..];
        last = last switch
        {
            "one" => "first",
            "two" => "second",
            "three" => "third",
            "five" => "fifth",
            "eight" => "eighth",
            "nine" => "ninth",
            "twelve" => "twelfth",
            _ when last.EndsWith('y') => last[..^1] + "ieth",
            _ => last + "th",
        };
        return " " + head + last + " ";
    }

    // one-letter units only right after the number ("10.2V", "5s"), so "GPS 2 A" or "wind 270 W" stay as they are
    [GeneratedRegex(@"(?<num>-?\d[\d,]*(?:\.\d+)?)(?:\s?(?<unit>%|°|mAh|m/s|km/h|kph|mph|kHz|MHz|GHz|Hz|dBm|dB|mA|Ah|ms|sec|min|km|cm|mm|kn)|(?<unit>V|A|s|h))(?![\w/])")]
    private static partial Regex NumberWithUnit();

    [GeneratedRegex(@"\b(\d+)(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();

    // a minus sign only where it starts a number (not "GPS-2" or "10-20")
    [GeneratedRegex(@"(?:(?<![\w.])-)?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"(\p{Ll})(\p{Lu})")]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"(\p{Lu}{2,})(\p{Lu}\p{Ll})")]
    private static partial Regex AcronymWord();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex SpaceBeforePunctuation();
}

/// <summary>
/// English text to phonemes through a voice's pronunciation lexicon, for sherpa-onnx's phoneme input.
/// sherpa-onnx 2.x has no eSpeak: its own lexicon front end skips every word it does not know, and
/// GCS messages are full of those (plurals Kokoro's lexicon leaves to rules, "EKF", "RTL", "geofence").
/// Here a missing word is built from what the lexicon has: a plural, past or -ing form from its stem,
/// two lexicon words ("waypoint" → way + point), or its letters ("EKF" → E K F), so nothing goes silent.
/// The output has the shape sherpa-onnx's lexicon path produces: each word's phones run together, words
/// separated by a space, punctuation right after the word.
/// </summary>
public sealed partial class Phonemizer
{
    private readonly Dictionary<string, string> _words;
    private readonly bool _british;

    /// <param name="words">Lower-case word → its phones (no separators).</param>
    /// <param name="british">Unstressed suffix vowel as in British lexicons (ɪ), else ᵻ.</param>
    public Phonemizer(Dictionary<string, string> words, bool british = false)
    {
        _words = words;
        _british = british;
    }

    public int Count => _words.Count;

    public bool Knows(string word) => _words.ContainsKey(word.ToLowerInvariant());

    /// <summary>
    /// Reads sherpa-onnx lexicons: "word || p h o n e s" (the shared English one) or "word p h o n e s"
    /// (Kokoro's); '#' lines are comments, multi-word entries are left out, the first entry of a word wins.
    /// </summary>
    public static Phonemizer Load(string path, bool british = false)
    {
        var words = new Dictionary<string, string>(200_000, StringComparer.Ordinal);
        var sb = new StringBuilder(32);
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string word;
            int phones;
            var bar = line.IndexOf(" || ", StringComparison.Ordinal);
            if (bar > 0)
            {
                word = line[..bar].Trim();
                if (word.Contains(' '))
                    continue;
                phones = bar + 4;
            }
            else
            {
                var end = line.IndexOfAny([' ', '\t']);
                if (end <= 0)
                    continue;
                word = line[..end];
                phones = end + 1;
            }

            sb.Clear();
            for (var i = phones; i < line.Length; i++)
            {
                if (!char.IsWhiteSpace(line[i]))
                    sb.Append(line[i]);
            }

            if (sb.Length > 0)
                words.TryAdd(word.ToLowerInvariant(), sb.ToString());
        }

        return new Phonemizer(words, british);
    }

    /// <summary>The phonemes of one sentence (already through <see cref="SpeechText.Normalize"/>).</summary>
    public string Phonemes(string sentence)
    {
        var sb = new StringBuilder(sentence.Length * 2);
        foreach (Match m in Token().Matches(sentence))
        {
            var t = m.Value;
            if (t.Length == 1 && ",.!?;:".Contains(t[0]))
            {
                if (sb.Length > 0 && sb[^1] == ' ')
                    sb.Length--;
                if (sb.Length > 0)
                    sb.Append(t).Append(' ');
                continue;
            }

            if (t is "-" or "—" or "–" or "(" or ")" or "\"")
            {
                // a short pause, as a comma, between two words
                if (sb.Length > 1 && sb[^1] == ' ' && !",.!?;:".Contains(sb[^2]))
                {
                    sb.Length--;
                    sb.Append(", ");
                }

                continue;
            }

            var phones = Word(t);
            if (phones.Length > 0)
                sb.Append(phones).Append(' ');
        }

        return sb.ToString().Trim();
    }

    /// <summary>A word's phones: the lexicon's, or built from parts it has (see the class summary).</summary>
    internal string Word(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_words.TryGetValue(lower, out var p))
            return p;
        var letters = word.Replace("'", "");
        if (letters.Length == 0)
            return "";
        if (letters.Length >= 2 && letters.All(char.IsUpper))
            return Spell(letters);
        if (lower.Contains('-'))
            return string.Join(" ", lower.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Word).Where(s => s.Length > 0));
        if (Inflected(lower) is { } inflected)
            return inflected;
        if (Compound(lower, 2) is { } compound)
            return compound;
        return Spell(letters);
    }

    private string Spell(string letters) =>
        string.Join(" ", letters.Where(char.IsLetter).Select(c => _words.TryGetValue(char.ToLowerInvariant(c).ToString(), out var p) ? p : "").Where(s => s.Length > 0));

    /// <summary>Plural / possessive, past and -ing forms of a word the lexicon has.</summary>
    private string? Inflected(string w)
    {
        var weak = _british ? "ɪ" : "ᵻ";
        if (w.EndsWith("'s", StringComparison.Ordinal) && Stem(w[..^2]) is { } owner)
            return owner + Plural(owner, weak);
        if (w.EndsWith("ies", StringComparison.Ordinal) && Stem(w[..^3] + "y") is { } y)
            return y + "z";
        if (w.EndsWith("es", StringComparison.Ordinal) && Stem(w[..^2]) is { } es && Sibilant(es))
            return es + weak + "z";
        if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && Stem(w[..^1]) is { } s)
            return s + Plural(s, weak);
        if (w.EndsWith("ied", StringComparison.Ordinal) && Stem(w[..^3] + "y") is { } ied)
            return ied + "d";
        if (w.EndsWith("ed", StringComparison.Ordinal))
        {
            var stem = Stem(w[..^2]) ?? Stem(w[..^1]) ?? (w.Length > 4 && w[^3] == w[^4] ? Stem(w[..^3]) : null);
            if (stem != null)
                return stem + Past(stem, weak);
        }

        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 4)
        {
            var stem = Stem(w[..^3]) ?? Stem(w[..^3] + "e") ?? (w[^4] == w[^5] ? Stem(w[..^4]) : null);
            if (stem != null)
                return stem + "ɪŋ";
        }

        return null;
    }

    private string? Stem(string w) => w.Length >= 2 && _words.TryGetValue(w, out var p) ? p : null;

    private static bool Sibilant(string phones) =>
        phones.EndsWith('s') || phones.EndsWith('z') || phones.EndsWith('ʃ') || phones.EndsWith('ʒ') || phones.EndsWith('ʧ') || phones.EndsWith('ʤ');

    private static bool Voiceless(string phones) =>
        phones.EndsWith('p') || phones.EndsWith('t') || phones.EndsWith('k') || phones.EndsWith('f') || phones.EndsWith('θ');

    private static string Plural(string stem, string weak) => Sibilant(stem) ? weak + "z" : Voiceless(stem) ? "s" : "z";

    private static string Past(string stem, string weak) =>
        stem.EndsWith('t') || stem.EndsWith('d') ? weak + "d"
        : Voiceless(stem) || stem.EndsWith('s') || stem.EndsWith('ʃ') || stem.EndsWith('ʧ') ? "t"
        : "d";

    /// <summary>Two (or three) lexicon words run together, the longest known start first: "rangefinder", "smartrtl".</summary>
    private string? Compound(string w, int depth)
    {
        for (var i = w.Length - 2; i >= 2; i--)
        {
            if (!_words.TryGetValue(w[..i], out var head))
                continue;
            var tail = w[i..];
            if (tail.Length >= 2 && _words.TryGetValue(tail, out var known))
                return head + " " + known;
            if (depth > 1 && (Inflected(tail) ?? Compound(tail, depth - 1)) is { } rest)
                return head + " " + rest;
            if (tail.Length <= 3)
                return head + " " + Spell(tail);
        }

        return null;
    }

    [GeneratedRegex(@"\p{L}[\p{L}'\-]*\p{L}|\p{L}|[,.!?;:\-—–()""]")]
    private static partial Regex Token();
}
