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
| DATA | HUD with video (right-click: RTSP, UDP H.264/H.265, MPEG-TS, MJPEG or a GStreamer pipeline); tabs Quick (6 configurable values), PreFlight, Gauges, Servo/Relay, Aux Function, Payload, Transponder, Actions (mode, arm/disarm, Auto/Loiter/RTL, set WP, change alt/speed, Do Action, Speech: announcements with a downloaded neural voice or the system voice), Messages, Status, Telemetry Logs (tlog playback), DataFlash Logs (download window, log browser with graphs and GPS track); map with vehicle, track, mission/fence/rally, heading lines, Fly To Here, Set Home Here; live tuning graph |
| PLAN | Mission / fence / rally editor: map editing (click, drag, insert, polygon tools), waypoint grid with per-command headers from `mavcmd.xml`, home, file load/save (QGC WPL), read/write to the vehicle, Survey (Grid) dialog |
| SETUP | Install Firmware (manifest, board detection, bootloader upload; STM32 DFU flashing of the bootloader + firmware .hex with the bundled dfu-util for boards running iNav, Betaflight or nothing), Frame Type, Accel / Compass / Radio calibration (optional QGroundControl-style channel mapping), Servo Output (with a Motor layout tab: frame type, motor order wizard, motor direction, reversed-direction switch), Serial Ports, ESC Calibration, Flight Modes, FailSafe, Battery Monitor, Motor Test, Rangefinder, Airspeed, Optical Flow, OSD, Mount, Parachute, CompassMot, ADSB, HW ID, Initial Parameters, RTK/GPS Inject, Joystick, SiK Radio, DroneCAN, Terminal |
| CONFIG | Basic / Extended Tuning (Mission Planner layouts per vehicle), Standard / Advanced Params, Full Parameter List (value lists, bitmask editor, compare, file load/save, reboot), Full Parameter Tree, User Params, MAVFtp browser, MAVLink Inspector |
| SETTINGS | App Settings: theme (Dark, Mission Planner, Light, High Contrast, Midnight), map, logs, parameter docs, telemetry rates, units |
| HELP | Version, check for updates (also at startup: a banner offers the new installer, downloads it and starts it), data and log folders, Discuss forum thread, documentation, bug reports; on Linux: Allow USB access (serial group, DFU and ModemManager udev rules, through the password prompt) and Add to applications menu |
| SIMULATION | Download and run ArduPilot SITL (Plane, Copter, Rover, Heli) and connect to it |

## Projects

| Project | What it holds |
|---|---|
| `MissionPlanner.App.Core` | Services (session, parameters, mission, map tiles) and view-models. No UI framework types. |
| `MissionPlanner.App` | Avalonia views, controls (map, HUD, layers) and the desktop entry point. |
| `MissionPlanner.App.Tests` | MSTest: protocol flows against scripted fake vehicles (TCP/UDP/serial pty), page logic, parsers. |
| `MissionPlanner.Mobile` | The phone app's UI (portrait): Fly, Plan, Setup, Messages, Link. Reuses Core's view-models and links the desktop HUD, map and layers. |
| `MissionPlanner.Mobile.Desktop` | The phone app in a phone-sized window, for developing the mobile UI on a computer. |
| `MissionPlanner.Mobile.Android` | The Android app (not in the solution: it needs the .NET Android workload). |

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

### Phone app

```sh
dotnet run --project src/MissionPlanner.Mobile.Desktop          # phone-sized preview window
MP_CONNECT=tcp:127.0.0.1:5760 dotnet run --project src/MissionPlanner.Mobile.Desktop   # connected to SITL at start
```

The phone app turns with the phone (or is held one way: Link tab › Screen). Portrait has the tabs at
the bottom; landscape puts them in a rail on the left, and Fly shows one view full screen with the other
as a small picture in the corner (tap it to swap), the action buttons in a column on the right.
`--landscape` starts the preview wide. Both have a status bar (mode, GPS, battery, link) and a readiness strip;
**Fly** (HUD, telemetry strip, map with tap-to-go-here, Arm / Takeoff / Land / RTL / Pause, every one
confirmed by a slide), **Plan** (tap to add waypoints, read / write), **Setup** (accelerometer and level,
compass, radio, sensors, flight modes, failsafe, battery, frame, motor test, all parameters),
**Messages** and **Link** (USB on Android, UDP for Wi-Fi telemetry bridges, TCP, recent links). The preview keeps its own
settings (`%LOCALAPPDATA%/MissionPlannerPhonePreview`).

