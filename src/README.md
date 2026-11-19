# Mission Planner — cross-platform app (preview)

A new user interface for Mission Planner on .NET 10 and Avalonia 12, running natively on Windows,
Linux and macOS. It reuses the protocol and vehicle libraries in `ExtLibs/` (MAVLink, Comms,
ArduPilot, Utilities, GMap.NET.Core, Maps); only the UI layer is new. The WinForms app
(`MissionPlanner.sln`) keeps building as before.

## Screens

Mission Planner's layout, flattened: top navigation with the port/baud/vehicle/connect controls,
left menus on SETUP and CONFIG (pages appear only when they apply to the connected vehicle).

| Screen | Contents |
|---|---|
| DATA | HUD with video (right-click: RTSP, UDP H.264/H.265, MPEG-TS, MJPEG or a GStreamer pipeline); tabs Quick (6 configurable values), PreFlight, Gauges, Servo/Relay, Aux Function, Payload, Transponder, Actions (mode, arm/disarm, Auto/Loiter/RTL, set WP, change alt/speed, Do Action), Messages, Status, Telemetry Logs (tlog playback), DataFlash Logs (download window, log browser with graphs and GPS track); map with vehicle, track, mission/fence/rally, heading lines, Fly To Here, Set Home Here; live tuning graph |
| PLAN | Mission / fence / rally editor: map editing (click, drag, insert, polygon tools), waypoint grid with per-command headers from `mavcmd.xml`, home, file load/save (QGC WPL), read/write to the vehicle, Survey (Grid) dialog |
| SETUP | Install Firmware (manifest, board detection, bootloader upload), Frame Type, Accel / Compass / Radio calibration (optional QGroundControl-style channel mapping), Servo Output (with a Motor layout tab: frame type, motor order wizard, motor direction, reversed-direction switch), Serial Ports, ESC Calibration, Flight Modes, FailSafe, Battery Monitor, Motor Test, Rangefinder, Airspeed, Optical Flow, OSD, Mount, Parachute, CompassMot, ADSB, HW ID, Initial Parameters, RTK/GPS Inject, Joystick, SiK Radio, DroneCAN, Terminal |
| CONFIG | Basic / Extended Tuning (Mission Planner layouts per vehicle), Standard / Advanced Params, Full Parameter List (value lists, bitmask editor, compare, file load/save, reboot), Full Parameter Tree, User Params, MAVFtp browser, MAVLink Inspector |
| SETTINGS | App Settings: theme (Dark, Mission Planner, Light, High Contrast, Midnight), map, logs, parameter docs, telemetry rates, units; About (version, data folders, Linux menu entry) |
| SIMULATION | Download and run ArduPilot SITL (Plane, Copter, Rover, Heli) and connect to it |

## Projects

| Project | What it holds |
|---|---|
| `MissionPlanner.App.Core` | Services (session, parameters, mission, map tiles) and view-models. No UI framework types. |
| `MissionPlanner.App` | Avalonia views, controls (map, HUD, layers) and the desktop entry point. |
| `MissionPlanner.App.Tests` | MSTest: protocol flows against scripted fake vehicles (TCP/UDP/serial pty), page logic, parsers. |

## Build, run, test (any OS with the .NET 10 SDK)

```sh
dotnet build MissionPlanner.Next.sln
dotnet run --project src/MissionPlanner.App
dotnet test src/MissionPlanner.App.Tests      # serial test needs socat on Linux/macOS
```

Opt-in check against a real flight controller (read-only: connect, parameters, telemetry):

```sh
MP_TEST_SERIAL_PORT=/dev/serial/by-id/usb-ArduPilot_...-if00 dotnet test src/MissionPlanner.App.Tests --filter TestCategory=Hardware
```

`MP_LOG_TILES=1` prints map tile request failures to the console.

## Data and settings

Shared with the WinForms app: the data folder (`~/.local/share/Mission Planner/` on Linux, the
usual folders on Windows/macOS) — map tile cache, parameter documentation, logs, SITL binaries.
Settings are kept separately in `config.next.xml` so the two apps never overwrite each other.

## Notes for contributors

- Pages are found by name: `ViewModels.<Area>.<Name>ViewModel` is shown by `Views.<Area>.<Name>View`.
- `VehicleSession` owns the one `MAVLinkInterface` and its reader task; library calls that talk to
  the vehicle block, so run them with `Task.Run`, never on the UI thread.
- `PageViewModel.Tick()` runs at 20 Hz on the UI thread for visible pages: copy telemetry there,
  without reflection.
- Opening a link never prompts: `LinkFactory` answers the transports' settings/input hooks from a
  `LinkSpec` (`tcp:host:port`, `udp:port`, `udpout:host:port`, `serial:port[:baud]`).
- Call `MapTiles.Initialize()` before anything touches `GMaps.Instance`.
- UI text is written in English and translated with Mission Planner's own translations (SETTINGS >
  App Settings > Language): `Text="{l:Tr 'Load File...'}"` in XAML (`xmlns:l="using:MissionPlanner.App.Localization"`,
  escape a quote as `\'`), `{l:TrBind {Binding StatusText}}` for English text from a view-model, `L.Tr("...")` /
  `L.Format("... {0}", x)` in code. Dialog titles, messages and window titles are translated by the dialog and
  window services. After adding strings run `dotnet run --project src/tools/LocalizationHarvest`, which rebuilds
  `MissionPlanner.App/Localization/<culture>.json` from the WinForms `.resx` files and lists the gaps in
  `Localization/untranslated.txt`; corrections and new translations go in a table's `"overrides"` section (kept
  by the tool), or for one user in `<data folder>/Localization/<culture>.json`.
