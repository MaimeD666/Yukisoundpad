"""Regenerate the application icon (development only; requires Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw

image = Image.new("RGBA", (256, 256))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((8, 8, 248, 248), radius=64, fill="#B9F58B")
for x, height in [(62, 62), (102, 130), (142, 96), (182, 44)]:
    draw.rounded_rectangle((x, 128-height//2, x+16, 128+height//2), radius=8, fill="#1B2917")
image.save(Path(__file__).resolve().parents[1] / "src" / "app.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
