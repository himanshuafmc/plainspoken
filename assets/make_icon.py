"""Builds assets/plainspoken.ico (and a 256 px PNG) from assets/plainspoken.svg.
With --social it instead renders assets/social-preview.svg to social-preview.png (1280x640),
the image GitHub shows when the repository link is shared.

Renders the SVG once at 1024 px with headless Chromium, then downsamples it (area average,
premultiplied alpha) to the usual Windows icon sizes and packs them as PNG-in-ICO.
No Python packages needed. Run:  python3 assets/make_icon.py [--social]
Set CHROME=/path/to/chrome if Chromium isn't found automatically.
"""
import glob, os, shutil, struct, subprocess, sys, tempfile, zlib

HERE = os.path.dirname(os.path.abspath(__file__))
SVG = os.path.join(HERE, "plainspoken.svg")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SRC = 1024


def find_chrome():
    for c in [os.environ.get("CHROME"), *glob.glob("/opt/pw-browsers/chromium-*/chrome-linux/chrome"),
              shutil.which("chromium"), shutil.which("chromium-browser"), shutil.which("google-chrome")]:
        if c and os.path.exists(c):
            return c
    sys.exit("Chromium not found; set CHROME=/path/to/chrome")


def render(tmp, svg=SVG, width=SRC, height=SRC):
    """Returns (width, height, RGBA rows). Headless Chromium's viewport is shorter than its window,
    so the window is made taller than needed and the result cropped."""
    html = os.path.join(tmp, "render.html")
    with open(html, "w") as f:
        f.write(f'<html><body style="margin:0;background:transparent"><img src="file://{svg}" '
                f'style="width:{width}px;height:{height}px;display:block"></body></html>')
    out = os.path.join(tmp, "big.png")
    subprocess.run([find_chrome(), "--headless=new", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                    "--default-background-color=00000000", f"--window-size={width},{height + 300}", f"--screenshot={out}",
                    "file://" + html], check=True, capture_output=True)
    w, h, rows = decode_png(open(out, "rb").read())
    assert w == width and h >= height, f"unexpected screenshot size {w}x{h}"
    return width, height, rows[:height]


def decode_png(data):
    """Minimal decoder for 8-bit RGBA, non-interlaced PNGs (what Chromium writes)."""
    w, h, depth, ctype, _, _, interlace = struct.unpack(">IIBBBBB", data[16:29])
    assert depth == 8 and ctype == 6 and interlace == 0, "expected 8-bit RGBA"
    pos, idat = 8, b""
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        if kind == b"IDAT":
            idat += data[pos + 8:pos + 8 + length]
        pos += 12 + length
    raw, stride, bpp = zlib.decompress(idat), w * 4, 4
    rows, prev = [], bytearray(stride)
    for y in range(h):
        f, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for x in range(stride):
            a = line[x - bpp] if x >= bpp else 0
            b = prev[x]
            c = prev[x - bpp] if x >= bpp else 0
            if f == 1: line[x] = (line[x] + a) & 255
            elif f == 2: line[x] = (line[x] + b) & 255
            elif f == 3: line[x] = (line[x] + (a + b) // 2) & 255
            elif f == 4:
                p = a + b - c; pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[x] = (line[x] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line); prev = line
    return w, h, rows


def downsample(w, h, rows, size):
    """Area-average with premultiplied alpha so edges don't get dark fringes."""
    out = []
    for ty in range(size):
        y0, y1 = ty * h / size, (ty + 1) * h / size
        line = bytearray()
        for tx in range(size):
            x0, x1 = tx * w / size, (tx + 1) * w / size
            r = g = b = a = wsum = 0.0
            for sy in range(int(y0), min(h, int(y1 + 0.999))):
                wy = min(y1, sy + 1) - max(y0, sy)
                row = rows[sy]
                for sx in range(int(x0), min(w, int(x1 + 0.999))):
                    wgt = wy * (min(x1, sx + 1) - max(x0, sx))
                    i = sx * 4; al = row[i + 3] / 255.0
                    r += row[i] * al * wgt; g += row[i + 1] * al * wgt; b += row[i + 2] * al * wgt
                    a += al * wgt; wsum += wgt
            if a > 0:
                line += bytes((round(r / a), round(g / a), round(b / a), round(255 * a / wsum)))
            else:
                line += b"\0\0\0\0"
        out.append(line)
    return out


def encode_png(size, rows, height=None):
    raw = b"".join(b"\0" + bytes(r) for r in rows)
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, height or size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def social():
    with tempfile.TemporaryDirectory() as tmp:
        w, h, rows = render(tmp, os.path.join(HERE, "social-preview.svg"), 1280, 640)
    png = encode_png(w, rows, h)
    open(os.path.join(HERE, "social-preview.png"), "wb").write(png)
    print(f"wrote social-preview.png ({len(png)} bytes)")


def main():
    if "--social" in sys.argv[1:]:
        social()
        return
    with tempfile.TemporaryDirectory() as tmp:
        w, h, rows = render(tmp)
    pngs = [(s, encode_png(s, downsample(w, h, rows, s))) for s in SIZES]
    ico = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    for s, p in pngs:
        ico += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(p), offset)
        offset += len(p)
    ico += b"".join(p for _, p in pngs)
    open(os.path.join(HERE, "plainspoken.ico"), "wb").write(ico)
    open(os.path.join(HERE, "plainspoken-256.png"), "wb").write(dict(pngs)[256])
    print(f"wrote plainspoken.ico ({len(ico)} bytes, sizes {SIZES}) and plainspoken-256.png")


if __name__ == "__main__":
    main()
