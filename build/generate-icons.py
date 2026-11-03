#!/usr/bin/env python3
"""Generates the app's original icon set (no third-party or game artwork).

Writes src/DbdPresence.App/Assets/{app,tray-connected,tray-disconnected,tray-paused}.ico and the 512x512
tools/DbdPresence.Spikes/spike-image.png (test image for the unshipped spike tool).
The mark is a gold diamond ring on a dark tile; tray variants add a status badge. The tray tooltip
always states the status in words, so the badge colour is never the only signal (UI_SPEC §6, §7).

It also writes the Microsoft Store package logos (build/msix/Assets, used by build/msix/AppxManifest.xml) from the same mark.
Requires Pillow (developer tool only; not a product dependency). Run: python3 build/generate-icons.py
To rewrite only the package logos: python3 build/generate-icons.py --msix-only
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "src" / "DbdPresence.App" / "Assets"
SPIKE_PNG = ROOT / "tools" / "DbdPresence.Spikes" / "spike-image.png"
MSIX_OUT = ROOT / "build" / "msix" / "Assets"
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)]
S = 1024  # supersampled canvas

SURFACE = (21, 26, 33, 255)       # Surface #151A21
EDGE = (42, 51, 62, 255)          # Border #2A333E
GOLD = (212, 166, 73, 255)        # AccentGold #D4A649
GREEN = (63, 185, 80, 255)        # Success #3FB950
GREY = (154, 166, 178, 255)       # TextMuted #9AA6B2
AMBER = (227, 160, 8, 255)        # Warning #E3A008
INK = (14, 17, 22, 255)           # Bg #0E1116


def diamond(cx, cy, r):
    return [(cx, cy - r), (cx + r, cy), (cx, cy + r), (cx - r, cy)]


def mark() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([24, 24, S - 24, S - 24], radius=200, fill=SURFACE, outline=EDGE, width=24)
    c = S // 2
    d.polygon(diamond(c, c, 360), fill=GOLD)
    d.polygon(diamond(c, c, 230), fill=SURFACE)
    d.polygon(diamond(c, c, 110), fill=GOLD)
    return img


def badge(img: Image.Image, colour, glyph: str) -> Image.Image:
    img = img.copy()
    d = ImageDraw.Draw(img)
    r = 250
    cx, cy = S - r - 44, S - r - 44
    d.ellipse([cx - r - 40, cy - r - 40, cx + r + 40, cy + r + 40], fill=INK)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=colour)
    if glyph == "check":
        d.line([(cx - 120, cy + 10), (cx - 30, cy + 100), (cx + 130, cy - 90)], fill=INK, width=80, joint="curve")
    elif glyph == "dash":
        d.rounded_rectangle([cx - 130, cy - 40, cx + 130, cy + 40], radius=40, fill=INK)
    elif glyph == "pause":
        d.rounded_rectangle([cx - 110, cy - 120, cx - 30, cy + 120], radius=24, fill=INK)
        d.rounded_rectangle([cx + 30, cy - 120, cx + 110, cy + 120], radius=24, fill=INK)
    return img


def save(img: Image.Image, name: str) -> None:
    big = img.resize((256, 256), Image.LANCZOS)
    big.save(OUT / name, format="ICO", sizes=SIZES)
    print(f"wrote {OUT / name}")


def msix_assets(base: Image.Image) -> None:
    """Square44x44Logo, Square150x150Logo, StoreLogo (50x50) and Wide310x150Logo for the MSIX manifest."""
    MSIX_OUT.mkdir(parents=True, exist_ok=True)
    for name, size in (("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50)):
        base.resize((size, size), Image.LANCZOS).save(MSIX_OUT / name, format="PNG", optimize=True)
        print(f"wrote {MSIX_OUT / name}")
    wide = Image.new("RGBA", (310, 150), SURFACE)
    mark_150 = base.resize((130, 130), Image.LANCZOS)
    wide.alpha_composite(mark_150, ((310 - 130) // 2, (150 - 130) // 2))
    wide.save(MSIX_OUT / "Wide310x150Logo.png", format="PNG", optimize=True)
    print(f"wrote {MSIX_OUT / 'Wide310x150Logo.png'}")


if __name__ == "__main__":
    if "--msix-only" in sys.argv:
        msix_assets(mark())
        sys.exit(0)
    OUT.mkdir(parents=True, exist_ok=True)
    base = mark()
    msix_assets(base)
    save(base, "app.ico")
    save(badge(base, GREEN, "check"), "tray-connected.ico")
    save(badge(base, GREY, "dash"), "tray-disconnected.ico")
    save(badge(base, AMBER, "pause"), "tray-paused.ico")
    SPIKE_PNG.parent.mkdir(parents=True, exist_ok=True)
    base.resize((512, 512), Image.LANCZOS).save(SPIKE_PNG, format="PNG", optimize=True)
    print(f"wrote {SPIKE_PNG}")
