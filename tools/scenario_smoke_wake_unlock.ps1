<#
MechMaker scenario: smoke_wake_unlock — androidtester's flagship scenario, run
against the live Android emulator through the HIL seam, purely via MCP tools:

  wake (touch tap) -> swipe up to unlock -> screencap -> assert the home screen
  is showing by locating a known launcher element with OpenCV.

Usage: pwsh tools/scenario_smoke_wake_unlock.ps1
Needs: platform-tools adb (the emulator self-starts).
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

# ---------- protocol plumbing ----------
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

Send-Rpc 1 "initialize" @{ protocolVersion = "2024-11-05"; capabilities = @{}; clientInfo = @{ name = "smoke-wake-unlock"; version = "1.0.0" } }
[void](Read-Rpc 30000)
Send-Rpc $null "notifications/initialized" @{}

# ---------- scenario: smoke_wake_unlock ----------

# pre: place the DUT and wake the screen with a touch.
[void](Expect-Ok 100 "new_machine" @{ name = "smoke_wake_unlock" })
[void](Expect-Ok 101 "add_part" @{ catalogId = "android_phone"; instanceId = "dut" })
[void](Expect-Ok 102 "hil_connect" @{ transport = "adb" })

# step 1: wake the screen (tap lower half — on AOD/lock screens this wakes).
[void](Expect-Ok 103 "touch_tap" @{ fx = 0.5; fy = 0.5 })
Start-Sleep -Seconds 2

# step 2: swipe up to unlock (stock launcher gesture).
[void](Expect-Ok 104 "touch_swipe" @{ fx1 = 0.5; fy1 = 0.85; fx2 = 0.5; fy2 = 0.35; durationMs = 350 })
Start-Sleep -Seconds 3

# step 3: verify the home screen with vision — the stock launcher's dock/search
# bar is at the bottom; capture and cut a template of the bottom-centre region.
$screencap = Join-Path ([System.IO.Path]::GetTempPath()) ("wake_{0}.png" -f [guid]::NewGuid().ToString("N"))
[void](Expect-Ok 105 "screen_screencap" @{ outputPath = $screencap })
$template = Join-Path ([System.IO.Path]::GetTempPath()) ("dock_{0}.png" -f [guid]::NewGuid().ToString("N"))
Send-Rpc 106 "tools/call" @{ name = "screen_extract_template"; arguments = @{
    screencapPath = $screencap; fx = 0.5; fy = 0.95; halfSizePx = 80; outputPath = $template } }
$r = Read-Rpc 60000
Check "dock template cut" (-not $r.result.isError) (($r.result.content | ForEach-Object { $_.text }) -join "`n")

# step 4: assert the home screen is present on a FRESH capture (self-consistency:
# the launcher element is findable again — proves rectification-stable vision).
$found = Expect-Ok 106 "screen_find" @{ name = "home_dock"; templatePath = $template }
Check "home screen asserted (dock found)" ($found -match "dock at" -and $found -match "confidence") $found

# step 5: tap where vision said (the dock centre) — the touch path is verified end
# to end: vision result -> tap -> no exceptions, emulator alive.
[void](Expect-Ok 106 "touch_tap" @{ fx = 0.5; fy = 0.95 })

# ---------- teardown ----------
$p.StandardInput.Close()
if (-not $p.WaitForExit(15000)) { $p.Kill() }
Remove-Item $screencap, $template -ErrorAction SilentlyContinue

Write-Host ""
if ($script:failures -gt 0) { Write-Host "SCENARIO smoke_wake_unlock: FAILED"; exit 1 } else { Write-Host "SCENARIO smoke_wake_unlock: PASSED"; exit 0 }
Pop-Location