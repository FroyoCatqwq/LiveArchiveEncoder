"""Burning the subtitle track that is already inside the container.

The gap this closes: libass takes a file name, and there is no argument that
points it at a stream. So the run unwraps the track into a file first, burns that,
and deletes the copy -- and the interesting questions are whether the caption
lands on its own clock, and whether the copy is really gone afterwards.

A file left beside the recording is a side effect nobody asked for, and the next
run would find a mystery in the folder.
"""

from __future__ import annotations

import subprocess

from conftest import FFMPEG, Work, duration
from test_subtitles import caption, drawn_span


def mkv_with_a_subtitle_track(work: Work, seconds: float = 5.0) -> str:
    """An MKV whose streams are 0=video, 1=audio, 2=subtitle -- the order the test
    then has to name."""
    path = work.root / "src.mkv"
    subs = caption(work.root / "subs.srt")

    subprocess.run(
        [FFMPEG, "-y", "-hide_banner", "-loglevel", "error",
         "-f", "lavfi", "-i", f"testsrc2=size=320x240:rate=25:duration={seconds}",
         "-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}",
         "-i", subs,
         "-map", "0:v", "-map", "1:a", "-map", "2:s",
         "-c:v", "mpeg4", "-q:v", "4",
         "-c:a", "libmp3lame", "-b:a", "64k",
         "-c:s", "srt", "-shortest", str(path)],
        check=True, capture_output=True,
    )
    return str(path)


def test_the_embedded_track_is_burned_on_its_own_clock(work: Work, needs_ffmpeg):
    source = mkv_with_a_subtitle_track(work, seconds=5)
    plain = work.root / "plain.avi"
    burned = work.root / "burned.avi"

    common = ["encode", source, "--encoder", "mpeg4", "--quality", "6", "--limit", "0"]

    assert work.run(*common, "--out", str(plain)).returncode == 0
    result = work.run(*common, "--out", str(burned), "--subtitle-track", "2")
    assert result.returncode == 0, result.stderr

    first, last, frames = drawn_span(str(plain), str(burned), work)

    # The caption is at 3.00-4.00 in source time and this run starts at zero, so
    # the unwrap must not seek: a `-ss 0` would be harmless, a `-ss 2` would move
    # the cluster.
    assert 2.9 <= first <= 3.2, f"caption starts at {first:.2f}s, expected 3.00"
    assert 3.9 <= last <= 4.2, f"caption ends at {last:.2f}s, expected 4.00"
    assert 11 <= frames <= 15, f"{frames} frames carry text, expected about 13"

    # A burn must not change how long the programme is. Measured against the
    # source rather than against the 5 s the fixture asked for: `-shortest` stops
    # the MKV on its shortest stream, and the subtitle track's last event is at
    # 4 s, so the source itself is about 4.08 s long.
    source_length = duration(work.probe(source))
    assert abs(duration(work.probe(str(burned))) - source_length) <= 0.15


def test_the_unwrapped_copy_does_not_survive_the_run(work: Work, needs_ffmpeg):
    source = mkv_with_a_subtitle_track(work, seconds=2)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output), "--subtitle-track", "2")

    assert result.returncode == 0, result.stderr
    assert output.exists()
    leftovers = list(work.root.glob("lae-subs-*.ass"))
    assert leftovers == [], f"the unwrapped subtitle was left behind: {leftovers}"


def test_a_track_that_is_not_a_subtitle_is_refused_before_encoding(work: Work, needs_ffmpeg):
    """Index 0 is the video. Naming it is a usage error, not a crash and not a
    run that writes a file with no captions on it."""
    source = mkv_with_a_subtitle_track(work, seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output), "--subtitle-track", "0")

    assert result.returncode == 2, result.stderr
    assert "not subtitle" in result.stderr or "video" in result.stderr, result.stderr
    assert not output.exists()
    assert work.parts() == []


def test_a_dry_run_names_the_unwrapped_file_and_writes_nothing(work: Work, needs_ffmpeg):
    source = mkv_with_a_subtitle_track(work, seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output), "--subtitle-track", "2", "--dry-run")

    assert result.returncode == 0, result.stderr
    assert "subtitles=filename=" in result.stdout, result.stdout
    assert "lae-subs-2-" in result.stdout, result.stdout

    assert not output.exists(), "a dry run must not write anything"
    assert list(work.root.glob("lae-subs-*.ass")) == [], "a dry run must not unwrap anything"
