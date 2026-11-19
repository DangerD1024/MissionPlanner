<#
  Builds the Windows installer for Mission Planner 2 (src/MissionPlanner.App):
    1. publishes the app self-contained for win-x64 (no .NET install needed on the target PC)
    2. adds SDL2.dll (joystick support; zlib licence) from libsdl.org's release
    3. packages it with WiX v5 into bin\MissionPlanner-<version>-win-x64.msi

  Needs the .NET 10 SDK and the WiX tool:  dotnet tool install --global wix --version 5.0.2
  The version is MissionPlannerVersion in src\Directory.Build.props.

  powershell -ExecutionPolicy Bypass -File installer\windows\build-msi.ps1
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Sdl2Version = "2.30.9"
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

# 3. MSI
$msi = Join-Path $root "bin\MissionPlanner-$display-$Runtime.msi"
$wixArch = if ($Runtime -eq "win-arm64") { "arm64" } elseif ($Runtime -eq "win-x86") { "x86" } else { "x64" }
if (-not ((wix extension list -g) -match "WixToolset.UI.wixext")) { wix extension add -g WixToolset.UI.wixext/5.0.2 }
Push-Location $PSScriptRoot
try {
    wix build Package.wxs -arch $wixArch -ext WixToolset.UI.wixext `
        -d "Version=$numeric" -d "DisplayVersion=$display" `
        -bindpath "publish=$publish" -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed" }
}
finally { Pop-Location }

Write-Host "Built $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
