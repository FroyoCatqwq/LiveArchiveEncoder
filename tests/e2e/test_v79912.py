"""v7.9.9.12: pixel-visible tearing, a real global bitrate ceiling,
and the all-encoder dynamic frame-rate ladder.

The first test is deliberately pixel-based: the old suite only checked that
``tblend`` appeared in the command string, which is why a CUI wiring bug and a
static test pattern could hide a tear that never reached the delivered frames.
"""
from __future__ import annotations

import json
import os
from pathlib import Path

import cv2
import numpy as np
import pytest

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


def _moving_clip(path: Path, frames: int = 40) -> None:
    w, h = 320, 240
    out = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), 13, (w, h))
    for i in range(frames):
        f = np.zeros((h, w, 3), dtype=np.uint8)
        x = 10 + (i * 7) % (w - 40)
        f[:, x:x + 24] = 255
        out.write(f)
    out.release()


def _half_centres(path):
    cap = cv2.VideoCapture(str(path))
    pairs = []
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        h = frame.shape[0]
        vals = []
        for y0, y1 in ((0, h // 2), (h // 2, h)):
            gray = cv2.cvtColor(frame[y0:y1], cv2.COLOR_BGR2GRAY)
            cols = (gray > 180).astype(np.float32).sum(axis=0)
            if cols.max() < (y1 - y0) // 3:
                vals.append(None)
            else:
                vals.append(float((cols * np.arange(len(cols))).sum() / cols.sum()))
        if vals[0] is not None and vals[1] is not None:
            pairs.append(vals[1] - vals[0])
    cap.release()
    return pairs


def test_tearing_is_visible_in_the_delivered_pixels(exe, tmp_path):
    cfg = _cfg(tmp_path)
    source = tmp_path / "moving.mp4"
    _moving_clip(source)
    plain = tmp_path / "plain.mkv"
    torn = tmp_path / "torn.mkv"
    for out in (plain, torn):
        args = [str(source), str(out), "--encoder", "libx264",
                "--no-realtime", "--fps", "13", "--ffmpeg-source", "path",
                "--lang", "en", "--quiet"]
        if out == torn:
            args += ["--tearing", "horizontal"]
        proc = _run(exe, cfg, *args)
        assert proc.returncode == 0, (proc.stdout[-500:], proc.stderr[-500:])

    before = _half_centres(plain)
    after = _half_centres(torn)
    assert before and after
    assert max(abs(o) for o in before) < 2, before[:8]
    # The moving bar must be ~one frame apart across the tear line on nearly
    # every delivered frame -- not merely present in the -vf string.
    visible = sum(1 for o in after if abs(o) >= 2)
    assert visible >= len(after) * 0.8, (visible, len(after), after[:8])


def test_global_cap_really_limits_the_video_bitrate(exe, tmp_path):
    cfg = _cfg(tmp_path)
    # A high-entropy source with a deliberately generous per-encoder target.
    noisy = tmp_path / "noisy.mp4"
    make = harness.run([
        "ffmpeg", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x240:r=13:d=3",
        "-vf", "noise=alls=80:allf=t+u", "-c:v", "libx264", "-b:v", "3000k",
        str(noisy)])
    assert make.returncode == 0, make.stderr[-400:]

    results = {}
    for name, extra in (("free", []), ("cap", ["--max-video-kbps", "150"])):
        out = tmp_path / f"{name}.mkv"
        proc = _run(exe, cfg, str(noisy), str(out), "--encoder", "libx264",
                    "--limit", "2000", "--no-realtime", "--fps", "13",
                    "--ffmpeg-source", "path", "--lang", "en", "--quiet", *extra)
        assert proc.returncode == 0, (proc.stdout[-500:], proc.stderr[-500:])
        info = json.loads(harness.run(
            ["ffprobe", "-v", "error", "-show_format", "-show_streams",
             "-of", "json", str(out)]).stdout)
        results[name] = int(info["format"]["bit_rate"])

    # Without a cap the encoder spends the 2000 kbps target; with one, only the
    # 150 kbps video ceiling plus a small audio track remain.
    assert results["cap"] < results["free"] * 0.4, results


def test_dynamic_fps_repeats_frames_for_intra_only_encoder(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    reports = {}
    for name, extra in (("plain", []), ("dynamic", ["--dynamic-fps"])):
        out = tmp_path / f"{name}.avi"
        proc = _run(exe, cfg, str(src), str(out), "--encoder", "mjpeg",
                    "--fps", "13", "--fps-levels", "13,6,3", "--limit", "20",
                    "--end", "00:00:03", "--ffmpeg-source", "path", "--lang", "en", *extra)
        assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
        text = proc.stdout
        marker = "Repeated slots"
        assert marker in text, text[-800:]
        tail = text.rsplit(marker, 1)[1]
        reports[name] = int(tail.split(":", 1)[1].strip().split()[0])

    # The historical rule pinned fixed-quantiser encoders to one rung. With the
    # global mode on, the ladder must actually repeat frames.
    assert reports["dynamic"] > reports["plain"], reports
