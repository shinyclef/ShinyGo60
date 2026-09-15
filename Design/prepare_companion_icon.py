"""Remove the generated checkerboard and export the companion's Windows icon."""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


design = Path(__file__).resolve().parent
source = Image.open(design / "companion-icon-concept-v4-draft.png").convert("RGB")
pixels = list(source.get_flattened_data())
icon = Image.new("RGBA", source.size)

# The three foreground colours are distinct from the neutral checkerboard.
# Refill each mask with a solid colour to avoid grey fringes at its edges.
regions = [
    ((0, 215, 231), lambda r, g, b: g - r > 80 and b - r > 80),
    ((255, 177, 0), lambda r, g, b: r - b > 100 and g - b > 65),
    ((255, 248, 229), lambda r, g, b: r > 235 and g > 225 and b > 190 and r - b > 10),
]
for colour, contains in regions:
    mask = Image.new("L", source.size)
    mask.putdata([255 if contains(*pixel) else 0 for pixel in pixels])
    mask = mask.filter(ImageFilter.MedianFilter(3)).filter(ImageFilter.GaussianBlur(0.6))
    layer = Image.new("RGBA", source.size, (*colour, 255))
    layer.putalpha(mask)
    icon = Image.alpha_composite(icon, layer)

output = design / "companion-icon-v4.png"
icon.save(output)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
icon.save(
    design.parent / "Windows/ShinyGo60.Companion/Assets/Companion.ico",
    sizes=[(size, size) for size in sizes],
)

preview = Image.new("RGB", (560, 240))
draw = ImageDraw.Draw(preview)
for row, background in enumerate(["#20252b", "#f4f6f8"]):
    draw.rectangle((0, row * 120, 560, (row + 1) * 120), fill=background)
    for column, size in enumerate([16, 24, 32, 48, 64]):
        x = 28 + column * 110
        small = icon.resize((size, size), Image.Resampling.LANCZOS)
        preview.paste(small, (x, row * 120 + 16), small)
        draw.text((x, row * 120 + 92), f"{size}px", fill="#87949e")
preview.save(design / "companion-icon-v4-preview.png")

assert icon.getpixel((0, 0))[3] == 0
assert icon.getpixel((420, 760))[3] == 0
assert icon.getpixel((825, 760))[3] == 0
assert icon.getpixel((627, 400))[3] == 255
with Image.open(design.parent / "Windows/ShinyGo60.Companion/Assets/Companion.ico") as saved:
    assert saved.ico.sizes() == {(size, size) for size in sizes}
print(f"Saved {output}; verified transparent background, cutouts, and all nine ICO sizes.")
