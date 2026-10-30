"""Burned subtitles: that they appear at all, and that they appear on time.

The interesting half is the timing. The decoder seeks with ``-ss`` before ``-i``,
so the output is rebased to zero while the subtitle file keeps the original
times. Get the shift wrong and every caption lands ``start`` seconds late -- and
a 20-minute excerpt taken from 18:00 has its captions arriving after the video
has ended.

The test asks a question a script can answer: burn the same caption into two
encodes that differ only in that flag, then compare them frame by frame. Every
frame differs a little, because libass re-renders the picture through RGBA, but
the frames carrying text differ a great deal. Where that cluster lands is where
the caption was drawn.
"""

from __future__ import annotations

import re
import subprocess

from conftest import FFMPEG, Work, duration, stream

# Frame rate the runs are encoded at, so a frame number can be turned into a time.
FPS = 13.0


def caption(path) -> str:
    """A four-second clip's caption sits at 3.00-4.00 in source time."""
    path.write_text(
        "1\n00:00:03,000 --> 00:00:04,000\nTHIS IS A CAPTION\n\n",
        encoding="utf-8",
    )
    return str(path)


def drawn_span(a: str, b: str, work: Work) -> tuple[float, float, int]:
    """Where the two encodes differ enough to be text: (first, last, frames)."""
    stats = work.root / "psnr.txt"
    stats.unlink(missing_ok=True)

    # The stats path goes inside a filtergraph, where a Windows drive colon is an
    # option separator; a bare name with cwd set is the spelling that parses.
    subprocess.run(
        [FFMPEG, "-hide_banner", "-loglevel", "error", "-i", a, "-i", b,
         "-lavfi", "psnr=stats_file=psnr.txt", "-f", "null", "-"],
        capture_output=True, cwd=str(work.root), check=True,
    )

    samples = []
    for line in stats.read_text(encoding="utf-8").splitlines():
        fields = dict(re.findall(r"(\w+):(\S+)", line))
        samples.append((int(fields["n"]), float(fields["mse_avg"])))

    peak = max(mse for _, mse in samples)
    loud = [(n - 1) / FPS for n, mse in samples if mse > max(1.0, peak * 0.5)]
    assert loud, f"the two encodes are indistinguishable (peak mse {peak:.2f})"
    return min(loud), max(loud), len(loud)


def test_a_caption_is_burned_into_the_picture(work: Work, needs_ffmpeg):
    source = work.source(seconds=5)
    subs = caption(work.root / "subs.srt")
    plain = work.root / "plain.avi"
    burned = work.root / "burned.avi"

    common = ["encode", source, "--encoder", "mpeg4", "--quality", "6", "--limit", "0", "--from", "0", "--to", "5"]
    assert work.run(*common, "--out", str(plain)).returncode == 0
    result = work.run(*common, "--out", str(burned), "--subtitle-file", subs)
    assert result.returncode == 0, result.stderr

    first, last, frames = drawn_span(str(plain), str(burned), work)

    # One second of caption at 13 fps. A frame or two of slack at each end: CFR
    # rounding puts the boundary frame on either side of the event time.
    assert 2.9 <= first <= 3.2, f"caption starts at {first:.2f}s, expected 3.00"
    assert 3.9 <= last <= 4.2, f"caption ends at {last:.2f}s, expected 4.00"
    assert 11 <= frames <= 15, f"{frames} frames carry text, expected about 13"

    # A burn must not change how long the programme is.
    assert abs(duration(work.probe(str(burned))) - 5.0) <= 0.15
    assert stream(work.probe(str(burned)), "video")["r_frame_rate"] == "13/1"


def test_a_trimmed_burn_keeps_the_caption_on_its_own_clock(work: Work, needs_ffmpeg):
    """The case that makes the shift worth having: start at 2 s, caption at 3 s."""
    source = work.source(seconds=5)
    subs = caption(work.root / "subs.srt")
    plain = work.root / "plain.avi"
    burned = work.root / "burned.avi"

    common = ["encode", source, "--encoder", "mpeg4", "--quality", "6", "--limit", "0", "--from", "2", "--to", "5"]
    assert work.run(*common, "--out", str(plain)).returncode == 0
    result = work.run(*common, "--out", str(burned), "--subtitle-file", subs)
    assert result.returncode == 0, result.stderr

    first, last, _ = drawn_span(str(plain), str(burned), work)

    # 3.00 and 4.00 in source time are 1.00 and 2.00 in the excerpt. Without the
    # pair of setpts calls this cluster would sit a full two seconds later.
    assert 0.9 <= first <= 1.2, f"caption starts at {first:.2f}s, expected 1.00"
    assert 1.9 <= last <= 2.2, f"caption ends at {last:.2f}s, expected 2.00"
    assert abs(duration(work.probe(str(burned))) - 3.0) <= 0.15


def test_a_missing_subtitle_file_fails_loudly(work: Work, needs_ffmpeg):
    """The planner does not check the disk, so FFmpeg's own complaint is the report."""
    source = work.source(seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output),
                      "--subtitle-file", str(work.root / "nope.srt"))

    assert result.returncode == 1, result.stderr
    assert "nope.srt" in result.stderr.lower() or "subtitle" in result.stderr.lower(), result.stderr
    assert not output.exists()
    assert work.parts() == []


def test_a_box_shape_is_named_in_the_dry_run(work: Work, needs_ffmpeg):
    """4:3 text on a 16:9 frame needs the crop/burn/overlay graph, and it is visible."""
    source = work.source(seconds=1, size="320x180")
    subs = caption(work.root / "subs.srt")

    result = work.run("encode", source, "--out", str(work.root / "out.avi"),
                      "--subtitle-file", subs, "--subtitle-box", "4:3", "--dry-run")

    assert result.returncode == 0, result.stderr
    assert "-filter_complex" in result.stdout, result.stdout
    assert "split=2" in result.stdout, result.stdout
    assert "overlay=" in result.stdout, result.stdout
