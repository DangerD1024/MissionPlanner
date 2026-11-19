using Avalonia.Media;
using Avalonia.Styling;
using MissionPlanner.App.Core.Services;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Services;

/// <summary>
/// Colour themes. Every view uses the palette brushes (Bg0..Bg3, Line, Text, Muted, Accent...)
/// through DynamicResource, so switching replaces them app-wide without a restart.
/// </summary>
public sealed class ThemeService : IThemeService
{
    public const string SettingKey = "next_theme";

    /// <summary>How the top-bar icons look in a theme.</summary>
    private enum IconStyle
    {
        /// <summary>Mission Planner's own picture icons.</summary>
        Original,
        Outline,
        Bold,
        Duotone,
    }

    private sealed record Palette(
        string Name, bool IsLight, IconStyle Icons,
        string Bg0, string Bg1, string Bg2, string Bg3, string Line,
        string Text, string Muted,
        string Accent, string AccentHover, string AccentText,
        string Danger, string Warn, string Info);

    private static readonly Palette[] Palettes =
    [
        new("Dark", false, IconStyle.Outline, "#101317", "#161a1f", "#1c2127", "#252b33", "#2c333c", "#e6e9ed", "#9aa4b0",
            "#8cc63f", "#a2d65c", "#0f1a05", "#e5534b", "#f0b429", "#4fa3e3"),
        // Mission Planner's default look (BurntKermit.mpsystheme): grey with lime buttons
        new("Mission Planner", false, IconStyle.Original, "#1e1f20", "#262728", "#2d2e2f", "#434445", "#3b3c3d", "#ffffff", "#b4b7ba",
            "#94c11f", "#cde296", "#2b3a03", "#ff4d4d", "#ffb020", "#4fa3e3"),
        new("Light", true, IconStyle.Outline, "#ffffff", "#f6f7f9", "#eef0f3", "#e2e6eb", "#d0d5dc", "#1d2329", "#5f6b78",
            "#5f9a1c", "#76b32c", "#ffffff", "#d93025", "#a86400", "#1a73e8"),
        // Mission Planner's HighContrast.mpsystheme: light grey, black text, yellow buttons
        new("High Contrast", true, IconStyle.Bold, "#ffffff", "#eeeeee", "#f7f7f7", "#e2e2e2", "#555555", "#000000", "#333333",
            "#ffd400", "#ffe766", "#000000", "#c00000", "#8a5a00", "#0047ab"),
        new("Midnight", false, IconStyle.Duotone, "#0b1220", "#101a2e", "#14203a", "#1d2c4d", "#24365c", "#e6edf7", "#94a3b8",
            "#38bdf8", "#7dd3fc", "#062033", "#f87171", "#fbbf24", "#a78bfa"),
    ];

    public IReadOnlyList<ThemeInfo> Themes { get; } = Palettes.Select(p => new ThemeInfo(p.Name, p.IsLight)).ToArray();

    /// <summary>The look a fresh install starts with.</summary>
    public const string DefaultTheme = "Mission Planner";

    public string Current { get; private set; } = DefaultTheme;

    /// <summary>Applies the saved theme (or the default); call once at start-up.</summary>
    public void ApplySaved() => Apply(Settings.Instance[SettingKey] ?? DefaultTheme, save: false);

    public void Apply(string name) => Apply(name, save: true);

    private void Apply(string name, bool save)
    {
        var app = Avalonia.Application.Current;
        if (app == null)
            return;

        var p = Palettes.FirstOrDefault(x => x.Name == name) ?? Palettes.First(x => x.Name == DefaultTheme);
        var res = app.Resources;

        void Brush(string key, string hex)
        {
            var color = Color.Parse(hex);
            res[key + "Color"] = color;
            res[key] = new SolidColorBrush(color);
        }

        Brush("Bg0", p.Bg0);
        Brush("Bg1", p.Bg1);
        Brush("Bg2", p.Bg2);
        Brush("Bg3", p.Bg3);
        Brush("Line", p.Line);
        Brush("Text", p.Text);
        Brush("Muted", p.Muted);
        Brush("Accent", p.Accent);
        Brush("AccentHover", p.AccentHover);
        Brush("AccentText", p.AccentText);
        Brush("Danger", p.Danger);
        Brush("Warn", p.Warn);
        Brush("Info", p.Info);
        // soft tints for highlighted rows and banners: translucent, so they suit light and dark pages
        Tint("AccentSoft", p.Accent, 0x2e);
        Tint("AccentSoftStrong", p.Accent, 0x48);
        Tint("WarnSoft", p.Warn, 0x2e);
        Tint("DangerSoft", p.Danger, 0x2e);
        Tint("InfoSoft", p.Info, 0x2e);

        void Tint(string key, string hex, byte alpha)
        {
            var c = Color.Parse(hex);
            res[key] = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        // panels floating over the map or the HUD: translucent page colour, readable with Text
        var overlay = Color.Parse(p.Bg1);
        res["OverlayColor"] = Color.FromArgb(0xd8, overlay.R, overlay.G, overlay.B);
        res["Overlay"] = new SolidColorBrush((Color)res["OverlayColor"]!);

        // top-bar icons: Mission Planner's own pictures, or vector icons styled per theme
        res["ShowOriginalIcons"] = p.Icons == IconStyle.Original;
        res["ShowVectorIcons"] = p.Icons != IconStyle.Original;
        res["NavIconStroke"] = p.Icons switch
        {
            IconStyle.Bold => 2.6,
            IconStyle.Original => 2.2,
            _ => 1.7,
        };
        var accentFill = Color.Parse(p.Accent);
        res["NavIconFill"] = p.Icons == IconStyle.Duotone
            ? new SolidColorBrush(Color.FromArgb(0x55, accentFill.R, accentFill.G, accentFill.B))
            : Brushes.Transparent;

        // Fluent controls (checkboxes, sliders, selection) follow the accent
        var accent = Color.Parse(p.Accent);
        res["SystemAccentColor"] = accent;
        res["SystemAccentColorDark1"] = Shade(accent, 0.85);
        res["SystemAccentColorDark2"] = Shade(accent, 0.7);
        res["SystemAccentColorLight1"] = Color.Parse(p.AccentHover);
        res["SystemAccentColorLight2"] = Color.Parse(p.AccentHover);

        app.RequestedThemeVariant = p.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Current = p.Name;

        if (save)
        {
            Settings.Instance[SettingKey] = p.Name;
            Settings.Instance.Save();
        }
    }

    private static Color Shade(Color c, double f) =>
        Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
}
