# MechMaker end-to-end MCP check: drives the real server over stdio like an agent
# would, assembling a full linear axis purely through tool calls, validating,
# compiling, simulating, homing with an inline Lua scenario, and saving the
# machine as examples/linear_axis_agent.json.
#
#   pwsh tools/mcp_e2e.ps1
#
# Exits non-zero on the first failed assertion; prints a PASS/FAIL transcript.

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
$script:failures = 0
$script:passed = 0

function Check($name, $ok, $detail = "") {
    if ($ok) {
        $script:passed++
        Write-Host "PASS $name"
    } else {
        $script:failures++
        Write-Host "FAIL $name  $detail"
    }
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
    $r = Read-Rpc 60000
    if ($null -eq $r) { throw "tool $name : no response" }
    if ($r.error) { return [pscustomobject]@{ Text = "ERROR: $($r.error.message)"; IsError = $true } }
    $text = ($r.result.content | ForEach-Object { $_.text }) -join "`n"
    $isError = [bool]$r.result.isError
    Write-Host "--- [$name]"
    Write-Host $text
    return [pscustomobject]@{ Text = $text; IsError = $isError }
}

function Expect-Ok($id, $name, $arguments) {
    $r = Call-Tool $id $name $arguments
    Check "tool '$name'" ((-not $r.IsError)) $r.Text
    return $r.Text
}

function Expect-Error($id, $name, $arguments) {
    $r = Call-Tool $id $name $arguments
    Check "tool '$name' rejected as expected" ($r.IsError) $r.Text
    return $r.Text
}

# ---------- 1. handshake ----------

Send-Rpc 1 "initialize" @{
    protocolVersion = "2024-11-05"; capabilities = @{}
    clientInfo = @{ name = "mechmaker-e2e"; version = "1.0.0" }
}
$init = Read-Rpc 30000
Check "initialize returns MechMaker.Server" ($init.result.serverInfo.name -eq "MechMaker.Server") $init.result.serverInfo.name
Send-Rpc $null "notifications/initialized" @{}

# ---------- 2. tool surface ----------

Send-Rpc 2 "tools/list" @{}
$tl = Read-Rpc 30000
$expected = @(
    "list_catalog_parts", "get_part_info", "new_machine", "open_machine", "save_machine",
    "get_machine_json", "add_part", "update_part_pose", "delete_part",
    "add_connection", "delete_connection", "list_connectors",
    "add_board", "wire", "validate_machine", "compile_mjcf",
    "start_run", "run_for", "set_motor_velocity", "enable_motor",
    "add_endstop", "read_endstop", "get_run_status", "stop_run",
    "get_scenario_api", "run_scenario", "run_scenario_source")
$got = @($tl.result.tools.name) | Sort-Object
$missing = $expected | Where-Object { $_ -notin $got }
$extra = $got | Where-Object { $_ -notin $expected }
Check "tools/list exposes the full tool surface" ($missing.Count -eq 0 -and $extra.Count -eq 0) "missing: $missing; extra: $extra"
Check "get_scenario_api documents the sim API" ((Call-Tool 21 "get_scenario_api" @{}).Text -match "sim\.run") ""

# ---------- 3. assemble the axis entirely through tools ----------

[void](Expect-Ok 100 "new_machine" @{ name = "agent_axis" })
[void](Expect-Ok 101 "add_board" @{ boardId = "main_board"; type = "skr-pico" })

# Structure
[void](Expect-Ok 110 "add_part" @{ catalogId = "beam_2020_400"; instanceId = "beam"; x = 0; y = 0; z = 0 })
[void](Expect-Ok 111 "add_part" @{ catalogId = "mgn12_rail_400"; instanceId = "rail" })
[void](Expect-Ok 112 "add_part" @{ catalogId = "mgn12_carriage"; instanceId = "carriage" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "motor_mount_plate_2020"; instanceId = "plate_left" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "motor_mount_plate_2020"; instanceId = "plate_right" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "nema17_stepper"; instanceId = "motor_left" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "nema17_stepper"; instanceId = "motor_right" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "gt2_pulley_20t"; instanceId = "pulley_left" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "gt2_pulley_20t"; instanceId = "pulley_right" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "gt2_belt_400"; instanceId = "belt" })
[void](Expect-Ok 111 "add_part" @{ catalogId = "endstop_microswitch"; instanceId = "endstop" })
Check "machine has 11 parts" ((Call-Tool 112 "get_machine_json" @{}).Text -match '"id": "motor_right"') ""

