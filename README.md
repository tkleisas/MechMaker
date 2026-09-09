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

## Status — M3 (visual builder), pre-alpha

What works today:

- **Part catalog** (`catalog/*.json`): 10 seed parts — NEMA 17 stepper (with rotor
  sub-body), 2020 beam, MGN12 rail + carriage (with belt clamp), GT2 20T pulley,
  GT2 belt, microswitch endstop, 2020 corner bracket, NEMA 17 mount plate.
- **Machine format** (`schema/machine.schema.json`): parts, connections, boards, wiring.
- **Compiler** (`MechMaker.Core`): validates the machine (machine-readable `mmNNN`
  diagnostics) and generates a MuJoCo MJCF model — body tree from welds, slide joints
  for carriages, hinge joints with torque actuators for motor rotors (with reflected
  rotor inertia), belt couplers (pulleys mirrored; belt-clamped parts ride the belt
  at pitch_radius × pulley angle).
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
- **CLI**: `dotnet run --project src/MechMaker.Cli -- examples/linear_axis_v0.json out/axis.xml`
- **Tests**: `dotnet test` (109 tests: core math/compile, engine physics, Lua scenarios,
  server/session).
- **MCP server** (`MechMaker.Server`): 27 tools over stdio that let LLM agents assemble,
  wire, validate, compile, and simulate machines — catalog browsing, part placement,
  typed-connector connections, board wiring, `mmNNN` validation diagnostics, MJCF
  compilation, and live closed-loop runs (enable/command motors, add endstops, watch
  for stalls).

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
  through 27 tools over stdio (`src/MechMaker.Server`).
- **M5 — Real Klipper host** on the virtual MCU (host sends step commands over the
  Klipper MCU protocol instead of velocity targets); catalog growth (leadscrews,
  servos, gears, fans, hotends...). The Lua scenario runner is the first taste of
  M5's "homing scenarios on the closed loop".

## Layout

```
catalog/                 part definitions (JSON)
examples/                example machines (incl. linear_axis_agent.json, assembled end-to-end via MCP tools)
scripts/                 Lua simulation scenarios
tools/                   mcp_e2e.ps1 — self-checking MCP stdio end-to-end test
schema/                  machine definition JSON schema
src/MechMaker.Core       model, validator, MJCF compiler
src/MechMaker.Engine     (M1/M2) MuJoCo runtime + virtual MCU
src/MechMaker.Cli        machine.json -> MJCF compiler CLI
src/MechMaker.Server     (M4/M5) API + MCP host
src/MechMaker.App        (M3) Avalonia visual builder
tests/                   xUnit tests
```

## License

MIT — do what you want, attribution appreciated.
