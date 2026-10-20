"""Functional equivalence: live_archive_encoder.py (v6.3.3) vs the C# build.

Both implementations encode the same material with identical parameters and
the products are compared with ffprobe. Structural fields (codec, size, fps,
duration, stream layout) must match. Bitrate / fresh / repeated are recorded
but allowed to drift, because the adaptive controller reacts to wall-clock
jitter -- which is also why this suite must not run under parallel load.

    pytest tests/e2e/test_compare_matrix.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe pytest tests/e2e/test_compare_matrix.py
    # one case only (replaces the old CM_ONLY knob):
    pytest tests/e2e/test_compare_matrix.py -k mpeg4_nocrop_bicubic
"""

from __future__ import annotations

import time

import pytest

import harness
from harness import run

# name, extension, extra CLI args (input/output are appended by the harness)
CASES = [
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


@pytest.mark.parametrize(("name", "ext", "extra"), CASES, ids=[c[0] for c in CASES])
def test_matches_the_baseline(exe, src, py_baseline, workdir, name, ext, extra):
    work = workdir("matrix")
    out_py = work / f"{name}_py.{ext}"
    out_cs = work / f"{name}_cs.{ext}"

    py_args = [str(src), str(out_py), "--end-time", "00:00:05", *extra]
    # --ffmpeg-source path pins the C# run to the system FFmpeg, the same
    # binary the Python baseline uses; a managed build would otherwise win.
    cs_args = [str(src), str(out_cs), "--end-time", "00:00:05",
               *extra, "--ffmpeg-source", "path"]

    started = time.time()
    py_proc = run([*py_baseline, *py_args])
    cs_proc = run([str(exe), *cs_args])

    assert py_proc.returncode == 0, f"python baseline failed: {py_proc.stderr[-300:]}"
    assert cs_proc.returncode == 0, f"c# build failed: {cs_proc.stderr[-300:]}"
    # The scratch directory is kept between runs, so "did this run write it"
    # is proven by timestamp rather than by deleting the previous product.
    assert harness.fresh_output(out_py, started), f"no fresh python output: {out_py}"
    assert harness.fresh_output(out_cs, started), f"no fresh c# output: {out_cs}"

    py_media = harness.media_info(out_py)
    cs_media = harness.media_info(out_cs)

    diffs, notes = harness.structural_diffs(py_media, cs_media)
    for note in notes:
        print(f"note: {note}")
    assert not diffs, "structural diff:\n  " + "\n  ".join(diffs)