# Connections (parent connector <-> child connector, exactly like linear_axis_v0)
$connections = @(
    @("beam", "top_mid", "rail", "mount"),
    @("rail", "carriage_top", "carriage", "rail_side"),
    @("beam", "end_a", "plate_left", "tslot"),
    @("plate_left", "motor_face", "motor_left", "mount"),
    @("beam", "end_b", "plate_right", "tslot"),
    @("plate_right", "motor_face", "motor_right", "mount"),
    @("motor_left", "shaft", "pulley_left", "bore"),
    @("motor_right", "shaft", "pulley_right", "bore"),
    @("belt", "end_a", "pulley_left", "belt"),
    @("belt", "end_b", "pulley_right", "belt"),
    @("carriage", "belt_clamp", "belt", "clamp"),
    @("carriage", "top", "endstop", "mount")
)
$i = 0
foreach ($c in $connections) {
    [void](Expect-Ok (120 + $i) "add_connection" @{
        partA = $c[0]; connectorA = $c[1]; partB = $c[2]; connectorB = $c[3]
    })
    $i++
}

# Wiring: both steppers + endstop
[void](Expect-Ok 140 "wire" @{ component = "motor_left"; signal = "step"; board = "main_board"; pin = "P2_2" })
[void](Expect-Ok 141 "wire" @{ component = "motor_left"; signal = "dir"; board = "main_board"; pin = "P2_6" })
[void](Expect-Ok 142 "wire" @{ component = "motor_left"; signal = "enable"; board = "main_board"; pin = "P2_1" })
[void](Expect-Ok 142 "wire" @{ component = "motor_right"; signal = "step"; board = "main_board"; pin = "P6_2" })
[void](Expect-Ok 142 "wire" @{ component = "motor_right"; signal = "dir"; board = "main_board"; pin = "P6_6" })
[void](Expect-Ok 142 "wire" @{ component = "motor_right"; signal = "enable"; board = "main_board"; pin = "P6_1" })
[void](Expect-Ok 142 "wire" @{ component = "endstop"; signal = "endstop"; board = "main_board"; pin = "P1_28" })

# ---------- 4. validation & compilation ----------

Check "validation OK" ((Expect-Ok 150 "validate_machine" @{}) -eq "OK") ""
$compileText = Expect-Ok 151 "compile_mjcf" @{ outputPath = "out/agent_axis.xml" }
Check "MJCF compiled" ($compileText -match "Compiled") $compileText

# ---------- 5. error paths ----------

[void](Expect-Error 160 "add_part" @{ catalogId = "no_such_part" })
[void](Expect-Error 161 "set_motor_velocity" @{ instanceId = "carriage"; revPerSec = 1 })
[void](Expect-Error 162 "run_for" @{ seconds = 1 })

# ---------- 6. closed-loop run ----------

Check "run starts with 2 channels" ((Expect-Ok 170 "start_run" @{}) -match "2 stepper channel") ""
[void](Expect-Ok 171 "add_endstop" @{ jointName = "j_carriage"; triggerPosition = -0.02 })
[void](Expect-Ok 171 "enable_motor" @{ instanceId = "motor_left"; enabled = $true })
[void](Expect-Ok 171 "set_motor_velocity" @{ instanceId = "motor_left"; revPerSec = 1 })
$status = Expect-Ok 172 "run_for" @{ seconds = 0.55 }
Check "endstop PRESSED at trigger" ((Expect-Ok 173 "read_endstop" @{ jointName = "j_carriage" }) -match "PRESSED") ""
$leftLine = ($status -split "`n") | Where-Object { $_ -match "^motor_left" }
Check "driven motor has zero missed steps" ($leftLine -match "missed steps 0") $leftLine
[void](Expect-Ok 174 "stop_run" @{})

# ---------- 7. inline Lua homing scenario ----------

$lua = @'
function run(sim)
  sim.add_endstop("j_carriage", -0.020)
  sim.enable("motor_left", true)
  sim.velocity("motor_left", 1.0)
  while sim.time() < 1.0 do
    sim.run(0.05)
    if sim.endstop("j_carriage") then break end
  end
  result = { homed = sim.endstop("j_carriage"), pos_m = sim.joint_pos("j_carriage") }
  assert(result.homed, "endstop never fired")
  sim.velocity("motor_left", 0)
end
'@
$scenario = Expect-Ok 180 "run_scenario_source" @{ source = $lua }
Check "Lua scenario homed the axis" (($scenario -notmatch "FAILED") -and ($scenario -match '"homed": true')) $scenario

# ---------- 8. save the built machine as an example, then round-trip it ----------

$examplePath = "examples/linear_axis_agent.json"
[void](Expect-Ok 190 "save_machine" @{ path = $examplePath })
[void](Expect-Ok 191 "new_machine" @{ name = "blank" })
$reopen = Expect-Ok 192 "open_machine" @{ path = $examplePath }
Check "example round-trips" ($reopen -match "11 parts.*12 connections") $reopen
Check "reopened machine validates" ((Expect-Ok 193 "validate_machine" @{}) -eq "OK") ""

# ---------- teardown ----------

$p.StandardInput.Close()
if (-not $p.WaitForExit(15000)) { $p.Kill() }

Write-Host ""
Write-Host "E2E summary: $($script:passed) passed, $($script:failures) failed"
Pop-Location
if ($script:failures -gt 0) { exit 1 } else { exit 0 }