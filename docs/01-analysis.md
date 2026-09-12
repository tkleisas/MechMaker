# MechMaker — Architecture & Verified Behaviour

> Every number in this document is asserted by a test. Run `dotnet test` (204 tests).

One artifact — `machine.json` — drives three frontends: the visual builder (M3),
the MCP server for LLM agents (M4), and the headless engine everything shares.

```
catalog (parts) ──►  machine.json  ◄── visual builder (Avalonia, M3)
                     - parts + poses        ──► MuJoCo physics (8 kHz) + virtual MCU
                     - connections          ──► MCP server, 43 tools for LLM agents (M4)
                     - wiring (netlist)     ──► Klipper wire protocol (M5) · HIL emulator (M6)
```

## The engine (M1/M2)

- MuJoCo 3.11 through Evergine.Bindings, **8 kHz fixed timestep, bitwise
  deterministic** (repeated runs assert equal state vectors).
- One virtual-MCU stepper channel per wired motor: commanded phase advances in
  microsteps; magnetic torque `T_hold · sin(electrical error)` pulls the rotor —
  so overloading the motor misses steps exactly like hardware.

Verified on the example belt axis (`examples/linear_axis_v0.json`, GT2-20T):

| Claim | Test-verified value |
|---|---|
| Ramp-following | 40 rev/s² ramp to 1 rev/s, **0.22 steps of lag**, zero missed steps |
| Kinematics | carriage travels **40 mm per pulley revolution** |
| Homing | endstop fires at the 20 mm trigger, debounced |
| Stall physics | 20 rev/s without ramp → stall with counted missed steps |
| Determinism | repeated runs bitwise identical |

![linear axis](img/linear_axis.png)

*The example axis: 2020 beam, MGN12 rail, belt-driven carriage, NEMA 17s with GT2
pulleys at the ends (yellow), endstop (green).*

## Couplers (M5 catalog)

Three kinematic couplings, each closed-loop tested:

| Coupling | Parts | Verified behaviour |
|---|---|---|
| Belt | pulleys + belt + clamp | 40 mm/rev carriage, stall detection, back-driving a de-energised slave is *not* a fault |
| Leadscrew | T8-8 screw + nut | **8 mm of nut travel per revolution** |
| Gear mesh | 20T/40T spur gears | half-speed ratio across the mesh |

![gear train](img/gear_train.png)
![leadscrew](img/leadscrew.png)

*Left: the 20T:40T gear train (mesh is a hinge-to-hinge ratio coupler). Right: the
leadscrew Z axis — one revolution of the screw advances the brass nut 8 mm.*

## Servos, fans, heat

- Servos are position actuators (hinge: degrees; slide: metres —
  `min_pos_m`/`max_pos_m`), damped ζ≈1 so commands converge in ~100 ms.
- DC components (40 mm fan) track duty × rated rpm.
- The hotend is the engine's first non-mechanical state: a lumped thermal model
  (25 W vs Newton cooling, τ ≈ 43 s — real hotends heat slowly too), integrated
  at the physics timestep, deterministic like everything else.

## Klipper MCU protocol (M5)

The virtual MCU speaks the real wire format — VLQ integers (ported byte-for-byte
from `klippy/msgproto.py`), CRC16-CCITT blocks, ack/nak sequencing, zlib data
dictionary over `identify`. The host role is klipper-shaped commands:

`allocate_oids` → `config_stepper`/`config_endstop` → `finalize_config` →
`reset_step_clock` → `set_next_step_dir` → `queue_step` → `stepper_get_position`,
plus `endstop_home` (move-queue halting on trigger) and `config_pwm_out`/
`set_pwm_out` for servos and fans.

Verified over the wire: queue_step drives the same closed loop as the velocity
path (40 mm/rev again), `endstop_home` halts the queued move at the 20 mm trigger,
out-of-order blocks are nakked and retransmits accepted.

## HIL seam — Android emulator + OpenCV (M6)

The machine's `android_phone` part pairs with an emulator:

- **fake transport** — renders synthetic launcher screens, records taps; tests run
  real OpenCV template matching with no adb;
