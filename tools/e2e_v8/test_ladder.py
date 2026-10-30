"""The adaptive ladder, and the presets that steer it.

The ladder is the one behaviour that is not visible in either FFmpeg command
line: it changes what goes down the pipe, not what the encoder was told. So these
tests assert what a user would actually notice -- the file gets smaller when the
ceiling is lowered, and it is still exactly as long.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

from conftest import FFMPEG, Work, duration, stream


def _static_plate(work: Work, seconds: float = 6.0) -> str:
    """A source that compresses to almost nothing: bars, no motion, no audio."""
    path = work.root / "bars.avi"
    subprocess.run(
        [FFMPEG, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
         "-i", f"smptebars=size=320x240:rate=25:duration={seconds}",
         "-c:v", "mpeg4", "-q:v", "4", "-y", str(path)],
        check=True, capture_output=True,
    )
    return str(path)


def test_a_low_ceiling_costs_bitrate_and_not_length(work: Work, needs_ffmpeg):
    source = work.source(seconds=8)
    free = work.root / "free.avi"
    tight = work.root / "tight.avi"

    # No ceiling worth the name: every slot carries a fresh frame.
    assert work.run("encode", source, "--out", str(free), "--limit",
                    "5000").returncode == 0

    # 60 kbps for 320x240 at 13 fps is not achievable; the ladder has to move.
    result = work.run("encode", source, "--out", str(tight), "--limit", "60")

    assert result.returncode == 0, result.stderr
    assert "motion fps" in result.stderr, result.stderr

    free_probe = work.probe(str(free))
    tight_probe = work.probe(str(tight))

    # Same programme, smaller file: that is the whole contract of the ladder.
    assert tight.stat().st_size < free.stat().st_size
    assert abs(duration(free_probe) - duration(tight_probe)) < 0.6

    video = stream(tight_probe, "video")
    assert video["r_frame_rate"] == "13/1", "the container rate is not what the ladder lowers"
    assert work.parts() == []


def test_a_preset_carries_its_settings_into_the_run(work: Work, needs_ffmpeg):
    assert work.run("preset", "add", "small", "--encoder", "mpeg4",
                    "--size", "160x120", "--limit", "80").returncode == 0

    source = work.source(seconds=2)
    output = work.root / "small.avi"

    dry = work.run("encode", source, "--preset", "small", "--out", str(output), "--dry-run")
    assert dry.returncode == 0, dry.stderr
    assert "160x120" in dry.stdout
    assert "80 kbps" in dry.stdout, dry.stdout

    result = work.run("encode", source, "--preset", "small", "--out", str(output))
    assert result.returncode == 0, result.stderr

    video = stream(work.probe(str(output)), "video")
    assert (video["width"], video["height"]) == (160, 120)


def test_an_unknown_preset_is_refused_before_anything_starts(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output), "--preset", "no-such-thing")

    assert result.returncode == 2, result.stderr
    assert "no preset named" in result.stderr
    assert not output.exists()
    assert work.parts() == []


def test_a_compressible_source_is_never_throttled(work: Work, needs_ffmpeg):
    """The other half of the ladder's contract.

    Dropping frames on a picture that already fits is a worse failure than never
    dropping at all: the output is visibly choppy for no reason, and the file is
    not even smaller. Bars at 320x240 sit far under the ceiling, so the ladder
    must stay on its top rung and the report must not mention motion at all.
    """
    source = _static_plate(work)
    output = work.root / "bars_out.avi"

    result = work.run("encode", source, "--out", str(output), "--limit", "200")

    assert result.returncode == 0, result.stderr
    assert "motion fps" not in result.stderr, result.stderr
    assert "ended at" not in result.stdout, result.stdout
    assert output.stat().st_size < 400_000


def test_named_rungs_reach_the_plan(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    output = work.root / "rungs.avi"

    dry = work.run("encode", source, "--out", str(output), "--dry-run",
                   "--limit", "120", "--fps-levels", "13,8,5")

    assert dry.returncode == 0, dry.stderr
    assert "13 > 8 > 5 fps" in dry.stdout, dry.stdout
    assert "120 kbps" in dry.stdout, dry.stdout
