<#
Starts the Android emulator and waits for full boot. Idempotent: if an emulator
is already booted, it exits successfully. Used by hil_live.ps1 (and by hand).

Usage: pwsh tools/emulator_start.ps1 [-Avd Medium_Phone_API_36.0] [-TimeoutS 180]
#>

param(
    [string]$Avd = "",
    [int]$TimeoutS = 180
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

$emulator = Join-Path $env:LOCALAPPDATA "Android\Sdk\emulator\emulator.exe"
$adb = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
foreach ($tool in @($emulator, $adb)) {
    if (-not (Test-Path $tool)) { throw "Not found: $tool (install Android Studio / platform-tools)" }
}

function Get-DeviceState {
    $out = & $adb devices 2>$null | Select-String "emulator-\d+\s+(\S+)"
    if ($out) { $out.Matches[0].Groups[1].Value } else { $null }
}

# Already booted?
$state = Get-DeviceState
if ($state -eq "device") {
    $boot = (& $adb shell getprop sys.boot_completed 2>$null)
    if ("$boot".Trim() -eq "1") {
        Write-Host "emulator already booted ($env:ANDROID_SERIAL)"
        exit 0
    }
}

# Start it (windowed so the operator can see the phone; drop -window for headless).
if (-not $Avd) {
    $Avd = (& $emulator -list-avds 2>$null | Select-Object -First 1)
    if (-not $Avd) { throw "No AVDs found - create one in Android Studio (Device Manager)." }
    Write-Host "no -Avd given, using first available: $Avd"
}
Write-Host "starting emulator '$Avd'..."
$windowArg = if ($env:MECHMAKER_HEADLESS) { "-no-window" } else { "-window" }
Start-Process -FilePath $emulator -ArgumentList "-avd `"$Avd`" -no-snapshot -no-boot-anim $windowArg"

# Wait for adb + boot completion.
$deadline = (Get-Date).AddSeconds($TimeoutS)
while ((Get-Date) -lt $deadline) {
    $state = Get-DeviceState
    if ($state -eq "device") {
        $boot = (& $adb shell getprop sys.boot_completed 2>$null)
        if ("$boot".Trim() -eq "1") {
            Start-Sleep -Seconds 3  # launcher settle
            Write-Host "BOOT COMPLETE: $(& $adb devices | Select-String emulator)"
            exit 0
        }
    }
    Start-Sleep -Seconds 3
}
throw "Emulator did not finish booting within $TimeoutS s."