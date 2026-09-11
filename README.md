# MechMaker

> A LEGO-style builder for electromechanical systems, with a physics engine as its mortar.
> Design a machine from a parts catalog — NEMA steppers, 2020 beams, linear rails, pulleys,
> belts, clamps, endstops — wire it to a simulated printer-class board, and validate it
> before any hardware exists.

Sibling projects: [androidtester](https://github.com/tkleisas/androidtester) (the machine that inspired this)
and [steropes](https://github.com/tkleisas/steropes) (the digital-twin whose lessons shaped the engine seam).

## Idea

One artifact, three frontends:

```
catalog (parts) ──►  machine.json  ◄── visual builder (primary UI)
                     - parts + poses        ──► headless engine (MuJoCo + virtual MCU)
                     - connections          ──► MCP server (LLM agents)
                     - wiring (netlist)     ──► CLI / CI
```

- `machine.json` is the single source of truth: placed parts, typed-connector
  connections, and the electrical netlist. The UI edits it, an LLM can generate it,
  the engine consumes it.
- The engine is headless, deterministic (fixed 1 ms timestep), and fast — the hot
  loop (step pulses → joint torque → sensor lines) is compiled C#, with MuJoCo
  doing the physics.
- Validation output is a structured report (stall margins, belt ratios, unconnected
  connectors, BOM) — machine-readable for CI and LLMs, readable for humans.

## Status — M5 (Klipper host), pre-alpha

What works today:

- **Part catalog** (`catalog/*.json`): 19 seed parts — NEMA 17 stepper, NEMA 17 with
  T8-8 leadscrew + brass nut, 2020 beam, MGN12 rail + carriage, GT2 20T pulley,
  GT2 belt, endstop, corner bracket, mount plate, 20T/40T spur gears, SG90 servo
  (position actuator), 40 mm DC fan (velocity actuator), hotend (lumped thermal
  model), the android_phone HIL device, and the guided T8 nut (rail-carried —
  groundwork for the two-axis gantry).
- **Machine format** (`schema/machine.schema.json`): parts, connections, boards, wiring.
- **Compiler** (`MechMaker.Core`): validates the machine (machine-readable `mmNNN`
  diagnostics — including **mm039 floating connections**: parts mating at connector
  points whose bodies don't touch, caught with rotation-aware bounding boxes) and
  generates a MuJoCo MJCF model — body tree from welds, slide joints
  for carriages and leadscrew nuts, hinge joints with torque actuators for motor rotors
  (with reflected rotor inertia), belt couplers (pulleys mirrored; belt-clamped parts
  ride the belt at pitch_radius × pulley angle), leadscrew couplers (nut = lead/(2π)
  per rotor radian), and gear-mesh couplers (ratio = pitch-radius ratio).
- **Physics runtime** (`MechMaker.Engine`): native MuJoCo 3.11, 8 kHz fixed timestep,
  bitwise deterministic.
- **Virtual MCU + stepper model**: one channel per wired motor; commands ramp at an
  acceleration limit (like firmware); each microstep advances the commanded phase and
  magnetic torque `T_hold · sin(electrical error)` pulls the rotor — so load exceeds
  torque and steps get missed exactly as in hardware. Endstops sample joint positions
  with debouncing. The whole machine runs from `machine.json` via
  `MachineSimulation.FromMachine(...)`.
- **Proven closed loop** (test-verified): the example axis accelerates at 40 rev/s²,
  tracks 1 rev/s within 0.22 steps of lag, moves the carriage 40 mm per pulley
  revolution (correct GT2-20T kinematics), an endstop fires on arrival, commanding
  20 rev/s without ramp stalls the motor with counted missed steps, and repeated
  runs are bitwise identical.
- **Visual builder** (`MechMaker.App`, Avalonia + software-rasterized 3D viewport):
  catalog list, orbit/zoom viewport, part placement, **connector-snapped placement**
  (pick a part connector + a target connector — the pose is computed with the same
  mating convention the compiler uses), selection highlight, pose editing,
  connections, **board wiring UI**, adjustable **reference grid** (color, thickness,
  spacing), and live run mode with adjustable rev/s.
- **Lua scenarios** (`MechMaker.Engine.Scripting`, MoonSharp, sandboxed): deterministic
  scripted simulations — the script plays the host role (drive motors, watch endstops,
  detect stalls) like the C# tests, but authored at runtime. `scripts/axis_home.lua`
  homes the example axis. Run from the app ("Run Script…"), the MCP server, or tests.
- **Klipper MCU protocol** (`MechMaker.Engine.Klipper`): the virtual MCU speaks the real
  klipper wire format — VLQ integers, CRC16-CCITT blocks, ack/nak sequencing, zlib
  data dictionary over identify — so the host role is played by klipper-shaped commands:
  `allocate_oids` → `config_stepper`/`config_endstop` → `finalize_config` →
  `reset_step_clock` → `set_next_step_dir` → `queue_step` → `stepper_get_position`,
  plus `endstop_home` with move-queue halting on trigger and `config_pwm_out` /
  `set_pwm_out` for servo/fan channels. Steps are full steps on an 8 kHz clock;
  queue_step motion reaches the same closed loop as the velocity path (test-verified:
  40 mm/rev carriage travel, homing halt at 20 mm, out-of-order nak handling). Exposed
  to agents via `klipper_connect` / `klipper_send` / `klipper_status`.
- **HIL seam — Android emulator + OpenCV + OCR** (`MechMaker.Hil`): the machine's
  `android_phone` part pairs with an emulator — **live over adb (verified end-to-end
  against a real Android 16 emulator: screencap → template → OpenCV find → tap), with
  a fake transport for offline tests** — so the rig's touch actions drive a real
  Android UI and vision reads the screen, the androidtester/steropes loop, tool by
  tool: `hil_connect` → `screen_screencap` → `screen_find` / `screen_wait_for`
  (OpenCV template matching, fraction coordinates like androidtester's
  `touch.tap {from: [50%, 90%]}`) → `touch_tap` / `touch_swipe`; **OCR via Tesseract**
  (`screen_find_text` / `screen_wait_for_text` — androidtester's pluggable OCR:
  `screen.wait_for {text: ...}`, needs `tools/fetch_tessdata.ps1`). **Physics-contact taps are live and scripted**: a
  `touch_finger` part — androidtester's spring finger as a servo-driven plunger —
  arms over the phone, and commanding its slide servo (`set_finger_position`,
  press −8 mm / retract +2 mm) physically taps the glass; each press dispatches an
  emulator tap at the contact point (one per press episode; hold for long-press
  timing). Servo-on-slide actuators take metre units (`min_pos_m`/`max_pos_m`).
  **The gantry moves the finger**: `examples/phone_gantry_rig.json` mounts the
  finger on a belt-driven carriage (linear_axis mechanics) riding above the phone,
  and **`tap_at(fx)` closes the whole loop**: given a screen fraction (a vision
  result, or an agent's choice), it measures the live geometry, positions the
  gantry (correct-and-retry, converging within ~0.6 mm), presses, and dispatches
  the tap at the *measured* contact point — two taps at different commanded
  columns, test-verified (35% → 35.6%, 65% → within tolerance).
  Rig: `examples/phone_test_rig.json` (minimal) + `phone_gantry_rig.json` (gantry);
  flagship scenario: `tools/scenario_smoke_wake_unlock.ps1` (wake → swipe to unlock →
  OCR-style home assertion via the dock); emulator self-start: `tools/emulator_start.ps1`.
- **CLI**: `dotnet run --project src/MechMaker.Cli -- examples/linear_axis_v0.json out/axis.xml`
- **Tests**: `dotnet test` (202 tests: core math/compile, engine physics, Lua scenarios,
  klipper wire+protocol, PWM components, HIL seam + physics taps + tap_at, transmission
  kinematics, server/session).
- **MCP server** (`MechMaker.Server`): 47 tools over stdio that let LLM agents assemble,
  wire, validate, compile, simulate, render, and drive real Android UIs — catalog
  browsing, part placement, typed-connector connections, board wiring, `mmNNN`
  validation diagnostics, MJCF compilation, live closed-loop runs (motors, endstops,
  stalls), scene rendering, and the HIL action loop.

A walkthrough with verified numbers and screenshots: **[docs/01-analysis.md](docs/01-analysis.md)**.

Two modeling lessons are baked in and documented in code: the physics runs at 8 kHz
because the stepper's magnetic spring is stiff, and the ground plane is visual-only
(assemblies are bolted to their own structure — a colliding floor shreds any shaft
that sits at z=0).

### Wiring the MCP server into a client

```json
{
  "mcpServers": {
    "mechmaker": {
      "command": "dotnet",
      "args": ["run", "--project", "src/MechMaker.Server", "--no-build"],
      "cwd": "/path/to/MechMaker"
    }
  }
}
```

The catalog directory is found by walking up from the working directory (or set
`MECHMAKER_CATALOG`). Typical agent flow: `list_catalog_parts` → `add_part` /
`add_connection` / `add_board` / `wire` → `validate_machine` → `start_run` →
`enable_motor` + `set_motor_velocity` → `run_for` / `read_endstop` → `save_machine`.

## Roadmap

- ~~**M3 — Visual builder**~~ **done**: Avalonia + software 3D viewport; place, snap,
  wire, run (`src/MechMaker.App`).
- ~~**M4 — MCP server**~~ **done**: LLM agents assemble/wire/validate/simulate machines
  through 40 tools over stdio (`src/MechMaker.Server`).
- **M6 — Emulator/vision HIL seam**: **done** (live adb, OpenCV vision, physics-contact
  taps, scripted finger presses, smoke_wake_unlock on live Android).
- **M5 — Real Klipper host** on the virtual MCU: **the protocol layer is done** (wire
  format, data dictionary, queue_step on the live loop, endstop homing, PWM out).
  Catalog growth: **leadscrews, gears, servos, fans, hotends done**. Remaining:
  embedding the actual klipper host binary (klippy) against the simulated transport.

## Layout

```
catalog/                 part definitions (JSON)
examples/                example machines (incl. linear_axis_agent.json, assembled end-to-end via MCP tools)
docs/                    01-analysis.md — architecture & verified behaviour (with rendered images)
scripts/                 Lua simulation scenarios
tools/                   mcp_e2e, hil_live, scenario_smoke_wake_unlock (stdio e2e) + emulator_start + render
schema/                  machine definition JSON schema
src/MechMaker.Core       model, validator, MJCF compiler
src/MechMaker.Engine     (M1/M2) MuJoCo runtime + virtual MCU + Lua + Klipper MCU
src/MechMaker.Hil        (M6) Android emulator + OpenCV seam
src/MechMaker.Cli        machine.json -> MJCF compiler CLI
src/MechMaker.Server     (M4/M5) MCP host
src/MechMaker.App        (M3) Avalonia visual builder
tests/                   xUnit tests
```

## License

MIT — do what you want, attribution appreciated.

