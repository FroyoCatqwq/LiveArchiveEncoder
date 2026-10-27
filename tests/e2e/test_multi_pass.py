"""A chain of recompress passes (v7.9.8.3).

A recording can be published more than once -- a 2007 portal first, the 2011 one
after it -- and each hop is a real transcode, so `--recompress-args` is
repeatable and every hop reads the previous hop's product.

    pytest tests/e2e/test_multi_pass.py

The two things worth pinning down are the naming (each hop keeps its own file,
and the name comes from the live product rather than from the previous hop) and
the container inference: a hop named by `--recompress-codec` decides its own
container, which is what pass 1 always did but pass 2 used to refuse.
"""

from __future__ import annotations

import harness
from harness import run


def _streams(path):
    info = harness.probe(path) or {}
    return [(s.get("codec_type"), s.get("codec_name")) for s in info.get("streams", [])]


def _names(directory):
    return sorted(p.name for p in directory.iterdir())


def test_two_passes_write_one_file_each(exe, workdir):
    """Pass 2 reads pass 1's file and is named from the live product."""
    out = workdir("multipass")
    src = out / "clip.mp4"
    src.write_bytes(harness.SRC.read_bytes())

    proc = run([str(exe), str(src), "--recompress-only",
                "--recompress-args", "-c:v libx264 -crf 26",
                "--recompress-container", "mp4",
                "--recompress-args", "-c:v flv -q:v 9",
                "--recompress-container", "flv",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")

    first, second = out / "clip_web.mp4", out / "clip_web2.flv"
    assert first.is_file(), f"expected {first.name}, found {_names(out)}"
    assert second.is_file(), f"expected {second.name}, found {_names(out)}"

    assert ("video", "h264") in _streams(first), _streams(first)
    assert ("video", "flv1") in _streams(second), _streams(second)


def test_a_hop_named_by_codec_picks_its_own_container(exe, workdir):
    """Pass 2 named by codec lands in .flv without being told so.

    It used to be refused with "flv cannot be written to .mp4": the container had
    defaulted before the codec was consulted, because only pass 1 ever inferred
    one. A chain written as two fragments therefore needed a redundant
    --recompress-container for every hop.
    """
    out = workdir("multipass")
    src = out / "infer.mp4"
    src.write_bytes(harness.SRC.read_bytes())

    proc = run([str(exe), str(src), "--recompress-only",
                "--recompress-args", "-c:v libx264 -crf 26",
                "--recompress-args", "-c:v flv -q:v 9",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    second = out / "infer_web2.flv"
    assert second.is_file(), f"expected {second.name}, found {_names(out)}"
    assert ("video", "flv1") in _streams(second), _streams(second)


def test_a_single_pass_keeps_the_old_name(exe, workdir):
    """One hop still writes `_web`, not `_web1`."""
    out = workdir("multipass")
    src = out / "alone.mp4"
    src.write_bytes(harness.SRC.read_bytes())

    proc = run([str(exe), str(src), "--recompress-only",
                "--recompress-codec", "libx264", "--recompress-quality", "30",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    assert (out / "alone_web.mp4").is_file(), _names(out)
    assert not (out / "alone_web1.mp4").exists(), _names(out)


def test_quality_is_paired_by_position(exe, workdir):
    """v7.9.8.4: each named-codec hop gets its own quality.

    One number cannot serve two scales -- 26 is a sensible CRF and an absurd
    MPEG quantiser -- so the flag repeats and pairs by position. The run prints
    the argv of every hop before it starts them, which is the assertion target:
    a per-pass flag that silently collapsed to one value would show up here.
    """
    out = workdir("multipass")
    src = out / "pair.mp4"
    src.write_bytes(harness.SRC.read_bytes())

    proc = run([str(exe), str(src), "--recompress-only",
                "--recompress-codec", "libx264", "--recompress-quality", "26",
                "--recompress-codec", "flv", "--recompress-quality", "9",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    hops = [line for line in proc.stdout.splitlines() if " -crf " in line or " -q:v " in line]
    assert len(hops) == 2, f"expected two hops, got {hops}"
    assert "-crf 26" in hops[0], hops[0]
    assert "-q:v 9" in hops[1], hops[1]
    assert (out / "pair_web.mp4").is_file(), _names(out)
    assert (out / "pair_web2.flv").is_file(), _names(out)
