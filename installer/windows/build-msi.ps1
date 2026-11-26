<#
  Builds the Windows installer for Mission Planner 3 (src/MissionPlanner.App):
    1. publishes the app self-contained for win-x64 (no .NET install needed on the target PC), with
       the bundled dfu-util for STM32 DFU flashing in Tools\dfu-util (src\MissionPlanner.App\Tools\dfu-util)
    2. adds SDL2.dll (joystick support; zlib licence) from libsdl.org's release
    3. adds GStreamer (video behind the HUD, streaming): the official MSVC runtime, unpacked (not
       installed), reduced to the plugins the app uses and exactly the DLLs they load
    4. packages it with WiX v5 into bin\MissionPlanner-<version>-win-x64.msi

  Needs the .NET 10 SDK and the WiX tool:  dotnet tool install --global wix --version 5.0.2
  The version is MissionPlannerVersion in src\Directory.Build.props.

  powershell -ExecutionPolicy Bypass -File installer\windows\build-msi.ps1
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Sdl2Version = "2.30.9",
    [string]$GStreamerVersion = "1.26.11",
    # sha256 of gstreamer-1.0-msvc-x86_64-<version>.msi, from gstreamer.freedesktop.org
    [string]$GStreamerSha256 = "31cbc21fa0950b5c1e79c80959b2799805cb05a7a35953a13a9f790776137605"
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
Set-Location $root

[xml]$props = Get-Content "src\Directory.Build.props"
$display = ($props.Project.PropertyGroup | Where-Object { $_.MissionPlannerVersion } | Select-Object -First 1).MissionPlannerVersion
if (-not $display) { throw "MissionPlannerVersion not found in src\Directory.Build.props" }
$numeric = [regex]::Match($display, '^\d+(\.\d+){0,3}').Value
Write-Host "Mission Planner $display (MSI version $numeric), $Runtime"

foreach ($tool in "dotnet", "wix") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool not found on PATH (see the comment at the top of this script)" }
}

# 1. publish
$publish = Join-Path $root "bin\next-$Runtime"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish src\MissionPlanner.App -c Release -r $Runtime --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# the MSI takes the whole publish folder, Tools\dfu-util included
if (-not (Test-Path (Join-Path $publish "Tools\dfu-util\dfu-util.exe"))) { throw "the bundled dfu-util is missing from the publish" }

# 2. SDL2 (cached between runs)
$cache = Join-Path $root "bin\cache"
New-Item -ItemType Directory -Force $cache | Out-Null
$arch = if ($Runtime -eq "win-x86") { "x86" } else { "x64" }
$sdlZip = Join-Path $cache "SDL2-$Sdl2Version-win32-$arch.zip"
if (-not (Test-Path $sdlZip)) {
    $url = "https://github.com/libsdl-org/SDL/releases/download/release-$Sdl2Version/SDL2-$Sdl2Version-win32-$arch.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $sdlZip -UseBasicParsing
}
$sdlDir = Join-Path $cache "SDL2-$Sdl2Version-$arch"
if (-not (Test-Path $sdlDir)) { Expand-Archive $sdlZip -DestinationPath $sdlDir }
Copy-Item (Join-Path $sdlDir "SDL2.dll") $publish
Copy-Item (Join-Path $sdlDir "README-SDL.txt") $publish