Android: `dotnet workload install android`, a JDK 17+ and an Android SDK (platform 36), then
`JAVA_HOME=... ANDROID_HOME=... installer/android/build-apk.sh` builds `bin/MissionPlanner-<version>-android-arm64.apk`
(signed with the build machine's debug key, for installing by hand).

iPhone (`MissionPlanner.Mobile.iOS`): on a Mac with Xcode and `dotnet workload install ios` (the workload
version must match the installed Xcode, e.g. 27.0.10722 for Xcode 27.0),
`BUILD_NUMBER=n installer/ios/build-ipa.sh` builds an Xcode archive; `xcodebuild -exportArchive` with
`-allowProvisioningUpdates` then signs and uploads it to App Store Connect using the Apple account signed in to
Xcode (or `installer/ios/build-ipa.sh upload` with an App Store Connect API key). iOS gives apps Bluetooth LE
only (no classic serial, no USB serial), so the iPhone links are Bluetooth LE, UDP and TCP. `MP_START_TAB=Plan`
(`SIMCTL_CHILD_MP_START_TAB` for `simctl launch`) opens a tab at start, for screenshots. Privacy policy:
`src/PRIVACY.md`.

USB on Android (`App.Core/Links/Usb`): the phone's USB host opens serial ports through drivers written in C#
for USB CDC-ACM (flight controllers on their own USB port), FTDI, CP210x and CH340 (telemetry radios); a
link is saved as `serial:usb:<vid>:<pid>:<port>:<baud>`, so a replugged device is found again. Plugging in
a known radio or board offers to open the app (`Resources/xml/device_filter.xml`). To try it in the
emulator, give it QEMU's FTDI chip wired to SITL's serial0:
`-qemu -chardev socket,id=s,host=127.0.0.1,port=5760,reconnect=2 -device qemu-xhci,id=x -device usb-serial,bus=x.0,chardev=s`
(started with `-writable-system`, after pushing a `android.hardware.usb.host` feature file to
`/system/etc/permissions`, since the emulator image has no USB host service otherwise).

## Bundled tools

`src/MissionPlanner.App/Tools/dfu-util/` holds dfu-util 0.11 (GPL-2.0-or-later) with libusb
(LGPL-2.1-or-later) for linux-x64, linux-arm64, win-x64, win-x86 and macOS (Intel build, Rosetta 2 on
Apple Silicon). `Tools/dfu-util/dfu-util.targets` copies the build for the target runtime
(`dotnet publish -r <rid>`, or this machine's for a plain build) to `Tools/dfu-util/` in the output, with its
licences; `README.txt` there lists where each binary comes from and its checksum. Install Firmware runs it
to flash boards in STM32 DFU mode (bundled copy first, then PATH; on Apple Silicon a native one from PATH or
Homebrew first). Windows needs the WinUSB driver for "STM32 BOOTLOADER" (Zadig); Linux needs the udev rule from
HELP › Allow USB access.

`src/MissionPlanner.App/Tools/sherpa-onnx/` adds the neural speech engine: sherpa-onnx 2.0.3 (Apache-2.0,
no GPL code) with ONNX Runtime 1.28.2 (MIT), from the `org.k2fsa.sherpa.onnx.runtime.<rid>` NuGet packages
for linux-x64, linux-arm64, win-x64, win-x86, win-arm64, osx-x64 and osx-arm64. `App.Core` references only
the managed API (`ExcludeAssets="native"`, so the phone apps carry no native engine and keep their speech);
`sherpa-onnx.targets` adds the runtime's libraries next to the app and `README.txt` with the licences to
`Tools/sherpa-onnx/`. No voice is bundled: DATA › Actions › Speech... › Engine & voice downloads them
(Kokoro, Kitten TTS; Apache-2.0) from sherpa-onnx's `tts-models` release; the system voice speaks until one is
ready, or when the engine cannot run (`App.Core/Speech/`: catalogue, download store, lexicon phonemizer,
engine, players).

## Data and settings

Shared with the WinForms app: the data folder (`~/.local/share/Mission Planner/` on Linux, the
usual folders on Windows/macOS) — map tile cache, parameter documentation, logs, SITL binaries.
Settings are kept separately in `config.next.xml` so the two apps never overwrite each other. Neural voices
are downloaded to `voices/` in the data folder (per user).

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