- **live adb** — verified end-to-end against a real Android 16 emulator
  (`tools/hil_live.ps1`).

The flagship scenario, `tools/scenario_smoke_wake_unlock.ps1`: wake → swipe to
unlock → home assertion by template → tap where vision pointed. All on live
Android, entirely through MCP tools.

![emulator home](img/emulator_home.png)

*The emulator under test (Android 16, API 36): the HIL seam drives this UI with
physics taps and reads it with OpenCV.*

## The gantry and `tap_at` — the full loop (M7, two-axis)

`examples/phone_gantry_rig.json` elevates the gantry above the deck: the phone
lies on a plate at the beam centre, the X rail rides on stacked corner-bracket
standoffs, and a Y stage hangs from the X carriage — a yaw-90 rail with a guided
T8 nut, driven by a leadscrew motor mounted at the rail's end. The `touch_finger`
hangs from the nut; its servo plunger descends to tap the glass.

**`tap_at(fx, fy)` closes vision → motion → contact on both axes**: it measures
the live scene (arm offset, glass plane, press depth — no catalog constants),
servos both stages continuously (no stop-and-settle — the belt/leadscrew
equality's recoil would eat the correction), presses with a measured slide
direction (the mount decides which sign presses), and reports the fraction the
tip *actually pressed*. The physics confirms it end-to-end: commanded (35%, 50%)
and (65%, 50%) each dispatch exactly one emulator tap at (35%, 48.7%) and
(64%, 48.4%).

Three rig-design traps fell to the closed loop:

1. **The arm sweeps through whatever drives the Y stage** — the finger's
   horizontal arm reaches ±54 mm in X and crosses every fixed Y position it can
   travel to; a carriage-mounted Y motor is guaranteed collision. The motor now
   mounts at the Y rail's *end* (max arm reach in screen space: y=+0.022 of the
   ±0.2 rail span).
2. **The X rail crosses the work area** — the finger's 34 mm drop puts the tip's
   travel band inside the rail's band, so a tap crossing the rail's y-position
   must duck. `tap_at` plungers down to 3 mm above the glass before traversing
   (tip top clears the rail bottom), then presses from there.
3. **A coplanar box contact glues a carriage** — the catalog carriage now floats
   its block half a millimetre inside its own body (a real MGN12 carriage wraps
   the rail on bearing blocks), instead of changing connector geometry (which
   the mating-math test guards).

![gantry rig](img/phone_gantry.png)

*The two-axis gantry rig: the phone on the deck, the elevated X rail on its
standoff stack, the Y rail and end-mounted leadscrew, the finger arming over
the screen.*

![rig close-up](img/phone_rig.png)

*The minimal rig: finger arming over the phone, side by side.*

### Floating connections: parts that mate at points but hang in the air

The connector system mates parts at *points* — nothing guaranteed the parts'
**bodies** actually touch. Measuring every connection in every rig with
rotation-aware bounding boxes found real offenders: pulleys floating 2 mm off
their motor shafts, the belt's visual strap 60 mm away from the pulleys it
couples, the belt clamp 17 mm off the belt, and the leadscrew nut riding 12 mm
of air past the end of its screw.

Fixes landed:
- the belt's strap now renders **between the pulleys it couples** (computed from
  their compiled positions, oriented along the line between them) instead of at
  an authored pose;
- the leadscrew's screw connector moved to mid-screw, so the nut lands on the
  metal instead of 12 mm past its end;
- and a new validator diagnostic, **mm039**, warns whenever a connection's
  bodies don't come within 2 mm of each other — rotation-aware (cylinders
  contribute radius on X/Y, half-length on Z), so rotated parts measure
  correctly. The gear_train's schematic gear placement is honestly flagged; the
  strap-spanning belt connections are exempt as a documented M0 simplification.

### The Y axis: a real rig-design lesson

A two-axis attempt (a T8 leadscrew stage stacked on the X carriage, the finger
hanging from its nut) taught a mechanism lesson the hard way. The build revealed
three things, each caught by the closed loop:

