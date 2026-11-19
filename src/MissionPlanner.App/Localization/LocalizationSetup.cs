using System.Reflection;
using Avalonia;
using Avalonia.Media;
using MissionPlanner.App.Core.Localization;

namespace MissionPlanner.App.Localization;

/// <summary>The translation tables built into the app (Localization/&lt;culture&gt;.json, embedded).</summary>
public sealed class EmbeddedTranslationSource : ITranslationSource
{
    private const string Prefix = "MissionPlanner.App.Localization.";
    private static readonly Assembly Assembly = typeof(EmbeddedTranslationSource).Assembly;

    public IEnumerable<string> Cultures =>
        Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^".json".Length]);

    public Stream? Open(string culture) => Assembly.GetManifestResourceStream(Prefix + culture + ".json");
}

/// <summary>
/// Connects the language to the windows: right-to-left layout for Arabic and a font that has the
/// language's glyphs. Windows pick both up through the AppFlowDirection / AppFontFamily resources
/// (Localization/LocalizationStyles.axaml).
/// </summary>
public static class LocalizationSetup
{
    /// <summary>Registers the built-in tables; call before <c>AppStartup.Initialize</c> applies the saved language.</summary>
    public static void AddBuiltInTables() => Localizer.Current.AddSource(new EmbeddedTranslationSource());

    /// <summary>Applies the current language's layout direction and font, now and on every change.</summary>
    public static void Attach(Application app)
    {
        Apply(app);
        Localizer.Current.LanguageChanged += (_, _) => Apply(app);
    }

    private static void Apply(Application app)
    {
        var loc = Localizer.Current;
        app.Resources["AppFlowDirection"] = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        app.Resources["AppFontFamily"] = FontFor(loc.Culture);
    }

    /// <summary>
    /// Chinese, Japanese and Korean share code points but draw them differently: put the language's
    /// own font first (Noto/Source Han on Linux, the system UI fonts on Windows and macOS). Other
    /// languages use the default font; the font manager's fallback covers any script it lacks.
    /// </summary>
    public static FontFamily FontFor(string culture) => culture switch
    {
        "zh-Hans" => new FontFamily("Noto Sans CJK SC, Source Han Sans SC, Microsoft YaHei UI, PingFang SC"),
        "zh-TW" => new FontFamily("Noto Sans CJK TC, Source Han Sans TC, Microsoft JhengHei UI, PingFang TC"),
        "ja-JP" => new FontFamily("Noto Sans CJK JP, Source Han Sans JP, Yu Gothic UI, Hiragino Sans"),
        "ko-KR" => new FontFamily("Noto Sans CJK KR, Source Han Sans KR, Malgun Gothic, Apple SD Gothic Neo"),
        _ => FontFamily.Default,
    };
}
