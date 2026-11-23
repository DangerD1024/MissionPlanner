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
        // Eager: a saved "sending" joystick must resume on connect even if this page
        // is never opened, so the VM (and its link subscription) exists from startup.
        var joystick = new JoystickViewModel(s);

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
        Add(Page("Battery Monitor 2", () => BatteryMonitorViewModel.Second(s), () => Ready() && HasParam("BATT2_MONITOR")));
        Add(Page("Motor Test", () => new MotorTestViewModel(s), () => Ready() && HasParam("FRAME_CLASS", "Q_FRAME_CLASS", "FRAME", "FRAME_TYPE")));
        Add(Page("Rangefinder", () => new RangefinderViewModel(s), () => Ready() && HasParam("RNGFND1_TYPE", "RNGFND_TYPE")));
        Add(Page("Airspeed", () => new AirspeedViewModel(s), () => Ready() && HasParam("ARSPD_TYPE", "ARSPD1_TYPE")));
        Add(Page("Optical Flow", () => new OpticalFlowViewModel(s), () => Ready() && HasParam("FLOW_TYPE")));
        Add(Page("OSD", () => new OsdViewModel(s), () => Ready() && HasParam("OSD_TYPE")));
        Add(Page("Mount", () => new MountViewModel(s), () => Ready() && HasParam("MNT1_TYPE", "MNT_TYPE")));
        Add(Page("Parachute", () => new ParachuteViewModel(s), () => Ready() && HasParam("CHUTE_ENABLED")));
        Add(Page("CompassMot", () => new CompassMotViewModel(s), () => Ready() && Multirotor()));
        Add(Page("ADSB", () => new AdsbSetupViewModel(s), () => Ready() && HasParam("ADSB_TYPE")));
        Add(Page("HW ID", () => new HardwareIdViewModel(s), Ready));
        Add(Page("Initial Parameters", () => new InitialParamsViewModel(s), () => Ready() && Multirotor()));
        Add(Page("RTK/GPS Inject", () => new GpsInjectViewModel(s), () => Connected));
        Add(Page("Joystick", () => joystick));
        Add(Page("SiK Radio", () => new SikRadioViewModel(s)));
        Add(Page("DroneCAN/UAVCAN", () => new DroneCanViewModel(s), () => Ready() && HasParam("CAN_P1_DRIVER")));
        Add(Header("Advanced"));
        Add(Page("Terminal", () => new TerminalViewModel(s)));
    }
}
