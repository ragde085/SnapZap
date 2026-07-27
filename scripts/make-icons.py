#!/usr/bin/env python3
"""Build every shipped icon from the one piece of source art.

    python scripts/make-icons.py            # from the repo root
    pip install pillow                      # the only dependency

Inputs
    assets/icon/snapzap-source.png          the artwork, tile-tight, one square

Outputs (all committed — this script exists to document how they were made, not
because a build step runs it)
    assets/icon/snapzap.png                 1024 master, for anything new that needs one
    assets/icon/snapzap-256.png             what README.md displays
    src/SnapZap.App/wwwroot/favicon.ico     the browser tab *and* the .exe icon
    src/SnapZap.App/wwwroot/snapzap.png     the mark beside the wordmark, in the app itself

The .ico lives in wwwroot and is used twice: SnapZap.App.csproj points
<ApplicationIcon> at it, and App.razor links it as the favicon. One file, so the
window in the taskbar and the tab in the browser can never drift apart.

snapzap.png is the same artwork again, for the top-left of the UI. It is a
separate file from the master only because the master is 1024px and 1.3 MB — far
too much to send down the wire for a 28px mark — and a separate file from the
.ico because an <img> wants a PNG. 128px, so it stays sharp at 2x and 3x.

Two things here are deliberate:

1. The source's cut-out edge is feathered — the border pixels come back at alpha
   ~160 rather than 255, which reads as a washed-out rim once the icon is small.
   So the alpha channel is discarded and rebuilt from a supersampled rounded
   rectangle at the radius the artwork was drawn with (20% of the edge). The 3px
   inset crop first drops the outermost rows, which are transparent-but-white in
   the source and would otherwise surface as a halo under the new mask.

2. Sizes at or below 48px are cropped in on the wolf's face rather than being a
   straight downscale of the whole tile. At 24px the full tile is an unreadable
   smear of blue; the cropped head still reads as a wolf. This is why the ICO is
   assembled entry by entry instead of handing Pillow a `sizes=` list.
"""

import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets" / "icon" / "snapzap-source.png"
MASTER = ROOT / "assets" / "icon" / "snapzap.png"
README_PNG = ROOT / "assets" / "icon" / "snapzap-256.png"
FAVICON = ROOT / "src" / "SnapZap.App" / "wwwroot" / "favicon.ico"
BRAND_PNG = ROOT / "src" / "SnapZap.App" / "wwwroot" / "snapzap.png"

BRAND_SIZE = 128  # displayed at 28px; the headroom is for high-DPI displays

CORNER_RATIO = 0.200  # measured off the source art
EDGE_INSET = 3        # rows of transparent-but-white pixels at the source's edge

# size -> how much of the tile to keep. 1.0 is the whole thing; below that the
# crop pulls in on the face, biased upward so the ears stay in frame.
ICO_PLAN = {
    16: 0.68,
    24: 0.68,
    32: 0.68,
    48: 0.82,
    64: 1.0,
    128: 1.0,
    256: 1.0,
}
FACE_BIAS = 0.28  # 0 = crop from the top, 0.5 = centred


def rounded_mask(size: int, supersample: int = 4) -> Image.Image:
    """An antialiased rounded-square alpha mask, drawn big and shrunk down."""
    big = size * supersample
    mask = Image.new("L", (big, big), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, big - 1, big - 1), radius=int(CORNER_RATIO * big), fill=255
    )
    return mask.resize((size, size), Image.LANCZOS)


def crop_square(img: Image.Image, keep: float) -> Image.Image:
    """Centre-crop to `keep` of the edge, biased toward the top, and re-round."""
    if keep >= 1.0:
        return img
    edge = img.size[0]
    side = int(edge * keep)
    left = (edge - side) // 2
    top = int((edge - side) * FACE_BIAS)
    out = img.crop((left, top, left + side, top + side))
    out.putalpha(rounded_mask(side))
    return out


def main() -> int:
    if not SOURCE.exists():
        print(f"missing source art: {SOURCE}", file=sys.stderr)
        return 1

    src = Image.open(SOURCE).convert("RGBA")
    if src.width != src.height:
        print(f"source must be square, got {src.size}", file=sys.stderr)
        return 1

    edge = src.width - 2 * EDGE_INSET
    tile = src.crop((EDGE_INSET, EDGE_INSET, EDGE_INSET + edge, EDGE_INSET + edge))
    tile.putalpha(rounded_mask(edge))

    master = tile.resize((1024, 1024), Image.LANCZOS)
    master.save(MASTER, optimize=True)
    master.resize((256, 256), Image.LANCZOS).save(README_PNG, optimize=True)

    # The whole tile, not a face crop: in the UI it sits at 28px next to the
    # wordmark, where it reads as a logo rather than as an icon in a list, and
    # the rounded tile is what makes it look like one.
    master.resize((BRAND_SIZE, BRAND_SIZE), Image.LANCZOS).save(BRAND_PNG, optimize=True)

    frames = [
        crop_square(master, keep).resize((size, size), Image.LANCZOS)
        for size, keep in sorted(ICO_PLAN.items())
    ]
    # Every entry has to be handed over explicitly — matched by size against
    # append_images — or Pillow ignores the cropped frames and just downscales
    # the base image for each size. Entries are PNG-compressed, which Windows
    # has read at every size since Vista and which the apphost's resource writer
    # copies through untouched.
    frames[-1].save(FAVICON, format="ICO", sizes=[f.size for f in frames],
                    append_images=frames[:-1])

    for path in (MASTER, README_PNG, FAVICON, BRAND_PNG):
        print(f"{path.relative_to(ROOT).as_posix():<40} {path.stat().st_size / 1024:7.1f} KB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
