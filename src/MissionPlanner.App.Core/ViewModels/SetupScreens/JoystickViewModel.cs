using MissionPlanner.App.Core.Services;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>Joystick: joystick/gamepad axes and buttons to RC channels and actions. STUB - replaced by the page's implementation.</summary>
public sealed partial class JoystickViewModel : PageViewModel
{
    public JoystickViewModel(AppServices services)
        : base("Joystick")
    {
        Services = services;
    }

    public AppServices Services { get; }

    public string Arrives => "joystick/gamepad axes and buttons to RC channels and actions";
}
