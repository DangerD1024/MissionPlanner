using MissionPlanner.App.Core.Mission;
using MissionPlanner.Utilities;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>
/// Survey (Grid): survey grid generator over a polygon, shown as a dialog by the Flight Plan screen.
/// STUB - replaced by the page's implementation; the public members below are the contract with
/// the Flight Plan screen and must be kept.
/// </summary>
public sealed partial class SurveyGridViewModel : PageViewModel, IDialogPage
{
    public SurveyGridViewModel(AppServices services)
        : base("Survey (Grid)")
    {
        Services = services;
    }

    public AppServices Services { get; }

    /// <summary>Survey area (closed implicitly), filled by the caller before the dialog opens.</summary>
    public List<PointLatLngAlt> Polygon { get; } = new();

    /// <summary>Items to append to the mission, valid after the dialog closed with true.</summary>
    public IReadOnlyList<MissionItem> Result { get; private set; } = [];

    /// <summary>Raised to close the dialog: true = accept <see cref="Result"/>.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Arrives => "survey grid generator over a polygon";
}
