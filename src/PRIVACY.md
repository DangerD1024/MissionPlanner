# Mission Planner 3 – Privacy Policy

Mission Planner 3 (the desktop app and the phone apps for Android and iPhone) is a ground control
station for ArduPilot vehicles. This policy describes what the app does with information.

## What the app collects

Nothing. The app has no accounts, no analytics, no advertising and no tracking, and it does not send
any personal information to the developers or to anyone else.

## What stays on your device

Settings, recent connections, flight logs (telemetry logs) and cached map tiles are stored only on
your device, in the app's own storage. Removing the app removes them.

## Network, Bluetooth and USB

- **Your vehicle:** the app connects to your vehicle or telemetry radio over Wi-Fi (UDP or TCP on
  your network), Bluetooth or USB, only when you ask it to. The data exchanged (telemetry, parameters,
  missions) goes only between your device and your vehicle.
- **Maps and elevation:** to show maps, the app downloads map tiles and terrain data from the map
  provider you choose (for example Google, Bing, Esri or OpenStreetMap). Those requests include the
  area being viewed, as any map app's do, and are covered by the provider's own privacy policy.
- **Place search** (desktop): a place name you search for is sent to OpenStreetMap Nominatim.
- **Updates** (desktop): the app asks GitHub for the latest release of Mission Planner 3.
- **Neural voices** (desktop): a voice you choose to download in the speech settings comes from the
  sherpa-onnx project's releases on GitHub. Speech is then made on your computer; no text is sent anywhere.

## Location

The phone apps do not use your phone's location. Positions shown on the map are your vehicle's,
reported by the vehicle itself.

## Children

The app is not directed at children and collects no information from anyone.

## Changes and contact

Changes to this policy are published in this file. Questions: open an issue at
https://github.com/DangerD1024/MissionPlanner/issues.
