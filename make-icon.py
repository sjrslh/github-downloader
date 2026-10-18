from PIL import Image
import os

root = r"C:\Users\sjrzh\Documents\GitHub下载器"
assets = os.path.join(root, "assets")
src = os.path.join(assets, "cand-hf.webp")

img = Image.open(src).convert("RGBA")
# 1) keep a large PNG for the repo / README
img.save(os.path.join(assets, "icon.png"), "PNG")
# 2) multi-size ICO for the exe
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save(os.path.join(assets, "icon.ico"), format="ICO", sizes=sizes)
print("icon.png", Image.open(os.path.join(assets, "icon.png")).size)
print("icon.ico", os.path.getsize(os.path.join(assets, "icon.ico")), "bytes")
