"""v7.9.9.10: the opt-in tear, and the DVD preset's real bitrate.

Two regressions are pinned here:

1. ``--tearing`` must reach the filter chain (it is the only thing that
   produces the artefact) and must stay off unless asked for.
2. ``--era dvd`` used to encode at ~2.1 Mbps because ``-q:v`` wins over
   ``-b:v`` inside FFmpeg; the preset says 6000, so the product has to land
   near it rather than at a third of it.
"""
from __future__ import annotations

import os

import harness


def _run(exe, config_dir, *args, timeout=300):
    env = {**os.environ,
           "LAE_CONFIG_DIR": str(config_dir),
           "LAE_LOG_DIR": str(config_dir / "logs"),
           "LAE_LOG": "0"}
    return harness.run([str(exe), *args], env=env, timeout=timeout)


def _cfg(tmp_path):
    path = tmp_path / "cfg"
    path.mkdir(exist_ok=True)
    return path


def test_tearing_is_off_by_default_and_lands_in_the_filter_chain(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    plain = tmp_path / "plain.mkv"
    proc = _run(exe, cfg, str(src), str(plain), "--encoder", "libx264",
                "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    assert "tblend" not in proc.stdout, proc.stdout[-800:]

    torn = tmp_path / "torn.mkv"
    proc = _run(exe, cfg, str(src), str(torn), "--encoder", "libx264",
                "--tearing", "horizontal", "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    assert "tblend=all_expr" in proc.stdout, proc.stdout[-800:]
    assert "H*0.5" in proc.stdout, proc.stdout[-800:]
    assert torn.is_file() and torn.stat().st_size > 0


def test_random_tearing_escapes_its_comma(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "random.mkv"
    proc = _run(exe, cfg, str(src), str(out), "--encoder", "libx264",
                "--tearing", "random", "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    # An unescaped comma would split the filtergraph and fail the encode.
    assert "mod(N*37" in proc.stdout, proc.stdout[-800:]
    assert out.is_file() and out.stat().st_size > 0


def test_an_unknown_tearing_mode_is_an_argument_error(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    proc = _run(exe, cfg, str(src), str(tmp_path / "x.mkv"),
                "--tearing", "bogus", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 2, (proc.stdout[-400:], proc.stderr[-400:])
    assert "--tearing" in (proc.stdout + proc.stderr)


def test_the_dvd_preset_really_encodes_at_its_bitrate(exe, src, tmp_path):
    """The preset says 6000 kbps; -q:v used to make FFmpeg ignore that."""
    cfg = _cfg(tmp_path)
    out = tmp_path / "dvd.mpg"
    proc = _run(exe, cfg, str(src), str(out), "--era", "dvd",
                "--end", "00:00:06", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en", "--quiet")
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])

    info = harness.probe(out) or {}
    kbps = float(info.get("format", {}).get("bit_rate") or 0) / 1000.0
    # 6000 video + 384 audio; the 6 s sample is short enough that VBV has room,
    # and the old buggy path produced ~2100.
    assert kbps > 4000, f"encoded at {kbps:.0f} kbps, expected near 6000"
