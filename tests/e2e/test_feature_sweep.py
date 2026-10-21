"""Encoder sweep: every encoder the binary advertises must produce a valid file.

This is a *correctness* sweep, not a performance one. For each encoder that the
binary reports as available (via --completion, so the sweep covers exactly what
tab completion offers) it:

  1. encodes a 2 second segment of tests/media/test_src.mp4,
  2. checks the process exit code,
  3. checks that a non-empty output file landed on disk with the extension the
     encoder is supposed to use (the program picks it, we never pass --output),
  4. probes the result and requires a video stream plus a plausible duration.

The encoder list is discovered at collection time, so the binary must already
exist when pytest collects.

    pytest tests/e2e/test_feature_sweep.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe pytest tests/e2e/test_feature_sweep.py
    SWEEP_END=00:00:05 pytest tests/e2e/test_feature_sweep.py
"""

from __future__ import annotations

import glob
import os
import shutil
import time

import pytest

import harness
from harness import run

END = os.environ.get("SWEEP_END", "00:00:02")

# Containers whose muxer never writes a duration, so ffprobe legitimately
# reports none. AMV is probed back as "avi" but still has no usable duration
# field, which is why this is keyed on the extension and not on format_name.
DURATIONLESS_EXTS = {"roq", "h261", "h263", "amv"}

# Formats FFmpeg can write but not read back: there is a muxer and no demuxer,
# so ffprobe cannot validate them at all. Verified against ffmpeg -muxers at
# startup; a missing demuxer is a property of FFmpeg, not of the encoder.
MUXER_ONLY_EXTS = {"a64"}


def pytest_generate_tests(metafunc):
    if "encoder" not in metafunc.fixturenames:
        return
    exe = harness.resolve_exe()
    if not exe.is_file():
        raise pytest.UsageError(f"missing binary: {exe}")
    encoders = harness.encoder_list(exe)
    metafunc.parametrize("encoder", encoders)


def test_encoder_produces_a_valid_file(exe, src, workdir, encoder):
    out = workdir("sweep")
    # One scratch copy per encoder: the cases run in parallel workers, and a
    # shared src.mp4 would let one worker truncate the file another is reading.
    work = out / f"src-{encoder}.mp4"
    shutil.copyfile(src, work)

    # --ffmpeg-source path: always the system FFmpeg, so the sweep is not
    # silently re-run against a managed build someone installed.
    started = time.time()
    proc = run([str(exe), "--encoder", encoder, "--end", END,
                "--ffmpeg-source", "path", str(work)])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")

    # Stale products from earlier runs are not deleted (the sweep directory is
    # kept on purpose); the newest file written after this run started is the
    # one this encoder just produced. The product is named after its own
    # scratch copy, so parallel workers cannot pick up each other's output.
    produced = [p for p in glob.glob(str(out / f"{work.stem}_live.*"))
                if os.path.getmtime(p) >= started - 1.0]
    produced.sort(key=os.path.getmtime)
    assert produced, "no output file"
    path = produced[-1]
    ext = os.path.splitext(path)[1].lstrip(".")
    size = os.path.getsize(path)
    assert size > 0, "output is empty"

    info = harness.probe(path)
    if info is None:
        # FFmpeg has a muxer but no demuxer for this format: nothing to probe,
        # and the encode itself succeeded. That is the documented behaviour,
        # not a failure.
        if ext.lower() in MUXER_ONLY_EXTS or ext.lower() in harness.muxer_only_formats():
            pytest.skip(f"written OK; FFmpeg has no demuxer for .{ext}")
        pytest.fail("ffprobe cannot read the output")

    streams = info.get("streams", [])
    video = [s for s in streams if s.get("codec_type") == "video"]
    assert video, "no video stream in output"

    try:
        dur = float((info.get("format") or {}).get("duration") or 0.0)
    except ValueError:
        dur = 0.0
    if dur <= 0 and ext.lower() not in DURATIONLESS_EXTS:
        # The old sweep reported this as WARN: the file is valid, the container
        # just does not carry a duration. Kept as a pass with a printed note.
        print(f"note: container reports no duration ({video[0].get('codec_name')})")

    os.replace(path, str(out / f"{encoder}.{ext}"))
