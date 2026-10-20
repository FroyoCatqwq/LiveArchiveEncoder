"""Shared plumbing for the LiveArchiveEncoder end-to-end suite.

The six standalone scripts this suite replaced each carried their own copy of
these helpers; they live here once now. Everything is plain functions and
module constants -- the pytest fixtures in conftest.py wire them up.

Environment knobs (unchanged from the scripts):

    CS_EXE      which binary to exercise (default: publish/jit/...)
    CM_OUT      scratch directory for the comparison matrix
    FS_OUT      scratch directory for the encoder sweep
    SWEEP_END   segment length for the sweep (default 00:00:02)

The scratch directories deliberately survive between runs so the produced
clips can be inspected; nothing here bulk-deletes anything.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

# The apphost carries no extension on Unix; every path in this suite goes
# through EXE_NAME so the same tests run on Windows and on Linux.
EXE_NAME = "LiveArchiveEncoder.exe" if os.name == "nt" else "LiveArchiveEncoder"

E2E = Path(__file__).resolve().parent
TESTS = E2E.parent
ROOT = TESTS.parent
SRC = TESTS / "media" / "test_src.mp4"
# The v6.3.3 baseline defaults to CUDA decoding; v7.5 defaults to software on
# both platforms. Pin the baseline to software too, otherwise the two sides
# decode differently on a machine that has an NVIDIA card.
PY_BASELINE = ROOT / "reference" / "live_archive_encoder.py"
OUT_ROOT = TESTS / "out"


def default_exe() -> Path:
    return ROOT / "publish" / "jit" / EXE_NAME


def resolve_exe() -> Path:
    """CS_EXE lets the same suite run against the NativeAOT build."""
    return Path(os.environ.get("CS_EXE") or default_exe())


def scratch(name: str) -> Path:
    """Per-suite scratch directory, honouring the legacy env overrides."""
    override = {"matrix": "CM_OUT", "sweep": "FS_OUT"}.get(name)
    base = Path(os.environ[override]) if override and os.environ.get(override) else OUT_ROOT / name
    base.mkdir(parents=True, exist_ok=True)
    return base


def run(cmd: list[str], *, timeout: int = 300, **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout, **kw)


def probe(path: Path, *, count_frames: bool = False) -> dict | None:
    """ffprobe a file into a dict, or None when ffprobe cannot read it."""
    cmd = ["ffprobe", "-v", "error"]
    if count_frames:
        cmd += ["-count_frames",
                "-show_entries",
                "stream=codec_type,codec_name,width,height,pix_fmt,r_frame_rate,"
                "duration,nb_read_frames",
                "-show_entries", "format=duration,format_name"]
    else:
        cmd += ["-show_streams", "-show_format"]
    cmd += ["-of", "json", str(path)]
    p = run(cmd)
    if p.returncode != 0:
        return None
    try:
        return json.loads(p.stdout)
    except json.JSONDecodeError:
        return None


def video_summary(path: Path) -> dict | None:
    """The structural read option_smoke needs: codec, size, duration, streams."""
    info = probe(path)
    if info is None:
        return None
    streams = info.get("streams", [])
    video = [s for s in streams if s.get("codec_type") == "video"]
    audio = [s for s in streams if s.get("codec_type") == "audio"]
    if not video:
        return {"no_video": True, "streams": len(streams)}
    v = video[0]
    try:
        dur = float((info.get("format") or {}).get("duration") or 0.0)
    except ValueError:
        dur = 0.0
    return {
        "codec": v.get("codec_name", "?"),
        "width": v.get("width"),
        "height": v.get("height"),
        "video": len(video),
        "audio": len(audio),
        "streams": len(streams),
        "duration": dur,
    }


def fresh_output(path: Path, since: float) -> bool:
    """True when `path` exists and was written after `since` (a time.time()
    reading taken just before the run started).

    The scratch directories survive between runs on purpose, so no suite
    deletes the previous product -- it proves the file it is about to probe
    came from this run instead of from an earlier one.
    """
    return path.is_file() and path.stat().st_mtime >= since - 1.0


def media_info(path: Path) -> dict:
    """The structural read compare_matrix needs: codec, size, fps, duration,
    decoded frame count, stream layout and container format.

    Note this is *normalised* (one flat dict of comparable scalars) rather than
    raw ffprobe JSON: the structural comparison compares field by field and
    prints the offenders, which raw stream dicts would make unreadable.
    """
    info = probe(path, count_frames=True)
    if info is None:
        return {"error": "ffprobe failed"}
    streams = info.get("streams", [])
    video = next((s for s in streams if s.get("codec_type") == "video"), {})
    audio = next((s for s in streams if s.get("codec_type") == "audio"), None)
    raw = video.get("duration")
    try:
        dur = round(float(raw), 3) if raw else 0.0
    except ValueError:
        dur = 0.0
    out = {
        "codec": video.get("codec_name"),
        "width": video.get("width"),
        "height": video.get("height"),
        "pix_fmt": video.get("pix_fmt"),
        "fps": video.get("r_frame_rate"),
        "duration": dur,
        "frames": video.get("nb_read_frames"),
        "audio": audio.get("codec_name") if audio else None,
        "streams": len(streams),
        "format": (info.get("format") or {}).get("format_name"),
    }
    out["size"] = path.stat().st_size if path.exists() else 0
    return out


def encoder_list(exe: Path) -> list[str]:
    """Reuse --completion so the sweep covers exactly what tab completion offers."""
    proc = run([str(exe), "--completion", "bash"])
    if proc.returncode != 0:
        raise RuntimeError(f"cannot query encoders: {proc.stderr.strip()}")
    lines = proc.stdout.splitlines()
    for i, line in enumerate(lines):
        if line.strip() == "--encoder)" and i + 1 < len(lines):
            inner = lines[i + 1].split('"')[1]
            return inner.split()
    raise RuntimeError("completion output has no --encoder case")


def muxer_only_formats() -> set[str]:
    """Format names that have a muxer but no demuxer."""

    def names(kind: str) -> set[str]:
        proc = run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-" + kind])
        found = set()
        for line in proc.stdout.splitlines():
            parts = line.strip().split()
            if len(parts) < 2:
                continue
            if set(parts[0]) <= {"D", "E"}:
                found.add(parts[1])
        return found

    return names("muxers") - names("demuxers")


def parse_summary(stdout: str, stderr: str, code: int) -> dict:
    """The lines both implementations print at the end of a live encode."""
    text = stdout + "\n" + stderr

    def grab(pattern: str) -> str | None:
        m = re.search(pattern, text)
        return m.group(1).strip() if m else None

    return {
        "exit": code,
        "duration": grab(r"Duration\s*:\s*([\d.]+)s"),
        "fresh": grab(r"Fresh frames\s*:\s*(\d+)"),
        "repeated": grab(r"Repeated slots\s*:\s*(\d+)"),
        "video_kbps": grab(r"Video average\s*:\s*([\d.]+) kbps"),
        "total_kbps": grab(r"Total average\s*:\s*([\d.]+) kbps"),
        "transport": grab(r"Transport pix fmt\s*:\s*(\S+)"),
        "resolution": grab(r"Output resolution\s*:\s*(\S+)"),
        "error": (stderr.strip().splitlines() or [""])[-1] if code != 0 else None,
    }


# ---------------------------------------------------------------------------
# CUI driving
# ---------------------------------------------------------------------------

def config_path() -> Path:
    """SpecialFolder.LocalApplicationData is %LOCALAPPDATA% on Windows and
    ~/.local/share everywhere else; the C# side asks the runtime, so this has
    to agree with it or the tests would back up the wrong file."""
    appdata = (os.environ.get("LOCALAPPDATA") if os.name == "nt"
               else os.path.expanduser("~/.local/share"))
    return Path(appdata) / "LiveArchiveEncoder" / "config.json"


def cui(exe: Path, args: list[str], keys: str, timeout: int = 180) -> subprocess.CompletedProcess:
    """Feed `keys` (newline separated menu choices) to the interactive shell."""
    return run([str(exe), *args], input=keys, timeout=timeout)


def read_config() -> dict | None:
    path = config_path()
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return None


# ---------------------------------------------------------------------------
# Structural comparison (the matrix's job)
# ---------------------------------------------------------------------------

STRUCTURAL = ["codec", "width", "height", "pix_fmt", "fps", "duration", "frames",
              "audio", "streams", "format"]

# MPEG-PS/TS stamp packets from when the pipe bytes actually arrive, not from a
# frame counter, so their container duration is not reproducible run to run even
# when both sides mux an identical frame sequence. These formats therefore get a
# tolerance (below) and fall back on the (exact) decoded frame count, which is
# the real structural signal; every other container is compared exactly.
PACING_DURATION_FORMATS = {"mpeg", "mpegts", "mpegvideo"}
# How far the two sides may drift before it counts as a difference.
#
# Measured, not guessed. On Windows the Python baseline alone produced 4.942
# and 4.984 for the same command (nb_read_frames=120 both times), i.e. about
# one frame of pure run-to-run jitter. On Linux the C# side sits *systematically*
# shorter than the baseline: mpeg1video came out 4.776 vs 4.942, a 0.167 s gap
# that is exactly four frames at 23.976 fps -- while the decoded frame count was
# identical (120/120). That is the wall-clock stamping, not a dropped frame, and
# it is why the tolerance has to clear four frames with headroom rather than one.
# Anything structural (a real frame loss, a truncated stream) moves the duration
# by far more than this, and the frame count is still compared exactly.
DURATION_TOL = 0.25


def duration_ok(py_media: dict, cs_media: dict) -> bool:
    fmt = str(cs_media.get("format") or "")
    py_d, cs_d = py_media.get("duration"), cs_media.get("duration")
    if py_d == cs_d:
        return True
    if fmt not in PACING_DURATION_FORMATS:
        return False
    if not isinstance(py_d, (int, float)) or not isinstance(cs_d, (int, float)):
        return False
    return abs(float(py_d) - float(cs_d)) <= DURATION_TOL


def structural_diffs(py_media: dict, cs_media: dict) -> tuple[list[str], list[str]]:
    """(hard diffs, tolerated notes) over the STRUCTURAL field list."""
    diffs, notes = [], []
    for field in STRUCTURAL:
        py_v, cs_v = py_media.get(field), cs_media.get(field)
        if py_v == cs_v:
            continue
        if field == "duration" and duration_ok(py_media, cs_media):
            notes.append(f"duration within tolerance: py={py_v} cs={cs_v} "
                         f"(delta {abs(float(py_v) - float(cs_v)):.3f}s, frames "
                         f"py={py_media.get('frames')} cs={cs_media.get('frames')})")
            continue
        diffs.append(f"{field}: py={py_v} cs={cs_v}")
    return diffs, notes


def py_command() -> list[str]:
    """The Python baseline as an argv prefix (pinned to software decoding)."""
    return [sys.executable, str(PY_BASELINE), "--no-cuda"]
