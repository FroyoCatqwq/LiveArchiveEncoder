#!/usr/bin/env python3
"""Measure the raw-video frame size of every pixel format this tool can transport.

Why this exists
---------------
The encoder reads frames from a pipe as bare ``rawvideo``. One frame is
``width * height * bytes-per-pixel`` bytes, plus 1024 for a palette format
(pal8 carries its palette inline). Get that number wrong by a single byte and
the pipe shifts by one byte per frame until the picture is garbage -- that is
exactly how msrle came out black in v7.9.4.

So the table in ``Core/PixelFormatCatalog.cs`` is *measured*, not remembered.
This script writes N frames of a known size and divides.

v7.9.6 found ``yuv440p`` declared as 1.5 bytes/pixel in the old table; it is
2.0 (4:4:0 keeps U and V at full width and half height). Nothing in a normal
run picked that format, which is why it survived -- a latent corruption
waiting for the one source that is 4:4:0.

Usage
-----
    python tools/pixfmt_sweep.py [--ffmpeg PATH] [--json out.json]

Exit code 0 always: this is a reporting tool, not a gate. The unit tests
(``PixelFormatCatalogTests``) are the gate; they read the same numbers the
script prints and check them against the catalog.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile

# 64x48 satisfies every chroma grid in the table (4:1:0 needs 4x4, 4:4:0 needs
# 1x2, 4:2:2 needs 2x1), and monob needs a width divisible by 8.
WIDTH, HEIGHT, FRAMES = 64, 48, 3

# Formats a source, an encoder or a user may plausibly name. Deliberately
# excludes hardware formats (vaapi/cuda/qsv/mediacodec) and exotic ones
# (xyz12, pfm, floats beyond f32) -- they are never a raw transport here.
CANDIDATES = [
    # --- planar YUV, 8 bit
    "yuv410p", "yuv411p", "yuv420p", "yuv422p", "yuv440p", "yuv444p",
    "yuvj411p", "yuvj420p", "yuvj422p", "yuvj444p",
    # --- planar YUV, 9/10/12/14/16 bit
    "yuv420p9le", "yuv420p10le", "yuv420p12le", "yuv420p14le", "yuv420p16le",
    "yuv422p9le", "yuv422p10le", "yuv422p12le", "yuv422p14le", "yuv422p16le",
    "yuv444p9le", "yuv444p10le", "yuv444p12le", "yuv444p14le", "yuv444p16le",
    # --- semi-planar / packed YUV
    "nv12", "nv21", "nv16", "nv20le", "nv24", "nv42",
    "p010le", "p012le", "p016le", "p210le", "p410le",
    "uyvy422", "yuyv422", "yvyu422",
    # --- alpha variants
    "yuva420p", "yuva422p", "yuva444p", "yuva420p10le",
    # --- grey
    "gray", "gray9le", "gray10le", "gray12le", "gray14le", "gray16le",
    "grayf32le", "ya8", "ya16le", "monob", "monow",
    # --- packed RGB
    "rgb24", "bgr24", "rgb8", "bgr8", "rgb4_byte", "bgr4_byte",
    "rgb555le", "rgb555be", "rgb565le", "rgb565be",
    "bgr555le", "bgr565le", "x2rgb10le", "x2bgr10le",
    "rgb48le", "rgb48be", "rgba", "bgra", "argb", "abgr", "rgb0", "bgr0",
    "0rgb", "0bgr",
    # --- planar RGB and RGB float
    "gbrp", "gbrp9le", "gbrp10le", "gbrp12le", "gbrp14le", "gbrp16le", "gbrpf32le",
    # --- palette
    "pal8",
]

# Known traps: the muxer/demuxer accepts these but a *raw* dump is not
# width*height*bpp (bit-packed rows are padded to whole bytes, which 64x48
# divides evenly, so they stay measurable).
PALETTE = {"pal8"}


def measure(ffmpeg: str, pix_fmt: str, tmpdir: str) -> dict:
    """Write FRAMES frames as rawvideo and divide the byte count."""
    out = os.path.join(tmpdir, f"{pix_fmt}.raw")
    cmd = [
        ffmpeg, "-hide_banner", "-loglevel", "error",
        "-f", "lavfi",
        "-i", f"testsrc2=size={WIDTH}x{HEIGHT}:rate=10:duration=1",
        "-vf", f"format={pix_fmt}",
        "-pix_fmt", pix_fmt,
        "-frames:v", str(FRAMES),
        "-f", "rawvideo", "-y", out,
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0 or not os.path.exists(out):
        return {"format": pix_fmt, "ok": False, "error": (proc.stderr or "").strip()[:200]}

    size = os.path.getsize(out)
    if size % FRAMES != 0:
        return {"format": pix_fmt, "ok": False, "error": f"{size} bytes is not a multiple of {FRAMES}"}

    per_frame = size // FRAMES
    palette = pix_fmt in PALETTE
    pixels = WIDTH * HEIGHT
    bpp = (per_frame - (1024 if palette else 0)) / pixels
    return {
        "format": pix_fmt,
        "ok": True,
        "frame_bytes": per_frame,
        "bytes_per_pixel": round(bpp, 6),
        "palette": palette,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--json", help="write the full result to this file")
    args = parser.parse_args()

    rows = []
    with tempfile.TemporaryDirectory(prefix="pixfmt_sweep_") as tmpdir:
        for pix_fmt in CANDIDATES:
            row = measure(args.ffmpeg, pix_fmt, tmpdir)
            rows.append(row)
            if row["ok"]:
                print(f'{row["format"]:<14} {row["frame_bytes"]:>8} B/frame  '
                      f'{row["bytes_per_pixel"]:>8} B/px' + ("  (+palette)" if row["palette"] else ""))
            else:
                print(f'{row["format"]:<14} FAILED: {row["error"]}', file=sys.stderr)

    print()
    print("// measured with tools/pixfmt_sweep.py -- paste into PixelFormatCatalog")
    for row in rows:
        if not row["ok"]:
            continue
        suffix = " + palette" if row["palette"] else ""
        print(f'        ["{row["format"]}"] = {row["bytes_per_pixel"]},   // {row["frame_bytes"]} B/frame{suffix}')

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(rows, handle, indent=2)

    failures = [r for r in rows if not r["ok"]]
    print(f"\n{len(rows) - len(failures)}/{len(rows)} formats measured; "
          f"{len(failures)} not available in this build")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
