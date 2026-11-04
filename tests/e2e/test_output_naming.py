"""Default output naming (v7.9.8.9).

Three rules, each of which used to be decided somewhere else:

* an extension the chosen encoder cannot write is **corrected**, not refused
  (``out.mp4`` with rv20 becomes ``out.rm``) -- this deliberately breaks from
  the v6.3.3 baseline, which is why the old ``bad_extension`` error-parity case
  in ``test_error_paths.py`` is gone;
* the corrected name never lands on the source, nor on a file nobody named;
* mp4 with H.264 carries AAC.

The network-share rule has unit tests only (``OutputAndAudioDefaultsTests``):
exercising it end to end needs a real share, and the suite has to pass on a
machine that has none.

    pytest tests/e2e/test_output_naming.py
"""

from __future__ import annotations

import harness
from harness import run


def _copy_source(directory, name: str = "clip.mp4"):
    directory.mkdir(parents=True, exist_ok=True)
    src = directory / name
    src.write_bytes(harness.SRC.read_bytes())
    return src


def _names(directory):
    return sorted(p.name for p in directory.iterdir())


def _codecs(path):
    info = harness.probe(path) or {}
    return [(s.get("codec_type"), s.get("codec_name")) for s in info.get("streams", [])]


def test_a_bad_extension_is_corrected_not_refused(exe, workdir):
    """rv20 cannot write mp4; the file is written as .rm and the run says so."""
    out = workdir("naming")
    src = _copy_source(out)

    proc = run([str(exe), str(src), str(out / "made.mp4"),
                "--encoder", "rv20", "--end-time", "00:00:02",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    stdout = proc.stdout
    assert "is not a container rv20 can write" in stdout, stdout[-400:]
    assert (out / "made.rm").is_file(), _names(out)
    assert not (out / "made.mp4").exists(), _names(out)
    assert ("video", "rv20") in _codecs(out / "made.rm"), _codecs(out / "made.rm")


def test_a_corrected_name_does_not_overwrite_an_existing_file(exe, workdir):
    """The corrected name is taken, so the number is bumped instead."""
    out = workdir("naming")
    src = _copy_source(out)
    taken = out / "taken.rm"
    taken.write_bytes(b"keep me")

    proc = run([str(exe), str(src), str(out / "taken.mp4"),
                "--encoder", "rv20", "--end-time", "00:00:02",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    assert (out / "taken (2).rm").is_file(), _names(out)
    assert taken.read_bytes() == b"keep me", "the corrected name overwrote a file nobody named"


def test_the_corrected_name_never_becomes_the_source(exe, workdir):
    """clip.rm with libx264 corrects to clip.mp4 -- which is the source."""
    out = workdir("naming")
    src = _copy_source(out)
    before = src.read_bytes()

    proc = run([str(exe), str(src), str(out / "clip.rm"),
                "--encoder", "libx264", "--end-time", "00:00:02",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    assert (out / "clip (2).mp4").is_file(), _names(out)
    assert src.read_bytes() == before, "the capture overwrote its own source"


def test_mp4_with_h264_carries_aac(exe, workdir):
    """The pair every player expects, and the one the second pass has always used."""
    out = workdir("naming")
    src = _copy_source(out)

    proc = run([str(exe), str(src), str(out / "h264.mp4"),
                "--encoder", "libx264", "--end-time", "00:00:02",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    assert ("audio", "aac") in _codecs(out / "h264.mp4"), _codecs(out / "h264.mp4")
