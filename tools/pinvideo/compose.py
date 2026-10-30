"""Compose the PIN-entry frame sequence from tools/pinvideo/out.

Requires Pillow and ffmpeg (MP4 is skipped if ffmpeg is unavailable).

    python3 tools/pinvideo/compose.py
"""
import json
import shutil
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
SRC = HERE / "out"
steps = json.loads((SRC / "steps.json").read_text())

BG = (30, 32, 36)
FG = (240, 243, 247)
MUT = (160, 168, 178)
HOT = (255, 145, 70)


def font(size, bold=False):
    name = "DejaVuSans-Bold.ttf" if bold else "DejaVuSans.ttf"
    return ImageFont.truetype(f"/usr/share/fonts/truetype/dejavu/{name}", size)


W, H = 1700, 1150
frames = []
for step in steps:
    canvas = Image.new("RGB", (W, H), BG)
    draw = ImageDraw.Draw(canvas)
    draw.text((60, 38), "DX8000 physical PIN entry — MechMaker HIL",
              font=font(44, True), fill=FG)
    draw.text((60, 98), "Physical finger · emulated POS app · tap_at contact dispatch",
              font=font(24), fill=MUT)

    robot = Image.open(SRC / f"robot_{step['frame']:02d}.png").convert("RGB").resize((800, 800))
    screen = Image.open(SRC / f"screen_{step['frame']:02d}.png").convert("RGB").resize((500, 1000))
    canvas.paste(robot, (55, 160))
    canvas.paste(screen, (960, 120))

    draw.text((960, 88), "Emulated DX8000 app", font=font(28, True), fill=FG)
    draw.text((70, 980), step["label"], font=font(28, True), fill=FG)
    draw.text((70, 1028),
              f"PIN buffer: {step['pin'] or '----'}   accepted: {step['accepted']}",
              font=font(24), fill=MUT)
    draw.text((70, 1080),
              "Each frame is a measured physical tap on the rig; the screen is the emulator state after the tap.",
              font=font(20), fill=MUT)

    if step.get("fx") is not None:
        cx = 960 + step["fx"] * 500
        cy = 120 + step["fy"] * 1000
        for r, width in ((28, 5), (40, 2)):
            draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=HOT, width=width)

    path = SRC / f"frame_{step['frame']:02d}.png"
    canvas.save(path)
    frames.append(path)

images = [Image.open(p).convert("P", palette=Image.Palette.ADAPTIVE) for p in frames]
gif = SRC / "pin_entry.gif"
images[0].save(gif, save_all=True, append_images=images[1:],
               duration=[1200] * len(images), loop=0, optimize=False)

if shutil.which("ffmpeg"):
    mp4 = SRC / "pin_entry.mp4"
    subprocess.run([
        "ffmpeg", "-y", "-framerate", "1/1.2", "-i", str(SRC / "frame_%02d.png"),
        "-c:v", "libx264", "-pix_fmt", "yuv420p",
        "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", str(mp4),
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print("wrote", mp4)

# Contact sheet: 3 columns x 2 rows.
frame_images = [Image.open(p).convert("RGB") for p in frames]
thumb_w = 820
thumb_h = int(frame_images[0].height * thumb_w / frame_images[0].width)
gap = 20
sheet = Image.new("RGB", (3 * thumb_w + 4 * gap, 110 + 2 * thumb_h + 3 * gap), BG)
d = ImageDraw.Draw(sheet)
d.text((gap, 30), "DX8000 physical PIN entry — frame sequence", font=font(42, True), fill=FG)
d.text((gap, 78), "Four digits + OK, each from a measured physical tap on the MechMaker HIL rig.",
       font=font(24), fill=MUT)
for i, image in enumerate(frame_images):
    row, col = divmod(i, 3)
    x = gap + col * (thumb_w + gap)
    y = 110 + gap + row * (thumb_h + gap)
    sheet.paste(image.resize((thumb_w, thumb_h)), (x, y))
sheet_path = SRC / "pin_entry_contact_sheet.png"
sheet.save(sheet_path)

print("wrote", gif)
print("wrote", sheet_path)
