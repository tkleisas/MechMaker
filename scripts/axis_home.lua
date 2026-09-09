-- MechMaker scenario: drive the linear axis, home it onto the endstop, report.
-- Run against examples/linear_axis_v0.json (two belt-coupled NEMA 17 steppers
-- driving a GT2 belt, MGN12 carriage: 40 mm of travel per pulley revolution).

result = {}

function run(sim)
  sim.add_endstop("j_carriage", -0.020)

  sim.enable("motor_left", true)
  sim.velocity("motor_left", 1.0)
  print("homing: moving toward -X at 1 rev/s")

  -- Advance in slices so we can watch the endstop like a host would.
  local deadline = 1.0
  while sim.time() < deadline do
    sim.run(0.05)
    if sim.endstop("j_carriage") then break end
  end

  if sim.endstop("j_carriage") then
    local pos = sim.joint_pos("j_carriage")
    print(string.format("homed at carriage x = %.1f mm", pos * 1000))
    result.homed = true
    result.carriage_pos_m = pos
  else
    print("WARNING: endstop never fired")
    result.homed = false
  end

  -- Stop and back off: reverse until the switch releases.
  sim.velocity("motor_left", -0.2)
  while sim.endstop("j_carriage") do
    sim.run(0.02)
  end
  print(string.format("released endstop, backed off to %.1f mm", sim.joint_pos("j_carriage") * 1000))
  result.backed_off_m = sim.joint_pos("j_carriage")

  sim.velocity("motor_left", 0)
  sim.run(0.1)

  local m = sim.motor("motor_left")
  print(string.format("rotor: commanded %.3f rev, missed steps %d",
    m.commanded_rev, m.missed_steps))
  result.missed_steps = m.missed_steps
  assert(m.missed_steps == 0, "expected zero missed steps")
end