"""v7.9.9.2 end-to-end regressions.

1. Direct (--no-realtime) compression must stay A/V synchronised. v7.9.9.1
   emitted all 65 slots in 0.44 s but only 3 of them were real frames -- a
   still picture over a full-length audio track.
2. The default FPS ladder follows the era rungs; ``none`` removes it.
3. Input/output directories are remembered, and naming a directory as the
   output completes it into a real filename.
4. A busy target is refused before a long encode, stale ``.part`` files are
   removed, and an alternate ``(2)`` save is reported AND followed downstream.
5. Generic encoder args (VBV triple, -profile:v / -ac / -b:a ...) work for
   both passes, with the -b:v dependency enforced.

Every test uses its OWN config root (``tmp_path``), so this module never
touches the shared settings file the ``shared_config`` group pins.
"""
from __future__ import annotations

import json
import os
import re
import threading
import time

import pytest

import harness

WINDOWS = os.name == "nt"

if WINDOWS:
    import ctypes
    from ctypes import wintypes

    _k32 = ctypes.windll.kernel32
    _CreateFileW = _k32.CreateFileW
    _CreateFileW.restype = wintypes.HANDLE
    _CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                             wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD,
                             wintypes.HANDLE]
    _CloseHandle = _k32.CloseHandle
    _CloseHandle.argtypes = [wintypes.HANDLE]
    _GENERIC_RW = 0x80000000 | 0x40000000
    _CREATE_ALWAYS = 2
    _FILE_NORMAL = 0x80
    _INVALID = ctypes.c_void_p(-1).value


def _env(config_dir):
    return {**os.environ,
            "LAE_CONFIG_DIR": str(config_dir),
            "LAE_LOG_DIR": str(config_dir / "logs"),
            "LAE_LOG": "0"}


def _run(exe, config_dir, *args, timeout=240):
    return harness.run([str(exe), *args], env=_env(config_dir), timeout=timeout)


def _cfg(tmp_path):
    path = tmp_path / "cfg"
    path.mkdir(exist_ok=True)
    return path


def _encode(exe, src, out, *extra):
    return [str(src), str(out), "--encoder", "mpeg4", "--end", "00:00:02",
            "--ffmpeg-source", "path", "--lang", "en", *extra]


def _frame_report(stdout):
    """The report prints the two counters on separate lines (with blank lines
    from the console mirror), not as one comma-separated line. The old regex
    never matched, and `if report:` silently skipped the core A/V regression.
    """
    fresh = re.search(r"Fresh frames\s*:\s*(\d+)", stdout)
    repeated = re.search(r"Repeated slots\s*:\s*(\d+)", stdout)
    if fresh is None or repeated is None:
        return None
    return int(fresh.group(1)), int(repeated.group(1))


def _exclusive_open(path):
    """Windows-only: the same kind of lock a player / ffplay holds."""
    return _CreateFileW(str(path), _GENERIC_RW, 0, None,
                        _CREATE_ALWAYS, _FILE_NORMAL, None)


def test_direct_mode_keeps_every_slot_a_real_frame(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "direct.avi"

    proc = _run(exe, cfg, *_encode(exe, src, out, "--no-realtime"))
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    assert out.is_file()

    info = harness.probe(out, count_frames=True) or {}
    streams = info.get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"), None)
    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)
    assert video and audio, info

    frames = int(video.get("nb_read_frames") or video.get("nb_frames") or 0)
    # 2 s @ 13 fps => ~26 slots. v7.9.9.1 produced only 3 fresh frames here.
    assert frames >= 20, f"only {frames} video frames: {video}"

    vdur = float(video.get("duration") or 0)
    adur = float(audio.get("duration") or 0)
    assert vdur > 1.5 and adur > 1.5, (vdur, adur)
    assert abs(vdur - adur) < 0.25, f"A/V desync: video={vdur} audio={adur}"

    # v7.9.9.3: this was `if report:`, so the whole regression could be
    # skipped silently whenever the report format changed. Require it.
    report = _frame_report(proc.stdout)
    assert report is not None, f"frame report missing: {proc.stdout[-800:]}"
    fresh, repeated = report
    assert repeated == 0, f"{repeated} repeated slots: {proc.stdout[-500:]}"
    assert fresh >= 20


