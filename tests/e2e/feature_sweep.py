#!/usr/bin/env python3
"""Functional sweep: encode one short clip with every detected legacy encoder.

This is a *correctness* sweep, not a performance one. For each encoder that the
binary reports as available it:

  1. encodes a 2 second segment of tests/media/test_src.mp4,
  2. checks the process exit code,
  3. checks that a non-empty output file landed on disk with the extension the
     encoder is supposed to use (the program picks it, we never pass --output),
  4. probes the result and requires a video stream plus a plausible duration.

Usage:
    python tests/e2e/feature_sweep.py                      # jit build
    CS_EXE=publish/aot/LiveArchiveEncoder.exe python tests/e2e/feature_sweep.py
    SWEEP_END=00:00:05 python tests/e2e/feature_sweep.py
"""

import glob
import json
import os
import shutil
import subprocess
import sys
import time

# tests/e2e/, so: HERE = tests/e2e, TESTS = tests, ROOT = project root.
HERE = os.path.dirname(os.path.abspath(__file__))
TESTS = os.path.dirname(HERE)
ROOT = os.path.dirname(TESTS)
DEFAULT_EXE = os.path.normpath(os.path.join(ROOT, "publish", "jit", "LiveArchiveEncoder.exe"))
EXE = os.path.normpath(os.environ.get("CS_EXE", DEFAULT_EXE))
SRC = os.path.join(TESTS, "media", "test_src.mp4")
# FS_OUT moves the scratch directory; pointing it somewhere empty avoids deleting
# the products of a previous run.
OUT = os.environ.get("FS_OUT") or os.path.join(TESTS, "out", "sweep")
END = os.environ.get("SWEEP_END", "00:00:02")

# Containers whose muxer never writes a duration, so ffprobe legitimately
# reports none. AMV is probed back as "avi" but still has no usable duration
# field, which is why this is keyed on the extension and not on format_name.
DURATIONLESS_EXTS = {"roq", "h261", "h263", "amv"}

# Formats FFmpeg can write but not read back: there is a muxer and no demuxer,
# so ffprobe cannot validate them at all. Verified against ffmpeg -muxers at
# startup; a missing demuxer is a property of FFmpeg, not of the encoder.
MUXER_ONLY_EXTS = {"a64"}


def muxer_only_formats():
    """Format names that have a muxer but no demuxer."""
    def names(kind):
        proc = run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-" + kind])
        found = set()
        for line in proc.stdout.splitlines():
            parts = line.strip().split()
            if len(parts) < 2:
                continue
            if set(parts[0]) <= {"D", "E"}:
                found.add(parts[1])
        return found

    return names("muxers") - names("demuxers")


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", **kw)


def encoder_list():
    """Reuse --completion so the sweep covers exactly what tab completion offers."""
    proc = run([EXE, "--completion", "bash"])
    if proc.returncode != 0:
        sys.exit(f"cannot query encoders: {proc.stderr.strip()}")
    m = None
    lines = proc.stdout.splitlines()
    for i, line in enumerate(lines):
        if line.strip() == "--encoder)" and i + 1 < len(lines):
            m = lines[i + 1]
            break
    if not m:
        sys.exit("completion output has no --encoder case")
    inner = m.split('"')[1]
    return inner.split()


def probe(path):
    proc = run(["ffprobe", "-v", "error", "-show_streams", "-show_format",
                "-of", "json", path])
    if proc.returncode != 0:
        return None, proc.stderr.strip()
    try:
        return json.loads(proc.stdout), None
    except json.JSONDecodeError as exc:
        return None, f"ffprobe json: {exc}"


