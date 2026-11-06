"""v7.9.8.11.1: MJPEG must keep its frame rate and start both streams together.

Two regressions are pinned here:

* the bitrate ladder used to walk MJPEG down to its last rung because a fixed
  quantiser cannot reach the default 200 kbps ceiling;
* the audio track is read from the source a second time, so its timestamps need
  to be normalised and its input bounded to the video span.
"""

from __future__ import annotations

import re

import pytest

import harness
from harness import run

MODES = [
    pytest.param([], id="inline"),
    pytest.param(["--audio-mode", "auto"], id="audio-auto"),
    pytest.param(["--parallel", "2"], id="parallel2"),
]


def _encode(exe, src, out, extra):
    return run(
        [
            str(exe), str(src), str(out),
            "--encoder", "mjpeg",
            "--end-time", "00:00:03",
            "--lang", "en",
            *extra,
        ],
        timeout=180,
    )


def test_mjpeg_keeps_the_base_frame_rate(exe, src, workdir):
    out = workdir("mjpeg_fps") / "base_fps.avi"
    out.unlink(missing_ok=True)
    proc = _encode(exe, src, out, [])
    assert proc.returncode == 0, f"stdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"

    # The strong signal is the ladder's own state: before the fix these status
    # lines sat at motion=3fps for the whole run. Fresh-frame counts also depend
    # on decoder supply and machine load, so they only get a loose bound.
    assert "motion=3fps" not in proc.stdout, f"frame rate collapsed:\n{proc.stdout}"
    motion = re.findall(r"motion=(\d+(?:\.\d+)?)fps", proc.stdout)
    assert motion, f"no motion report in:\n{proc.stdout}"
    assert float(motion[-1]) >= 13.0, f"did not return to the base rate:\n{proc.stdout}"

    match = re.search(r"Fresh frames\s*:\s*(\d+)", proc.stdout)
    assert match, f"no fresh-frame report in:\n{proc.stdout}"
    assert int(match.group(1)) >= (3 * 13) * 0.5, (
        f"too many repeated slots: {match.group(1)} fresh frames"
    )


@pytest.mark.parametrize("extra", MODES)
def test_mjpeg_streams_start_together(exe, src, workdir, extra):
    label = "_".join(x.lstrip("-") for x in extra) or "inline"
    out = workdir("mjpeg_av") / f"av_{label}.avi"
    out.unlink(missing_ok=True)

    proc = _encode(exe, src, out, extra)
    assert proc.returncode == 0, f"stdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"

    info = harness.probe(out)
    assert info is not None, "ffprobe could not read the product"
    video = next(s for s in info["streams"] if s["codec_type"] == "video")
    audio = next(s for s in info["streams"] if s["codec_type"] == "audio")

    assert abs(float(video["start_time"]) - float(audio["start_time"])) < 0.05, (video, audio)
    assert abs(float(video["duration"]) - float(audio["duration"])) < 0.25, (video, audio)
