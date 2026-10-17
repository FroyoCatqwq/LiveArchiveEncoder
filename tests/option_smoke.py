#!/usr/bin/env python3
"""Option-path smoke test.

The encoder matrix (tests/feature_sweep.py) covers "does this codec work"; this
one covers "do the option paths still work" -- turbo, sharding, the split audio
pipeline, deinterlacing, cropping and time ranges. Each case only has to produce
a structurally valid file; bit-exactness is the matrix's job.

    python tests/option_smoke.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe python tests/option_smoke.py
"""

import json
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_EXE = ROOT / "publish" / "jit" / "LiveArchiveEncoder.exe"
EXE = Path(os.environ.get("CS_EXE", DEFAULT_EXE))
SRC = ROOT / "tests" / "media" / "test_src.mp4"
OUT = ROOT / "tests" / "out_options"

# name, extra args, output extension. Every case names its encoder explicitly:
# the default is rv20 (rm), and the extension has to match the muxer.
CASES = [
    ("baseline",        ["--encoder", "rv20"],                  "rm"),
    ("turbo",           ["--encoder", "rv20", "--turbo"],       "rm"),
    ("turbo_mpeg4",     ["--encoder", "mpeg4", "--turbo"],      "avi"),
    ("parallel4",       ["--encoder", "mpeg4", "--turbo", "--parallel", "4"], "avi"),
    ("parallel_roq",    ["--encoder", "roqvideo", "--turbo", "--parallel", "4"], "roq"),
    ("parallel_rv20",   ["--encoder", "rv20", "--turbo", "--parallel", "4"],     "rm"),
    ("audio_separate",  ["--encoder", "mpeg4", "--audio-mode", "separate"], "avi"),
    ("audio_auto",      ["--encoder", "mpeg4", "--audio-mode", "auto"],     "avi"),
    ("audio_inline",    ["--encoder", "mpeg4", "--audio-mode", "inline"],   "avi"),
    ("quiet",           ["--encoder", "mpeg4", "--quiet"],      "avi"),
    ("deint_yadif",     ["--encoder", "mpeg4", "--deinterlace", "yadif"],  "avi"),
    ("deint_bwdif",     ["--encoder", "mpeg4", "--deinterlace", "bwdif",
                         "--deint-mode", "send_field"],         "avi"),
    ("nocrop",          ["--encoder", "mpeg4", "--no-crop", "--scale-algo", "bicubic"], "avi"),
    ("timerange",       ["--encoder", "mpeg4", "--start-time", "00:00:02",
                         "--end-time", "00:00:08"],             "avi"),
    ("fps_levels",      ["--encoder", "mpeg4", "--fps", "25",
                         "--fps-levels", "25,15,10,5"],         "avi"),
    ("x264",            ["--encoder", "libx264", "--qv", "23",
                         "--x264-preset", "faster"],            "mp4"),
    ("amv_forced",      ["--encoder", "amv"],                   "amv"),
    ("high_limit",      ["--encoder", "mpeg4", "--limit", "99999",
                         "--recover", "90000"],                 "avi"),
]


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=300)


def probe(path):
    p = run(["ffprobe", "-v", "error", "-show_streams", "-show_format",
             "-of", "json", str(path)])
    if p.returncode != 0:
        return None
    try:
        return json.loads(p.stdout)
    except json.JSONDecodeError:
        return None


def main():
    if not EXE.is_file():
        sys.exit(f"missing binary: {EXE}")
    OUT.mkdir(parents=True, exist_ok=True)

    print(f"binary : {EXE}")
    print(f"source : {SRC}")
    print()
    print(f"{'case':<18}{'result':<8}{'size':>9}  {'wall':>6}  detail")
    print("-" * 92)

    fails = 0
    for name, extra, ext in CASES:
        dst = OUT / f"{name}.{ext}"
        t0 = time.time()
        proc = run([str(EXE), str(SRC), str(dst), "--end", "00:00:04", *extra])
        wall = time.time() - t0

        status, detail = "PASS", ""
        if proc.returncode != 0:
            status = "FAIL"
            detail = f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:80]}"
        elif not dst.exists() or dst.stat().st_size == 0:
            status, detail = "FAIL", "no output"
        else:
            info = probe(dst)
            if info is None:
                status, detail = "FAIL", "ffprobe cannot read the output"
            else:
                streams = info.get("streams", [])
                video = [s for s in streams if s.get("codec_type") == "video"]
                audio = [s for s in streams if s.get("codec_type") == "audio"]
                if not video:
                    status, detail = "FAIL", "no video stream"
                else:
                    v = video[0]
                    codec = v.get("codec_name", "?")
                    dur = 0.0
                    try:
                        dur = float((info.get("format") or {}).get("duration") or 0.0)
                    except ValueError:
                        dur = 0.0
                    detail = (f"{codec} {v.get('width')}x{v.get('height')} "
                              f"v={len(video)} a={len(audio)} dur={dur:.2f}s")
                    if name == "quiet" and proc.stdout.strip():
                        status, detail = "FAIL", "--quiet still printed output"

        size = dst.stat().st_size if dst.exists() else 0
        if status == "FAIL":
            fails += 1
        print(f"{name:<18}{status:<8}{size:>9}  {wall:>5.2f}s  {detail}")

    print("-" * 92)
    print(f"total={len(CASES)} pass={len(CASES) - fails} fail={fails}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
