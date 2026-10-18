#!/usr/bin/env python3
"""Functional equivalence harness: live_archive_encoder.py (v6.3.3) vs C# v7.

Runs both implementations over the same material with identical parameters and
compares the produced media with ffprobe. Structural fields (codec, size, fps,
duration, stream layout) must match. Bitrate / fresh / repeated are recorded but
allowed to drift, because the adaptive controller reacts to wall-clock jitter.
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
MEDIA = ROOT / "tests" / "media"
SRC = MEDIA / "test_src.mp4"
PY = ROOT / "reference" / "live_archive_encoder.py"
# CS_EXE lets the same matrix be run against the NativeAOT build.
CS = Path(os.environ.get("CS_EXE")
          or (ROOT / "src" / "LiveArchiveEncoder" / "bin" / "Release" / "net10.0"
              / EXE_NAME))
# CM_OUT moves the scratch directory. Handy for running two builds back to back
# without deleting the previous products.
WORK = Path(os.environ.get("CM_OUT") or (ROOT / "tests" / "out" / "matrix"))

# name -> extra CLI args (input/output are appended by the harness)
CASES: list[tuple[str, str, list[str]]] = [
    ("rv20_default",         "rm",   ["--encoder", "rv20"]),
    ("mpeg4_avi",            "avi",  ["--encoder", "mpeg4"]),
    ("libx264_mp4",          "mp4",  ["--encoder", "libx264"]),
    ("mpeg1video_mpg",       "mpg",  ["--encoder", "mpeg1video"]),
    ("wmv2_asf",             "wmv",  ["--encoder", "wmv2"]),
    ("mjpeg_avi",            "avi",  ["--encoder", "mjpeg"]),
    ("roqvideo_roq",         "roq",  ["--encoder", "roqvideo"]),
    ("h261_special",         "h261", ["--encoder", "h261"]),
    ("rv20_timerange",       "rm",   ["--encoder", "rv20", "--start-time", "00:00:02", "--end-time", "00:00:05"]),
    ("mpeg4_nocrop_bicubic", "avi",  ["--encoder", "mpeg4", "--no-crop", "--scale-algo", "bicubic",
                                      "--width", "320", "--height", "240"]),
    ("mpeg4_yadif",          "avi",  ["--encoder", "mpeg4", "--deinterlace", "yadif"]),
    ("mpeg4_lowfps",         "avi",  ["--encoder", "mpeg4", "--fps", "8", "--fps-levels", "8,5,3"]),
]


def probe(path: Path) -> dict:
    cmd = [
        "ffprobe", "-v", "error", "-count_frames",
        "-show_entries", "stream=codec_type,codec_name,width,height,pix_fmt,r_frame_rate,"
                         "duration,nb_read_frames",
        "-show_entries", "format=duration,format_name",
        "-of", "json", str(path),
    ]
    p = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if p.returncode != 0:
        return {"error": p.stderr.strip() or "ffprobe failed"}
    data = json.loads(p.stdout)
    streams = data.get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"), {})
    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)
    out = {
        "codec": video.get("codec_name"),
        "width": video.get("width"),
        "height": video.get("height"),
        "pix_fmt": video.get("pix_fmt"),
        "fps": video.get("r_frame_rate"),
        "duration": round(float(video.get("duration") or 0), 3),
        "frames": video.get("nb_read_frames"),
        "audio": audio.get("codec_name") if audio else None,
        "streams": len(streams),
        "format": data.get("format", {}).get("format_name"),
    }
    out["size"] = path.stat().st_size if path.exists() else 0
    return out


def run_py(args: list[str]) -> tuple[dict, float]:
    t0 = time.perf_counter()
    p = subprocess.run([sys.executable, str(PY), *args],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    elapsed = time.perf_counter() - t0
    return parse_summary(p.stdout, p.stderr, p.returncode), elapsed


def run_cs(args: list[str]) -> tuple[dict, float]:
    t0 = time.perf_counter()
    p = subprocess.run([str(CS), *args],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    elapsed = time.perf_counter() - t0
    return parse_summary(p.stdout, p.stderr, p.returncode), elapsed


def parse_summary(stdout: str, stderr: str, code: int) -> dict:
    text = stdout + "\n" + stderr
    def grab(pattern: str) -> str | None:
        m = re.search(pattern, text)
        return m.group(1).strip() if m else None

    return {
        "exit": code,
        "duration": grab(r"Duration\s*:\s*([\d.]+)s"),
        "fresh": grab(r"Fresh frames\s*:\s*(\d+)"),
        "repeated": grab(r"Repeated slots\s*:\s*(\d+)"),
        "video_kbps": grab(r"Video average\s*:\s*([\d.]+) kbps"),
        "total_kbps": grab(r"Total average\s*:\s*([\d.]+) kbps"),
        "transport": grab(r"Transport pix fmt\s*:\s*(\S+)"),
        "resolution": grab(r"Output resolution\s*:\s*(\S+)"),
        "error": (stderr.strip().splitlines() or [""])[-1] if code != 0 else None,
    }


STRUCTURAL = ["codec", "width", "height", "pix_fmt", "fps", "duration", "frames",
              "audio", "streams", "format"]

# MPEG-PS/TS stamp packets from when the pipe bytes actually arrive, not from a
# frame counter, so the container duration wobbles by about one frame run to run
# even when both sides mux an identical frame sequence. Measured: the *Python*
# baseline alone produced 4.942 and 4.984 on two runs of the same command, with
# nb_read_frames=120 both times. Comparing that field exactly only measures how
# busy the machine was, so these formats get a tolerance and fall back on the
# (exact) decoded frame count, which is the real structural signal.
PACING_DURATION_FORMATS = {"mpeg", "mpegts", "mpegvideo"}
DURATION_TOL = 0.12


# CM_ONLY restricts the matrix to a comma separated subset of case names.
# Handy when one case fails and you want its full diff without a 3 minute rerun.
ONLY = {s.strip() for s in os.environ.get("CM_ONLY", "").split(",") if s.strip()}

def duration_ok(py_media: dict, cs_media: dict) -> bool:
    fmt = str(cs_media.get("format") or "")
    py_d, cs_d = py_media.get("duration"), cs_media.get("duration")
    if py_d == cs_d:
        return True
    if fmt not in PACING_DURATION_FORMATS:
        return False
    if not isinstance(py_d, (int, float)) or not isinstance(cs_d, (int, float)):
        return False
    return abs(float(py_d) - float(cs_d)) <= DURATION_TOL


def run_case(name: str, ext: str, extra: list[str]):
    """Encode one case on both sides and return (py_s, cs_s, py_m, cs_m, py_t, cs_t, diffs, notes)."""
    out_py = WORK / f"{name}_py.{ext}"
    out_cs = WORK / f"{name}_cs.{ext}"
    for p in (out_py, out_cs):
        if p.exists():
            p.unlink()

    py_args = [str(SRC), str(out_py), "--end-time", "00:00:05", *extra]
    # --ffmpeg-source path pins the C# run to the system FFmpeg, the same
    # binary the Python baseline uses; a managed build would otherwise win.
    cs_args = [str(SRC), str(out_cs), "--end-time", "00:00:05",
               *extra, "--ffmpeg-source", "path"]

    py_summary, py_time = run_py(py_args)
    cs_summary, cs_time = run_cs(cs_args)

    py_media = probe(out_py) if out_py.exists() else {"error": "no output"}
    cs_media = probe(out_cs) if out_cs.exists() else {"error": "no output"}

    diffs, notes = [], []
    for field in STRUCTURAL:
        py_v, cs_v = py_media.get(field), cs_media.get(field)
        if py_v == cs_v:
            continue
        if field == "duration" and duration_ok(py_media, cs_media):
            notes.append(f"duration within tolerance: py={py_v} cs={cs_v} "
                         f"(delta {abs(float(py_v) - float(cs_v)):.3f}s, frames "
                         f"py={py_media.get('frames')} cs={cs_media.get('frames')})")
            continue
        diffs.append(f"{field}: py={py_v} cs={cs_v}")

    return py_summary, cs_summary, py_media, cs_media, py_time, cs_time, diffs, notes


def main() -> int:
    cases = [c for c in CASES if not ONLY or c[0] in ONLY]
    WORK.mkdir(parents=True, exist_ok=True)
    print(f"source : {SRC}")
    print(f"python : {PY}")
    print(f"csharp : {CS}\n")

    failures = 0
    rows = []
    for name, ext, extra in cases:
        (py_summary, cs_summary, py_media, cs_media,
         py_time, cs_time, diffs, notes) = run_case(name, ext, extra)

        ok = not diffs and py_summary["exit"] == 0 and cs_summary["exit"] == 0
        if not ok:
            failures += 1

        rows.append((name, ok, py_summary, cs_summary, py_media, cs_media,
                     py_time, cs_time, diffs, notes))

        print(f"[{'PASS' if ok else 'FAIL'}] {name}")
        print(f"        media : {cs_media}")
        if diffs:
            print(f"        DIFF  : {'; '.join(diffs)}")
        for n in notes:
            print(f"        note  : {n}")
        print(f"        py    : fresh={py_summary['fresh']} repeat={py_summary['repeated']} "
              f"video={py_summary['video_kbps']} kbps wall={py_time:.2f}s exit={py_summary['exit']}")
        print(f"        cs    : fresh={cs_summary['fresh']} repeat={cs_summary['repeated']} "
              f"video={cs_summary['video_kbps']} kbps wall={cs_time:.2f}s exit={cs_summary['exit']}")
        if py_summary["error"]:
            print(f"        py err: {py_summary['error']}")
        if cs_summary["error"]:
            print(f"        cs err: {cs_summary['error']}")
        print()

    # Summary table
    print("=" * 96)
    print(f"{'case':24} {'result':7} {'codec':10} {'size':12} {'fps':8} {'dur':7} {'audio':8} {'py_wall':8} {'cs_wall':8}")
    print("-" * 96)
    for name, ok, py_s, cs_s, py_m, cs_m, py_t, cs_t, _, _ in rows:
        print(f"{name:24} {'PASS' if ok else 'FAIL':7} "
              f"{str(cs_m.get('codec')):10} {str(cs_m.get('width'))+'x'+str(cs_m.get('height')):12} "
              f"{str(cs_m.get('fps')):8} {str(cs_m.get('duration')):7} {str(cs_m.get('audio')):8} "
              f"{py_t:7.2f}s {cs_t:7.2f}s")
    print("=" * 96)
    print(f"total={len(rows)} pass={len(rows) - failures} fail={failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
