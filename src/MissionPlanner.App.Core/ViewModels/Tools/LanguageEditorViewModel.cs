using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Localization;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>One English text, its built-in translation and the user's own.</summary>
public sealed partial class TranslationRow(string english, string builtIn, string mine) : ObservableObject
{
    public string English { get; } = english;

    public string BuiltIn { get; } = builtIn;

    /// <summary>What the file had when it was read.</summary>
    internal string Saved { get; set; } = mine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(IsChanged), nameof(Shown))]
    private string _mine = mine;

    /// <summary>The text the app shows: the user's when there is one.</summary>
    public string Shown => Mine.Length > 0 ? Mine : BuiltIn;

    public bool IsTranslated => BuiltIn.Length > 0 || Mine.Length > 0;

    /// <summary>Edited since it was read.</summary>
    public bool IsChanged => Mine != Saved;

    /// <summary>The {0} placeholders differ from the English: the app would ignore this translation.</summary>
    public bool HasProblem => Mine.Length > 0 && !LocalizationText.SameFormatItems(LocalizationText.Split(English).Core, LocalizationText.Split(Mine).Core);
}

/// <summary>
/// Mission Planner's language editor (Ctrl+F > "Lang Edit"): the app's English texts beside the
/// current translation, with a column for the user's own. Saving writes them to the user data
/// folder's Localization/&lt;culture&gt;.json, which the app applies on top of its own tables.
/// </summary>
public sealed partial class LanguageEditorViewModel : ToolPageViewModel
{
    private const int MaxShown = 3000;
    private readonly Localizer _localizer;
    private readonly string _directory;
    private List<TranslationRow> _all = [];

    public LanguageEditorViewModel(AppServices services)
        : this(services, Localizer.Current, null)
    {
    }

