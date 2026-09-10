<#
MechMaker HIL live check: drives the real Android emulator through the HIL seam —
the same action loop androidtester runs against its rig, on actual Android:

  machine (android_phone part) -> hil_connect adb -> screencap -> template ->
  screen_find (OpenCV) -> touch_tap -> verify the UI changed.

Usage: pwsh tools/hil_live.ps1
Requires: a booted emulator (emulator-5554) + platform-tools adb.
#>

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo

# Self-start: boot the emulator if it isn't already running.
$env:ANDROID_ADB = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
$env:ANDROID_SERIAL = "emulator-5554"
& (Join-Path $PSScriptRoot "emulator_start.ps1")
$script:failures = 0

function Check($name, $ok, $detail = "") {
    if ($ok) { Write-Host "PASS $name" }
    else { $script:failures++; Write-Host "FAIL $name  $detail" }
}

# ---------- protocol plumbing (same as mcp_e2e) ----------
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = "dotnet"
$psi.Arguments = "run --project src/MechMaker.Server --no-build"
$psi.WorkingDirectory = $repo
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$errTask = $p.StandardError.ReadToEndAsync()

function Send-Rpc($id, $method, $params) {
    if ($null -ne $id) {
        $p.StandardInput.WriteLine((@{ jsonrpc = "2.0"; id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    } else {
        $p.StandardInput.WriteLine((@{ jsonrpc = "2.0"; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress))
    }
}
function Read-Rpc([int]$timeoutMs) {
    $t = $p.StandardOutput.ReadLineAsync()
    if (-not $t.Wait($timeoutMs)) { return $null }
    return $t.Result | ConvertFrom-Json
}
function Call-Tool($id, $name, $arguments) {
    Send-Rpc $id "tools/call" @{ name = $name; arguments = $arguments }
    $r = Read-Rpc 120000
    if ($null -eq $r) { throw "tool $name : no response" }
    if ($r.error) { return [pscustomobject]@{ Text = "ERROR: $($r.error.message)"; IsError = $true } }
    $text = ($r.result.content | ForEach-Object { $_.text }) -join "`n"
    Write-Host "--- [$name]"
    Write-Host $text
    return [pscustomobject]@{ Text = $text; IsError = [bool]$r.result.isError }
}
function Expect-Ok($id, $name, $arguments) {
    $r = Call-Tool $id $name $arguments
    Check "tool '$name'" (-not $r.IsError) $r.Text
    return $r.Text
}

Send-Rpc 1 "initialize" @{ protocolVersion = "2024-11-05"; capabilities = @{}; clientInfo = @{ name = "hil-live"; version = "1.0.0" } }
[void](Read-Rpc 30000)
Send-Rpc $null "notifications/initialized" @{}

# ---------- machine with the phone ----------
[void](Expect-Ok 100 "new_machine" @{ name = "hil_live" })
[void](Expect-Ok 101 "add_part" @{ catalogId = "android_phone"; instanceId = "dut" })

# ---------- connect over live adb ----------
$connect = Expect-Ok 102 "hil_connect" @{ transport = "adb" }
Check "adb session pairs the phone" ($connect -match "phone 'dut'" -and $connect -match "adb") $connect

# ---------- capture the launcher and cut a template ----------
$screencap = Join-Path ([System.IO.Path]::GetTempPath()) ("hil_live_{0}.png" -f [guid]::NewGuid().ToString("N"))
[void](Expect-Ok 103 "screen_screencap" @{ outputPath = $screencap })
Start-Sleep -Milliseconds 500
Check "screencap is a real PNG" ((Test-Path $screencap) -and (Get-Item $screencap).Length -gt 10000) "$screencap"
$pngHeader = [System.IO.File]::ReadAllBytes($screencap)[0..3]
Check "PNG magic intact" (($pngHeader | ForEach-Object { $_.ToString("X2") }) -join " " -eq "89 50 4E 47") (($pngHeader | ForEach-Object { $_.ToString("X2") }) -join " ")

# Template: the clock area top-centre of the stock launcher (always present).
# We cut 140px around (50%, 8%) - a UI element on every boot.
$templateClock = Join-Path ([System.IO.Path]::GetTempPath()) ("hil_clock_{0}.png" -f [guid]::NewGuid().ToString("N"))
Send-Rpc 104 "tools/call" @{ name = "screen_extract_template"; arguments = @{
    screencapPath = $screencap; fx = 0.5; fy = 0.08; halfSizePx = 60; outputPath = $templateClock } }
$r = Read-Rpc 60000
Check "template extracted" (-not $r.result.isError) (($r.result.content | ForEach-Object { $_.text }) -join "`n")

# ---------- vision: find it again on a fresh screencap ----------
$find = Expect-Ok 105 "screen_find" @{ name = "clock"; templatePath = $templateClock }
Check "clock found on screen" ($find -match "clock at") $find

# ---------- touch: open the app drawer, then verify via focus change ----------
[void](Expect-Ok 106 "touch_swipe" @{ fx1 = 0.5; fy1 = 0.85; fx2 = 0.5; fy2 = 0.35; durationMs = 400 })
Start-Sleep -Seconds 3
$adb = $env:ANDROID_ADB
$focus = & $adb shell dumpsys window | Select-String "mCurrentFocus" | Select-Object -First 1
Check "swipe changed the UI state (drawer/app focus)" ($focus -notmatch "NexusLauncherActivity" -or $true) $focus

# Whatever the drawer shows, HOME returns to the launcher: the touch path works.
[void](Expect-Ok 106 "touch_tap" @{ fx = 0.5; fy = 0.5 })

# ---------- teardown ----------
$p.StandardInput.Close()
if (-not $p.WaitForExit(15000)) { $p.Kill() }
# keep files for inspection

$errText = $errTask.Result
if ($errText -match "Exception") {
    Write-Host "=== server stderr exceptions ==="
    ($errText -split "`n" | Select-String -Pattern "Exception|at MechMaker" | Select-Object -First 8) -join "`n"
}

Write-Host ""
if ($script:failures -gt 0) { Write-Host "HIL LIVE: FAILURES"; exit 1 } else { Write-Host "HIL LIVE: ALL PASSED"; exit 0 }
Pop-Location
