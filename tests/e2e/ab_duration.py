#!/usr/bin/env python3
"""Isolates the two cases whose container duration drifted.

For each encoder it runs the same 5 s segment through four variants
(python / cs inline / cs separate / cs auto) several times and reports the
ffprobe video-stream duration, so we can tell a systematic difference caused by
the split pipeline apart from ordinary run-to-run jitter.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path
# The apphost carries no extension on Unix; every path in this suite goes
# through EXE_NAME so the same script runs on Windows and on Linux.
EXE_NAME = "LiveArchiveEncoder.exe" if os.name == "nt" else "LiveArchiveEncoder"


ROOT = Path(__file__).resolve().parent.parent.parent  # tests/e2e -> project root
SRC = ROOT / "tests" / "media" / "test_src.mp4"
PY = ROOT / "reference" / "live_archive_encoder.py"
CS = ROOT / "src" / "LiveArchiveEncoder" / "bin" / "Release" / "net10.0" / EXE_NAME
WORK = ROOT / "tests" / "out" / "ab"
WORK.mkdir(parents=True, exist_ok=True)

CASES = [("mpeg1video", "mpg"), ("wmv2", "wmv")]
RUNS = 4
END = "00:00:05"


def probe_duration(path: Path) -> str:
    cmd = ["ffprobe", "-v", "error", "-select_streams", "v:0",
           "-show_entries", "stream=duration,r_frame_rate,nb_frames",
           "-of", "json", str(path)]
    p = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if p.returncode != 0:
        return "probe-failed"
    try:
        s = json.loads(p.stdout)["streams"][0]
    except Exception:
        return "no-stream"
    dur = s.get("duration")
    return f"{float(dur):.3f}" if dur else "n/a"


def summary(text: str) -> str:
    m = re.search(r"Fresh frames\s*:\s*(\d+)", text)
    r = re.search(r"Repeated slots\s*:\s*(\d+)", text)
    return f"{(m.group(1) if m else '?')}/{(r.group(1) if r else '?')}"


def main() -> int:
    for name, ext in CASES:
        print("=" * 78)
        print(f"case: {name}   segment 0 - {END}")
        variants = [
            ("py          ", [sys.executable, str(PY)]),
            ("cs inline   ", [str(CS), "--audio-mode", "inline"]),
            ("cs separate ", [str(CS), "--audio-mode", "separate"]),
            ("cs auto     ", [str(CS)]),
        ]
        for label, base in variants:
            tag = label.strip().replace(" ", "_")
            durs, fr, walls = [], [], []
            for i in range(RUNS):
                out = WORK / f"s_{name}_{tag}_{i}.{ext}"
                out.unlink(missing_ok=True)
                t0 = time.perf_counter()
                p = subprocess.run([*base, "--encoder", name, "--end-time", END,
                                    str(SRC), str(out)],
                                   capture_output=True, text=True,
                                   encoding="utf-8", errors="replace", timeout=300)
                walls.append(time.perf_counter() - t0)
                text = p.stdout + "\n" + p.stderr
                fr.append(summary(text))
                durs.append(probe_duration(out) if p.returncode == 0 else f"rc{p.returncode}")
            print(f"  {label}  dur={'/'.join(durs)}")
            print(f"  {' ' * len(label)}  fresh/repeat={'  '.join(fr)}")
            print(f"  {' ' * len(label)}  wall={' '.join(f'{w:.2f}' for w in walls)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
