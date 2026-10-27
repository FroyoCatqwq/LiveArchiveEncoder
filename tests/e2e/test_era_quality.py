"""v7.9.8.2: an era states its quality, and it states it in the encoder's scale.

The defect these tests exist for: a preset never touched the live quality, so
whatever the previous encoder had left in the settings went straight to FFmpeg.
Applying ``--era youku/2007`` (352x264@15, 320 kbps) and naming libx264 handed
``-crf 9`` to x264 -- measured 935 kbps for a 320 kbps stream, 2.9x the ceiling
the frame-rate ladder reads. The ladder went to its last rung inside the first
second and the run reported a 15 fps capture that never ran at 15 fps.
"""

from __future__ import annotations

import harness
from harness import run

CEILING_KBPS = 320          # youku/2007's video ceiling


def _total_kbps(path) -> float:
    """Whole-file bitrate: video + audio + container, the number a viewer sees."""
    info = harness.probe(path) or {}
    raw = (info.get("format") or {}).get("bit_rate")
    return int(raw) / 1000.0 if raw else 0.0


def test_h264_on_a_2007_row_stays_inside_the_ceiling(exe, src, workdir):
    """x264 must encode the era's picture, not an MPEG quantiser read as CRF.

    Before the fix this produced 935 kbps of video into a 320 kbps ceiling. The
    tolerance is generous because the number includes 32 kbps of MP3, the
    container overhead and a keyframe-heavy ten-second clip; what it has to
    reject is anything near 3x.
    """
    out = workdir("era_quality")
    dst = out / "h264_2007.flv"
    proc = run([str(exe), str(src), str(dst), "--era", "youku/2007",
                "--encoder", "libx264", "--end", "00:00:10",
                "--ffmpeg-source", "path", "--lang", "en"], timeout=300)
    assert proc.returncode == 0, (proc.stderr or "")[-400:]

    total = _total_kbps(dst)
    assert total > 0, "ffprobe cannot read the output"
    assert total <= CEILING_KBPS * 1.3, (
        f"{total:.0f} kbps into a {CEILING_KBPS} kbps ceiling: the quality is in "
        "the wrong scale")
    # And it is not silently pinned to nothing, either.
    assert total >= CEILING_KBPS * 0.45, f"{total:.0f} kbps is far below the era"


def test_a_wrong_quality_number_cannot_collapse_the_bitrate(exe, src, workdir):
    """The ceiling is also enforced with VBV, so a stray q:v cannot undo the fix.

    ``--qv 9`` on an H.264 run is a request for near-lossless, i.e. exactly the
    mistake this version fixes. It now costs picture quality instead of the
    whole ladder: measured 413 kbps with the cap, 935 without.
    """
    out = workdir("era_quality")
    dst = out / "forced_qv.flv"
    proc = run([str(exe), str(src), str(dst), "--era", "youku/2007",
                "--encoder", "libx264", "--qv", "9", "--end", "00:00:08",
                "--ffmpeg-source", "path", "--lang", "en"], timeout=300)
    assert proc.returncode == 0, (proc.stderr or "")[-400:]

    total = _total_kbps(dst)
    assert total <= CEILING_KBPS * 1.5, (
        f"{total:.0f} kbps: the VBV cap did not hold")


def test_an_openh264_row_is_pinned_to_its_ceiling(exe, src, workdir):
    """OpenH264 has no quality knob at all; the ceiling is the only dial.

    A given ceiling of 700 kbps must produce roughly 700 kbps of video, not the
    encoder's own 1.1 Mbps default.
    """
    out = workdir("era_quality")
    dst = out / "bh264_2011.mp4"
    proc = run([str(exe), str(src), str(dst), "--era", "cctv/2011",
                "--end", "00:00:08", "--ffmpeg-source", "path", "--lang", "en"],
               timeout=300)
    assert proc.returncode == 0, (proc.stderr or "")[-400:]

    info = harness.video_summary(dst) or {}
    assert info.get("codec") == "h264", info
    total = _total_kbps(dst)
    # 700 kbps of video + 96 kbps of audio, with room for an I-frame burst.
    assert total <= 1000, f"{total:.0f} kbps: the ceiling was not passed to OpenH264"
    assert total >= 500, f"{total:.0f} kbps: far below the row's own ladder"


def test_a_row_carries_its_window_buffer_and_deinterlacer(exe, src, workdir):
    """The capture pipeline is part of the year (v7.9.8.2).

    2003: a three-second averaging window, four frames of queue, and no
    deinterlacer -- yadif did not exist. 2014: two seconds, eight frames, yadif
    on the frames the source marks as interlaced.
    """
    out = workdir("era_quality")

    old = run([str(exe), str(src), str(out / "pipe_2003.asf"), "--era", "cctv/2003",
               "--end", "00:00:02", "--emit-args",
               "--ffmpeg-source", "path", "--lang", "en"], timeout=180)
    assert old.returncode == 0, (old.stderr or "")[-300:]
    line = old.stdout.strip().splitlines()[-1]
    assert "--window 3" in line, line
    assert "--buffer-frames 4" in line, line
    assert "--deinterlace" not in line, line
    # A flag for a filter that is not running says nothing and should not print.
    assert "--deint-marked-only" not in line, line

    new = run([str(exe), str(src), str(out / "pipe_2014.mp4"), "--era", "bilibili/2014",
               "--end", "00:00:02", "--emit-args",
               "--ffmpeg-source", "path", "--lang", "en"], timeout=180)
    assert new.returncode == 0, (new.stderr or "")[-300:]
    line = new.stdout.strip().splitlines()[-1]
    assert "--buffer-frames 8" in line, line
    assert "--deinterlace yadif" in line, line
    assert "--deint-marked-only" in line, line


def test_an_explicit_flag_still_beats_the_presets_pipeline(exe, src, workdir):
    """A preset is a fallback. --window and --deinterlace must survive --era."""
    out = workdir("era_quality")
    proc = run([str(exe), str(src), str(out / "pipe_override.asf"), "--era", "cctv/2003",
                "--window", "1.5", "--buffer-frames", "10", "--deinterlace", "bwdif",
                "--end", "00:00:02", "--emit-args",
                "--ffmpeg-source", "path", "--lang", "en"], timeout=180)
    assert proc.returncode == 0, (proc.stderr or "")[-300:]
    line = proc.stdout.strip().splitlines()[-1]
    assert "--window 1.5" in line, line
    assert "--buffer-frames 10" in line, line
    assert "--deinterlace bwdif" in line, line
