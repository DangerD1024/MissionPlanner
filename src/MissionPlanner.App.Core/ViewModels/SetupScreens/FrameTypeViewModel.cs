using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Frame Type: FRAME_CLASS / FRAME_TYPE selection. STUB - replaced by the page's implementation.</summary>
public sealed partial class FrameTypeViewModel : PageViewModel
{
    public FrameTypeViewModel(AppServices services)
        : base("Frame Type")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "FRAME_CLASS / FRAME_TYPE selection";
}
