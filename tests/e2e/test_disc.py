"""v7.9.9.8: disc images and physical disc targets.

The numbers asserted here are the published format limits, not preferences:
DVD-Video is MPEG-2 at 720x576/25 (PAL) or 720x480/30 (NTSC) with AC-3 at
48 kHz; Video CD and CD-i are MPEG-1 at 352x288/240 with MP2 at 44.1 kHz and
224 kbit/s. A disc image that is neither DVD-Video nor VCD is refused instead
of being encoded blind.
"""
from __future__ import annotations

import os

import harness


def _run(exe, config_dir, *args, timeout=240):
    env = {**os.environ,
           "LAE_CONFIG_DIR": str(config_dir),
           "LAE_LOG_DIR": str(config_dir / "logs"),
           "LAE_LOG": "0"}
    return harness.run([str(exe), *args], env=env, timeout=timeout)


def _cfg(tmp_path):
    path = tmp_path / "cfg"
    path.mkdir(exist_ok=True)
    return path


def _streams(path):
    info = harness.probe(path) or {}
    return {s.get("codec_type"): s for s in info.get("streams", [])}, info


def test_disc_vcd_pins_the_video_cd_envelope(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "vcd.mpg"

    proc = _run(exe, cfg, str(src), str(out), "--disc", "vcd",
                "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en", "--quiet")
    assert proc.returncode == 0, (proc.stdout[-700:], proc.stderr[-700:])

    streams, _ = _streams(out)
    video, audio = streams.get("video", {}), streams.get("audio", {})
    assert video.get("codec_name") == "mpeg1video", video
    assert (video.get("width"), video.get("height")) == (352, 288), video
    assert audio.get("codec_name") == "mp2", audio
    assert audio.get("sample_rate") == "44100", audio
    assert audio.get("channels") == 2, audio


def test_disc_dvd_pins_the_dvd_video_envelope(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "dvd.mpg"

    proc = _run(exe, cfg, str(src), str(out), "--disc", "dvd",
                "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en", "--quiet")
    assert proc.returncode == 0, (proc.stdout[-700:], proc.stderr[-700:])

    streams, _ = _streams(out)
    video, audio = streams.get("video", {}), streams.get("audio", {})
    assert video.get("codec_name") == "mpeg2video", video
    assert (video.get("width"), video.get("height")) == (720, 576), video
    assert audio.get("codec_name") == "ac3", audio
    assert audio.get("sample_rate") == "48000", audio


def test_disc_cd_and_ntsc_switch(exe, src, tmp_path):
    """CD-i shares VCD's MPEG-1 envelope; --fps 29.97 selects the NTSC sizes."""
    cfg = _cfg(tmp_path)
    cd = tmp_path / "cdi.mpg"
    proc = _run(exe, cfg, str(src), str(cd), "--disc", "cd",
                "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en", "--quiet")
    assert proc.returncode == 0, (proc.stdout[-700:], proc.stderr[-700:])
    streams, _ = _streams(cd)
    assert streams.get("video", {}).get("codec_name") == "mpeg1video", streams

    ntsc = tmp_path / "dvd-ntsc.mpg"
    proc = _run(exe, cfg, str(src), str(ntsc), "--disc", "dvd",
                "--fps", "29.97", "--end", "00:00:02", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en", "--quiet")
    assert proc.returncode == 0, (proc.stdout[-700:], proc.stderr[-700:])
    streams, _ = _streams(ntsc)
    video = streams.get("video", {})
    assert (video.get("width"), video.get("height")) == (720, 480), video


def test_an_unknown_disc_target_is_an_argument_error(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    proc = _run(exe, cfg, str(src), str(tmp_path / "x.mpg"), "--disc", "bluray",
                "--no-realtime", "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 2, (proc.stdout[-400:], proc.stderr[-400:])
    assert "--disc" in (proc.stdout + proc.stderr)


def test_a_non_video_iso_is_refused(exe, tmp_path):
    """A data disc is not a DVD-Video or a VCD and must not be encoded."""
    cfg = _cfg(tmp_path)
    fake = tmp_path / "data.iso"
    fake.write_bytes(b"\x00" * 2048)
    proc = _run(exe, cfg, str(fake), str(tmp_path / "out.mpg"),
                "--disc", "dvd", "--no-realtime",
                "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 2, (proc.stdout[-400:], proc.stderr[-400:])
    text = (proc.stdout + proc.stderr).lower()
    assert "iso" in text or "disc" in text, text[-400:]