    /// <summary>For tests: another localizer and user folder.</summary>
    internal LanguageEditorViewModel(AppServices services, Localizer localizer, string? directory)
        : base("Translation editor",
            "Correct or complete the translation of the app: type your own text for any English string. It is saved in your data folder and used instead of the built-in translation.",
            services)
    {
        _localizer = localizer;
        _directory = directory ?? UserTranslations.Directory;
        Languages = localizer.Languages.Where(l => l.Code != LanguageCatalog.English).ToList();
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == localizer.Culture) ?? Languages.FirstOrDefault();
        Load();
    }

    public override bool NeedsVehicle => false;

    public IReadOnlyList<LanguageInfo> Languages { get; }

    [ObservableProperty]
    private LanguageInfo? _selectedLanguage;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private bool _untranslatedOnly;

    [ObservableProperty]
    private bool _mineOnly;

    [ObservableProperty]
    private IReadOnlyList<TranslationRow> _rows = [];

    [ObservableProperty]
    private string _countText = "";

    public string FilePath => SelectedLanguage == null ? "" : UserTranslations.PathFor(SelectedLanguage.Code, _directory);

    /// <summary>Every row, filtered or not (tests).</summary>
    internal IReadOnlyList<TranslationRow> AllRows => _all;

    partial void OnSelectedLanguageChanged(LanguageInfo? value)
    {
        Load();
        OnPropertyChanged(nameof(FilePath));
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnUntranslatedOnlyChanged(bool value) => ApplyFilter();

    partial void OnMineOnlyChanged(bool value) => ApplyFilter();

    private void Load()
    {
        _all = [];
        if (SelectedLanguage is not { } language)
        {
            ApplyFilter();
            return;
        }

        var builtIn = _localizer.LoadTable(language.Code, s => !UserTranslations.IsUserSource(s, _directory));
        Dictionary<string, string> mine;
        try
        {
            mine = UserTranslations.Read(FilePath);
        }
        catch (Exception ex)
        {
            Report("Could not read " + FilePath + ": " + ex.Message, true);
            mine = [];
        }

        // one row per English text, whichever list it came from
        var rows = new Dictionary<string, (string English, string BuiltIn, string Mine)>(StringComparer.Ordinal);

        void Add(string english, string? translation, string? own)
        {
            var core = LocalizationText.Split(english).Core;
            if (core.Length == 0 || !LocalizationText.HasLetters(core))
                return;
            var key = core.ToLowerInvariant();
            rows.TryGetValue(key, out var r);
            rows[key] = (r.English ?? core, translation ?? r.BuiltIn ?? "", own ?? r.Mine ?? "");
        }

        foreach (var english in _localizer.EnglishStrings())
            Add(english, null, null);
        if (builtIn != null)
        {
            foreach (var (english, translation) in builtIn.Entries)
                Add(english, translation, null);
        }

        foreach (var (english, translation) in mine)
            Add(english, null, translation);

        _all = rows.Values
            .Select(r => new TranslationRow(r.English, r.BuiltIn, r.Mine))
            .OrderBy(r => r.English, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var f = Filter.Trim();
        IEnumerable<TranslationRow> q = _all;
        if (UntranslatedOnly)
            q = q.Where(r => !r.IsTranslated);
        if (MineOnly)
            q = q.Where(r => r.Mine.Length > 0);
        if (f.Length > 0)
            q = q.Where(r => r.English.Contains(f, StringComparison.OrdinalIgnoreCase) || r.BuiltIn.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                             r.Mine.Contains(f, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        Rows = list.Count > MaxShown ? list.Take(MaxShown).ToList() : list;
        var translated = _all.Count(r => r.IsTranslated);
        var own = _all.Count(r => r.Mine.Length > 0);
        CountText = L.Format("{0} texts, {1} translated, {2} of your own; showing {3}", _all.Count, translated, own, Rows.Count);
    }

    /// <summary>The user's translations to write: those typed, without the ones that only repeat the built-in text.</summary>
    internal List<KeyValuePair<string, string>> Overrides() =>
        _all.Where(r => r.Mine.Trim().Length > 0 && r.Mine.Trim() != r.BuiltIn)
            .Select(r => new KeyValuePair<string, string>(r.English, r.Mine.Trim()))
            .ToList();

    [RelayCommand]
    private void Save()
    {
        if (SelectedLanguage is not { } language)
            return;
        var problems = _all.Count(r => r.HasProblem);
        var overrides = Overrides();
        try
        {
            UserTranslations.Write(FilePath, language.Code, overrides);
        }
        catch (Exception ex)
        {
            Report("Could not save: " + ex.Message, true);
            return;
        }

        foreach (var r in _all)
            r.Saved = r.Mine;
        if (string.Equals(_localizer.Culture, language.Code, StringComparison.OrdinalIgnoreCase))
            _localizer.Reload();
        ApplyFilter();
        Report(problems > 0
            ? L.Format("Saved {0} translations. {1} of them have other {{0}} placeholders than the English and are not used.", overrides.Count, problems)
            : L.Format("Saved {0} translations to {1}", overrides.Count, FilePath), problems > 0);
    }

    /// <summary>Writes the user's translations to another file, e.g. to send them in.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (SelectedLanguage is not { } language)
            return;
        var file = await Services.Dialogs.SaveFileAsync("Export translations", language.Code + ".json", new FileFilter("Translation table", "json"));
        if (file == null)
            return;
        try
        {
            UserTranslations.Write(file, language.Code, Overrides());
            Report("Saved " + file);
        }
        catch (Exception ex)
        {
            Report("Could not save: " + ex.Message, true);
        }
    }

    /// <summary>Discards unsaved edits.</summary>
    [RelayCommand]
    private void Reload() => Load();

    /// <summary>Starts the user's text from the built-in translation (or the English).</summary>
    [RelayCommand]
    private void CopyBuiltIn(TranslationRow? row)
    {
        if (row != null && row.Mine.Length == 0)
            row.Mine = row.BuiltIn.Length > 0 ? row.BuiltIn : row.English;
    }
}
