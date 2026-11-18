# Mission Planner — cross-platform app (preview)

A new user interface for Mission Planner on .NET 10 and Avalonia 12, running natively on Windows,
Linux and macOS. It reuses the existing protocol and vehicle libraries in `ExtLibs/` unchanged
(MAVLink, Comms, ArduPilot, Utilities, GMap.NET.Core, Maps); only the UI layer is new. The
WinForms app (`MissionPlanner.sln`) is untouched and keeps building as before.

## Layout

| Project | What it holds |
|---|---|
| `MissionPlanner.App.Core` | Services and view-models. No UI framework types. |
| `MissionPlanner.App` | Avalonia views, controls and the desktop entry point. |
| `MissionPlanner.App.Tests` | MSTest: link parsing, connects against a fake vehicle (TCP, UDP, serial via a socat pty pair), data paths, tile cache. |

## Build, run, test (any OS with the .NET 10 SDK)

```sh
dotnet build MissionPlanner.Next.sln
dotnet run --project src/MissionPlanner.App
dotnet test src/MissionPlanner.App.Tests      # serial test needs socat on Linux/macOS
```

`MP_LOG_TILES=1` prints map tile request failures to the console.

## Notes for contributors

- Opening a link never prompts: `LinkFactory` answers the transports' settings/input hooks from a
  `LinkSpec` (`tcp:host:port`, `udp:port`, `udpout:host:port`, `serial:port[:baud]`).
- `MAVLinkInterface.Open` blocks until connected. Call it off the UI thread with a
  `BackgroundProgressReporter` in scope (`BackgroundProgressReporter.Scope.Use`).
- Call `MapTiles.Initialize()` before anything touches `GMaps.Instance`; otherwise GMap looks for
  the WinForms assembly.
- Map tiles share the WinForms app's cache: `<data dir>/gmapcache/TileDBv3`.
