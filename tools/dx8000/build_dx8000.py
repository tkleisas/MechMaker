"""Build the first-pass DX8000 visual shell OBJ set.

Run with Blender 4.x:

    blender -b -P tools/dx8000/build_dx8000.py

The meshes are authored in metres in the MechMaker DX8000 frame:
origin = screen centre, +Z = screen normal, +X = toward the head.
They are visual-only assets; catalog/dx8000.json keeps the two-box collision
proxy and marks these meshes collision=false, contributes_mass=false.
"""
import bpy
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "catalog" / "assets" / "dx8000"
OUT.mkdir(parents=True, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)


def rounded_box(name, dims, loc, bevel, segments=4, color=(0.5, 0.5, 0.5, 1)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = bpy.context.object
    obj.name = name
    obj.scale = dims
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel > 0:
        mod = obj.modifiers.new("Bevel", 'BEVEL')
        mod.width = bevel
        mod.segments = segments
        mod.limit_method = 'ANGLE'
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=mod.name)
    mat = bpy.data.materials.new(name + "_mat")
    mat.diffuse_color = color
    obj.data.materials.append(mat)
    return obj


def join(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    obj = bpy.context.object
    obj.name = name
    return obj


def export(obj, filename):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_mesh.stl(
        filepath=os.fspath(OUT / filename),
        use_selection=True,
        global_scale=1.0,
        ascii=False,
        use_mesh_modifiers=True,
        axis_forward='Y',
        axis_up='Z',
    )


# body: thin screen slab + thicker printer/contactless head
slab = rounded_box("slab", (0.150, 0.083, 0.025), (-0.005, 0, -0.0125), 0.006, 4)
head = rounded_box("head", (0.048, 0.083, 0.0625), (0.094, 0, -0.02925), 0.010, 4)
body = join([slab, head], "dx8000_body")

bezel = rounded_box("bezel", (0.142, 0.074, 0.0010), (0.0, 0, 0.0005), 0.002, 3)
screen = rounded_box("screen", (0.136, 0.068, 0.0010), (0.0, 0, 0.0015), 0.0015, 3)
pad = rounded_box("pad", (0.030, 0.040, 0.0010), (0.094, 0, 0.0028), 0.003, 4)

buttons = [
    rounded_box(f"button{i}", (0.012, 0.0030, 0.0040), (x, -0.0420, -0.008), 0.0012, 2)
    for i, x in enumerate((0.025, 0.040, 0.055))
]
buttons_obj = join(buttons, "dx8000_buttons")

details = [
    rounded_box("slot", (0.0020, 0.054, 0.0015), (-0.0805, 0.0, -0.0125), 0.0005, 2),
    rounded_box("usb", (0.008, 0.0020, 0.0030), (-0.055, -0.0420, -0.016), 0.0006, 2),
    rounded_box("scanner", (0.008, 0.008, 0.0010), (0.074, 0.0, 0.0026), 0.0015, 3),
    rounded_box("printerseam", (0.030, 0.0020, 0.0010), (0.094, 0.0, 0.0026), 0.0005, 2),
]
details_obj = join(details, "dx8000_details")

for obj, name in [
    (body, "dx8000_body.stl"),
    (bezel, "dx8000_bezel.stl"),
    (screen, "dx8000_screen.stl"),
    (pad, "dx8000_pad.stl"),
    (buttons_obj, "dx8000_buttons.stl"),
    (details_obj, "dx8000_details.stl"),
]:
    export(obj, name)
    print("exported", name, len(obj.data.vertices), "verts", len(obj.data.polygons), "faces")
