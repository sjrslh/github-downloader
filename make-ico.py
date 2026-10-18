from PIL import Image
import struct, io, os

root = r"C:\Users\sjrzh\Documents\GitHub下载器"
assets = os.path.join(root, "assets")
img = Image.open(os.path.join(assets, "icon.png")).convert("RGBA")

sizes = [16, 24, 32, 48, 64, 128, 256]
entries = []
for s in sizes:
    im = img.resize((s, s), Image.LANCZOS)
    if s >= 256:
        buf = io.BytesIO()
        im.save(buf, "PNG")
        data = buf.getvalue()
    else:
        px = im.load()
        xor = bytearray()
        for y in range(s - 1, -1, -1):
            for x in range(s):
                r, g, b, a = px[x, y]
                xor += bytes((b, g, r, a))
        and_stride = ((s + 31) // 32) * 4
        and_mask = bytes(and_stride * s)
        header = struct.pack("<IiiHHIIiiII", 40, s, s * 2, 1, 32, 0, len(xor), 0, 0, 0, 0)
        data = header + bytes(xor) + and_mask
    entries.append((s, data))

out = bytearray()
out += struct.pack("<HHH", 0, 1, len(entries))
offset = 6 + 16 * len(entries)
dirs = bytearray()
blobs = bytearray()
for s, data in entries:
    w = 0 if s >= 256 else s
    h = 0 if s >= 256 else s
    dirs += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset)
    blobs += data
    offset += len(data)
out += dirs + blobs

ico_path = os.path.join(assets, "icon.ico")
with open(ico_path, "wb") as f:
    f.write(out)
print("icon.ico", os.path.getsize(ico_path), "bytes,", len(entries), "entries")
