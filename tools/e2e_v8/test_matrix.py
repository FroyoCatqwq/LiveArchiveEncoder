"""v7 against v8, on the same source.

The comparison is deliberately in two stages, cheapest first:

1. the container facts a script can read -- duration, frame count, resolution,
   frame rate, codec, audio parameters;
2. the picture, as PSNR against the source.

Stage 1 is the one that catches a wrong argument; stage 2 is the one that catches
"the arguments were right and the frames were wrong". Comparing the two outputs
pixel-for-pixel is not useful and not attempted: v7 repeats frames according to
its adaptive ladder and v8 has not ported that yet, so the two files differ in
*when* a frame is repeated, which costs about 20 dB of PSNR between them and none
against the source.
"""

from __future__ import annotations

import re
import subprocess

from conftest import FFPROBE, Work, duration, stream

# v7's adaptive ladder drops the frame rate once the output passes its default
# 200 kbps ceiling. v8 has no ladder until M2, so the comparison has to run v7
# with a ceiling it will not reach -- otherwise it is comparing two different
# features, not two implementations of one.
V7_ARGS = ["--encoder", "mpeg4", "--qv", "8", "--width", "256", "--height", "192",
           "--fps", "13", "--limit", "5000", "--recover", "4000"]


def _v8(work: Work, source: str, output: str) -> None:
    result = work.run("encode", source, "--out", output, "--encoder", "mpeg4",
                      "--quality", "8", "--size", "256x192")
    assert result.returncode == 0, result.stderr


def _v7(work: Work, source: str, output: str) -> None:
    result = work.run_v7(*V7_ARGS, source, output)
    assert result.returncode == 0, result.stderr


def _psnr_against_source(source: str, candidate: str) -> float:
    """Average PSNR of a candidate against the source, normalised to its size."""
    result = subprocess.run(
        ["ffmpeg", "-hide_banner", "-i", source, "-i", candidate,
         "-lavfi", "[0:v]fps=13,scale=256:192:flags=neighbor,setsar=1[a];[1:v]setsar=1[b];[a][b]psnr",
         "-f", "null", "-"],
        capture_output=True, text=True,
    )
    match = re.search(r"average:([0-9.]+|inf)", result.stderr)
    assert match, result.stderr[-800:]
    return 99.0 if match.group(1) == "inf" else float(match.group(1))


def test_v8_matches_v7_on_everything_a_script_reads(work: Work, needs_ffmpeg, needs_v7):
    source = work.source(seconds=4)
    v7_out = work.root / "v7.avi"
    v8_out = work.root / "v8.avi"

    _v7(work, source, str(v7_out))
    _v8(work, source, str(v8_out))

    v7 = work.probe(str(v7_out))
    v8 = work.probe(str(v8_out))

    # Same container-level length. This is the assertion that pins the frame
    # count rule: TargetFrames = ceiling(span * fps), same as v7's.
    assert abs(duration(v7) - duration(v8)) <= 0.08, (duration(v7), duration(v8))

    v7_video, v8_video = stream(v7, "video"), stream(v8, "video")
    assert v7_video["codec_name"] == v8_video["codec_name"] == "mpeg4"
    assert v7_video["width"] == v8_video["width"] == 256
    assert v7_video["height"] == v8_video["height"] == 192
    assert v7_video["r_frame_rate"] == v8_video["r_frame_rate"] == "13/1"


def test_v8_audio_matches_v7(work: Work, needs_ffmpeg, needs_v7):
    source = work.source(seconds=3)
    v7_out = work.root / "v7.avi"
    v8_out = work.root / "v8.avi"

    _v7(work, source, str(v7_out))
    _v8(work, source, str(v8_out))

    v7_audio = stream(work.probe(str(v7_out)), "audio")
    v8_audio = stream(work.probe(str(v8_out)), "audio")

    assert v7_audio["codec_name"] == v8_audio["codec_name"]
    assert v7_audio["sample_rate"] == v8_audio["sample_rate"]
    assert v7_audio["channels"] == v8_audio["channels"]


def test_v8_picture_is_as_close_to_the_source_as_v7_is(work: Work, needs_ffmpeg, needs_v7):
    source = work.source(seconds=3)
    v7_out = work.root / "v7.avi"
    v8_out = work.root / "v8.avi"

    _v7(work, source, str(v7_out))
    _v8(work, source, str(v8_out))

    v7_psnr = _psnr_against_source(source, str(v7_out))
    v8_psnr = _psnr_against_source(source, str(v8_out))

    # Generous on purpose. The two differ in when each repeats a frame, so a
    # frame landing one slot apart costs a couple of dB against the source even
    # though both are faithful. A real quality regression is 10 dB, not 2.
    assert v8_psnr >= v7_psnr - 3.0, f"v7 {v7_psnr:.2f} dB, v8 {v8_psnr:.2f} dB"
