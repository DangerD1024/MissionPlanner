# Mission Planner 3

[![Mission Planner 3 build and tests](https://github.com/DangerD1024/MissionPlanner/actions/workflows/next.yml/badge.svg?branch=main)](https://github.com/DangerD1024/MissionPlanner/actions/workflows/next.yml)

A cross-platform ground control station for [ArduPilot](https://ardupilot.org) vehicles: plan
missions, set up and tune the autopilot, and fly with live telemetry, map and video. It is a new
interface for [Mission Planner](https://ardupilot.org/planner/), built on .NET 10 and Avalonia 12,
that runs natively on **Linux, Windows and macOS** while reusing Mission Planner's proven MAVLink
and vehicle libraries.

> **Status: preview (3.0.2b).** The main screens of the classic Mission Planner are in place and
> tested against ArduPilot SITL, and the setup screens were checked read-only on a real flight
> controller. It has not yet been flown with real aircraft — try it on the simulator or a bench
> vehicle with the propellers removed.

- Website: https://ardupilot.org/planner/
- Forum: https://discuss.ardupilot.org/c/ground-control-software/mission-planner
- License: [GPL v3](COPYING.txt)

## What's in it

The layout follows Mission Planner: screens along the top, connection controls on the right,
and a menu on the left of Setup and Config that shows only the pages your vehicle needs.

| Screen | Highlights |
|---|---|
| **Data** | Attitude HUD with video behind it (RTSP, UDP H.264/H.265, MPEG-TS, MJPEG or any GStreamer pipeline) or a 3D view of the terrain from the vehicle (FPV camera field of view and tilt adjustable, or as a fallback when there is no video); the HUD can go to its own window (another monitor, fullscreen) and be streamed to other apps over RTSP or RTP/UDP, with or without the OSD; map in 2D, 3D (third-person view following the vehicle over satellite-draped terrain, orbit with the mouse) or both; tabs for quick values, actions (arm, modes, waypoints, Do Action), messages, status, pre-flight checklist, gauges, servo/relay, aux functions, gimbal, transponder; map with vehicle, track, mission, ADS-B traffic, Fly To Here; telemetry log playback; DataFlash log download and browser; live tuning graph; spoken alerts |
| **Plan** | Mission, fence and rally editor on the map; waypoint grid with per-command fields; survey grid; read/write to the vehicle; QGC WPL files |
| **Setup** | Firmware install; frame type; accelerometer, compass and radio calibration (with optional QGroundControl-style channel mapping); servo outputs with a motor layout tab (motor order wizard, motor direction, frame type); serial ports; ESC calibration; flight modes; failsafe; battery; motor test; rangefinder, airspeed, optical flow, OSD layout editor, gimbal, parachute, CompassMot, ADS-B, hardware IDs, initial tuning parameters; RTK/GPS injection (NTRIP or base station); joystick; SiK radio; DroneCAN; MAVLink shell terminal |
| **Config** | Basic and extended tuning laid out as in Mission Planner for each vehicle; standard and advanced parameters; full parameter list and tree with value lists and bitmask editors, compare, load/save, reboot; user parameter list; MAVFTP file browser; MAVLink inspector |
| **Simulation** | Downloads and runs ArduPilot SITL (Plane, Copter, Rover, Heli) and connects to it |
| **Settings** | Theme (Mission Planner, Dark, Light, High Contrast, Midnight), maps, logs, parameter docs, telemetry rates, units |

Connecting: pick a serial port or TCP/UDP at the top right, or tick **Auto-connect** to find a
vehicle on USB or UDP 14550 by itself. Parameters download over MAVFTP when the autopilot
supports it. Unplugging USB disconnects; a TCP or UDP link that drops shows *Reconnecting* and
resumes on its own.

## Getting it

Download the latest release ([release notes](https://github.com/DangerD1024/MissionPlanner/releases/latest), [all releases](https://github.com/DangerD1024/MissionPlanner/releases)):

- Windows installer: https://github.com/DangerD1024/MissionPlanner/releases/latest/download/MissionPlanner-win-x64.msi
- Linux package: https://github.com/DangerD1024/MissionPlanner/releases/latest/download/MissionPlanner-linux-x64.tar.gz


- **Windows:** `MissionPlanner-<version>-win-x64.msi` — installs to *Program Files\Mission Planner 3*
  with Start menu and desktop shortcuts, next to (not over) the classic Mission Planner. Includes
  the .NET runtime and SDL2 (joystick); video needs the GStreamer runtime (below).
- **Linux:** `MissionPlanner-<version>-linux-x64.tar.gz` — unpack anywhere and run
  `./MissionPlanner.App`; includes the .NET runtime. Needs the usual desktop X11 libraries
  (`libice6 libsm6 libfontconfig1`, already present on desktop installs). Built with
  `installer/linux/build-tarball.sh`.

The Windows installer is built with `installer\windows\build-msi.ps1` (needs the .NET 10 SDK and
`dotnet tool install --global wix --version 5.0.2`).

### From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git.

```sh
git clone https://github.com/DangerD1024/MissionPlanner.git
cd MissionPlanner
dotnet run --project src/MissionPlanner.App
```

A self-contained folder you can copy and launch (Linux x64 shown; use `win-x64`, `osx-arm64` or
`osx-x64` for the others):

```sh
dotnet publish src/MissionPlanner.App -c Release -r linux-x64 --self-contained false -o bin/next-linux-x64
./bin/next-linux-x64/MissionPlanner.App
```

On Linux, Settings → *Add to applications menu* (at the bottom) adds it to your desktop's launcher.

### Optional system packages

The app runs without these; the features that need them say so when they are missing.

| Feature | Linux (Debian/Ubuntu) | Windows | macOS |
|---|---|---|---|
| Video in the HUD, streaming the HUD | `gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-plugins-ugly gstreamer1.0-libav gstreamer1.0-rtsp` | included in the installer | `brew install gstreamer` |
| 3D view | OpenGL 3.3 (any desktop driver) | OpenGL 3.3 | OpenGL 3.3 |
| Joystick | `libsdl2-2.0-0` | `SDL2.dll` next to the app | `brew install sdl2` |
| Spoken alerts | `speech-dispatcher` or `espeak-ng` | built in | built in |
| Serial ports | add yourself to the `dialout` group | — | — |

Linux is where it is developed and tested. Windows is built and tested in CI and has been run
against the Windows SITL build (Simulation screen, connect, modes, arming, parameters); macOS has
not been built or run yet.

On Windows, ArduPilot's SITL build (cygwin) ends when its TCP client disconnects: after
Disconnect, start it again from the Simulation screen (the app drops the link when it exits).

## Development

```sh
dotnet build MissionPlanner.Next.sln
dotnet test src/MissionPlanner.App.Tests
```

The tests talk to scripted fake vehicles over TCP, UDP and a virtual serial port (`socat`, Linux
and macOS). Opt-in suites go further:

| Variable | Runs |
|---|---|
| `MP_TEST_SITL=1` | end-to-end tests against ArduPilot SITL (connect, parameters, missions, arm/take off/land, tuning, setup pages, reconnect); uses the binaries the Simulation screen downloads |
| `MP_TEST_SERIAL_PORT=/dev/serial/by-id/...` | read-only connect and telemetry check against a real flight controller |
| `MP_TEST_NETWORK=1` | fetches a tile from every map provider |

Project layout:

| Path | Contents |
|---|---|
| `src/MissionPlanner.App.Core` | Vehicle session, parameters, missions, maps and every screen's logic (no UI types) |
| `src/MissionPlanner.App` | Avalonia views, controls (map, HUD, motor diagram…) and the desktop entry point |
| `src/MissionPlanner.App.Tests` | MSTest suite |
| `ExtLibs/` | Libraries shared with the classic app: MAVLink, comms, vehicle state, firmware, logs, maps |

More detail for contributors is in [src/README.md](src/README.md). The version shown in the title
bar is set in one place, `MissionPlannerVersion` in `src/Directory.Build.props`.

## Data and settings

Mission Planner 3 shares the classic app's data folder — map tile cache, terrain (SRTM),
parameter documentation, logs and SITL binaries — so both apps can be used side by side.
Its own settings live in `config.next.xml` and never overwrite the classic app's `config.xml`.

| OS | Folder |
|---|---|
| Linux | `~/.local/share/Mission Planner/` (or `~/Documents/Mission Planner/` if that folder exists) |
| Windows | `C:\ProgramData\Mission Planner\` (shared data) and `Documents\Mission Planner\` (settings, logs) |
| macOS | `~/Library/Application Support/Mission Planner/` or `~/Documents/Mission Planner/` |

Copy the map cache (`gmapcache`), `srtm` folder and `*.pdef.xml` files to another computer to work
offline there.

## Internet access

| Server | Used for |
|---|---|
| `firmware.ardupilot.org` | Firmware manifest and downloads, SITL binaries |
| `firmware.oborne.me` | Mirror of the firmware manifest, used when the main one fails |
| `autotest.ardupilot.org` | Parameter documentation (checked weekly) |
| `raw.githubusercontent.com` | SITL default parameters; peripheral firmware manifest |
| `terrain.ardupilot.org` | Terrain elevation tiles |
| Map providers you select | Google, Bing, OpenStreetMap, Esri |
| NTRIP casters you configure | RTK corrections |

There is no analytics or usage tracking.

## Classic Mission Planner

This repository still contains the Windows Forms Mission Planner (`MissionPlanner.sln`), which the
new app's libraries come from. Downloads: [Windows installer](https://firmware.ardupilot.org/Tools/MissionPlanner/MissionPlanner-latest.msi),
[Android](https://play.google.com/store/apps/details?id=com.michaeloborne.MissionPlanner); history
in the [changelog](ChangeLog.txt).

To build it on Windows: install Visual Studio 2022 with the components in
[vs2022.vsconfig](vs2022.vsconfig) (Visual Studio Installer → More → Import configuration), then

```bat
git submodule update --init
.nuget\nuget.exe restore MissionPlanner.sln
msbuild -v:m -restore -t:Build -p:Configuration=Release MissionPlanner.sln
```

or open `MissionPlanner.sln` and build. On Linux it runs under Mono — see
[Linux/README.md](Linux/README.md) (`./Linux/build.sh`, then `./Linux/run.sh`).
