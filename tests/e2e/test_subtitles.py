"""Subtitle end-to-end (pytest).

v7.9 goal 2: subtitles can be read (sidecar file or embedded track), burned
into the picture, or carried as a stream.

    pytest tests/e2e/test_subtitles.py

Fixtures live in tests/media/: test_src.ass and test_src.srt say "HELLO
SUBTITLE" between 0.5-2.0s and "SECOND LINE" between 2.5-4.0s; test_subs.mkv
is test_src.mp4 plus that SubRip track as stream 2.

A flag that is accepted and then ignored would pass every structural check, so
test_burn_changes_the_picture compares a burned encode against a plain one: at
a moment where the subtitle is on screen the two videos must differ, and the
burned frame was verified by eye to carry the text (see the session notes).
"""

from __future__ import annotations

import pytest

import harness
from harness import run

ASS = harness.TESTS / "media" / "test_src.ass"
SRT = harness.TESTS / "media" / "test_src.srt"
MKV = harness.TESTS / "media" / "test_subs.mkv"


def _encode(exe, workdir, name, src, extra, ext="avi"):
    out = workdir("subs")
    dst = out / f"{name}.{ext}"
    proc = run([str(exe), str(src), str(dst), "--end", "00:00:03", *extra,
                "--ffmpeg-source", "path"])
    return proc, dst


def _reject(exe, workdir, extra, ext="avi"):
    out = workdir("subs")
    dst = out / f"reject.{ext}"
    return run([str(exe), str(harness.SRC), str(dst), "--end", "00:00:03", *extra,
                "--ffmpeg-source", "path"])


def _video_md5(path):
    proc = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:v", "-f", "md5", "-"])
    assert proc.returncode == 0, f"cannot hash {path}: {proc.stderr[-200:]}"
    return proc.stdout.strip()


@pytest.fixture(scope="module")
def sub_media():
    for path in (ASS, SRT, MKV):
        if not path.is_file():
            raise pytest.UsageError(f"missing subtitle fixture: {path}")


# ---------------------------------------------------------------------------
# The happy paths
# ---------------------------------------------------------------------------

def test_burn_sidecar_ass(exe, sub_media, workdir):
    """An external .ass file is rendered into the picture."""
    proc, dst = _encode(exe, workdir, "burn_ass", harness.SRC,
                        ["--encoder", "mpeg4", "--subtitle", str(ASS)])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert harness.video_summary(dst) is not None


def test_burn_sidecar_srt(exe, sub_media, workdir):
    proc, dst = _encode(exe, workdir, "burn_srt", harness.SRC,
                        ["--encoder", "mpeg4", "--subtitle", str(SRT)])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert harness.video_summary(dst) is not None


def test_burn_embedded_track(exe, sub_media, workdir):
    """--subtitle-stream picks a track inside the source; it is staged to a
    file the filter can read, then burned like a sidecar."""
    proc, dst = _encode(exe, workdir, "burn_stream", MKV,
                        ["--encoder", "mpeg4", "--subtitle-stream", "2"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert harness.video_summary(dst) is not None


def test_burn_changes_the_picture(exe, sub_media, workdir):
    """The burned encode is not the same video as the plain one.

    The subtitle is only on screen for part of the clip, so the *video stream*
    differs while everything else is identical -- which is the point.
    """
    plain, plain_dst = _encode(exe, workdir, "plain", harness.SRC, ["--encoder", "mpeg4"])
    burned, burned_dst = _encode(exe, workdir, "burned", harness.SRC,
                                 ["--encoder", "mpeg4", "--subtitle", str(SRT)])
    assert plain.returncode == 0 and burned.returncode == 0

    assert _video_md5(plain_dst) != _video_md5(burned_dst), \
        "burning had no effect on the picture"


def test_copy_sidecar_into_mkv(exe, sub_media, workdir):
    """--subtitle-mode copy carries the track as a real stream, transcoded to
    the codec the container prefers (ass in Matroska, per ffmpeg -encoders)."""
    proc, dst = _encode(exe, workdir, "copy_mkv", harness.SRC,
                        ["--encoder", "libx264", "--container", "mkv",
                         "--subtitle", str(SRT), "--subtitle-mode", "copy"], ext="mkv")

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    streams = (harness.probe(dst) or {}).get("streams", [])
    subs = [s for s in streams if s.get("codec_type") == "subtitle"]
    assert subs, f"no subtitle stream in the output: {streams}"
    assert subs[0].get("codec_name") == "ass", f"expected ass, got {subs[0].get('codec_name')}"


# The bitmap policy (burn refused, copy allowed for dvdsub/dvbsub/xsub) is
# pinned by the unit tests: FFmpeg refuses text->bitmap transcoding, so a real
# bitmap fixture would test FFmpeg more than this program.

def test_bad_request_is_refused(exe, sub_media, workdir):
    """Every refused request fails before any encoding, with the reason.

    Each case carries its own encoder where the container matters: copy into
    AVI would actually work (xsub), so the no-container case names rv20 (RM).
    """
    out = workdir("subs")
    # Real stubs, so cases that test *later* checks get past the
    # missing-file check (which the first case already covers).
    side1 = out / "side1.srt"
    side2 = out / "side2.srt"
    for stub in (side1, side2):
        stub.write_text("1\n00:00:00,500 --> 00:00:01,500\nstub\n", encoding="utf-8")

    cases = [
        # mpeg4 keeps the .avi output valid, so the *subtitle* check is what
        # fires rather than the output-extension check.
        (["--encoder", "mpeg4", "--subtitle", "no-such-file.ass"],
         "subtitle file not found", "avi"),
        (["--encoder", "mpeg4", "--subtitle", str(side1), "--subtitle-stream", "2"],
         "either --subtitle (an external file) or --subtitle-stream", "avi"),
        (["--encoder", "mpeg4", "--subtitle-stream", "9"],
         "does not name a stream", "avi"),
        (["--encoder", "mpeg4", "--subtitle-stream", "0"],
         "not a subtitle", "avi"),
        (["--encoder", "mpeg4", "--subtitle", str(side1), "--subtitle", str(side2),
          "--subtitle-mode", "copy"], "copy takes one subtitle file", "avi"),
        # rm has no subtitle stream concept, and the .rm output keeps the
        # extension check quiet so the container refusal is what surfaces.
        (["--subtitle", str(side1), "--subtitle-mode", "copy", "--encoder", "rv20"],
         "cannot carry a subtitle stream", "rm"),
        (["--subtitle", str(side1), "--subtitle-mode", "copy",
          "--encoder", "libx264", "--container", "mkv",
          "--subtitle-codec", "fancy"], "cannot write subtitle codec 'fancy'", "mkv"),
    ]
    for extra, want, ext in cases:
        proc = _reject(exe, workdir, extra, ext)
        blob = proc.stdout + proc.stderr
        assert proc.returncode != 0, f"expected failure for {extra}, got rc=0"
        assert want in blob, f"missing {want!r} in: {blob[-300:]}"


def test_bad_mode_is_a_parse_error(exe):
    proc = run([str(exe), "--lang", "en", "--no-cui", "--subtitle-mode", "squash"])
    assert proc.returncode == 2
    assert "--subtitle-mode must be one of" in (proc.stdout + proc.stderr)
