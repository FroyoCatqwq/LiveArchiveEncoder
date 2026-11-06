"""v7.9.8.11: the live MJPEG pass must write a full-range (JPEG) picture.

FFmpeg's mjpegenc rejects limited-range YUV at the default strictness
(libavcodec/mjpegenc_common.c, ff_mjpeg_encode_check_pix_fmt), unless
``-strict unofficial`` is passed -- which this tool never does. The live pass
used to inherit both the source's limited-range transport format and its
``tv`` colour tag, so ``--encoder mjpeg`` died in the preflight with
"Non full-range YUV is non-standard". Pin the product's range here, not just
the argument builder.
"""

from __future__ import annotations

import harness
from harness import run


def test_mjpeg_live_pass_is_full_range(exe, src, workdir):
    out = workdir("mjpeg_range") / "mjpeg_full_range.avi"
    out.unlink(missing_ok=True)

    proc = run(
        [
            str(exe), str(src), str(out),
            "--encoder", "mjpeg",
            "--end-time", "00:00:02",
            "--lang", "en",
            "--quiet",
        ],
        timeout=180,
    )
    assert proc.returncode == 0, f"stdout:\n{proc.stdout}\nstderr:\n{proc.stderr}"

    info = harness.probe(out)
    assert info is not None, "ffprobe could not read the MJPEG product"
    video = next(s for s in info["streams"] if s["codec_type"] == "video")
    assert video["codec_name"] == "mjpeg"
    assert video["pix_fmt"].startswith("yuvj"), video
    # A container that records the range must record the full one.
    assert video.get("color_range", "unknown") in ("pc", "jpeg", "unknown"), video
