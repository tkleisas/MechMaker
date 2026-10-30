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
composed = []
for step in steps:
    canvas = Image.new("RGB", (W, H), BG)
    draw = ImageDraw.Draw(canvas)
    draw.text((60, 38), "DX8000 physical PIN entry — MechMaker HIL",
              font=font(44, True), fill=FG)
    draw.text((60, 98), "Physical finger · emulated POS app · tap_at contact dispatch",
              font=font(24), fill=MUT)

    robot = Image.open(SRC / f"robot_{step['frame']:02d}.png").convert("RGB").resize((800, 800))
    screen = Image.open(SRC / f"screen_{step['frame']:02d}.png").convert("RGB").resize((500, 1000))
    closeup = Image.open(SRC / f"closeup_{step['frame']:02d}.png").convert("RGB").resize((280, 280))
    canvas.paste(robot, (55, 160))
    canvas.paste(screen, (960, 120))

    inset = (75, 660)
    canvas.paste(closeup, inset)
    draw.rectangle([inset[0] - 2, inset[1] - 2,
                    inset[0] + 282, inset[1] + 282], outline=(90, 96, 108), width=2)
    draw.rectangle([inset[0], inset[1] + 244, inset[0] + 280, inset[1] + 280],
                   fill=(20, 22, 26))
    draw.text((inset[0] + 8, inset[1] + 250), "finger / Y-nut close-up",
              font=font(18), fill=MUT)

    draw.text((960, 88), "Emulated DX8000 app", font=font(28, True), fill=FG)
    draw.text((70, 980), step["label"], font=font(28, True), fill=FG)
    draw.text((70, 1028),
              f"PIN buffer: {step['pin'] or '----'}   accepted: {step['accepted']}   taps: {step.get('taps', 0)}",
              font=font(24), fill=MUT)
    draw.text((70, 1080),
              f"frame {step['frame']:03d} · captured from the live physics run",
              font=font(20), fill=MUT)

    if step.get("fx") is not None:
        cx = 960 + step["fx"] * 500
        cy = 120 + step["fy"] * 1000
        for r, width in ((28, 5), (40, 2)):
            draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline=HOT, width=width)

    path = SRC / f"frame_{step['frame']:03d}.png"
    canvas.save(path)
    composed.append(path)

# MP4: 2x playback at 30 fps. GIF: every second frame at 30 fps display, downscaled.
if shutil.which("ffmpeg"):
    mp4 = SRC / "pin_entry.mp4"
    subprocess.run([
        "ffmpeg", "-y", "-framerate", "30", "-i", str(SRC / "frame_%03d.png"),
        "-vf", "setpts=0.5*PTS,pad=ceil(iw/2)*2:ceil(ih/2)*2",
        "-r", "30", "-c:v", "libx264", "-pix_fmt", "yuv420p", str(mp4),
    ], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print("wrote", mp4)

gif_images = []
for p in composed[::4]:
    image = Image.open(p).convert("RGB").resize((1000, 676))
    gif_images.append(image.convert("P", palette=Image.Palette.ADAPTIVE))
gif = SRC / "pin_entry.gif"
gif_images[0].save(gif, save_all=True, append_images=gif_images[1:],
                   duration=[33] * len(gif_images), loop=0, optimize=False)
print("wrote", gif)


def last_with_pin(pin):
    matches = [i for i, s in enumerate(steps) if s["pin"] == pin]
    return matches[-1] if matches else None


# Contact sheet: Ready + one contact frame per tap.
chosen = [0]
for n in range(1, 6):
    idx = next((i for i, s in enumerate(steps) if s.get("taps", 0) >= n), None)
    if idx is not None:
        chosen.append(idx)
chosen = chosen[:6]

thumbs = [Image.open(composed[i]).convert("RGB") for i in chosen]
thumb_w = 820
thumb_h = int(thumbs[0].height * thumb_w / thumbs[0].width)
gap = 20
sheet = Image.new("RGB", (3 * thumb_w + 4 * gap, 110 + 2 * thumb_h + 3 * gap), BG)
d = ImageDraw.Draw(sheet)
d.text((gap, 30), "DX8000 physical PIN entry — key frames", font=font(42, True), fill=FG)
d.text((gap, 78), "Ready plus the contact frame for each physical tap.",
       font=font(24), fill=MUT)
for i, image in enumerate(thumbs):
    row, col = divmod(i, 3)
    x = gap + col * (thumb_w + gap)
    y = 110 + gap + row * (thumb_h + gap)
    sheet.paste(image.resize((thumb_w, thumb_h)), (x, y))
sheet_path = SRC / "pin_entry_contact_sheet.png"
sheet.save(sheet_path)
print("wrote", sheet_path)

# Close-up contact sheet: inspect the finger/Y-nut attachment at the same frames.
close_thumbs = [Image.open(SRC / f"closeup_{steps[i]['frame']:02d}.png").convert("RGB")
                for i in chosen]
close_w = 500
close_h = 500
close_sheet = Image.new("RGB", (3 * close_w + 4 * gap, 100 + 2 * close_h + 3 * gap), BG)
cd = ImageDraw.Draw(close_sheet)
cd.text((gap, 24), "Finger / Y-nut attachment — contact frames", font=font(38, True), fill=FG)
for i, image in enumerate(close_thumbs):
    row, col = divmod(i, 3)
    x = gap + col * (close_w + gap)
    y = 100 + gap + row * (close_h + gap)
    close_sheet.paste(image.resize((close_w, close_h)), (x, y))
    cd.text((x + 8, y + close_h - 28), steps[chosen[i]]["label"], font=font(16), fill=FG)
close_path = SRC / "pin_entry_closeup_sheet.png"
close_sheet.save(close_path)
print("wrote", close_path)
