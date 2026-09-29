"""Draws the Play Store icon and feature graphic from the app icon's shapes (Resources/AppIcon/*.svg).

Run from the repo root: python docs/play/make_assets.py
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "docs" / "play" / "assets"
FONTS = ROOT / "src" / "Transcriber.Mobile" / "Resources" / "Fonts"
BLUE = (37, 99, 235)
WHITE = (255, 255, 255)

# appiconfg.svg: seven rounded bars on a 456-unit canvas (x, y, height; all 22 wide, fully rounded).
BARS = [(118, 198, 60), (152, 166, 124), (186, 128, 200), (220, 176, 104), (254, 140, 176), (288, 182, 92), (322, 206, 44)]


def draw_bars(draw: ImageDraw.ImageDraw, scale: float, dx: float, dy: float, color=WHITE) -> None:
    for x, y, h in BARS:
        box = [dx + x * scale, dy + y * scale, dx + (x + 22) * scale, dy + (y + h) * scale]
        draw.rounded_rectangle(box, radius=11 * scale, fill=color)


def icon() -> None:
    # Play masks the corners itself, so the icon is a full square.
    size = 512
    img = Image.new("RGB", (size, size), BLUE)
    draw_bars(ImageDraw.Draw(img), size / 456, 0, 0)
    img.save(OUT / "icon-512.png")


def feature() -> None:
    w, h = 1024, 500
    img = Image.new("RGB", (w, h), BLUE)
    d = ImageDraw.Draw(img)
    # Waveform on the left, vertically centred (the bars span y 128..328 of 456).
    scale = 1.35
    draw_bars(d, scale, 40 - 118 * scale + 40, h / 2 - 228 * scale)
    title = ImageFont.truetype(str(FONTS / "OpenSans-Semibold.ttf"), 84)
    body = ImageFont.truetype(str(FONTS / "OpenSans-Regular.ttf"), 34)
    x = 440
    d.text((x, 150), "Transcriber", font=title, fill=WHITE)
    for i, line in enumerate(["Meetings to Markdown notes,", "on your own Whisper server."]):
        d.text((x, 275 + i * 48), line, font=body, fill=(219, 234, 254))
    img.save(OUT / "feature-1024x500.png")


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    icon()
    feature()
    print("wrote", ", ".join(p.name for p in sorted(OUT.glob("*.png"))))
