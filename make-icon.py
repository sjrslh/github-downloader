from PIL import Image, ImageDraw, ImageFilter
import os

root = r"C:\Users\sjrzh\Documents\GitHub下载器"
assets = os.path.join(root, "assets")
src = os.path.join(assets, "icon-src.webp")

img = Image.open(src).convert("RGBA")
size = img.size[0]
assert img.size[0] == img.size[1], "icon source must be square"

# The AI image is a rounded square painted on white. Cut the white corners out
# with an anti-aliased rounded-rect mask so the icon is transparent outside.
scale = 4
radius_ratio = 0.295  # measured from the source art
mask = Image.new("L", (size * scale, size * scale), 0)
draw = ImageDraw.Draw(mask)
r = int(size * radius_ratio) * scale
draw.rounded_rectangle([0, 0, size * scale - 1, size * scale - 1], radius=r, fill=255)
mask = mask.resize((size, size), Image.LANCZOS)

out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
out.paste(img, (0, 0), mask)
out.save(os.path.join(assets, "icon.png"), "PNG")

# quick sanity check
px = out.load()
print("corner alpha:", [px[2, 2][3], px[size - 3, 2][3], px[2, size - 3][3], px[size - 3, size - 3][3]])
print("center alpha:", px[size // 2, size // 2][3])
print("icon.png", os.path.getsize(os.path.join(assets, "icon.png")), "bytes")