# 3. GStreamer subset
if ($Runtime -eq "win-x64") {
    $gstMsi = Join-Path $cache "gstreamer-1.0-msvc-x86_64-$GStreamerVersion.msi"
    if (-not (Test-Path $gstMsi)) {
        $url = "https://gstreamer.freedesktop.org/data/pkg/windows/$GStreamerVersion/msvc/gstreamer-1.0-msvc-x86_64-$GStreamerVersion.msi"
        Write-Host "Downloading $url"
        Invoke-WebRequest $url -OutFile $gstMsi -UseBasicParsing
    }
    $hash = (Get-FileHash $gstMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $GStreamerSha256) { throw "GStreamer download checksum mismatch ($hash)" }

    # administrative "install": only unpacks the files, no admin rights, nothing registered
    $gstX = Join-Path $cache "gst-$GStreamerVersion"
    if (-not (Test-Path $gstX)) {
        $p = Start-Process msiexec -ArgumentList "/a `"$gstMsi`" /qn TARGETDIR=`"$gstX`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "unpacking GStreamer failed ($($p.ExitCode))" }
    }
    $gst = Join-Path $gstX "PFiles64\gstreamer\1.0\msvc_x86_64"

    # what the app runs: playback (RTSP, UDP H.264/H.265, MPEG-TS, MJPEG, test pattern, decodebin3
    # with hardware decoders) and streaming (inter channel, H.264 encoders, RTSP server, RTP/UDP)
    $plugins = @(
        "coreelements", "app", "videoconvertscale", "videotestsrc", "playback", "typefindfunctions", "autodetect", "pbtypes",
        "rtp", "rtpmanager", "rtsp", "udp", "tcp", "soup", "multipart", "jpeg", "libav", "videoparsersbad", "mpegtsdemux",
        "audioparsers", "inter", "x264", "openh264", "mediafoundation", "nvcodec", "qsv", "d3d11", "d3d12")
    $libs = @("gstreamer-1.0-0.dll", "gstapp-1.0-0.dll", "glib-2.0-0.dll", "gstrtspserver-1.0-0.dll")
    $vcRuntime = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll", "concrt140.dll")

    Add-Type -Path (Join-Path $PSScriptRoot "PeImports.cs")
    $out = Join-Path $publish "gstreamer"
    $outBin = Join-Path $out "bin"
    $outPlugins = Join-Path $out "lib\gstreamer-1.0"
    $outExec = Join-Path $out "libexec\gstreamer-1.0"
    New-Item -ItemType Directory -Force $outBin, $outPlugins, $outExec | Out-Null

    $binFiles = @{}
    Get-ChildItem (Join-Path $gst "bin") -Filter *.dll | ForEach-Object { $binFiles[$_.Name.ToLowerInvariant()] = $_.FullName }
    $queue = New-Object System.Collections.Generic.Queue[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($pl in $plugins) {
        $f = Join-Path $gst "lib\gstreamer-1.0\gst$pl.dll"
        if (-not (Test-Path $f)) { throw "GStreamer plugin gst$pl.dll is not in the runtime" }
        Copy-Item $f $outPlugins
        $queue.Enqueue($f)
    }
    $scanner = Join-Path $gst "libexec\gstreamer-1.0\gst-plugin-scanner.exe"
    Copy-Item $scanner $outExec
    $queue.Enqueue($scanner)
    foreach ($l in $libs) { $queue.Enqueue($binFiles[$l]) }

    # every DLL they load, transitively, from the runtime's bin (Windows' own DLLs stay out)
    $vcNeeded = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    while ($queue.Count -gt 0) {
        $file = $queue.Dequeue()
        if (-not $seen.Add($file)) { continue }
        if ($file.StartsWith((Join-Path $gst "bin"), [StringComparison]::OrdinalIgnoreCase)) { Copy-Item $file $outBin }
        foreach ($import in [PeImports]::Read($file)) {
            $key = $import.ToLowerInvariant()
            if ($binFiles.ContainsKey($key)) { $queue.Enqueue($binFiles[$key]) }
            elseif ($vcRuntime -contains $key) { [void]$vcNeeded.Add($key) }
        }
    }

    # the Visual C++ runtime GStreamer is built against, app-local (a fresh Windows may not have it)
    foreach ($vc in $vcNeeded) {
        $src = Join-Path $env:SystemRoot "System32\$vc"
        if (-not (Test-Path $src)) { throw "$vc (Visual C++ runtime) is needed by GStreamer but not installed on this build machine" }
        Copy-Item $src $outBin
    }

    @"
GStreamer $GStreamerVersion (official MSVC x86_64 runtime, https://gstreamer.freedesktop.org/), a subset:
the plugins in lib\gstreamer-1.0 and the libraries they need. GStreamer is LGPL-2.1; some plugins
(x264) are GPL. Sources: https://gstreamer.freedesktop.org/src/ and the cerbero build recipes.
"@ | Set-Content -Encoding ascii (Join-Path $out "README.txt")
    $mb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
    Write-Host "GStreamer $GStreamerVersion subset: $($plugins.Count) plugins, $((Get-ChildItem $outBin).Count) libraries, $mb MB"
}

# 4. MSI
$msi = Join-Path $root "bin\MissionPlanner-$display-$Runtime.msi"
$wixArch = if ($Runtime -eq "win-arm64") { "arm64" } elseif ($Runtime -eq "win-x86") { "x86" } else { "x64" }
foreach ($ext in "WixToolset.UI.wixext", "WixToolset.Firewall.wixext") {
    if (-not ((wix extension list -g) -match $ext)) { wix extension add -g "$ext/5.0.2" }
}
Push-Location $PSScriptRoot
try {
    wix build Package.wxs -arch $wixArch -ext WixToolset.UI.wixext -ext WixToolset.Firewall.wixext `
        -d "Version=$numeric" -d "DisplayVersion=$display" `
        -bindpath "publish=$publish" -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
}
finally { Pop-Location }

Write-Host "Built $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"

# the same file without the version, for the fixed .../releases/latest/download/ link: attach both to the release
$latest = Join-Path $root "bin\latest"
New-Item -ItemType Directory -Force $latest | Out-Null
Copy-Item $msi (Join-Path $latest "MissionPlanner-$Runtime.msi")
Write-Host "Copy for the latest link: $latest\MissionPlanner-$Runtime.msi"
