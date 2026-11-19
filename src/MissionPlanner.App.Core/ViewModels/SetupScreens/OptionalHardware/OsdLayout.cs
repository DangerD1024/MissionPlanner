using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens.OptionalHardware;

/// <summary>
/// One OSD element on one screen: OSDn_NAME_EN / _X / _Y. Holds the edited values and the
/// vehicle's, so the page knows what to write.
/// </summary>
public sealed partial class OsdItem : ObservableObject
{
    public OsdItem(int screen, string name, bool enabled, int x, int y)
    {
        Screen = screen;
        Name = name;
        _enabled = OriginalEnabled = enabled;
        _x = OriginalX = x;
        _y = OriginalY = y;
    }

    public int Screen { get; }

    /// <summary>The element name in the parameters ("ALTITUDE", "BAT_VOLT", "PARAM1").</summary>
    public string Name { get; }

    public string Prefix => $"OSD{Screen.ToString(CultureInfo.InvariantCulture)}_{Name}";

    public string EnabledParam => Prefix + "_EN";

    public string XParam => Prefix + "_X";

    public string YParam => Prefix + "_Y";

    public bool OriginalEnabled { get; private set; }

    public int OriginalX { get; private set; }

    public int OriginalY { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyPropertyChangedFor(nameof(XValue))]
    private int _x;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyPropertyChangedFor(nameof(YValue))]
    private int _y;

    /// <summary><see cref="X"/> for a numeric editor.</summary>
    public decimal? XValue
    {
        get => X;
        set
        {
            if (value is { } v)
                X = (int)Math.Round(v);
        }
    }

    /// <summary><see cref="Y"/> for a numeric editor.</summary>
    public decimal? YValue
    {
        get => Y;
        set
        {
            if (value is { } v)
                Y = (int)Math.Round(v);
        }
    }

    /// <summary>Sample text drawn on the layout.</summary>
    public string Caption => OsdCaptions.For(Name);

    [ObservableProperty]
    private bool _isSelected;

    public bool IsChanged => Enabled != OriginalEnabled || X != OriginalX || Y != OriginalY;

    /// <summary>The parameters that differ from the vehicle's values.</summary>
    public IEnumerable<KeyValuePair<string, double>> Changes()
    {
        if (Enabled != OriginalEnabled)
            yield return new(EnabledParam, Enabled ? 1 : 0);
        if (X != OriginalX)
            yield return new(XParam, X);
        if (Y != OriginalY)
            yield return new(YParam, Y);
    }

    /// <summary>The vehicle now holds these values (after a successful write).</summary>
    public void Accept(string param)
    {
        if (param == EnabledParam)
            OriginalEnabled = Enabled;
        else if (param == XParam)
            OriginalX = X;
        else if (param == YParam)
            OriginalY = Y;
        OnPropertyChanged(nameof(IsChanged));
    }

    /// <summary>Back to the vehicle's values.</summary>
    public void Discard()
    {
        Enabled = OriginalEnabled;
        X = OriginalX;
        Y = OriginalY;
    }

    /// <summary>Takes new vehicle values (a refresh) and drops the edits.</summary>
    public void Reset(bool enabled, int x, int y)
    {
        OriginalEnabled = enabled;
        OriginalX = x;
        OriginalY = y;
        Discard();
        OnPropertyChanged(nameof(IsChanged));
    }

    public override string ToString() => Name;
}

/// <summary>One OSD screen (OSD1..OSD6) and its elements.</summary>
public sealed class OsdScreen(int number, IReadOnlyList<OsdItem> items, IReadOnlyList<string> settings)
{
    public int Number { get; } = number;

    public string Title => "Screen " + Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Elements, by name.</summary>
    public IReadOnlyList<OsdItem> Items { get; } = items;

    /// <summary>The screen's own parameters (OSDn_ENABLE, _CHAN_MIN, _TXT_RES...).</summary>
    public IReadOnlyList<string> Settings { get; } = settings;

    /// <summary>Screens 5 and 6 show parameters for in-flight tuning instead of telemetry.</summary>
    public bool IsParamScreen => Items.Count > 0 && Items.All(i => i.Name.StartsWith("PARAM", StringComparison.Ordinal));

    public IEnumerable<KeyValuePair<string, double>> Changes() => Items.SelectMany(i => i.Changes());

    public override string ToString() => Title;
}