1. **Mount frames compound** — every connector mating composes a rotation; the
   finger's orientation is the product of the whole chain from the root part.
   Hand-deriving the adapter euler fails; the honest approach is measuring body
   quaternions from the compiled model (`GetBodyQuaternion`) and solving for the
   adapter — implemented in the Simulator for exactly this.
2. **A leadscrew nut spins with its screw.** The Y stage's nut (and the finger
   hanging from it) is free to yaw around the screw axis — under the gantry's
   X-acceleration the whole arm swings ±27 mm and jams the belt (400+ missed
   steps). A real rig constrains the nut with a second rail or keyway.
3. **The press axis sign is mating-dependent** — the plunger's slide axis in
   world flips with the mount; a positioning controller must measure the axis
   direction and not assume a sign.

The 2-axis stage is therefore parked until the catalog grows a guided-carriage
pair (or a keyway/anti-rotation connector) — the physics is already honest about
why the naive rig fails, which is exactly what a digital twin is for.

### Round three: the elevated frame, and the duck-and-debounce lessons

The structural wall fell in M7: the gantry now elevates on a standoff stack
(two corner brackets), holding the rail over the deck-mounted phone — a compact
version of androidtester's deck/gantry separation. Both axes drive taps; the
guided nut carries the finger without yaw. What remained was a *mechanics*
clean-up the closed loop exposed:

- **The press bounced**: a long fall onto the glass rebounds, and each rebound
  counted as a fresh tap. Landing now happens in two steps (settle 1 mm above
  the kiss, then push 2 mm past), and the dispatch bridge ignores re-contact
  inside a 100 ms refractory window — one tap per press again.
- **The press's sign is mount-dependent, and its *offset* too**: the Y stage's
  mounting flips the plunger axis (positive slide presses), and the compiled
  geometry (catalog extents halve into MJCF sizes) makes the contact point
  flush with the tip body. `tap_at` probes the slide with a small nudge and
  derives direction and offset from the measured slope — no catalog constants.
- **Stop-and-settle recoil cancels slow corrections**: the belt's (and the
  leadscrew's) equality springs unwind when the loop stops to settle, so both
  converge loops now drive continuously and only stop inside tolerance.

![rig close-up](img/phone_rig.png)

*The two-axis rig: elevated rail on the standoff stack, Y rail across it with
the end-mounted leadscrew, finger arming over the deck-mounted phone.*

## Lessons the tests forced us to learn (so you don't have to)

1. **8 kHz, not 1 kHz**: the stepper's magnetic spring is stiff; coarser
   zero-order-hold updates make it explode. The whole machine runs at the physics
   rate.
2. **The floor is visual-only**: assemblies are bolted to their own structure; a
   colliding ground plane shreds any shaft sitting at z=0.
3. **Energising every stepper of a belt loop makes them fight**: channels start
   de-energised; the host drives one motor and the belt back-drives the released
   slave (back-driving is not a fault — no missed steps).
4. **A position servo must be stiff enough to hold its payload**: kp=2 sagged
   20 mm (the plunger never left the glass); kp=80 sags 0.02 mm.
5. **MuJoCo cylinders take radius + FULL half-length** (the compiler halves the
   Y extent) and their axis is local **Z** — a "vertical plunger" authored with
   `rot [90,0,0]` maps Z→−Y and lies on its side.
6. **`adb exec-out` corrupts binary on Windows** (CRLF translation): screencap
   goes device-file → `adb pull` → temp file, byte-exact.
7. **Solid-colour templates have zero variance** — normalized cross-correlation
   degenerates; test icons carry internal structure.
8. **Simulator body positions are zero until kinematics run** — anything reading
   the compiled pose must refresh first.
9. **Coplanar box-on-box contact is glue**: μ×(static friction on a full-face
   contact) outdrags a soft kinematic coupler; model sliding parts with a hair
   of clearance instead of changing connector geometry.
10. **Catalog extents halve into MJCF sizes** — every "contact depth" derived
    from the catalog must be checked against the compiled scene, not the JSON.
11. **Contact episodes need a refractory**: edges alone count a bounce as two
    taps; re-arm only after a sustained (100 ms) clear interval.
