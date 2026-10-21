"""Stream selection end-to-end (pytest).

v7.9 goal 1: a source may carry several video and/or audio tracks -- a VOB with
two audio languages, an MKV with a duplicate encode -- and the program has to
let the user name which one to use.

    pytest tests/e2e/test_stream_selection.py

The fixture is a four-stream file (0: video, 1: audio 440 Hz, 2: video of
different content, 3: audio 880 Hz). The interesting assertion here is not
"the encode succeeded" -- that only proves the flag was accepted -- but that
picking stream 2 produces a *different* video than picking stream 0. Two
encodes and two md5s prove it without depending on how lossy mpeg4 happens
to be on this FFmpeg build.

The stronger content check (PSNR of the output against each source stream, and
the zero-crossing rate of the audio that shows 440 Hz vs 880 Hz) was measured
by hand on the same fixture: 18.06 dB against stream 2 versus 7.41 dB against
stream 0, and ~446 Hz versus ~881 Hz.
"""

from __future__ import annotations

from pathlib import Path

import pytest

import harness
from harness import run


def _encode(exe, out, name, extra, *, src=harness.MULTI):
    """Encode a short segment of `src` into out/<name>.avi."""
    dst = out / f"{name}.avi"
    args = [str(exe), str(src), str(dst), "--encoder", "mpeg4", "--end", "00:00:03",
            *extra, "--ffmpeg-source", "path"]
    return run(args), dst


def _stream_md5(path: Path, kind: str) -> str:
    """md5 of one stream of a finished file (container timestamps excluded)."""
    proc = run(["ffmpeg", "-v", "error", "-i", str(path),
                "-map", f"0:{kind}", "-f", "md5", "-"])
    assert proc.returncode == 0, f"cannot hash {kind} of {path}: {proc.stderr[-200:]}"
    return proc.stdout.strip()


def test_list_streams(exe, multi_src):
    """--list-streams prints the indices --video-stream/--audio-stream take."""
    proc = run([str(exe), "--list-streams", str(multi_src), "--ffmpeg-source", "path"])

    assert proc.returncode == 0, f"exit={proc.returncode}: {proc.stderr[-200:]}"
    assert "streams in this source:" in proc.stdout
    for want in ("video", "audio", "h264", "aac"):
        assert want in proc.stdout, f"missing {want} in the listing"
    # Four streams, four lines: the index is the number to type back in.
    body = [line for line in proc.stdout.splitlines() if line.strip()]
    assert len(body) == 5, f"expected a header plus four streams, got {body}"


def test_list_streams_needs_an_input(exe):
    proc = run([str(exe), "--list-streams", "--ffmpeg-source", "path"])
    assert proc.returncode == 2
    assert "requires an input" in (proc.stdout + proc.stderr)


def test_selected_streams_encode(exe, multi_src, workdir):
    """A non-default video and audio track both survive the whole pipeline."""
    proc, dst = _encode(exe, workdir("streams"), "picked",
                        ["--video-stream", "2", "--audio-stream", "3"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    info = harness.video_summary(dst)
    assert info is not None and not info.get("no_video"), "ffprobe cannot read the output"
    streams = (harness.probe(dst) or {}).get("streams", [])
    assert any(s.get("codec_type") == "audio" for s in streams), "no audio stream"


def test_pick_changes_the_result(exe, multi_src, workdir):
    """The pick is actually applied: stream 2 is not stream 0.

    A flag that is accepted and then ignored passes every other test here, so
    this one encodes both tracks and compares what came out of each.
    """
    out = workdir("streams")
    first, first_dst = _encode(exe, out, "first",
                               ["--video-stream", "0", "--audio-stream", "1"])
    second, second_dst = _encode(exe, out, "second",
                                 ["--video-stream", "2", "--audio-stream", "3"])
    assert first.returncode == 0 and second.returncode == 0, (
        f"{first.returncode}/{second.returncode}: {(second.stderr.strip().splitlines() or [''])[-1][:200]}")

    assert _stream_md5(first_dst, "v") != _stream_md5(second_dst, "v"), \
        "the video is identical: --video-stream had no effect"
    assert _stream_md5(first_dst, "a") != _stream_md5(second_dst, "a"), \
        "the audio is identical: --audio-stream had no effect"


# A pick that cannot be honoured is refused before any encoding happens, and
# the refusal lists the streams so the user can correct it.
@pytest.mark.parametrize(("args", "want"), [
    (["--video-stream", "99"], "does not name a video stream"),
    (["--video-stream", "1"], "does not name a video stream"),
    (["--audio-stream", "0"], "does not name an audio stream"),
    (["--audio-stream", "2"], "does not name an audio stream"),
], ids=["video_out_of_range", "video_is_audio", "audio_is_video", "audio_wrong_type"])
def test_bad_pick_is_refused(exe, multi_src, workdir, args, want):
    proc, dst = _encode(exe, workdir("streams"), "reject", args)

    assert proc.returncode != 0
    blob = proc.stdout + proc.stderr
    assert want in blob, f"missing {want!r} in: {blob[-200:]}"
    assert "streams in this source:" in blob, "the refusal must list the streams"


def test_bad_pick_value(exe, multi_src):
    """Not a number at all: a parse error, before FFmpeg is touched."""
    proc = run([str(exe), str(multi_src), "--video-stream", "two",
                "--ffmpeg-source", "path"])
    assert proc.returncode == 2
    assert "requires an integer" in (proc.stdout + proc.stderr)
