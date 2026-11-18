using MissionPlanner.App.Core.Services;
using MissionPlanner.ArduPilot;
using static MissionPlanner.App.Core.ViewModels.MenuItemViewModel;

namespace MissionPlanner.App.Core.ViewModels.SetupScreens;

/// <summary>SETUP: firmware and hardware setup, with Mission Planner's left menu.</summary>
public sealed class SetupViewModel : MenuScreenViewModel
{
    public SetupViewModel(AppServices s)
        : base("Setup", s)
    {
        bool Ready() => Connected && s.Params.HasParams;
        bool Multirotor() => Firmware == Firmwares.ArduCopter2 || HasParam("Q_ENABLE") && s.Params.Get("Q_ENABLE") > 0;

        Add(Page("Install Firmware", () => new FirmwareViewModel(s), () => !Connected));
        Add(Header("Mandatory Hardware"));
        Add(Page("Frame Type", () => new FrameTypeViewModel(s), () => Ready() && HasParam("FRAME_CLASS", "Q_FRAME_CLASS", "FRAME")));
        Add(Page("Accel Calibration", () => new AccelCalibrationViewModel(s), Ready));
        Add(Page("Compass", () => new CompassViewModel(s), Ready));
        Add(Page("Radio Calibration", () => new RadioCalibrationViewModel(s), Ready));
        Add(Page("Servo Output", () => new ServoOutputViewModel(s), () => Ready() && HasParam("SERVO1_FUNCTION")));
        Add(Page("Serial Ports", () => new SerialPortsViewModel(s), () => Ready() && HasParam("SERIAL1_PROTOCOL")));
        Add(Page("ESC Calibration", () => new EscCalibrationViewModel(s), () => Ready() && Multirotor() && HasParam("ESC_CALIBRATION")));
        Add(Page("Flight Modes", () => new FlightModesViewModel(s), () => Ready() && HasParam("FLTMODE1", "MODE1")));
        Add(Page("FailSafe", () => new FailsafeViewModel(s), Ready));
        Add(Header("Optional Hardware"));
        Add(Page("Battery Monitor", () => new BatteryMonitorViewModel(s), () => Ready() && HasParam("BATT_MONITOR")));
        Add(Page("Motor Test", () => new MotorTestViewModel(s), () => Ready() && HasParam("FRAME_CLASS", "Q_FRAME_CLASS", "FRAME", "FRAME_TYPE")));
    }
}
