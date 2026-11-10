"""Regenerate Harness branding from Assets/harness-logo.png (requires Pillow).

Run from any directory: python tools/regenerate-assets.py [--source logo.png]
The optional source is copied verbatim; transparent artwork is never color-keyed.
"""

import argparse
import math
import shutil
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "Harness" / "Assets"
SCALES = (100, 125, 150, 200, 400)
ICON_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def render(artwork, width, height, mark_size):
    canvas = Image.new("RGBA", (width, height))
    ratio = min(mark_size / artwork.width, mark_size / artwork.height)
    size = (max(1, round(artwork.width * ratio)), max(1, round(artwork.height * ratio)))
    # Premultiplied alpha preserves color at the transparent silhouette when resized.
    mark = artwork.convert("RGBa").resize(size, Image.Resampling.LANCZOS).convert("RGBA")
    canvas.alpha_composite(mark, ((width - mark.width) // 2, (height - mark.height) // 2))
    return canvas


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path)
    args = parser.parse_args()
    source = ASSETS / "harness-logo.png"
    candidate = (args.source or source).resolve()
    with Image.open(candidate) as image:
        image.load()
        rgba = image.convert("RGBA")
    bounds = rgba.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("The source logo is fully transparent.")
    artwork = rgba.crop(bounds)
    if candidate != source.resolve():
        shutil.copyfile(candidate, source)

    # Base dimensions match Package.appxmanifest. Wide canvases center the mark.
    tiles = {
        "Square44x44Logo": (44, 44, 38),
        "Square150x150Logo": (150, 150, 112),
        "Wide310x150Logo": (310, 150, 112),
        "StoreLogo": (50, 50, 42),
        "SplashScreen": (620, 300, 192),
        "LockScreenLogo": (24, 24, 22),
    }
    for name, (width, height, mark_size) in tiles.items():
        for scale in SCALES:
            factor = scale / 100
            suffix = "" if scale == 100 else f".scale-{scale}"
            dimensions = [math.floor(value * factor + 0.5) for value in (width, height, mark_size)]
            render(artwork, *dimensions).save(ASSETS / f"{name}{suffix}.png", optimize=True)

    for size in (16, 20, 24, 32, 40, 48, 64, 96, 256):
        mark_size = size - max(2, round(size * 0.08))
        render(artwork, size, size, mark_size).save(
            ASSETS / f"Square44x44Logo.targetsize-{size}_altform-unplated.png", optimize=True
        )

    render(artwork, 180, 180, 164).save(ASSETS / "apple-touch-icon.png", optimize=True)
    render(artwork, 32, 32, 30).save(ASSETS / "_debug_windowicon.png", optimize=True)

    # Render each ICO frame directly from the source, avoiding chained resampling.
    frames = [render(artwork, size, size, size - max(2, round(size * 0.08))) for size in ICON_SIZES]
    frames[-1].save(
        ASSETS / "WindowIcon.ico", format="ICO",
        sizes=[(size, size) for size in ICON_SIZES], append_images=frames[:-1]
    )
    print(f"Generated {len(tiles) * len(SCALES) + 12} branding assets from {source.name}.")


if __name__ == "__main__":
    main()
