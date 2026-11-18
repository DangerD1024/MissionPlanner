using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.FlightPlan;

/// <summary>Survey (Grid): survey grid generator over a polygon. STUB - replaced by the page's implementation.</summary>
public sealed partial class SurveyGridViewModel : PageViewModel
{
    public SurveyGridViewModel(AppServices services)
        : base("Survey (Grid)")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "survey grid generator over a polygon";
}
