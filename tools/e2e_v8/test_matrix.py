"""v7 against v8, on the same source.

The comparison is deliberately in two stages, cheapest first:

1. the container facts a script can read -- duration, frame count, resolution,
   frame rate, codec, audio parameters;
2. the picture, as PSNR against the source.

Stage 1 is the one that catches a wrong argument; stage 2 is the one that catches
"the arguments were right and the frames were wrong".

The three comparison tests run the two programs with the *ladder switched off*
(v7's ceiling parked at 5000 kbps), which makes them a comparison of the encoder
arguments alone. The ladder has its own test at the bottom, where both programs
are told the same ceiling: that one compares decisions about which frames to
repeat, and it is the only place the two are expected to agree on something
timing-dependent.
"""

from __future__ import annotations

import re
import subprocess

from conftest import FFPROBE, Work, duration, stream

# A ceiling v7 will never reach, so its adaptive ladder stays on the top rung and
# the comparison is about the pipeline rather than about throttling. The ladder
# itself is compared separately, at the end of this file.
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


def test_the_two_ladders_reach_the_same_picture_size(work: Work, needs_ffmpeg, needs_v7):
    """The only timing-dependent comparison in the suite.

    Same source, same ceiling, same rungs, same quality. Both programs throttle
    and both keep the programme the same length; the file sizes are allowed to
    differ by a third, and it is worth being precise about why that much.

    v7's first ladder reading on this material comes out at **1096 kbps** -- an
    artefact of dividing a fresh run's opening bytes by a window that has barely
    started -- so it fires three times in three slots and spends the next three
    seconds at 3 fps. v8 reads 326 kbps at the same moment, because its
    denominator is the programme span the bytes actually cover. The two therefore
    disagree about *when* to throttle, not about the ceiling, and on a six-second
    clip that shows up as roughly 20% in file size. A wrong ceiling, a ladder that
    never fires, or one that never stops firing would all be far outside this
    band; v8's earlier no-ladder build came out 13% above v7 on a long clip.
    """
    source = work.source(seconds=6)
    v7_out = work.root / "v7.avi"
    v8_out = work.root / "v8.avi"

    v7 = work.run_v7("--encoder", "mpeg4", "--qv", "8", "--width", "256", "--height", "192",
                     "--fps", "13", "--limit", "200", "--recover", "150",
                     source, str(v7_out))
    assert v7.returncode == 0, v7.stderr

    v8 = work.run("encode", source, "--out", str(v8_out), "--encoder", "mpeg4",
                  "--quality", "8", "--size", "256x192", "--limit", "200")
    assert v8.returncode == 0, v8.stderr

    # v8 says so on stderr when its rung moves; v7 only when the console is not
    # quiet. Both must have moved, or this comparison is vacuous.
    assert "motion fps" in v8.stderr, v8.stderr
    assert "motion FPS" in v7.stdout, v7.stdout

    v7_size, v8_size = v7_out.stat().st_size, v8_out.stat().st_size
    assert v8_size <= v7_size * 1.35, f"v7 {v7_size} bytes, v8 {v8_size} bytes"
    assert v8_size >= v7_size * 0.65, f"v7 {v7_size} bytes, v8 {v8_size} bytes"

    # Whatever the ladders decided, the programme is the same length and the
    # container rate is untouched: the ladder drops pictures, not time.
    v7_probe, v8_probe = work.probe(str(v7_out)), work.probe(str(v8_out))
    assert abs(duration(v7_probe) - duration(v8_probe)) <= 0.1
    assert stream(v8_probe, "video")["r_frame_rate"] == "13/1"