def test_fps_levels_none_never_drops_the_frame_rate(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "none.avi"

    proc = _run(exe, cfg, *_encode(exe, src, out, "--fps-levels", "none"))
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    report = _frame_report(proc.stdout)
    assert report is not None, f"frame report missing: {proc.stdout[-800:]}"
    assert report[0] >= 20, report
    assert "DOWN" not in proc.stdout, proc.stdout[-600:]

    bad = _run(exe, cfg, *_encode(exe, src, tmp_path / "bad.avi",
                                  "--fps-levels", "abc"))
    assert bad.returncode != 0, bad.stdout[-300:]


def test_default_ladder_is_the_era_standard(exe, src, tmp_path):
    """A run without --era/--fps-levels still encodes; the rungs are 13/12/10/8/5."""
    cfg = _cfg(tmp_path)
    out = tmp_path / "default.avi"
    proc = _run(exe, cfg, *_encode(exe, src, out))
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    assert out.is_file()
    # The run writes its snapshot; it must not pin the retired Python baseline.
    settings = json.loads((cfg / "config.json").read_text(encoding="utf-8"))
    levels = settings.get("FpsLevels") or []
    assert levels, "config.json has no FpsLevels -- default ladder was not saved"
    assert [float(x) for x in levels][:5] == [13.0, 12.0, 10.0, 8.0, 5.0], levels


def test_output_directory_is_completed_and_directories_are_remembered(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    outdir = tmp_path / "Compressed"
    outdir.mkdir()

    proc = _run(exe, cfg, *_encode(exe, src, outdir, "--no-realtime"))
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    assert (outdir / "test_src_live.avi").is_file(), sorted(
        p.name for p in outdir.iterdir())

    settings = json.loads((cfg / "config.json").read_text(encoding="utf-8"))
    recent = [os.path.normcase(os.path.abspath(p))
              for p in settings.get("RecentDirectories", [])]
    assert os.path.normcase(str(outdir)) in recent, recent
    assert os.path.normcase(str(src.parent)) in recent, recent
    assert len(recent) <= 8


def test_vbv_triple_requires_a_target_bitrate(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    proc = _run(exe, cfg, str(src), str(tmp_path / "vbv.mp4"),
                "--encoder", "mpeg4", "--recompress",
                "--recompress-codec", "libx264", "--recompress-maxrate", "300",
                "--ffmpeg-source", "path", "--lang", "en", "--quiet")
    assert proc.returncode != 0
    assert "need a target bitrate" in (proc.stdout + proc.stderr)


def test_generic_args_reach_both_passes(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "generic.avi"
    proc = _run(exe, cfg, str(src), str(out), "--encoder", "mpeg4",
                "--end", "00:00:02", "--no-realtime",
                "--extra-encoder-args", "-maxrate 300k -bufsize 600k",
                "--recompress", "--recompress-codec", "libx264",
                "--recompress-video-kbps", "250",
                "--recompress-maxrate", "300", "--recompress-minrate", "100",
                "--recompress-bufsize", "600",
                "--recompress-extra", "-profile:v high -ac 1 -b:a 64k",
                "--ffmpeg-source", "path", "--lang", "en")
    assert proc.returncode == 0, (proc.stdout[-800:], proc.stderr[-800:])
    assert out.is_file()
    assert (tmp_path / "generic_web.mp4").is_file(), sorted(
        p.name for p in tmp_path.iterdir())


def test_stale_part_files_are_removed_before_encoding(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "stale.avi"
    stale = tmp_path / "stale.deadbeef.part.avi"
    stale.write_bytes(b"stale")

    proc = _run(exe, cfg, *_encode(exe, src, out, "--no-realtime"))
    assert proc.returncode == 0, (proc.stdout[-600:], proc.stderr[-600:])
    assert not stale.exists()
    assert out.is_file()


@pytest.mark.skipif(not WINDOWS,
                    reason="POSIX rename ignores an open handle; the lock semantics are Windows-only")
def test_busy_target_is_rejected_before_encoding(exe, src, tmp_path):
    cfg = _cfg(tmp_path)
    out = tmp_path / "busy.avi"
    out.write_bytes(b"old")

    holder = _exclusive_open(out)
    assert holder != _INVALID
    try:
        proc = _run(exe, cfg, *_encode(exe, src, out, "--no-realtime"))
        assert proc.returncode != 0
        assert "in use" in (proc.stdout + proc.stderr)
    finally:
        _CloseHandle(holder)


@pytest.mark.skipif(not WINDOWS,
                    reason="an alternate save needs a Windows sharing violation")
def test_alternate_save_is_reported_and_followed(exe, src, tmp_path):
    """Lock the target AFTER the pre-flight but BEFORE Promote.

    Regression for two bugs: UnauthorizedAccessException used to bypass the
    ``(2)`` fallback entirely, and the "Recompressed:" line used to name the
    file that could not be written.
    """
    cfg = _cfg(tmp_path)
    clip = tmp_path / "clip.avi"
    made = _run(exe, cfg, *_encode(exe, src, clip, "--no-realtime"))
    assert made.returncode == 0, (made.stdout[-600:], made.stderr[-600:])

    web = tmp_path / "clip_web.mp4"
    alternate = tmp_path / "clip_web (2).mp4"
    for path in [web, alternate, *tmp_path.glob("clip_web.*.part*")]:
        path.unlink(missing_ok=True)

    state = {"holder": None, "saw_part": False}

    def grab():
        deadline = time.time() + 120
        while time.time() < deadline:
            if any(tmp_path.glob("clip_web.*.part.mp4")):
                state["saw_part"] = True
                break
            time.sleep(0.002)
        if state["saw_part"]:
            state["holder"] = _exclusive_open(web)

    worker = threading.Thread(target=grab, daemon=True)
    worker.start()
    proc = _run(exe, cfg, str(clip), "--recompress-only",
                "--recompress-codec", "libx264", "--recompress-video-kbps", "250",
                "--recompress-extra", "-preset veryslow",
                "--ffmpeg-source", "path", "--lang", "en")
    worker.join(timeout=5)
    if state["holder"] and state["holder"] != _INVALID:
        _CloseHandle(state["holder"])

    assert proc.returncode == 0, (proc.stdout[-800:], proc.stderr[-800:])
    assert state["saw_part"]
    assert alternate.is_file() and alternate.stat().st_size > 1000, sorted(
        p.name for p in tmp_path.iterdir())
    assert "(2)" in proc.stdout, proc.stdout[-600:]
