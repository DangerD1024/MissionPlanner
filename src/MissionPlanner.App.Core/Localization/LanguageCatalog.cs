namespace MissionPlanner.App.Core.Localization;

/// <summary>A language the app can show, named in its own language.</summary>
public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, bool IsRightToLeft = false)
{
    /// <summary>"Deutsch (German)"; English alone for English.</summary>
    public string DisplayName => NativeName == EnglishName ? NativeName : $"{NativeName} ({EnglishName})";

    /// <summary>The English name when it differs from the native one, else empty.</summary>
    public string SecondaryName => NativeName == EnglishName ? "" : EnglishName;

    public override string ToString() => DisplayName;
}

/// <summary>
/// Mission Planner's languages (GCSViews/ConfigurationView/ConfigPlanner.cs), in its order, and the
/// .resx culture folders each one is harvested from: its own first, then the cultures .NET falls back
/// to for it, then other files written in the same language.
/// </summary>
public static class LanguageCatalog
{
    public const string English = "en-US";

    public static IReadOnlyList<LanguageInfo> Languages { get; } =
    [
        new(English, "English", "English"),
        new("zh-Hans", "中文 (简体)", "Chinese, Simplified"),
        new("zh-TW", "中文 (繁體)", "Chinese, Traditional"),
        new("ru-RU", "Русский", "Russian"),
        new("fr", "Français", "French"),
        new("pl", "Polski", "Polish"),
        new("it-IT", "Italiano", "Italian"),
        new("es-ES", "Español", "Spanish"),
        new("de-DE", "Deutsch", "German"),
        new("ja-JP", "日本語", "Japanese"),
        new("id-ID", "Bahasa Indonesia", "Indonesian"),
        new("ko-KR", "한국어", "Korean"),
        new("ar", "العربية", "Arabic", IsRightToLeft: true),
        new("pt", "Português", "Portuguese"),
        new("tr", "Türkçe", "Turkish"),
        new("ru-KZ", "Русский (Казахстан)", "Russian, Kazakhstan"),
        new("uk", "Українська", "Ukrainian"),
    ];

    /// <summary>The .resx culture suffixes a language's table is built from, best first.</summary>
    public static IReadOnlyList<string> Sources(string code) => code switch
    {
        "zh-Hans" => ["zh-Hans", "zh-CN"],
        "zh-TW" => ["zh-TW", "zh-Hant", "tw"],
        // Mission Planner has few ru-RU files; its ru-KZ files are Russian too
        "ru-RU" => ["ru-RU", "ru", "ru-KZ"],
        "ru-KZ" => ["ru-KZ", "ru", "ru-RU"],
        "it-IT" => ["it-IT", "it"],
        "es-ES" => ["es-ES", "es"],
        "de-DE" => ["de-DE", "de"],
        "ja-JP" => ["ja-JP", "ja"],
        _ => [code],
    };

    public static LanguageInfo? Find(string? code) =>
        string.IsNullOrEmpty(code)
            ? null
            : Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
}
