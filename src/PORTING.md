# What is left to port from the WinForms Mission Planner

Checklist of features the classic app has and Mission Planner 3 (`src/`) does not yet have, built from a
comparison of both code bases (Nov 2026). Ticked items are done. Items marked *won't port* have a reason.

## Hotkeys and the Ctrl+F tools screen
- [x] Global hotkeys: F12 connect, F2 DATA, F3 PLAN, F4 CONFIG, F5 refresh parameters, Ctrl+F tools, Ctrl+Y write params to flash
- [x] TOOLS screen (Ctrl+F) with a left menu
- [x] Force accel calibrated / force compass calibrated (PREFLIGHT_CALIBRATION 76)
- [x] QNH / adjust ground pressure (GND_ABS_PRESS, BARO1_GND_PRESS)
- [x] Bootloader update over MAVLink (FLASH_BOOTLOADER) and reboot to DFU
- [x] Arm and takeoff (one click)
- [x] Lock-up test (PREFLIGHT_REBOOT_SHUTDOWN 42,24,71,93)
- [x] Message interval for any one message
- [x] Download onboard file (@SYS/threads.txt etc.)
- [x] Parameter restore (ordered: enables first, refetch, retry, report)
- [x] MAVLink packet decoder (hex / decimal bytes → fields); hardware ID decoder (typed number)
- [x] APJ tool: embed a .param file into a .apj firmware
- [ ] Outputs: MAVLink mirror (serial/TCP/UDP), NMEA position out, Cursor-on-Target, MicroDrone
- [ ] Log tools: .bin → .log, KML + GPX from .bin/.tlog, split a log, sort logs, anonymise a log,
      DashWare CSV, extract GPS inject data from a tlog, remote DataFlash logger
- [ ] Map cache: size per provider, clear, delete tiles older than 30 days, clear custom imagery
- [ ] Mag calibration from a log
- [ ] Message interval / camera test / DEVICE_OP (Ctrl+J) developer tools
- [ ] Firmware download of every build ("rip all fw")
- *won't port*: driver clean (Windows-only driver tool), structtest (developer benchmark), VLC button (the app plays video itself)

## Connection
- [ ] Read-only link (never transmit)
- [ ] Extra links at the same time (Connection Options) and connection list file
- [ ] UDP client and WebSocket link types
- [ ] Link statistics window
- [ ] ZeroConf / mDNS discovery of MAVLink and video
- [ ] Update check (stable / beta)

## DATA
- [ ] Actions: Change loiter radius, Resume mission, Abort landing, mount mode, Raw sensor view, send text message
- [ ] Map menu: Fly to coords, Point camera coords, Trigger camera now, Set EKF origin here, Takeoff, Jump to tag
- [ ] Points of interest (add / delete / save / load) on DATA and PLAN maps
- [ ] Camera overlap (CAMERA_FEEDBACK footprints)
- [ ] Map: hdop/sats, wind indicator, mission progress bar, click ADS-B aircraft for details
- [ ] HUD: record to video file, user items, ground colour, battery cell voltage, swap with map, click EKF/Vibe/prearm for details
- [ ] Payload: gimbal roll
- [ ] Quick view: number of rows/columns
- [ ] Tabs: Ctrl+1…0, choose visible tabs
- [ ] Telemetry log: 0.1x speed, Space/+/- keys, tlog → KML
- [ ] DataFlash: auto analysis, Matlab, .bin → .log, KML + GPX, geotag images
- [ ] Scripts tab (Python scripting)

## PLAN
- [ ] Insert WP at current position, Jump (start / WP #), DO_SET_ROI from the map
- [ ] Polygon: from SHP, from waypoints, offset
- [ ] Fence .fen and rally .ral files; mission .plan/.kml/.shp/.mission load, load-and-append, .mission save
- [ ] Auto WP: circle, spline circle, circle survey, text
- [ ] Survey: camera database, overlap/sidelap, GSD, footprints
- [ ] Map tools: measure distance, rotate map, zoom to place/coords, prefetch tiles (area and WP path),
      KML overlay, elevation graph, reverse WPs
- [ ] Modify alt (all), enter UTM coords, coordinate display Geo/UTM/MGRS (+ grid columns)
- [ ] Verify height, alt warn, default spline, write fast, MAVFtp upload, home = vehicle position
- [ ] Group select (Ctrl+drag / Ctrl+click), group move, undo (Ctrl+Z), Ctrl+O/S, Ctrl+Shift+W/R/F
- [ ] Zoom to vehicle / home, zoom slider, grid columns Angle and TagData

## SETUP
- [x] Battery Monitor 2
- [ ] Heli setup (swashplate, servos, governor)
- [ ] Previous firmware versions; bootloader update; .hex firmware
- [ ] Secure boot (keys, signing)
- [ ] CubeID / Remote ID module update; CAN GPS order
- [ ] SiK radio: firmware upload, load/save settings, presets, PPM failsafe
- [ ] uAvionix ADS-B registration and flight ID
- [ ] DroneCAN: online firmware update, inspector, stats, passthrough
- [ ] Bluetooth (HC-05/06) and ESP8266 setup
- [ ] FFT setup
- [ ] Antenna tracker (Tracker firmware setup and tuning, and the GCS-driven tracker)
- [ ] Advanced: Warnings manager, MAVLink signing keys, Proximity view, NMEA output, Follow Me, Moving base, spectrogram / FFT of logs
- *won't port*: legacy board presets for APM 2.5 / PX4v1, old APM CLI terminal buttons

## CONFIG
- [ ] GeoFence page
- [ ] Full parameter list: Load presaved, Reset to default, Commit params
- [ ] OSD parameter slots (OSD_PARAM_CONFIG)
- [ ] MAVFtp: download burst
- [ ] Planner settings: track length, map line toggles, ADS-B display, GCS sysid, load WPs on connect, slow-machine mode, …
- *won't port*: Ateryx pages (vehicle no longer exists)

## Bigger systems
- [ ] Swarm / formation / follow the leader
- [ ] Geotag images (GeoRef)
- [ ] No-fly zones (KMZ)
- [ ] Web server (KML / MJPEG)
- [ ] Plugin system
- [ ] Python scripting
