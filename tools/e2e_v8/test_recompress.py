"""The post-encode chain: what a site would have done to the upload.

A recording can be published more than once -- a 2004 portal first, then the one
that replaced it -- and each hop is a real transcode of the previous product. The
rule the whole stage rests on is that the recording itself is never touched: a hop
that fails leaves it exactly as it was.

These cost wall-clock time because they are real encodes, so there are few of them
and each asserts several things.
"""

from __future__ import annotations

import subprocess

import pytest

from conftest import FFMPEG, Work, duration, stream


def codecs(work: Work, path) -> set[str]:
    probe = work.probe(str(path))
    return {item["codec_name"] for item in probe["streams"] if item["codec_type"] == "video"}


def require_encoder(name: str) -> None:
    listing = subprocess.run([FFMPEG, "-hide_banner", "-encoders"], capture_output=True, text=True).stdout
    if name not in listing:
        pytest.skip(f"this FFmpeg has no {name}")


def test_a_chain_of_two_hops_keeps_every_product(work: Work, needs_ffmpeg):
    require_encoder("libx264")

    source = work.source(seconds=2)
    output = work.root / "out.avi"

    result = work.run(
        "encode", source, "--out", str(output),
        # An MP4-era hop, then the AVI one that replaced it. Different containers
        # and different codecs, which is what makes this a chain rather than a
        # second encode of the same thing.
        "--recompress", "-c:v libx264 -crf 32",
        "--recompress", "-c:v mjpeg -q:v 4",
    )

    assert result.returncode == 0, result.stderr

    recording = work.root / "out.avi"
    first = work.root / "out_web.mp4"
    second = work.root / "out_web2.avi"

    # The recording survives the chain: it is the thing worth keeping.
    assert recording.exists()
    assert first.exists(), result.stdout
    assert second.exists(), result.stdout

    assert "mpeg4" in codecs(work, recording)
    assert "h264" in codecs(work, first)
    assert "mjpeg" in codecs(work, second)

    # Both hops keep the length: a transcode that drops the tail is the failure
    # this asserts against, not the picture.
    for path in (first, second):
        assert 1.7 <= duration(work.probe(str(path))) <= 2.4

    assert work.parts() == [], "a finished chain must not leave part files behind"


def test_the_container_follows_the_codec(work: Work, needs_ffmpeg):
    """A VP9 hop lands in .webm without being told to.

    Leaving the container to FFmpeg means guessing it before the codec is known,
    which is how `-c:v libvpx-vp9` ends up inside a file called _web.mp4 and dies
    in the muxer several seconds into the pass.
    """
    encoders = subprocess.run(
        [FFMPEG, "-hide_banner", "-encoders"], capture_output=True, text=True,
    ).stdout
    if "libvpx-vp9" not in encoders:
        pytest.skip("this FFmpeg has no libvpx-vp9")

    source = work.source(seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output),
                      "--recompress", "-c:v libvpx-vp9 -crf 40 -b:v 0")

    assert result.returncode == 0, result.stderr
    webm = work.root / "out_web.webm"
    assert webm.exists(), result.stdout
    assert "vp9" in codecs(work, webm)


def test_an_explicit_container_wins(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output),
                      "--recompress", "-c:v mjpeg -q:v 4",
                      "--recompress-container", "mov")

    assert result.returncode == 0, result.stderr
    assert (work.root / "out_web.mov").exists(), result.stdout


def test_the_recording_survives_a_hop_that_fails(work: Work, needs_ffmpeg):
    """A hop that cannot run must not cost the twenty minutes of capture.

    The exit code is still 1 -- the user asked for a chain and did not get one --
    but the recording is named in the message and is still there.
    """
    source = work.source(seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output),
                      "--recompress", "-c:v nosuchcodec -q:v 4")

    assert result.returncode == 1, result.stderr
    assert output.exists(), "the recording must survive a failed hop"
    assert not (work.root / "out_web.avi").exists()
    assert not (work.root / "out_web.mp4").exists()
    assert "the recording is untouched" in result.stderr
    assert work.parts() == []


def test_a_dry_run_shows_the_whole_chain(work: Work, needs_ffmpeg):
    """A dry run exists to be pasted into a shell, so it has to contain the hops.

    Printing the capture and hiding two transcodes is a plan that lies about what
    it is going to write.
    """
    source = work.source(seconds=1, audio=False)
    output = work.root / "out.avi"

    require_encoder("libx264")

    result = work.run("encode", source, "--out", str(output), "--dry-run",
                      "--recompress", "-c:v libx264 -crf 32",
                      "--recompress", "-c:v mjpeg -q:v 4")

    assert result.returncode == 0, result.stderr
    assert "recompress:" in result.stdout
    assert "out_web.mp4" in result.stdout
    assert "out_web2.avi" in result.stdout
    assert not output.exists(), "a dry run must not write anything"
    assert not (work.root / "out_web.mp4").exists()


def test_a_chain_can_run_against_a_file_that_already_exists(work: Work, needs_ffmpeg):
    """Tuning a chain should not cost a capture every time.

    --recompress-only skips the run and works on the file named on the command
    line, which must come out of it byte for byte the same.
    """
    source = work.source(seconds=1)
    live = work.root / "live.avi"
    assert work.run("encode", source, "--out", str(live)).returncode == 0

    before = live.read_bytes()

    result = work.run("encode", str(live),
                      "--recompress", "-c:v mjpeg -q:v 4",
                      "--recompress-container", "avi",
                      "--recompress-only")

    assert result.returncode == 0, result.stderr
    assert (work.root / "live_web.avi").exists(), result.stdout
    assert live.read_bytes() == before, "the recording must not be touched"
    assert work.parts() == []


def test_a_chain_the_muxer_cannot_hold_is_refused_before_encoding(work: Work, needs_ffmpeg):
    """The refusal has to come before the capture, not after it."""
    source = work.source(seconds=1, audio=False)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output),
                      "--recompress", "-c:v mjpeg -q:v 4",
                      "--recompress-container", "rm")

    assert result.returncode == 2, result.stderr
    assert not output.exists(), "the capture must not run for a chain that cannot work"
    assert "mjpeg cannot be written to .rm" in result.stderr
