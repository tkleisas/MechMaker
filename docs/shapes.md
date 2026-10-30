# Shapes

MechMaker parts are built from one or more shapes on each body. Multiple shapes on
one body are the compound-shape mechanism: a bracket with a box body, cylinder
bosses and a mesh shell is normal.

The catalog JSON uses `kind` plus either `extents` (primitives) or `mesh_file`
(mesh). All lengths are metres.

## Supported kinds

| `kind` | Dimensions | MuJoCo geom | Volume used for mass |
|---|---|---|---|
| `box` | `extents` = full X/Y/Z | `box` | `x*y*z` |
| `cylinder` | `extents.x` = radius, `extents.y` = full length | `cylinder` (local Z) | `πr²L` |
| `sphere` | `extents.x` = radius | `sphere` | `4/3πr³` |
| `capsule` | `extents.x` = radius, `extents.y` = cylindrical length | `capsule` (local Z) | `πr²L + 4/3πr³` |
| `ellipsoid` | `extents` = radii X/Y/Z | `ellipsoid` | `4/3πabc` |
| `mesh` | `mesh_file`, `mesh_scale`; `extents` ignored | `mesh` | signed mesh volume |

`relative_pose` places the shape on its body. `rgba` is the render colour.

## Mesh shapes

```json
{
  "kind": "mesh",
  "mesh_file": "assets/dx8000_shell.stl",
  "mesh_scale": [0.001, 0.001, 0.001],
  "collision": true,
  "visible": true,
  "contributes_mass": true,
  "smooth_normal": false,
  "rgba": "0.18 0.18 0.20 1"
}
```

- `mesh_file` is relative to the catalog directory, or absolute.
- `mesh_scale` converts file units to metres. A millimetre STL uses `0.001`.
- `collision: false` makes the geom visual-only (`contype=0 conaffinity=0`).
  Use this for decorative shells or parts you never want to collide.
- `visible: false` makes the geom a collision-only proxy. It still contacts and
  carries mass, but the native renderer and browser viewer skip it.
- `contributes_mass: false` emits the geom with mass 0 and excludes it from the
  body's mass/volume distribution. Use `collision: false` + `contributes_mass: false`
  for a detailed visual shell riding a simplified collision proxy.
- `smooth_normal: true` asks MuJoCo to use smooth vertex normals.
- STL and OBJ are accepted. MuJoCo's STL decoder is binary-only, so MechMaker
  converts ASCII STL to a content-addressed binary cache file automatically.
- MuJoCo uses the **convex hull** for mesh collision. Use a compound of convex
  pieces if the collision shape needs concavity.
- Mesh mass is distributed by signed volume. Open/non-watertight meshes produce a
  warning (`mm043`) and fall back to bounding-box volume for mass estimation.
- A missing or unreadable mesh is a validation error (`mm042`).

## Validation

Primitive dimensions must be positive (`mm044`). Mesh shapes must name an existing
file and have non-zero scale. The compiler emits an `<asset><mesh>` entry before the
worldbody, with an absolute path so the MJCF can be loaded from a temp file.

## Rendering

- The native software renderer tessellates boxes, cylinders, spheres, capsules,
  ellipsoids and real mesh triangles.
- The browser viewer draws primitives exactly and uses an axis-aligned box proxy
  for meshes (the native renderer is the exact path).
- `Simulator.GetScene()` refreshes kinematics first, so callers do not need a
  separate `RefreshKinematics()` for a correct pose.
- MuJoCo recenters a mesh asset on its inertia frame and aligns its principal axes,
  so the compiled `geom_xmat` is not the authored catalog pose. MechMaker
  reconstructs the authored frame from the body pose, authored geom pose and the
  model's `mesh_pos`/`mesh_quat`, and offsets the raw mesh by its AABB centre.
  Visual meshes therefore stay where the catalog placed them.

## Not yet supported

- Heightfields (`hfield`)
- Signed-distance-field collision
- General planes as part geometry (the ground plane is emitted by the compiler)
- Texture/material assets

These can be added on the same seam: a shape kind, a volume/AABB rule, an MJCF geom
emission, and a renderer case.

## HIL devices

A part that can act as the Android device under test declares:

```json
"hil_device": "android",
"params": {
  "screen_width_m": 0.068,
  "screen_height_m": 0.136,
  "screen_plane_z_m": 0.0,
  "resolution_x": 720,
  "resolution_y": 1440
}
```

The HIL session finds the device by `hil_device`, and both the physics tap bridge
and `tap_at` read the screen width/height/plane from the catalog. The device root
is assumed to be the screen centre; the screen plane offset is along local +Z.
This lets `android_phone` (screen at +2 mm) and `dx8000` (screen at 0) share the
same loop.
