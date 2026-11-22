using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MissionPlanner.App.Core.Params;
using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.Mobile;

/// <summary>One allowed value of an enumerated parameter.</summary>
public sealed record ParamChoice(double Value, string Label)
{
    public string Text => Value.ToString("0.######", CultureInfo.InvariantCulture) + ": " + Label;
}

/// <summary>One parameter in the phone's list: name, value and, when known, its title and units.</summary>
public sealed record MobileParam(string Name, double Value, ParamMeta? Meta)
{
    public string ValueText => FormatValue(Value, Meta);

    public string Title => Meta?.DisplayName is { Length: > 0 } d && d != Name ? d : "";

    public string Units => Meta?.Units ?? "";

    public static string FormatValue(double value, ParamMeta? meta)
    {
        var text = value.ToString("0.######", CultureInfo.InvariantCulture);
        if (meta?.Values.FirstOrDefault(v => Math.Abs(v.Key - value) < 1e-6) is { Value: { Length: > 0 } label })
            return $"{text} ({label})";
        return text;
    }
}

/// <summary>
/// Parameters on a phone: a search box over the full list, tap one to edit it in a sheet
/// (choices for enumerations, the range and units otherwise), written straight to the vehicle.
/// </summary>
public sealed partial class MobileParamsViewModel : ObservableObject
{
    private const int MaxShown = 400;

    private readonly AppServices _services;
    private IReadOnlyList<MobileParam> _all = [];

    public MobileParamsViewModel(AppServices services)
    {
        _services = services;
        services.Params.ParamsChanged += (_, _) => services.Dispatcher.Post(Reload);
        services.Params.MetadataChanged += (_, _) => services.Dispatcher.Post(Reload);
        Reload();
    }

    [ObservableProperty]
    private string _search = "";

    partial void OnSearchChanged(string value) => ApplyFilter();

    [ObservableProperty]
    private IReadOnlyList<MobileParam> _shown = [];

    [ObservableProperty]
    private string _summary = "Connect to see the parameters.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(EditChoices), nameof(HasChoices), nameof(EditRange), nameof(EditDescription))]
    private MobileParam? _editing;

    [ObservableProperty]
    private string _editValue = "";

    [ObservableProperty]
    private string _editError = "";

    [ObservableProperty]
    private bool _isBusy;

    public bool IsEditing => Editing != null;

    public IReadOnlyList<ParamChoice> EditChoices =>
        Editing?.Meta?.Values.Select(v => new ParamChoice(v.Key, v.Value)).ToList() ?? (IReadOnlyList<ParamChoice>)[];

    public bool HasChoices => EditChoices.Count > 0;

    public string EditDescription => Editing?.Meta?.Description ?? "";

    public string EditRange
    {
        get
        {
            var m = Editing?.Meta;
            if (m == null)
                return "";
            var parts = new List<string>();
            if (m.Min != null || m.Max != null)
                parts.Add($"Range {Fmt(m.Min)} … {Fmt(m.Max)}");
            if (m.Units.Length > 0)
                parts.Add(m.Units);
            if (m.RebootRequired)
                parts.Add("reboot needed");
            return string.Join(" · ", parts);
        }
    }

    private static string Fmt(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?";

    public void Reload()
    {
        _all = _services.Params.Snapshot().Select(p => new MobileParam(p.Name, p.Value, p.Meta)).ToList();
        ApplyFilter();
    }

    /// <summary>Name or title contains every word typed.</summary>
    public static IEnumerable<MobileParam> Filter(IEnumerable<MobileParam> all, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? all
            : all.Where(p => words.All(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                            p.Title.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyFilter()
    {
        var matches = Filter(_all, Search).ToList();
        Shown = matches.Take(MaxShown).ToList();
        Summary = _all.Count == 0
            ? (_services.Session.IsConnected ? "No parameters yet: tap Refresh." : "Connect to see the parameters.")
            : matches.Count > MaxShown
                ? $"{matches.Count} match, the first {MaxShown} shown: type more to narrow"
                : $"{matches.Count} of {_all.Count} parameters";
    }

    [RelayCommand]
    private void Edit(MobileParam? p)
    {
        Editing = p;
        EditValue = p?.Value.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        EditError = "";
    }

    [RelayCommand]
    private void Choose(ParamChoice? choice)
    {
        if (choice != null)
            EditValue = choice.Value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void CancelEdit() => Editing = null;

    [RelayCommand]
    private async Task Save()
    {
        if (Editing is not { } p)
            return;
        if (!double.TryParse(EditValue.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            EditError = $"\"{EditValue}\" is not a number.";
            return;
        }

        if (p.Meta is { } m && ((m.Min is { } min && v < min) || (m.Max is { } max && v > max)) &&
            !await _services.Dialogs.ConfirmAsync(p.Name, $"{v} is outside the range {Fmt(m.Min)} … {Fmt(m.Max)}. Write it anyway?", "Write"))
            return;

        IsBusy = true;
        try
        {
            var ok = await _services.Params.SetAsync(p.Name, v);
            if (!ok)
            {
                EditError = "The vehicle did not accept the value.";
                return;
            }

            _services.Notify($"{p.Name} = {MobileParam.FormatValue(v, p.Meta)}" + (p.Meta?.RebootRequired == true ? " (reboot to apply)" : ""));
            Editing = null;
            Reload();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (!_services.Session.IsConnected)
        {
            Summary = "Connect to see the parameters.";
            return;
        }

        IsBusy = true;
        try
        {
            await _services.Params.RefreshAsync(new Progress<Links.OperationProgress>(p => Summary = p.Status));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            Summary = "Could not get the parameters: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }
}
