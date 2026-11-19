using System.ComponentModel;
using System.Globalization;
using log4net;

namespace MissionPlanner.App.Core.Localization;

/// <summary>Where translation tables come from (the app's embedded files, a folder of corrections).</summary>
public interface ITranslationSource
{
    /// <summary>The cultures this source has a table for.</summary>
    IEnumerable<string> Cultures { get; }

    /// <summary>Opens the table of a culture, or returns null.</summary>
    Stream? Open(string culture);
}

/// <summary>Tables as &lt;culture&gt;.json files in a folder.</summary>
public sealed class DirectoryTranslationSource(string directory) : ITranslationSource
{
    public string Directory { get; } = directory;

    public IEnumerable<string> Cultures =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            : [];

    public Stream? Open(string culture)
    {
        var path = Path.Combine(Directory, culture + ".json");
        return File.Exists(path) ? File.OpenRead(path) : null;
    }
}

/// <summary>
/// The UI language. Text is written in English in the code and looked up in the current language's
/// table (Mission Planner's own translations); anything without a translation stays English.
/// <see cref="Current"/> is the app's instance; views update when <see cref="LanguageChanged"/> fires.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(Localizer));

    /// <summary>The settings key, as in the WinForms app.</summary>
    public const string SettingKey = "language";

    private readonly List<ITranslationSource> _sources = new();
    private volatile TranslationTable? _table;

    /// <summary>The app-wide instance (English until <see cref="AppStartup"/> applies the saved language).</summary>
    public static Localizer Current { get; } = new();

    public Localizer(params ITranslationSource[] sources) => _sources.AddRange(sources);

    public event EventHandler? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The current language.</summary>
    public LanguageInfo Language { get; private set; } = LanguageCatalog.Languages[0];

    /// <summary>The current culture name ("en-US", "ru-RU"...); views bind to it to refresh.</summary>
    public string Culture => Language.Code;

    public bool IsRightToLeft => Language.IsRightToLeft;

    /// <summary>Number of entries in the current table (0 for English).</summary>
    public int TranslationCount => _table?.Count ?? 0;

    /// <summary>Later sources override earlier ones entry by entry.</summary>
    public void AddSource(ITranslationSource source)
    {
        _sources.Add(source);
    }

    /// <summary>English first, then Mission Planner's languages that have a table, then any other table found.</summary>
    public IReadOnlyList<LanguageInfo> Languages
    {
        get
        {
            var available = new HashSet<string>(_sources.SelectMany(SafeCultures), StringComparer.OrdinalIgnoreCase);
            var list = LanguageCatalog.Languages
                .Where(l => l.Code == LanguageCatalog.English || available.Contains(l.Code))
                .ToList();
            foreach (var code in available.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            {
                if (list.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var ci = TranslationTable.TryCulture(code);
                if (ci == null)
                    continue;
                var native = ci.NativeName.Length > 0 ? char.ToUpper(ci.NativeName[0], ci) + ci.NativeName[1..] : code;
                list.Add(new LanguageInfo(ci.Name, native, ci.EnglishName, ci.TextInfo.IsRightToLeft));
            }

            return list;
        }
    }

    /// <summary>The translation of English text in the current language, or the text itself.</summary>
    public string Tr(string? english)
    {
        if (string.IsNullOrEmpty(english))
            return english ?? "";
        var table = _table;
        return table != null && table.TryTranslate(english, out var result) ? result : english;
    }

    /// <summary>
    /// Translates a composite format string and formats it with the current culture; falls back to
    /// the English format if a translation cannot be formatted.
    /// </summary>
    public string Format(string englishFormat, params object?[] args)
    {
        var format = Tr(englishFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, englishFormat, args);
        }
    }

    /// <summary>Whether the current language translates this text.</summary>
    public bool Has(string english) => _table?.Contains(english) == true;

    /// <summary>
    /// Switches the language (unknown codes fall back to English), saves it under "language" if asked,
    /// and raises <see cref="LanguageChanged"/>. Call on the UI thread.
    /// </summary>
    public void SetLanguage(string? code, bool save = false)
    {
        var language = Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
                       ?? LanguageCatalog.Languages[0];
        var table = language.Code == LanguageCatalog.English ? null : LoadTable(language.Code);
        if (table == null)
            language = LanguageCatalog.Languages[0];

        if (save)
        {
            try
            {
                Utilities.Settings.Instance[SettingKey] = language.Code;
                Utilities.Settings.Instance.Save();
            }
            catch (Exception ex)
            {
                Log.Warn("save language", ex);
            }
        }

        var changed = language.Code != Language.Code;
        _table = table;
        Language = language;

        // CultureInfo.CurrentUICulture is left alone: the shared libraries derive names from it that are
        // also stored in settings (GMap.NET's map provider names). Their message boxes are translated by
        // the dialog service, whose lookup includes ExtLibs/Strings' translations.
        if (!changed)
            return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRightToLeft)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies the saved language, or the system's when none is saved and it is available.</summary>
    public void ApplySaved()
    {
        string? saved = null;
        try
        {
            saved = Utilities.Settings.Instance[SettingKey];
        }
        catch (Exception ex)
        {
            Log.Warn("read language", ex);
        }

        SetLanguage(string.IsNullOrEmpty(saved) ? MatchCulture(Languages.Select(l => l.Code), SystemCulture) : saved);
    }

    /// <summary>The user's culture when the app started, before a language was applied.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// The available language closest to a system culture: the same culture or a parent of it
    /// (zh-CN → zh-Hans), else one sharing its most specific parent (zh-HK → zh-TW, de-AT → de-DE).
    /// English when nothing matches.
    /// </summary>
    public static string MatchCulture(IEnumerable<string> available, CultureInfo system)
    {
        var codes = available.ToList();
        var chains = codes.ToDictionary(c => c, c => Chain(TranslationTable.TryCulture(c)), StringComparer.OrdinalIgnoreCase);
        foreach (var s in Chain(system))
        {
            var exact = codes.FirstOrDefault(c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;
            var related = codes.FirstOrDefault(c => chains[c].Contains(s, StringComparer.OrdinalIgnoreCase));
            if (related != null)
                return related;
        }

        return LanguageCatalog.English;
    }

    private static List<string> Chain(CultureInfo? culture)
    {
        var list = new List<string>();
        for (var c = culture; c != null && !string.IsNullOrEmpty(c.Name) && list.Count < 8; c = c.Parent)
            list.Add(c.Name);
        return list;
    }

    private TranslationTable? LoadTable(string code)
    {
        TranslationTable? table = null;
        foreach (var source in _sources)
        {
            try
            {
                using var stream = source.Open(code);
                if (stream == null)
                    continue;
                if (table == null)
                    table = TranslationTable.Load(stream, code);
                else
                    table.MergeFrom(stream);
            }
            catch (Exception ex)
            {
                Log.Warn($"translation table {code}", ex);
            }
        }

        return table;
    }

    private static IEnumerable<string> SafeCultures(ITranslationSource source)
    {
        try
        {
            return source.Cultures.ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("translation source", ex);
            return [];
        }
    }
}

/// <summary>Short forms of <see cref="Localizer.Current"/> for view-models: <c>L.Tr("Connect")</c>.</summary>
public static class L
{
    /// <summary>Translates English UI text into the current language.</summary>
    public static string Tr(string? english) => Localizer.Current.Tr(english);

    /// <summary>Translates a composite format string, then formats it.</summary>
    public static string Format(string englishFormat, params object?[] args) => Localizer.Current.Format(englishFormat, args);

    /// <summary>
    /// Marks English text for the string list without translating it, for text a view translates
    /// when it shows it (menu titles bound with {l:TrBind}).
    /// </summary>
    public static string N(string english) => english;
}