/// <summary>Character grid of an OSD (MAX7456 PAL 30×16, NTSC uses 13 rows; MSP DisplayPort HD 50×18 or 60×22).</summary>
public readonly record struct OsdGrid(int Columns, int Rows, int? NtscRows = null)
{
    public static readonly OsdGrid Sd = new(30, 16, 13);
    public static readonly OsdGrid Hd50 = new(50, 18);
    public static readonly OsdGrid Hd60 = new(60, 22);

    /// <summary>The grid for an OSDn_TXT_RES value (0 = 30×16, 1 = 50×18, 2 = 60×22).</summary>
    public static OsdGrid FromTextResolution(double? txtRes) => (int)Math.Round(txtRes ?? 0) switch
    {
        1 => Hd50,
        2 => Hd60,
        _ => Sd,
    };

    public override string ToString() => $"{Columns}×{Rows}";
}

/// <summary>
/// Builds the OSD screens from the parameter list the way Mission Planner's OSD configurator
/// (OSDConfigurator.ConfigFactory) does: an element is a name with all three of _EN, _X and _Y on
/// a screen; the screen's other parameters are its settings, except an element's extra fields
/// (OSD5_PARAM1_KEY...).
/// </summary>
public static partial class OsdLayout
{
    public const int MaxScreens = 6;

    [GeneratedRegex(@"^OSD([1-9])_(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScreenParam();

    public static IReadOnlyList<OsdScreen> Parse(IEnumerable<KeyValuePair<string, double>> parameters)
    {
        var byScreen = new SortedDictionary<int, List<KeyValuePair<string, double>>>();
        foreach (var p in parameters)
        {
            var m = ScreenParam().Match(p.Key);
            if (!m.Success)
                continue;
            var n = m.Groups[1].Value[0] - '0';
            if (n is < 1 or > MaxScreens)
                continue;
            if (!byScreen.TryGetValue(n, out var list))
                byScreen[n] = list = new List<KeyValuePair<string, double>>();
            list.Add(new(m.Groups[2].Value, p.Value));
        }

        var screens = new List<OsdScreen>();
        foreach (var (n, list) in byScreen)
        {
            var values = list.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            var items = new List<OsdItem>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in values.Keys)
            {
                if (!key.EndsWith("_EN", StringComparison.Ordinal))
                    continue;
                var name = key[..^3];
                if (name.Length == 0 || !values.TryGetValue(name + "_X", out var x) || !values.TryGetValue(name + "_Y", out var y))
                    continue;
                items.Add(new OsdItem(n, name, values[key] > 0, ToCell(x), ToCell(y)));
                used.Add(key);
                used.Add(name + "_X");
                used.Add(name + "_Y");
            }

            items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var names = items.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            var prefix = $"OSD{n.ToString(CultureInfo.InvariantCulture)}_";
            var settings = values.Keys
                .Where(k => !used.Contains(k) && !IsItemExtra(k, names))
                .OrderBy(k => SettingOrder(k)).ThenBy(k => k, StringComparer.Ordinal)
                .Select(k => prefix + k)
                .ToList();
            screens.Add(new OsdScreen(n, items, settings));
        }

        return screens;
    }

    /// <summary>
    /// Copies element visibility and positions from one screen to another for the elements both
    /// have (Mission Planner's Copy / Paste Layout).
    /// </summary>
    public static void CopyLayout(OsdScreen from, OsdScreen to)
    {
        if (ReferenceEquals(from, to))
            return;
        foreach (var src in from.Items)
        {
            var dst = to.Items.FirstOrDefault(i => i.Name == src.Name);
            if (dst == null)
                continue;
            dst.Enabled = src.Enabled;
            dst.X = src.X;
            dst.Y = src.Y;
        }
    }

    /// <summary>Keeps a position on the grid (an element starting at the last column or row stays visible).</summary>
    public static (int X, int Y) Clamp(int x, int y, OsdGrid grid) =>
        (Math.Clamp(x, 0, grid.Columns - 1), Math.Clamp(y, 0, grid.Rows - 1));

    private static int ToCell(double v) => double.IsFinite(v) ? (int)Math.Round(v) : 0;

    // OSD5_PARAM1_KEY belongs to element PARAM1, not to the screen
    private static bool IsItemExtra(string key, HashSet<string> items)
    {
        var cut = key.LastIndexOf('_');
        return cut > 0 && items.Contains(key[..cut]);
    }

    private static int SettingOrder(string key) => key switch
    {
        "ENABLE" => 0,
        "CHAN_MIN" => 1,
        "CHAN_MAX" => 2,
        "TXT_RES" => 3,
        "FONT" => 4,
        _ => 5,
    };
}
