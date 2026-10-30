"""A real encode, start to finish, and the two ways it can end badly.

These cost real wall-clock time -- a 3 s source takes about 3 s -- so there are
few of them and each one asserts several things. The rule the whole program
rests on is in here: an interrupted or failed run leaves no half-written file
and no running children.
"""

from __future__ import annotations

import os
import subprocess

from conftest import Work, duration, interrupt, start, stream


def test_a_real_encode_produces_the_requested_length(work: Work, needs_ffmpeg):
    source = work.source(seconds=3)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output), "--encoder", "mpeg4", "--quality", "8")

    assert result.returncode == 0, result.stderr
    assert output.exists()
    assert work.parts() == [], "a finished run must not leave a part file"

    probe = work.probe(str(output))
    video = stream(probe, "video")

    assert video["codec_name"] == "mpeg4"
    assert (video["width"], video["height"]) == (256, 192)
    assert video["r_frame_rate"] == "13/1"
    # 3 s of source at 13 fps, within the slack a legacy muxer costs.
    assert 2.7 <= duration(probe) <= 3.4
    assert 36 <= int(video["nb_frames"]) <= 46


def test_the_audio_track_is_carried_over(work: Work, needs_ffmpeg):
    source = work.source(seconds=2)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output))

    assert result.returncode == 0, result.stderr
    audio = stream(work.probe(str(output)), "audio")
    assert audio["codec_name"] in {"mp3", "mp2", "libmp3lame"}


def test_a_second_run_replaces_the_first(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    output = work.root / "out.avi"

    assert work.run("encode", source, "--out", str(output)).returncode == 0
    first = output.stat().st_size
    assert work.run("encode", source, "--out", str(output)).returncode == 0

    assert output.exists()
    assert abs(output.stat().st_size - first) < first * 0.5
    assert work.parts() == []


def test_a_trimmed_run_covers_only_the_trim_range(work: Work, needs_ffmpeg):
    source = work.source(seconds=4)
    output = work.root / "trimmed.avi"

    result = work.run("encode", source, "--out", str(output), "--from", "1", "--to", "2")

    assert result.returncode == 0, result.stderr
    assert 0.7 <= duration(work.probe(str(output))) <= 1.4


def test_an_interrupted_run_leaves_nothing_behind(work: Work, needs_ffmpeg):
    source = work.source(seconds=20)
    output = work.root / "out.avi"

    process = start(work, "encode", source, "--out", str(output))
    interrupt(process, delay=4)

    stdout, stderr = process.communicate(timeout=120)

    assert process.returncode == 130, f"expected 130, got {process.returncode}: {stderr[-400:]}"
    assert "Interrupted." in stderr
    assert not output.exists()
    assert work.parts() == []

    # And the two FFmpeg children are gone with it. A leftover encoder holds the
    # destination open, which is how a "cancelled" run keeps writing.
    leftover = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "(Get-Process ffmpeg -ErrorAction SilentlyContinue | Measure-Object).Count"]
        if os.name == "nt" else ["sh", "-c", "pgrep -c ffmpeg || true"],
        capture_output=True, text=True,
    ).stdout.strip()
    assert int(leftover or 0) == 0
