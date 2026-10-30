"""Carrying subtitle tracks into the output instead of drawing them.

This is the answer for a track that cannot be burned -- a WEB-DL's PGS tracks are
pictures of text, so libass has nothing to lay out -- and the only thing that keeps
the original cue timing.

The end-to-end suite can only synthesize *text* tracks (FFmpeg has no PGS encoder),
so the bitmap half is covered by the unit tests against a real
`hdmv_pgs_subtitle` probe, and was verified by hand on a 1080p WEB-DL whose two
PGS tracks came out of the run untouched.
"""

from __future__ import annotations

import subprocess

from conftest import FFMPEG, Work, stream
from test_embedded_subtitles import mkv_with_a_subtitle_track


def subtitles(probe: dict) -> list[dict]:
    return [item for item in probe["streams"] if item["codec_type"] == "subtitle"]


def test_a_text_track_is_carried_into_mkv(work: Work, needs_ffmpeg):
    source = mkv_with_a_subtitle_track(work, seconds=5)
    output = work.root / "carried.mkv"

    result = work.run("encode", source, "--out", str(output),
                      "--container", "mkv", "--subtitle-copy", "2")

    assert result.returncode == 0, result.stderr
    probe = work.probe(str(output))
    tracks = subtitles(probe)

    assert len(tracks) == 1, probe["streams"]
    # Copied, not re-encoded: the codec that went in comes out.
    assert tracks[0]["codec_name"] == "subrip", tracks[0]
    assert stream(probe, "video")["codec_name"] == "mpeg4"


def test_into_mp4_the_same_track_becomes_mov_text(work: Work, needs_ffmpeg):
    """MP4 has one subtitle codec and it is text, so this is a real conversion."""
    source = mkv_with_a_subtitle_track(work, seconds=5)
    output = work.root / "carried.mp4"

    result = work.run("encode", source, "--out", str(output),
                      "--container", "mp4", "--subtitle-copy", "2")

    assert result.returncode == 0, result.stderr
    tracks = subtitles(work.probe(str(output)))

    assert len(tracks) == 1, tracks
    assert tracks[0]["codec_name"] == "mov_text", tracks[0]


def test_an_avi_cannot_carry_a_subtitle_track(work: Work, needs_ffmpeg):
    """AVI has no subtitle muxer. The refusal has to come before the encode."""
    source = mkv_with_a_subtitle_track(work, seconds=1)
    output = work.root / "carried.avi"

    result = work.run("encode", source, "--out", str(output), "--subtitle-copy", "2")

    assert result.returncode == 2, result.stderr
    assert "no subtitle muxer" in result.stderr, result.stderr
    assert not output.exists(), "nothing should be encoded for a request that cannot work"


def test_naming_the_video_stream_as_a_subtitle_is_refused(work: Work, needs_ffmpeg):
    source = mkv_with_a_subtitle_track(work, seconds=1)
    output = work.root / "carried.mkv"

    result = work.run("encode", source, "--out", str(output),
                      "--container", "mkv", "--subtitle-copy", "0")

    assert result.returncode == 2, result.stderr
    assert "not subtitle" in result.stderr or "video" in result.stderr, result.stderr
    assert not output.exists()


def test_the_dry_run_shows_the_map_and_the_copy(work: Work, needs_ffmpeg):
    source = mkv_with_a_subtitle_track(work, seconds=1)
    output = work.root / "carried.mkv"

    result = work.run("encode", source, "--out", str(output),
                      "--container", "mkv", "--subtitle-copy", "2", "--dry-run")

    assert result.returncode == 0, result.stderr
    assert "-map 1:2" in result.stdout, result.stdout
    assert "-c:s copy" in result.stdout, result.stdout
    assert not output.exists()


def test_a_trimmed_run_still_carries_the_track(work: Work, needs_ffmpeg):
    """The second input is capped by the same -t as the audio, so an excerpt keeps
    its subtitle stream instead of running to the end of the source."""
    source = mkv_with_a_subtitle_track(work, seconds=6)
    output = work.root / "hand.mkv"

    result = work.run("encode", source, "--out", str(output), "--from", "0", "--to", "2",
                      "--container", "mkv", "--subtitle-copy", "2")

    assert result.returncode == 0, result.stderr
    assert len(subtitles(work.probe(str(output)))) == 1
    assert float(work.probe(str(output))["format"]["duration"]) <= 2.4