def main():
    if not os.path.isfile(SRC):
        sys.exit(f"missing source clip: {SRC}")
    if not os.path.isfile(EXE):
        sys.exit(f"missing binary: {EXE}")

    # Not rmtree: the directory deliberately survives between runs so the
    # produced clips can be inspected, and a bulk delete of dozens of files is
    # exactly the kind of thing a sandbox or a careless operator should not have
    # to authorise. Per-encoder outputs are overwritten individually instead.
    os.makedirs(OUT, exist_ok=True)

    work = os.path.join(OUT, "src.mp4")
    shutil.copyfile(SRC, work)

    encoders = encoder_list()
    unreadable = muxer_only_formats()
    print(f"binary   : {EXE}")
    print(f"encoders : {len(encoders)}")
    print(f"segment  : 00:00:00 - {END}")
    print(f"muxer-only formats (ffprobe cannot read them back): "
          f"{', '.join(sorted(unreadable)) or 'none'}")
    print()

    rows = []
    for name in encoders:
        for stale in glob.glob(os.path.join(OUT, "src_live.*")):
            os.remove(stale)

        t0 = time.time()
        # --ffmpeg-source path: always the system FFmpeg, so the sweep is not
        # silently re-run against a managed build someone installed.
        proc = run([EXE, "--encoder", name, "--end", END, "--ffmpeg-source", "path", work])
        wall = time.time() - t0

        produced = glob.glob(os.path.join(OUT, "src_live.*"))
        status, note = "PASS", ""

        if proc.returncode != 0:
            status = "FAIL"
            note = f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:90]}"
            rows.append((name, "-", 0, status, note, wall, proc))
            continue

        if not produced:
            status, note = "FAIL", "no output file"
            rows.append((name, "-", 0, status, note, wall, proc))
            continue

        path = produced[0]
        ext = os.path.splitext(path)[1].lstrip(".")
        size = os.path.getsize(path)
        if size == 0:
            status, note = "FAIL", "output is empty"
            rows.append((name, ext, size, status, note, wall, proc))
            continue

        info, err = probe(path)
        if info is None:
            if ext.lower() in MUXER_ONLY_EXTS:
                status = "NOPROBE"
                note = "written OK; FFmpeg has no demuxer for this format"
            else:
                status = "FAIL"
                note = err or "ffprobe failed"
            rows.append((name, ext, size, status, note, wall, proc))
            continue

        streams = info.get("streams", [])
        video = [s for s in streams if s.get("codec_type") == "video"]
        if not video:
            status = "FAIL"
            note = "no video stream in output"
        else:
            fmt = info.get("format", {})
            fmt_name = fmt.get("format_name", "")
            try:
                dur = float(fmt.get("duration") or 0.0)
            except ValueError:
                dur = 0.0
            v = video[0]
            codec = v.get("codec_name", "?")
            label = f"{codec} {v.get('width')}x{v.get('height')} dur={dur:.2f}s streams={len(streams)}"
            if dur <= 0 and ext.lower() not in DURATIONLESS_EXTS:
                status, note = "WARN", f"{label} (container reports no duration)"
            else:
                note = label

        target = os.path.join(OUT, f"{name}.{ext}")
        os.replace(path, target)
        rows.append((name, ext, size, status, note, wall, proc))

    print("=" * 100)
    print(f"{'encoder':<14}{'ext':<6}{'size':>10}  {'result':<6} detail")
    print("-" * 100)
    for name, ext, size, status, note, wall, _ in rows:
        print(f"{name:<14}{ext:<6}{size:>10}  {status:<6} {note}")
    print("=" * 100)

    fails = [r for r in rows if r[3] == "FAIL"]
    warns = [r for r in rows if r[3] == "WARN"]
    print(f"total={len(rows)} pass={len(rows) - len(fails) - len(warns)} "
          f"warn={len(warns)} fail={len(fails)}")

    if fails:
        print("\n--- failing encoders: last FFmpeg diagnostic ---")
        for name, _, _, _, note, _, proc in fails:
            print(f"\n[{name}] {note}")
            tail = (proc.stderr or "").strip().splitlines()[-4:]
            for line in tail:
                print(f"    {line}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
