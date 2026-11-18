using MissionPlanner.ArduPilot;

namespace MissionPlanner.App.Core.ViewModels.ConfigScreens;

/// <summary>
/// Extended Tuning: Mission Planner's ConfigArducopter, card for card. Copters get it as "Extended
/// Tuning"; planes with QuadPlane enabled as "QP Extended Tuning", with the Q_ parameters. Edits
/// are highlighted and sent with Write Params; "Lock Pitch and Roll Values" keeps the roll and pitch
/// controllers equal.
/// </summary>
public sealed class ExtendedTuningViewModel : TuningPageViewModel
{
    public ExtendedTuningViewModel(AppServices services)
        : base(TuningLayouts.ExtendedTitle, services)
    {
    }

    protected override TuningLayout? CardLayout() => TuningLayouts.Extended(Firmware, Services.Params.Get);

    internal override void Rebuild()
    {
        base.Rebuild();
        IsEmpty = !HasCards;
        Heading = TuningLayouts.ExtendedTitleFor(Firmware);
        VehicleText = Firmware == Firmwares.ArduPlane ? "ArduPlane QuadPlane" : FirmwareName(Firmware);
    }

    public override string EmptyText => Firmware == Firmwares.ArduPlane && Services.Params.HasParams && !TuningLayouts.QuadPlaneEnabled(Services.Params.Get)
        ? "QuadPlane is not enabled on this plane (Q_ENABLE)."
        : base.EmptyText;
}
