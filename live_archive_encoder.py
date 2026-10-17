#!/usr/bin/env python3
"""
Live Archive Encoder CUI v6.3.3

Windows-friendly CUI/CLI frontend for classic/legacy FFmpeg video encoders.

Design goals:
  * Prefer a local BtbN FFmpeg GPL build.
  * Discover legacy/classic video encoders from `ffmpeg -encoders` at runtime.
  * Probe encoder pixel formats and frame-rate capabilities instead of forcing
    every codec through yuv420p.
  * Keep a stable output cadence while Python adapts the cadence of fresh
    pictures according to the measured encoded-byte rate.
  * Keep the encoder focused on archival/live-style encoding. Post-encode
    frame drops, packet loss, and bitstream corruption belong to frame_damage.py.
  * Support FFmpeg deinterlacing filters such as bwdif, yadif and w3fdif when
    available in the selected FFmpeg build.
  * Support libswscale algorithms including neighbor, bicubic/cubic, lanczos,
    bilinear, area, spline, sinc and gauss.
  * Keep x264-only options isolated to libx264.
  * Run a short video-only preflight using the real filter/codec/container
    chain before the realtime encode starts.

Requires Python 3.13+.
No third-party Python modules.
"""

from __future__ import annotations

import argparse
import collections
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from collections.abc import Iterable
from dataclasses import dataclass, field
from fractions import Fraction
from pathlib import Path

WIDTH = 256
HEIGHT = 192
DEFAULT_FPS = 13.0
DEFAULT_FPS_LEVELS = "13,8,5,3"
MPEG1_REAL_FPS = (
    24000 / 1001, 24.0, 25.0, 30000 / 1001, 30.0, 50.0, 60000 / 1001, 60.0
)

type FrameSample = tuple[int, bytes]
type EncodeResult = dict[str, object]


BTBN_GPL_RELEASE_URL = (
    "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/"
    "ffmpeg-master-latest-win64-gpl.zip"
)

VIDEO_EXTENSIONS = {
    ".avi", ".mov", ".mp4", ".mkv", ".webm", ".wmv", ".mpg", ".mpeg",
    ".m4v", ".flv", ".rm", ".rmvb", ".ts", ".m2ts", ".vob", ".ogv", ".asf",
    ".roq", ".amv",
}

# Positive legacy/classic families. The classifier is intentionally driven by
# encoder names/families so an updated FFmpeg build can add a matching encoder
# without requiring a hardcoded version-specific list.
LEGACY_ENCODER_PATTERNS = [
    r"^a64multi(?:5)?$",
    r"^amv$",
    r"^asv[12]$",
    r"^cinepak$",
    r"^cljr$",
    r"^flashsv2?$",
    r"^flv$",
    r"^h261$",
    r"^h263(?:p)?$",
    r"^mjpeg$",
    r"^msmpeg4(?:v2)?$",
    r"^msrle$",
    r"^msvideo1$",
    r"^mpeg[124]video$",
    r"^mpeg4$",
    r"^qtrle$",
    r"^roqvideo$",
    r"^rpza$",
    r"^rv(?:10|20)$",
    r"^smc$",
    r"^snow$",
    r"^svq1$",
    r"^wmv[12]$",
    r"^zmbv$",
    r"^libtheora$",
    r"^libx264$",
    r"^libxvid$",
    r"^vivo$",
]
LEGACY_RE = [re.compile(p, re.IGNORECASE) for p in LEGACY_ENCODER_PATTERNS]

MODERN_PREFIXES = (
    "av1", "libaom", "librav1e", "libsvtav1",
    "vp8", "vp9", "libvpx",
    "hevc", "libx265", "libkvazaar",
    "vvc", "libvvenc", "libxavs2",
    "h264_", "av1_", "hevc_",
)

NON_ARCHIVE_EXACT = {
    "rawvideo", "wrapped_avframe",
    "ffv1", "ffv1_vulkan", "ffvhuff", "huffyuv", "ljpeg", "jpegls",
    "jpeg2000", "libopenjpeg", "dvvideo", "dnxhd",
    "prores", "prores_aw", "prores_ks", "prores_ks_vulkan", "cfhd",
    "dpx", "exr", "fits", "png", "bmp", "gif", "tiff", "qoi",
    "webp", "libwebp", "libwebp_anim", "libjxl", "libjxl_anim",
    "r10k", "r210", "v210", "speedhq", "utvideo", "magicyuv", "vc2",
    "dxv", "sgi", "targa", "ppm", "pgm", "pam", "xbm", "xwd", "xface",
    "sunrast", "pfm", "phm", "zlib",
}

CONTAINER_BY_ENCODER: dict[str, tuple[str, str]] = {
    "a64multi": ("a64", "a64"),
    "a64multi5": ("a64", "a64"),
    "amv": ("amv", "amv"),
    "wmv1": ("wmv", "asf"),
    "wmv2": ("wmv", "asf"),
    "rv10": ("rm", "rm"),
    "rv20": ("rm", "rm"),
    "mpeg1video": ("mpg", "mpeg"),
    "mpeg2video": ("mpg", "mpeg"),
    "mpeg4": ("avi", "avi"),
    "libxvid": ("avi", "avi"),
    "libtheora": ("ogv", "ogg"),
    "libx264": ("mp4", "mp4"),
    "flv": ("flv", "flv"),
    "flashsv": ("flv", "flv"),
    "flashsv2": ("flv", "flv"),
    "h261": ("h261", "h261"),
    "h263": ("h263", "h263"),
    "h263p": ("avi", "avi"),
    "mjpeg": ("avi", "avi"),
    "qtrle": ("mov", "mov"),
    "rpza": ("mov", "mov"),
    "smc": ("mov", "mov"),
    "svq1": ("mov", "mov"),
    "roqvideo": ("roq", "roq"),
}

DEFAULT_QUALITY = {
    "libx264": 28,
    "libtheora": 7,
    "rv10": 8,
    "rv20": 8,
    "wmv1": 10,
    "wmv2": 10,
    "mpeg1video": 8,
    "mpeg2video": 8,
    "mpeg4": 8,
    "libxvid": 8,
    "cinepak": 10,
    "cljr": 10,
    "h261": 10,
    "h263": 10,
    "h263p": 10,
    "mjpeg": 6,
    "flv": 10,
    "flashsv": 10,
    "flashsv2": 10,
    "qtrle": 10,
    "rpza": 10,
    "smc": 10,
    "svq1": 10,
    "roqvideo": 10,
    "zmbv": 10,
    "amv": 10,
    "asv1": 10,
    "asv2": 10,
    "a64multi": 10,
    "a64multi5": 10,
    "msmpeg4v2": 10,
    "msmpeg4": 10,
    "msvideo1": 10,
    "msrle": 10,
    "snow": 8,
}

DEFAULT_AUDIO_BY_CONTAINER = {
    "asf": ["wmav2", "wmav1", "libmp3lame"],
    "mpeg": ["mp2", "mp3_mf", "libmp3lame"],
    "avi": ["libmp3lame", "mp3_mf", "mp2", "aac"],
    "ogg": ["libvorbis", "vorbis", "libopus", "opus"],
    "rm": ["ac3", "aac"],
    "mp4": ["aac", "libopus", "libmp3lame"],
    "flv": ["aac", "libmp3lame", "mp3_mf"],
    "mov": ["aac", "libmp3lame"],
    "amv": ["adpcm_ima_amv", "adpcm_ima_wav"],
    "roq": ["roq_dpcm"],
}

NO_AUDIO_FORMATS = {"a64", "h261", "h263"}

# Extensions accepted by the muxer that this script explicitly selects with
# `-f`. Never let an AVI-looking filename silently contain a RoQ stream.
FORMAT_EXTENSIONS: dict[str, set[str]] = {
    "a64": {".a64"},
    "amv": {".amv"},
    "asf": {".asf", ".wmv"},
    "avi": {".avi"},
    "flv": {".flv"},
    "h261": {".h261"},
    "h263": {".h263"},
    "mpeg": {".mpg", ".mpeg", ".mpe", ".vob"},
    "mp4": {".mp4", ".m4v"},
    "mov": {".mov", ".qt"},
    "ogg": {".ogg", ".ogv"},
    "rm": {".rm", ".rmvb"},
    "roq": {".roq"},
}
AMV_FPS = (10.0, 14.0, 15.0, 18.0, 21.0, 25.0, 30.0)
SPECIAL_SIZES = {
    "h261": ((176, 144), (352, 288)),
    "h263": ((128, 96), (176, 144), (352, 288), (704, 576), (1408, 1152)),
}

# These are scaler algorithms exposed by libswscale's -sws_flags. They are
# aliases/terms understood by current FFmpeg builds; "cubic" is accepted as a
# user-friendly alias for FFmpeg's "bicubic".
SCALE_ALGORITHM_ALIASES = {
    "neighbor": "neighbor",
    "nearest": "neighbor",
    "point": "neighbor",
    "bilinear": "bilinear",
    "bicubic": "bicubic",
    "cubic": "bicubic",
    "lanczos": "lanczos",
    "spline": "spline",
    "sinc": "sinc",
    "area": "area",
    "gauss": "gauss",
}
SCALE_ALGORITHMS = tuple(SCALE_ALGORITHM_ALIASES)
DEINTERLACE_FILTERS = ("bwdif", "yadif", "w3fdif")

PIXEL_FORMAT_SIZE = {
    "yuv420p": 3 / 2,
    "yuv422p": 2,
    "yuv444p": 3,
    "yuv411p": 3 / 2,
    "yuv410p": 9 / 8,
    "yuv440p": 3 / 2,
    "yuvj420p": 3 / 2,
    "yuvj422p": 2,
    "yuvj444p": 3,
    "yuvj411p": 3 / 2,
    "yuv420p10le": 3,
    "yuv422p10le": 4,
    "yuv444p10le": 6,
    "yuv420p12le": 3,
    "yuv422p12le": 4,
    "yuv444p12le": 6,
    "yuv420p16le": 3,
    "yuv422p16le": 4,
    "yuv444p16le": 6,
    "gray": 1,
    "gray10le": 2,
    "gray16le": 2,
    "rgb24": 3,
    "bgr24": 3,
    "rgba": 4,
    "bgra": 4,
    "argb": 4,
    "abgr": 4,
    "rgb0": 4,
    "bgr0": 4,
    "rgb555le": 2,
    "rgb555be": 2,
    "rgb565le": 2,
    "rgb565be": 2,
    "rgb48le": 6,
    "rgb48be": 6,
    "pal8": 1,
    "rgb8": 1,
    "bgr8": 1,
}


@dataclass(slots=True)
class StreamInfo:
    width: int
    height: int
    pix_fmt: str
    color_range: str | None = None
    color_space: str | None = None
    color_primaries: str | None = None
    color_transfer: str | None = None


@dataclass(slots=True)
class EncoderCaps:
    name: str
    description: str
    flags: str
    supported_pix_fmts: list[str] = field(default_factory=list)
    supported_rates: list[float] = field(default_factory=list)
    has_crf: bool = False
    has_qscale: bool = False


@dataclass(slots=True)
class EncoderInfo:
    name: str
    flags: str
    description: str
    codec: str
    family: str
    extension: str
    format_name: str
    caps: EncoderCaps | None = None


@dataclass(slots=True)
class Config:
    input_path: Path | None = None
    output_path: Path | None = None
    output_is_auto: bool = True
    start_time_seconds: float = 0.0
    end_time_seconds: float | None = None
    encoder: EncoderInfo | None = None
    audio_encoder: str | None = "libmp3lame"
    fps: float = DEFAULT_FPS
    fps_levels: list[float] = field(default_factory=lambda: [13.0, 8.0, 5.0, 3.0])
    limit: float = 200.0
    recover: float = 150.0
    window: float = 2.0
    qv: int = 8
    threads: int = 16
    buffer_frames: int = 6
    audio_kbps: int = 32
    audio_channels: int = 1
    audio_rate: int | None = None
    width: int = WIDTH
    height: int = HEIGHT
    crop_4_3: bool = True
    scale_algo: str = "neighbor"
    deinterlace: str = "off"
    deint_mode: str = "send_frame"
    deint_parity: str = "auto"
    deint_marked_only: bool = False
    cuda: bool = True
    x264_preset: str = "medium"
    x264_tune: str = ""
    x264_profile: str = ""
    x264_level: str = ""


def exe_path(value: str) -> str:
    if os.path.sep in value or (os.path.altsep and os.path.altsep in value):
        p = Path(value)
        if p.exists():
            return str(p)
        raise FileNotFoundError(f"Executable not found: {value}")
    found = shutil.which(value)
    if found:
        return found
    raise FileNotFoundError(f"Cannot find {value}; add FFmpeg/bin to PATH.")


def run_capture(cmd: list[str], timeout: int = 30) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
        timeout=timeout,
    )


def candidate_btbn_ffmpeg_paths() -> list[Path]:
    here = Path(__file__).resolve().parent
    result: list[Path] = []
    env = os.environ.get("BTBN_FFMPEG_DIR")
    if env:
        base = Path(env)
        result += [base / "bin" / "ffmpeg.exe", base / "ffmpeg.exe"]

    names = [
        "ffmpeg-master-latest-win64-gpl",
        "ffmpeg-n9.0-latest-win64-gpl-9.0",
        "ffmpeg-n8.1-latest-win64-gpl-8.1",
        "ffmpeg",
    ]
    for name in names:
        base = here / name
        result += [base / "bin" / "ffmpeg.exe", base / "ffmpeg.exe"]

    result += [here / "bin" / "ffmpeg.exe", here / "ffmpeg.exe"]
    try:
        for child in sorted(here.iterdir(), key=lambda p: p.name.lower()):
            if child.is_dir():
                result.append(child / "bin" / "ffmpeg.exe")
    except OSError:
        pass

    seen: set[Path] = set()
    unique: list[Path] = []
    for item in result:
        try:
            key = item.resolve()
        except OSError:
            key = item
        if key not in seen:
            seen.add(key)
            unique.append(item)
    return unique


def find_ffmpeg(user_value: str | None) -> tuple[str, bool]:
    if user_value:
        return exe_path(user_value), False
    for path in candidate_btbn_ffmpeg_paths():
        if path.exists():
            return str(path), True
    return exe_path("ffmpeg"), False


def find_ffprobe(user_value: str | None, ffmpeg: str) -> str:
    if user_value:
        return exe_path(user_value)
    sibling = Path(ffmpeg).with_name("ffprobe.exe")
    if sibling.exists():
        return str(sibling)
    return exe_path("ffprobe")


def ffmpeg_build_info(ffmpeg: str) -> tuple[str, str, bool]:
    p = run_capture([ffmpeg, "-hide_banner", "-version"])
    text = (p.stdout or "") + "\n" + (p.stderr or "")
    version = next((x.strip() for x in text.splitlines() if x.strip()), "unknown FFmpeg")
    config = next(
        (x.strip() for x in text.splitlines() if x.strip().startswith("configuration:")),
        "",
    )
    return version, config, "--enable-gpl" in config


def parse_video_encoders(text: str) -> list[tuple[str, str, str]]:
    result: list[tuple[str, str, str]] = []
    for line in text.splitlines():
        m = re.match(r"^\s*V([A-Z.]{5})\s+(\S+)\s+(.+?)\s*$", line)
        if m:
            result.append((m.group(2), m.group(1), m.group(3)))
    return result


def extract_codec(description: str, name: str) -> str:
    m = re.search(r"\(codec\s+([^\)]+)\)", description, re.IGNORECASE)
    return m.group(1).strip() if m else name


def legacy_family(name: str, description: str) -> str | None:
    lname = name.lower()
    if lname in NON_ARCHIVE_EXACT:
        return None
    if any(lname.startswith(prefix) for prefix in MODERN_PREFIXES):
        return None
    desc = description.lower()
    if any(rx.fullmatch(lname) for rx in LEGACY_RE):
        if lname in {"libx264", "libxvid", "libtheora"}:
            return "classic-library"
        if lname.startswith(("mpeg", "msmpeg")):
            return "mpeg-classic"
        if lname.startswith(("wmv", "rv")):
            return "proprietary-classic"
        if "commodore" in desc:
            return "home-computer"
        return "retro/classic"
    return None


def guess_container(name: str) -> tuple[str, str]:
    if name in CONTAINER_BY_ENCODER:
        return CONTAINER_BY_ENCODER[name]
    if name.lower().startswith("wmv"):
        return "wmv", "asf"
    return "avi", "avi"


def detect_legacy_encoders(ffmpeg: str) -> list[EncoderInfo]:
    p = run_capture([ffmpeg, "-hide_banner", "-encoders"])
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or "ffmpeg -encoders failed")
    result: list[EncoderInfo] = []
    for name, flags, desc in parse_video_encoders(p.stdout):
        family = legacy_family(name, desc)
        if family is None:
            continue
        ext, fmt = guess_container(name)
        result.append(
            EncoderInfo(
                name=name,
                flags=flags,
                description=desc.strip(),
                codec=extract_codec(desc, name),
                family=family,
                extension=ext,
                format_name=fmt,
            )
        )
    return sorted(result, key=lambda e: e.name.lower())


def detect_audio_encoders(ffmpeg: str) -> set[str]:
    p = run_capture([ffmpeg, "-hide_banner", "-encoders"])
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or "ffmpeg -encoders failed")
    found: set[str] = set()
    for line in p.stdout.splitlines():
        m = re.match(r"^\s*A[A-Z.]{5}\s+(\S+)\s+", line)
        if m:
            found.add(m.group(1))
    return found


def detect_video_filters(ffmpeg: str) -> set[str]:
    """Parse the current FFmpeg filter registry robustly on Windows and POSIX.

    FFmpeg normally writes the `-filters` table to stdout, but builds/wrappers
    can redirect diagnostic output to stderr.  The previous implementation only
    inspected stdout, which made an otherwise fully functional build appear to
    have no filters at all (including `scale`, `bwdif`, `yadif`, and `w3fdif`).
    """
    p = run_capture([ffmpeg, "-hide_banner", "-filters"])
    if p.returncode != 0:
        raise RuntimeError((p.stderr or p.stdout).strip() or "ffmpeg -filters failed")

    text = (p.stdout or "") + "\n" + (p.stderr or "")
    filters: set[str] = set()
    for line in text.splitlines():
        # Current FFmpeg format starts with 3 capability flags followed by the
        # filter name and an input/output media signature such as `V->V`.
        m = re.match(r"^\s*[TSC.]{3}\s+(\S+)\s+[VAFN|.>-]+(?:\s|$)", line)
        if m:
            filters.add(m.group(1))
    return filters


def verify_named_filters(ffmpeg: str, filters: set[str], names: Iterable[str]) -> set[str]:
    """Verify important filters directly when registry parsing is inconclusive."""
    verified = set(filters)
    for name in names:
        if name in verified:
            continue
        p = run_capture([ffmpeg, "-hide_banner", "-h", f"filter={name}"], timeout=20)
        text = (p.stdout or "") + "\n" + (p.stderr or "")
        if not re.search(rf"^Unknown filter ['\"]{re.escape(name)}['\"]\.?\s*$", text, re.MULTILINE | re.IGNORECASE):
            # `ffmpeg -h filter=...` returns useful help with rc=0 on current
            # FFmpeg, while unknown filters emit an explicit `Unknown filter`.
            verified.add(name)
    return verified


def detect_scale_algorithms(ffmpeg: str) -> set[str]:
    """Read the scaler algorithms exposed by the selected FFmpeg build."""
    p = run_capture([ffmpeg, "-hide_banner", "-h", "filter=scale"], timeout=20)
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or "ffmpeg -h filter=scale failed")
    text = (p.stdout or "") + "\n" + (p.stderr or "")
    found: set[str] = set()
    for canonical in set(SCALE_ALGORITHM_ALIASES.values()):
        if re.search(rf"^\s+{re.escape(canonical)}\s+", text, re.IGNORECASE | re.MULTILINE):
            found.add(canonical)
    # Some FFmpeg builds expose gaussian under the canonical libswscale name
    # while the historical sws flag is called gauss.
    if "gauss" in found or re.search(r"^\s+gaussian\s+", text, re.IGNORECASE | re.MULTILINE):
        found.add("gauss")
    if not found:
        raise RuntimeError(
            "Could not detect scaler algorithms from `ffmpeg -h filter=scale`"
        )
    return found


def choose_audio_encoder(container: str, available: set[str]) -> str | None:
    if container in NO_AUDIO_FORMATS:
        return None
    for candidate in DEFAULT_AUDIO_BY_CONTAINER.get(container, ["aac", "libmp3lame"]):
        if candidate in available:
            return candidate
    return None


def validate_output_extension(dst: Path, encoder: EncoderInfo) -> None:
    """Fail early when the path suffix contradicts the explicitly selected muxer."""
    allowed = FORMAT_EXTENSIONS.get(encoder.format_name)
    suffix = dst.suffix.lower()
    if allowed and suffix not in allowed:
        expected = ", ".join(sorted(allowed))
        if encoder.name == "roqvideo":
            hint = "RoQ video is not an AVI codec/container combination; use a .roq output or choose an AVI-compatible encoder such as mpeg4/libxvid."
        else:
            hint = f"Use one of: {expected}, or choose an encoder/container that matches the requested extension."
        raise ValueError(
            f"Output extension '{suffix or '(none)'}' does not match selected muxer "
            f"'{encoder.format_name}' for encoder '{encoder.name}'. {hint}"
        )


def effective_audio_settings(
    encoder: EncoderInfo,
    audio_encoder: str | None,
    audio_rate: int | None,
    audio_channels: int,
) -> tuple[int | None, int]:
    """Apply mandatory format-specific audio settings and reject impossible ones."""
    if audio_encoder == "roq_dpcm":
        if encoder.format_name != "roq":
            raise ValueError("roq_dpcm audio is only valid in the RoQ (.roq) container")
        if audio_channels not in (1, 2):
            raise ValueError("roq_dpcm supports mono or stereo audio only; choose 1 or 2 channels")
        if audio_rate not in (None, 22050):
            raise ValueError("roq_dpcm requires 22050 Hz audio; set audio sample rate to 22050 or auto")
        # FFmpeg's roq_dpcm encoder rejects all sample rates except 22050 Hz.
        return 22050, audio_channels
    return audio_rate, audio_channels


def validate_roq_dimensions(width: int, height: int) -> str | None:
    """Validate hard RoQ macroblock constraints and report Quake compatibility."""
    if width <= 0 or height <= 0 or width % 16 or height % 16:
        raise ValueError(
            f"roqvideo requires width and height to be divisible by 16; got {width}x{height}"
        )
    is_pow2 = lambda value: value > 0 and (value & (value - 1)) == 0
    if not (is_pow2(width) and is_pow2(height)):
        return (
            f"RoQ output is {width}x{height}. FFmpeg can encode this size, but it is not "
            "power-of-two and may not be compatible with Quake's RoQ decoder."
        )
    return None


def parse_rate(value: str) -> float | None:
    try:
        f = Fraction(value)
    except (ValueError, ZeroDivisionError):
        return None
    if not f.numerator or not f.denominator:
        return None
    v = float(f)
    return v if math.isfinite(v) and v > 0 else None


def probe_encoder_caps(ffmpeg: str, encoder: EncoderInfo) -> EncoderCaps:
    p = run_capture([ffmpeg, "-hide_banner", "-h", f"encoder={encoder.name}"], timeout=20)
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or f"cannot query encoder {encoder.name}")
    text = p.stdout
    pix: list[str] = []
    rates: list[float] = []
    m = re.search(r"Supported pixel formats:\s*(.+)", text, re.IGNORECASE)
    if m:
        pix = m.group(1).strip().split()
    m = re.search(r"Supported framerates:\s*(.+)", text, re.IGNORECASE)
    if m:
        for item in m.group(1).strip().split():
            rate = parse_rate(item)
            if rate is not None:
                rates.append(rate)
    return EncoderCaps(
        name=encoder.name,
        description=encoder.description,
        flags=encoder.flags,
        supported_pix_fmts=pix,
        supported_rates=sorted(set(round(x, 6) for x in rates)),
        has_crf=bool(re.search(r"^\s*-crf\s+", text, re.MULTILINE)),
        has_qscale=bool(re.search(r"^\s*-qscale\s+", text, re.MULTILINE)),
    )


def probe_stream(ffprobe: str, src: Path) -> StreamInfo:
    cmd = [
        ffprobe, "-v", "error", "-select_streams", "v:0",
        "-show_entries",
        "stream=width,height,pix_fmt,color_range,color_space,color_primaries,color_transfer",
        "-of", "json", str(src),
    ]
    p = run_capture(cmd)
    if p.returncode != 0:
        raise RuntimeError(p.stderr.strip() or "cannot probe video stream")
    try:
        stream = json.loads(p.stdout)["streams"][0]
        return StreamInfo(
            width=int(stream["width"]),
            height=int(stream["height"]),
            pix_fmt=stream.get("pix_fmt") or "yuv420p",
            color_range=stream.get("color_range"),
            color_space=stream.get("color_space"),
            color_primaries=stream.get("color_primaries"),
            color_transfer=stream.get("color_transfer"),
        )
    except (KeyError, IndexError, ValueError, TypeError, json.JSONDecodeError) as exc:
        raise RuntimeError("ffprobe returned incomplete video stream information") from exc


_TIMECODE_RE = re.compile(
    r"(?P<hours>\d+):(?P<minutes>\d{1,2}):(?P<seconds>\d{1,2}(?:\.\d{1,3})?)"
)


def parse_timecode(value: str) -> float:
    """Parse a timecode in HH:MM:SS or HH:MM:SS.mmm format into seconds."""
    raw = value.strip()
    match = _TIMECODE_RE.fullmatch(raw)
    if not match:
        raise ValueError(
            f"invalid timecode '{value}'; use HH:MM:SS or HH:MM:SS.mmm"
        )
    hours = int(match.group("hours"))
    minutes = int(match.group("minutes"))
    seconds = float(match.group("seconds"))
    if minutes >= 60 or not 0 <= seconds < 60:
        raise ValueError("minutes and seconds in a timecode must be between 00 and 59")
    result = hours * 3600 + minutes * 60 + seconds
    if not math.isfinite(result):
        raise ValueError("timecode must be finite")
    return result


def parse_timecode_arg(value: str) -> float:
    """argparse adapter for :func:`parse_timecode`."""
    try:
        return parse_timecode(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(str(exc)) from exc


def format_timecode(seconds: float | None) -> str:
    """Format seconds as HH:MM:SS, retaining milliseconds when needed."""
    if seconds is None:
        return "end of source"
    if not math.isfinite(seconds) or seconds < 0:
        return str(seconds)
    total_ms = int(round(seconds * 1000))
    hours, remainder = divmod(total_ms, 3_600_000)
    minutes, remainder = divmod(remainder, 60_000)
    whole_seconds, milliseconds = divmod(remainder, 1000)
    base = f"{hours:02d}:{minutes:02d}:{whole_seconds:02d}"
    return f"{base}.{milliseconds:03d}" if milliseconds else base


def resolve_time_range(
    start_time_seconds: float,
    end_time_seconds: float | None,
    source_duration: float | None,
) -> tuple[float | None, float | None]:
    """Validate and resolve the selected segment end and duration.

    Returns ``(resolved_end_seconds, selected_duration_seconds)``. Duration is
    ``None`` only when FFprobe cannot determine the source duration and no end
    time was supplied.
    """
    start = float(start_time_seconds)
    if not math.isfinite(start) or start < 0:
        raise ValueError("start time must be a finite non-negative timecode")
    if end_time_seconds is not None:
        end = float(end_time_seconds)
        if not math.isfinite(end) or end < 0:
            raise ValueError("end time must be a finite non-negative timecode")
        if end <= start:
            raise ValueError("end time must be later than start time")
    else:
        end = None

    if source_duration is not None:
        if not math.isfinite(source_duration) or source_duration <= 0:
            source_duration = None
        else:
            if start >= source_duration:
                raise ValueError(
                    f"start time {format_timecode(start)} is at or beyond the source duration "
                    f"({format_timecode(source_duration)})"
                )
            if end is not None:
                # Small container/stream duration rounding differences are common.
                if end > source_duration + 0.05:
                    raise ValueError(
                        f"end time {format_timecode(end)} exceeds the source duration "
                        f"({format_timecode(source_duration)})"
                    )
                end = min(end, source_duration)
            else:
                end = source_duration

    if end is None:
        return None, None
    duration = end - start
    if duration <= 0:
        raise ValueError("selected time range has no duration")
    return end, duration


def probe_duration(ffprobe: str, src: Path) -> float | None:
    """Return video-stream duration, falling back to container duration."""
    stream_probe = run_capture([
        ffprobe, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=duration",
        "-of", "default=nw=1:nk=1", str(src),
    ])
    if stream_probe.returncode == 0:
        try:
            value = float(stream_probe.stdout.strip())
        except ValueError:
            value = None
        if value is not None and math.isfinite(value) and value > 0:
            return value

    format_probe = run_capture([
        ffprobe, "-v", "error", "-show_entries", "format=duration",
        "-of", "default=nw=1:nk=1", str(src),
    ])
    if format_probe.returncode != 0:
        return None
    try:
        value = float(format_probe.stdout.strip())
    except ValueError:
        return None
    return value if math.isfinite(value) and value > 0 else None


def probe_has_audio(ffprobe: str, src: Path) -> bool:
    p = run_capture([
        ffprobe, "-v", "error", "-select_streams", "a:0",
        "-show_entries", "stream=index", "-of", "csv=p=0", str(src),
    ])
    return p.returncode == 0 and bool(p.stdout.strip())


def raw_frame_size(width: int, height: int, pix_fmt: str) -> int:
    ratio = PIXEL_FORMAT_SIZE.get(pix_fmt)
    if ratio is None:
        raise ValueError(
            f"Unsupported raw transport pixel format '{pix_fmt}'. "
            "Choose a known encoder-compatible raw format."
        )
    return int(width * height * ratio + 0.5)


def pixel_format_family(fmt: str) -> str:
    f = fmt.lower()
    if f.startswith("yuv"):
        if "420" in f:
            return "yuv420"
        if "422" in f:
            return "yuv422"
        if "444" in f:
            return "yuv444"
        if "411" in f:
            return "yuv411"
        if "410" in f:
            return "yuv410"
        return "yuv"
    if f.startswith("gray"):
        return "gray"
    if any(token in f for token in ("rgb", "bgr", "argb", "rgba", "bgra", "abgr")):
        return "rgb"
    if f == "pal8":
        return "pal8"
    return f


def choose_transport_pix_fmt(source_fmt: str, caps: EncoderCaps) -> str:
    supported = caps.supported_pix_fmts
    if not supported:
        return source_fmt if source_fmt in PIXEL_FORMAT_SIZE else "yuv420p"
    if source_fmt in supported and source_fmt in PIXEL_FORMAT_SIZE:
        return source_fmt

    source_family = pixel_format_family(source_fmt)
    same_family = [
        fmt for fmt in supported
        if pixel_format_family(fmt) == source_family and fmt in PIXEL_FORMAT_SIZE
    ]
    if same_family:
        return same_family[0]

    for preferred in (
        "yuv420p", "yuv422p", "yuv444p", "rgb24", "gray", "yuv411p", "yuv410p"
    ):
        if preferred in supported and preferred in PIXEL_FORMAT_SIZE:
            return preferred
    for item in supported:
        if item in PIXEL_FORMAT_SIZE:
            return item
    raise ValueError(
        f"No supported raw transport format known for encoder {caps.name}: {supported}"
    )


def choose_base_fps(requested: float, caps: EncoderCaps) -> tuple[float, bool]:
    rates = caps.supported_rates
    if caps.name == "amv" and not rates:
        rates = list(AMV_FPS)
    if caps.name == "mpeg1video":
        # FFmpeg advertises a few legacy rates in `-h encoder=mpeg1video` that
        # the actual encoder rejects. Keep only the MPEG-1 rates the encoder
        # accepts in practice.
        rates = list(MPEG1_REAL_FPS)
    if not rates:
        return requested, True
    nearest = min(rates, key=lambda value: abs(value - requested))
    exact = abs(nearest - requested) < 0.01
    return nearest, exact


def normalize_dimensions(name: str, width: int, height: int) -> tuple[int, int, bool]:
    allowed = SPECIAL_SIZES.get(name)
    if not allowed:
        return width, height, True
    if (width, height) in allowed:
        return width, height, True
    candidates = [size for size in allowed if size[0] <= width and size[1] <= height]
    if not candidates:
        candidates = list(allowed)
    chosen = min(candidates, key=lambda size: abs(size[0] - width) + abs(size[1] - height))
    return chosen[0], chosen[1], False


def required_divisibility(pix_fmt: str) -> tuple[int, int]:
    fmt = pix_fmt.lower()
    if "420" in fmt or "440" in fmt:
        return 2, 2
    if "422" in fmt:
        return 2, 1
    if "411" in fmt:
        return 4, 1
    if "410" in fmt:
        return 4, 4
    return 1, 1


def normalize_transport_dimensions(width: int, height: int, pix_fmt: str) -> tuple[int, int, bool]:
    div_w, div_h = required_divisibility(pix_fmt)
    if width % div_w == 0 and height % div_h == 0:
        return width, height, True
    new_w = max(div_w, width - width % div_w)
    new_h = max(div_h, height - height % div_h)
    return new_w, new_h, False


def select_levels(base_fps: float, raw: Iterable[float] | None) -> list[float]:
    raw_values = [base_fps, 8.0, 5.0, 3.0] if raw is None else [*raw, base_fps]
    values: list[float] = []
    for item in raw_values:
        try:
            value = float(item)
        except (TypeError, ValueError):
            continue
        if math.isfinite(value) and 0 < value <= base_fps + 1e-9:
            values.append(value)
    result = sorted(set(round(value, 6) for value in values), reverse=True)
    return result or [base_fps]


def target_level(rate: float, limit: float, levels: list[float]) -> int:
    if len(levels) <= 1 or limit <= 0:
        return 0
    ratio = rate / limit
    if ratio >= 1.60:
        return len(levels) - 1
    if ratio >= 1.25:
        return min(2, len(levels) - 1)
    if ratio >= 1.00:
        return min(1, len(levels) - 1)
    return 0


def default_quality(name: str) -> int:
    return DEFAULT_QUALITY.get(name, 10)


def build_video_quality_args(name: str, quality: int) -> list[str]:
    return ["-crf", str(quality)] if name == "libx264" else ["-q:v", str(quality)]


def build_x264_args(
    preset: str | None,
    tune: str | None,
    profile: str | None,
    level: str | None,
) -> list[str]:
    args: list[str] = []
    if preset:
        args += ["-preset", preset]
    if tune:
        args += ["-tune", tune]
    if profile:
        args += ["-profile:v", profile]
    if level:
        args += ["-level:v", level]
    return args


def build_color_args(info: StreamInfo) -> list[str]:
    args: list[str] = []
    if info.color_range:
        args += ["-color_range", info.color_range]
    if info.color_space and info.color_space != "unknown":
        args += ["-colorspace", info.color_space]
    if info.color_primaries and info.color_primaries != "unknown":
        args += ["-color_primaries", info.color_primaries]
    if info.color_transfer and info.color_transfer != "unknown":
        args += ["-color_trc", info.color_transfer]
    return args


def canonical_scale_algo(value: str) -> str:
    key = value.strip().lower()
    try:
        return SCALE_ALGORITHM_ALIASES[key]
    except KeyError as exc:
        allowed = ", ".join(SCALE_ALGORITHMS)
        raise ValueError(f"unknown scale algorithm '{value}'; choose: {allowed}") from exc


def deinterlace_filter_expr(
    filter_name: str,
    mode: str,
    parity: str,
    marked_only: bool,
) -> str:
    if filter_name == "off":
        return ""
    if filter_name not in DEINTERLACE_FILTERS:
        raise ValueError(f"unsupported deinterlace filter: {filter_name}")
    parity_value = parity if parity in {"auto", "tff", "bff"} else "auto"
    deint_value = "interlaced" if marked_only else "all"
    if filter_name == "w3fdif":
        mode_value = "field" if mode == "send_field" else "frame"
    else:
        mode_value = mode if mode in {"send_frame", "send_field"} else "send_frame"
    return f"{filter_name}=mode={mode_value}:parity={parity_value}:deint={deint_value}"


def build_filters(
    out_w: int,
    out_h: int,
    base_fps: float,
    crop_4_3: bool,
    scale_algo: str,
    transport_fmt: str,
    deinterlace: str,
    deint_mode: str,
    deint_parity: str,
    deint_marked_only: bool,
) -> str:
    filters: list[str] = []
    if crop_4_3:
        filters.append(r"crop=min(iw\,ih*4/3):ih:(iw-min(iw\,ih*4/3))/2:0")
    deint_expr = deinterlace_filter_expr(
        deinterlace, deint_mode, deint_parity, deint_marked_only
    )
    if deint_expr:
        filters.append(deint_expr)
    # Apply FPS after deinterlacing. In send_field/bob mode, putting fps first
    # would throw away the extra field-derived pictures before they reach the
    # final cadence controller.
    filters.append(f"fps={base_fps:g}:round=down")
    filters.append(f"scale={out_w}:{out_h}:flags={canonical_scale_algo(scale_algo)}")
    filters.append(f"format={transport_fmt}")
    return ",".join(filters)


def validate_common_config(c: Config) -> None:
    numeric = {
        "limit": c.limit,
        "recover": c.recover,
        "window": c.window,
        "fps": c.fps,
        "start time": c.start_time_seconds,
    }
    if c.end_time_seconds is not None:
        numeric["end time"] = c.end_time_seconds
    for name, value in numeric.items():
        if not math.isfinite(value):
            raise ValueError(f"{name} must be finite")
    if c.start_time_seconds < 0:
        raise ValueError("start time must be >= 00:00:00")
    if c.end_time_seconds is not None and c.end_time_seconds <= c.start_time_seconds:
        raise ValueError("end time must be later than start time")
    if c.limit <= 0:
        raise ValueError("limit must be > 0")
    if not 0 < c.recover < c.limit:
        raise ValueError("recover must be > 0 and < limit")
    if c.window <= 0:
        raise ValueError("window must be > 0")
    if c.fps <= 0:
        raise ValueError("FPS must be > 0")
    if c.threads <= 0:
        raise ValueError("threads must be > 0")
    if c.buffer_frames <= 0:
        raise ValueError("capture buffer must be > 0")
    if c.width <= 0 or c.height <= 0:
        raise ValueError("resolution must be positive")
    if c.qv < 0:
        raise ValueError("quality must be >= 0")
    if c.audio_kbps <= 0:
        raise ValueError("audio bitrate must be > 0")
    if c.audio_channels <= 0:
        raise ValueError("audio channels must be > 0")
    if c.audio_rate is not None and c.audio_rate <= 0:
        raise ValueError("audio sample rate must be > 0")
    if c.encoder is not None and c.audio_encoder == "roq_dpcm":
        effective_audio_settings(c.encoder, c.audio_encoder, c.audio_rate, c.audio_channels)
    c.scale_algo = canonical_scale_algo(c.scale_algo)
    if c.deinterlace not in {"off", *DEINTERLACE_FILTERS}:
        raise ValueError("unsupported deinterlace filter")
    if c.deint_mode not in {"send_frame", "send_field"}:
        raise ValueError("deinterlace mode must be send_frame or send_field")
    if c.deint_parity not in {"auto", "tff", "bff"}:
        raise ValueError("deinterlace parity must be auto, tff, or bff")


def preflight_video_encode(
    *,
    ffmpeg: str,
    src: Path,
    encoder: EncoderInfo,
    fps: float,
    quality: int,
    threads: int,
    width: int,
    height: int,
    vf: str,
    muxer: str,
    stream: StreamInfo,
    x264_preset: str | None,
    x264_tune: str | None,
    x264_profile: str | None,
    x264_level: str | None,
    hwaccel_cuda: bool,
    start_time_seconds: float = 0.0,
) -> None:
    """Validate the real video filter/codec/muxer chain on a few frames."""
    with tempfile.TemporaryDirectory(prefix="live_archive_preflight_") as tmp:
        output = Path(tmp) / f"preflight.{encoder.extension}"
        cmd: list[str] = [ffmpeg, "-hide_banner", "-loglevel", "error"]
        if hwaccel_cuda:
            cmd += ["-hwaccel", "cuda"]
        if start_time_seconds > 0:
            cmd += ["-ss", f"{start_time_seconds:.6f}"]
        cmd += [
            "-threads", str(max(1, threads)),
            "-i", str(src),
            "-map", "0:v:0",
            "-an",
            "-vf", vf,
            "-frames:v", "3",
            "-c:v", encoder.name,
            *build_video_quality_args(encoder.name, quality),
            "-r", f"{fps:g}",
        ]
        if encoder.name == "libx264":
            cmd += build_x264_args(x264_preset, x264_tune, x264_profile, x264_level)
        cmd += [*build_color_args(stream), "-f", muxer, "-y", str(output)]
        result = run_capture(cmd, timeout=120)
        if result.returncode != 0:
            detail = result.stderr.strip() or "preflight failed without FFmpeg diagnostics"
            raise RuntimeError(f"Video preflight failed for {encoder.name}: {detail}")
        if not output.is_file() or output.stat().st_size == 0:
            raise RuntimeError("Video preflight produced no usable output")


def preflight_audio_encode(
    *,
    ffmpeg: str,
    src: Path,
    audio_encoder: str,
    audio_kbps: int,
    audio_channels: int,
    audio_rate: int | None,
    start_time_seconds: float = 0.0,
) -> None:
    """Open the selected audio encoder on a short sample before long encoding."""
    cmd: list[str] = [ffmpeg, "-hide_banner", "-loglevel", "error"]
    if start_time_seconds > 0:
        cmd += ["-ss", f"{start_time_seconds:.6f}"]
    cmd += ["-i", str(src), "-map", "0:a:0", "-vn", "-t", "2"]
    cmd += [
        "-c:a", audio_encoder,
        "-b:a", f"{int(audio_kbps)}k",
        "-ac", str(max(1, int(audio_channels))),
    ]
    if audio_rate:
        cmd += ["-ar", str(int(audio_rate))]
    cmd += ["-f", "null", "-"]
    result = run_capture(cmd, timeout=120)
    if result.returncode != 0:
        detail = result.stderr.strip() or "audio preflight failed without FFmpeg diagnostics"
        if audio_encoder == "roq_dpcm":
            raise RuntimeError(
                "Audio preflight failed for roq_dpcm. This encoder requires 22050 Hz "
                f"and mono/stereo audio. FFmpeg said: {detail}"
            )
        raise RuntimeError(f"Audio preflight failed for {audio_encoder}: {detail}")


class OutputMonitor:
    """Thread-safe rolling monitor for FFmpeg -progress output."""

    def __init__(self, window: float) -> None:
        self.window = max(0.1, float(window))
        self.lock = threading.Lock()
        self.samples: collections.deque[tuple[float, int, int | None]] = collections.deque()
        # Rolling aggregates avoid rescanning the progress window on every
        # output slot. They are updated only when a progress sample enters/leaves.
        self.window_bytes = 0
        self.window_media_us = 0
        self.total_size = 0
        self.last_out_time_us: int | None = None
        self.diagnostics: collections.deque[str] = collections.deque(maxlen=64)
        self.progress_end = False

    def add_progress(self, total_size: int, out_time_us: int) -> None:
        now = time.monotonic()
        with self.lock:
            if total_size >= self.total_size:
                delta = total_size - self.total_size
            else:
                delta = 0
                self.samples.clear()
                self.window_bytes = 0
                self.window_media_us = 0
                self.last_out_time_us = None
            self.total_size = total_size

            media_delta: int | None = None
            if self.last_out_time_us is not None and out_time_us >= self.last_out_time_us:
                media_delta = out_time_us - self.last_out_time_us
            self.last_out_time_us = out_time_us

            if delta > 0:
                self.samples.append((now, delta, media_delta))
                self.window_bytes += delta
                if media_delta is not None and media_delta > 0:
                    self.window_media_us += media_delta
            cutoff = now - self.window
            while self.samples and self.samples[0][0] < cutoff:
                _, old_bytes, old_media_us = self.samples.popleft()
                self.window_bytes -= old_bytes
                if old_media_us is not None and old_media_us > 0:
                    self.window_media_us -= old_media_us

    def rate_kbps(self) -> float:
        now = time.monotonic()
        with self.lock:
            if not self.samples:
                return 0.0
            total_bytes = self.window_bytes
            if total_bytes <= 0:
                return 0.0
            media_us = self.window_media_us
            if media_us > 0:
                return total_bytes * 8.0 / (media_us / 1_000_000.0) / 1000.0
            span = max(0.05, now - self.samples[0][0])
            return total_bytes * 8.0 / span / 1000.0

    def total_bytes(self) -> int:
        with self.lock:
            return self.total_size


def tail_lines(values: Iterable[str], count: int) -> list[str]:
    """Return the last *count* diagnostic lines from any iterable container."""
    if count <= 0:
        return []
    return list(values)[-count:]


def progress_reader(proc: subprocess.Popen[bytes], monitor: OutputMonitor) -> None:
    """Consume FFmpeg's machine-readable -progress=pipe:2 stream."""
    if proc.stderr is None:
        return
    size: int | None = None
    out_time_us: int | None = None
    try:
        for raw in proc.stderr:
            line = raw.decode("utf-8", errors="replace").strip()
            if not line:
                continue
            if "=" not in line:
                monitor.diagnostics.append(line)
                continue
            key, value = line.split("=", 1)
            if key == "total_size":
                try:
                    size = int(value)
                except ValueError:
                    size = None
            elif key == "out_time_us":
                try:
                    out_time_us = int(value)
                except ValueError:
                    out_time_us = None
            elif key == "progress":
                if size is not None and out_time_us is not None:
                    monitor.add_progress(size, out_time_us)
                if value == "end":
                    monitor.progress_end = True
                size = None
                out_time_us = None
    except Exception as exc:  # pragma: no cover - diagnostics only
        monitor.diagnostics.append(f"progress reader: {exc}")


def pipe_reader(proc: subprocess.Popen[bytes], diagnostics: collections.deque[str]) -> None:
    if proc.stderr is None:
        return
    try:
        for raw in proc.stderr:
            line = raw.decode("utf-8", errors="replace").strip()
            if line:
                diagnostics.append(line)
    except Exception as exc:  # pragma: no cover - diagnostics only
        diagnostics.append(f"decoder stderr reader: {exc}")


def read_exact(stream, size: int) -> bytes:
    parts: list[bytes] = []
    remaining = size
    while remaining > 0:
        chunk = stream.read(remaining)
        if not chunk:
            break
        parts.append(chunk)
        remaining -= len(chunk)
    return b"".join(parts)


def realtime_encode(
    *,
    ffmpeg: str,
    ffprobe: str,
    src: Path,
    dst: Path,
    encoder: EncoderInfo,
    fps: float,
    fps_levels: list[float],
    quality: int,
    threads: int,
    limit_kbps: float,
    recover_kbps: float,
    window: float,
    buffer_frames: int,
    audio_encoder: str | None,
    audio_kbps: int,
    audio_channels: int,
    audio_rate: int | None,
    hwaccel_cuda: bool,
    width: int,
    height: int,
    crop_4_3: bool,
    scale_algo: str,
    deinterlace: str,
    deint_mode: str,
    deint_parity: str,
    deint_marked_only: bool,
    x264_preset: str | None,
    x264_tune: str | None,
    x264_profile: str | None,
    x264_level: str | None,
    start_time_seconds: float = 0.0,
    end_time_seconds: float | None = None,
    available_filters: set[str] | None = None,
    available_scale_algorithms: set[str] | None = None,
    verbose: bool = True,
) -> EncodeResult:
    """Realtime adaptive encoder used by both CUI and CLI."""
    src = src.resolve()
    dst = dst.resolve()
    if not src.is_file():
        raise FileNotFoundError(f"Input not found: {src}")
    if src == dst:
        raise ValueError("Output must differ from input")
    validate_output_extension(dst, encoder)

    # If RoQ is selected, the encoder imposes restrictions beyond generic
    # FFmpeg capability detection. Reject impossible dimensions before startup.
    roq_dimension_warning = validate_roq_dimensions(width, height) if encoder.name == "roqvideo" else None
    audio_rate, audio_channels = effective_audio_settings(
        encoder, audio_encoder, audio_rate, audio_channels
    )

    config = Config(
        input_path=src,
        output_path=dst,
        start_time_seconds=start_time_seconds,
        end_time_seconds=end_time_seconds,
        encoder=encoder,
        audio_encoder=audio_encoder,
        fps=fps,
        fps_levels=fps_levels,
        limit=limit_kbps,
        recover=recover_kbps,
        window=window,
        qv=quality,
        threads=threads,
        buffer_frames=buffer_frames,
        audio_kbps=audio_kbps,
        audio_channels=audio_channels,
        audio_rate=audio_rate,
        width=width,
        height=height,
        crop_4_3=crop_4_3,
        scale_algo=scale_algo,
        deinterlace=deinterlace,
        deint_mode=deint_mode,
        deint_parity=deint_parity,
        deint_marked_only=deint_marked_only,
        cuda=hwaccel_cuda,
        x264_preset=x264_preset or "medium",
        x264_tune=x264_tune or "",
        x264_profile=x264_profile or "",
        x264_level=x264_level or "",
    )
    validate_common_config(config)

    stream = probe_stream(ffprobe, src)
    source_duration = probe_duration(ffprobe, src)
    resolved_end_seconds, duration = resolve_time_range(
        start_time_seconds, end_time_seconds, source_duration
    )
    source_has_audio = probe_has_audio(ffprobe, src)
    has_audio = source_has_audio and audio_encoder is not None and encoder.format_name not in NO_AUDIO_FORMATS
    if encoder.caps is None:
        encoder.caps = probe_encoder_caps(ffmpeg, encoder)
    caps = encoder.caps

    if available_filters is None:
        available_filters = detect_video_filters(ffmpeg)
    if available_scale_algorithms is None:
        available_scale_algorithms = detect_scale_algorithms(ffmpeg)

    if "scale" not in available_filters:
        raise ValueError("Selected FFmpeg build does not provide the scale filter")
    if deinterlace != "off" and deinterlace not in available_filters:
        raise ValueError(f"FFmpeg filter '{deinterlace}' is not available in this build")
    scale_algo = canonical_scale_algo(scale_algo)
    if scale_algo not in available_scale_algorithms:
        raise ValueError(
            f"Scale algorithm '{scale_algo}' is not exposed by this FFmpeg build"
        )
    transport_fmt = choose_transport_pix_fmt(stream.pix_fmt, caps)
    width, height, size_exact = normalize_dimensions(encoder.name, width, height)
    width, height, transport_exact = normalize_transport_dimensions(width, height, transport_fmt)
    if encoder.name == "roqvideo":
        roq_dimension_warning = validate_roq_dimensions(width, height)
    if not size_exact and verbose:
        print(f"Adjusted {encoder.name} to supported output size: {width}x{height}")
    elif not transport_exact and verbose:
        print(f"Adjusted output size for {transport_fmt}: {width}x{height}")
    if roq_dimension_warning and verbose:
        print(f"WARNING: {roq_dimension_warning}")

    vf = build_filters(
        out_w=width,
        out_h=height,
        base_fps=fps,
        crop_4_3=crop_4_3,
        scale_algo=scale_algo,
        transport_fmt=transport_fmt,
        deinterlace=deinterlace,
        deint_mode=deint_mode,
        deint_parity=deint_parity,
        deint_marked_only=deint_marked_only,
    )
    frame_size = raw_frame_size(width, height, transport_fmt)
    target_slots = math.ceil(duration * fps - 1e-9) if duration else None
    _, muxer = encoder.extension, encoder.format_name
    dst.parent.mkdir(parents=True, exist_ok=True)

    preflight_video_encode(
        ffmpeg=ffmpeg,
        src=src,
        encoder=encoder,
        fps=fps,
        quality=quality,
        threads=threads,
        width=width,
        height=height,
        vf=vf,
        muxer=muxer,
        stream=stream,
        x264_preset=x264_preset if encoder.name == "libx264" else None,
        x264_tune=x264_tune if encoder.name == "libx264" else None,
        x264_profile=x264_profile if encoder.name == "libx264" else None,
        x264_level=x264_level if encoder.name == "libx264" else None,
        hwaccel_cuda=hwaccel_cuda,
        start_time_seconds=start_time_seconds,
    )
    if has_audio and audio_encoder:
        preflight_audio_encode(
            ffmpeg=ffmpeg,
            src=src,
            audio_encoder=audio_encoder,
            audio_kbps=audio_kbps,
            audio_channels=audio_channels,
            audio_rate=audio_rate,
            start_time_seconds=start_time_seconds,
        )

    dec_cmd: list[str] = [ffmpeg, "-hide_banner", "-loglevel", "error"]
    if hwaccel_cuda:
        dec_cmd += ["-hwaccel", "cuda"]
    if start_time_seconds > 0:
        dec_cmd += ["-ss", f"{start_time_seconds:.6f}"]
    dec_cmd += ["-i", str(src)]
    if duration is not None:
        dec_cmd += ["-t", f"{duration:.6f}"]
    dec_cmd += [
        "-map", "0:v:0",
        "-an",
        "-vf", vf,
        "-pix_fmt", transport_fmt,
        "-f", "rawvideo",
        "pipe:1",
    ]

    enc_cmd: list[str] = [
        ffmpeg, "-hide_banner", "-loglevel", "error",
        "-threads", str(max(1, threads)),
        "-f", "rawvideo",
        "-pix_fmt", transport_fmt,
        "-s:v", f"{width}x{height}",
        "-framerate", f"{fps:g}",
        "-i", "pipe:0",
    ]
    if has_audio:
        if start_time_seconds > 0:
            enc_cmd += ["-ss", f"{start_time_seconds:.6f}"]
        enc_cmd += ["-i", str(src)]

    enc_cmd += ["-map", "0:v:0"]
    if has_audio:
        enc_cmd += ["-map", "1:a:0"]
    enc_cmd += ["-c:v", encoder.name]
    enc_cmd += build_video_quality_args(encoder.name, quality)
    if encoder.name == "libx264":
        enc_cmd += build_x264_args(x264_preset, x264_tune, x264_profile, x264_level)
    enc_cmd += ["-r", f"{fps:g}", "-fps_mode", "cfr"]
    enc_cmd += build_color_args(stream)

    if has_audio:
        if audio_encoder is None:
            raise RuntimeError("internal error: audio encoder vanished")
        enc_cmd += [
            "-c:a", audio_encoder,
            "-b:a", f"{int(audio_kbps)}k",
            "-ac", str(max(1, int(audio_channels))),
        ]
        if audio_rate:
            enc_cmd += ["-ar", str(int(audio_rate))]

    # The selected segment duration bounds both streams. There is no -shortest
    # because the raw-video producer must not race the audio input into a
    # premature encoder shutdown.
    if duration:
        enc_cmd += ["-t", f"{duration:.6f}"]
    enc_cmd += [
        "-flush_packets", "1",
        "-progress", "pipe:2",
        "-f", muxer,
        "-y", str(dst),
    ]

    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0
    if verbose:
        print(f"Encoder command: {encoder.name} -> {muxer} -> {dst}")
        print(f"Source pix_fmt : {stream.pix_fmt}")
        print(f"Pipe pix_fmt   : {transport_fmt}")
        print(f"Output size    : {width}x{height}")
        print(
            f"Time range     : {format_timecode(start_time_seconds)} - "
            f"{format_timecode(resolved_end_seconds)}"
            + (f" ({duration:.3f}s)" if duration is not None else "")
        )
        print(f"Filters        : {vf}")

    decoder: subprocess.Popen[bytes] | None = None
    encoder_proc: subprocess.Popen[bytes] | None = None
    progress_thread: threading.Thread | None = None
    capture_thread: threading.Thread | None = None
    decoder_stderr_thread: threading.Thread | None = None
    monitor = OutputMonitor(window)
    decoder_diagnostics: collections.deque[str] = collections.deque(maxlen=64)
    captured: collections.deque[FrameSample] = collections.deque(maxlen=max(1, buffer_frames))
    capture_done = threading.Event()
    capture_stop = threading.Event()
    capture_errors: list[str] = []
    captured_sequence = 0
    decoder_error: str | None = None

    def capture_worker() -> None:
        nonlocal captured_sequence
        try:
            assert decoder is not None and decoder.stdout is not None
            interval = 1.0 / fps
            next_deadline = time.perf_counter()
            while not capture_stop.is_set():
                frame = read_exact(decoder.stdout, frame_size)
                if len(frame) != frame_size:
                    break

                captured_sequence += 1
                captured.append((captured_sequence, frame))

                next_deadline += interval
                delay = next_deadline - time.perf_counter()
                if delay > 0:
                    time.sleep(delay)
                else:
                    next_deadline = time.perf_counter()
        except Exception as exc:
            capture_errors.append(str(exc))
        finally:
            capture_done.set()

    levels = select_levels(fps, fps_levels)
    current_level = 0
    effective_fps = levels[0]
    cadence = 1.0
    slot_interval = 1.0 / fps
    slots = 0
    fresh = 0
    repeated = 0
    start = time.perf_counter()
    last_status = 0.0
    high_since: float | None = None
    low_since: float | None = None

    try:
        decoder = subprocess.Popen(
            dec_cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            creationflags=creationflags,
        )
        decoder_stderr_thread = threading.Thread(
            target=pipe_reader,
            args=(decoder, decoder_diagnostics),
            daemon=True,
        )
        decoder_stderr_thread.start()

        encoder_proc = subprocess.Popen(
            enc_cmd,
            stdin=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            creationflags=creationflags,
        )
        progress_thread = threading.Thread(
            target=progress_reader,
            args=(encoder_proc, monitor),
            daemon=True,
        )
        progress_thread.start()
        capture_thread = threading.Thread(target=capture_worker, daemon=True)
        capture_thread.start()

        assert encoder_proc.stdin is not None
        previous: bytes | None = None
        previous_sequence: int | None = None
        next_slot = time.perf_counter()

        while True:
            if target_slots is not None and slots >= target_slots:
                break
            decoder_rc_now = decoder.poll()
            if decoder_rc_now is not None and decoder_rc_now != 0 and not capture_done.is_set():
                decoder_error = (
                    "decoder exited before capture reached EOF "
                    f"(code {decoder_rc_now}); "
                    f"{' | '.join(tail_lines(decoder_diagnostics, 8)) or 'no decoder diagnostic'}"
                )
                raise RuntimeError(decoder_error)
            if target_slots is None and capture_done.is_set() and not captured:
                break

            # Do not consume an output time slot until the first decoded frame
            # is available. Otherwise short files can lose their first slot
            # simply because the decoder/capture thread had not started yet,
            # making the final duration and frame count shorter than requested.
            if previous is None and not captured:
                if capture_done.is_set():
                    break
                time.sleep(min(0.01, slot_interval))
                continue

            next_slot += slot_interval
            delay = next_slot - time.perf_counter()
            if delay > 0:
                time.sleep(delay)
            else:
                next_slot = time.perf_counter()

            enc_rc = encoder_proc.poll()
            if enc_rc is not None:
                detail = " | ".join(tail_lines(monitor.diagnostics, 8)) or "no FFmpeg diagnostic"
                raise RuntimeError(
                    f"encoder exited before final frame (code {enc_rc}); {detail}"
                )

            slots += 1
            now = time.monotonic()
            total_rate = monitor.rate_kbps()
            video_rate = max(0.0, total_rate - (audio_kbps if has_audio else 0.0))

            if video_rate >= limit_kbps:
                low_since = None
                if high_since is None:
                    high_since = now
                target = target_level(video_rate, limit_kbps, levels)
                if target > current_level:
                    hold = 0.18 if video_rate >= limit_kbps * 1.25 else 0.30
                    if now - high_since >= hold:
                        current_level += 1
                elif target < current_level and video_rate < limit_kbps * 1.10:
                    if now - high_since >= 0.80:
                        current_level -= 1
            elif video_rate <= recover_kbps:
                high_since = None
                if low_since is None:
                    low_since = now
                if current_level > 0 and now - low_since >= 0.75:
                    current_level -= 1
            else:
                high_since = None
                low_since = None

            current_level = max(0, min(current_level, len(levels) - 1))
            new_effective = levels[current_level]
            if abs(new_effective - effective_fps) > 1e-9 and verbose:
                direction = "DOWN" if new_effective < effective_fps else "UP"
                print(
                    f"\n[{slots:7d}] video={video_rate:6.1f} kbps "
                    f"motion FPS {effective_fps:g} -> {new_effective:g} ({direction})",
                    flush=True,
                )
            effective_fps = new_effective

            cadence += effective_fps / fps
            emit_fresh = previous is None or cadence >= 1.0

            if emit_fresh:
                if captured:
                    sequence, frame = captured[-1]
                    captured.clear()
                elif previous is not None:
                    sequence, frame = previous_sequence, previous
                else:
                    continue

                is_new_frame = sequence != previous_sequence
                if is_new_frame:
                    previous = frame
                    fresh += 1
                    previous_sequence = sequence
                    payload = frame
                else:
                    if previous is None:
                        # The first frame is always accepted so the stream has a
                        # valid starting picture/keyframe.
                        previous = frame
                        previous_sequence = sequence
                        fresh += 1
                        payload = frame
                    else:
                        payload = previous
                        repeated += 1
                cadence -= 1.0
            else:
                if previous is None:
                    continue
                payload = previous
                repeated += 1

            try:
                encoder_proc.stdin.write(payload)
            except (BrokenPipeError, OSError) as exc:
                rc = encoder_proc.poll()
                detail = " | ".join(tail_lines(monitor.diagnostics, 8)) or "no FFmpeg diagnostic"
                raise RuntimeError(
                    f"encoder stdin closed at output slot {slots} "
                    f"(returncode={rc}); {detail}"
                ) from exc

            elapsed = time.perf_counter() - start
            if verbose and elapsed - last_status >= 0.5:
                state = "LIVE" if current_level == 0 else "THROTTLED"
                print(
                    f"\r[{elapsed:7.1f}s] video={video_rate:6.1f} kbps "
                    f"{state:<9} motion={effective_fps:g}fps "
                    f"slots={slots:7d} new={fresh:7d} repeat={repeated:7d}",
                    end="",
                    flush=True,
                )
                last_status = elapsed

        try:
            encoder_proc.stdin.close()
        except (BrokenPipeError, OSError) as exc:
            rc = encoder_proc.poll()
            detail = " | ".join(tail_lines(monitor.diagnostics, 8)) or "no FFmpeg diagnostic"
            raise RuntimeError(
                f"encoder stdin closed before final frame (returncode={rc}); {detail}"
            ) from exc

        # The raw decoder is only a live source for the Python controller. Once
        # the requested output cadence has been emitted, stop it explicitly
        # instead of waiting for a second independent EOF race on a pipe.
        capture_stop.set()
        if decoder.poll() is None:
            try:
                decoder.kill()
            except (OSError, ProcessLookupError):
                pass

        encoder_wait_timeout = max(60.0, (duration or 60.0) * 2.0 + 60.0)
        try:
            enc_rc = encoder_proc.wait(timeout=encoder_wait_timeout)
        except subprocess.TimeoutExpired as exc:
            try:
                encoder_proc.kill()
            except (OSError, ProcessLookupError):
                pass
            encoder_proc.wait(timeout=5)
            raise RuntimeError(
                f"encoder did not terminate within {encoder_wait_timeout:.0f}s"
            ) from exc

        decoder_wait_timeout = max(10.0, (duration or 30.0) * 0.25 + 10.0)
        try:
            dec_rc = decoder.wait(timeout=decoder_wait_timeout)
        except subprocess.TimeoutExpired as exc:
            try:
                decoder.kill()
            except (OSError, ProcessLookupError):
                pass
            decoder.wait(timeout=5)
            raise RuntimeError(
                f"decoder did not terminate within {decoder_wait_timeout:.0f}s after stop request"
            ) from exc

        if capture_thread is not None:
            capture_thread.join(timeout=5)
        if progress_thread is not None:
            progress_thread.join(timeout=3)
        if decoder_stderr_thread is not None:
            decoder_stderr_thread.join(timeout=3)

        if decoder_error:
            raise RuntimeError(decoder_error)
        # Non-zero decoder return codes after our deliberate stop are
        # expected; the pipe was intentionally stopped after the output slots
        # were produced. Real decode failures are caught above while running.
        if enc_rc != 0:
            detail = " | ".join(tail_lines(monitor.diagnostics, 10)) or "no FFmpeg diagnostic"
            raise RuntimeError(f"encoder exited with code {enc_rc}; {detail}")
        if not dst.is_file() or dst.stat().st_size == 0:
            raise RuntimeError("FFmpeg reported success but output file is empty")

        if capture_errors:
            detail = " | ".join(capture_errors[-5:])
            raise RuntimeError(f"frame capture failed: {detail}")

        final_path_ok = dst.is_file() and dst.stat().st_size > 0
        if not final_path_ok:
            raise RuntimeError("Final output file is missing or empty")

        measured_duration = duration if duration else max(0.001, slots / fps)
        total_bytes = monitor.total_bytes()
        avg_total_kbps = total_bytes * 8.0 / measured_duration / 1000.0
        avg_video_kbps = max(
            0.0, avg_total_kbps - (float(audio_kbps) if has_audio else 0.0)
        )
        return {
            "duration": float(measured_duration),
            "start_time_seconds": float(start_time_seconds),
            "end_time_seconds": resolved_end_seconds,
            "source_pix_fmt": stream.pix_fmt,
            "transport_pix_fmt": transport_fmt,
            "output_width": width,
            "output_height": height,
            "avg_video_kbps": float(avg_video_kbps),
            "avg_total_kbps": float(avg_total_kbps),
            "fresh": int(fresh),
            "repeated": int(repeated),
            "deinterlace": deinterlace,
            "scale_algo": canonical_scale_algo(scale_algo),
        }
    finally:
        for proc in (decoder, encoder_proc):
            if proc is not None and proc.poll() is None:
                try:
                    proc.kill()
                except (OSError, ProcessLookupError):
                    pass


def default_output(src: Path, encoder: EncoderInfo) -> Path:
    return src.with_name(f"{src.stem}_live.{encoder.extension}")


def input_file_menu() -> Path | None:
    raw = input("\nInput file or directory: ").strip().strip('"')
    path = Path(raw)
    if path.is_file():
        return path
    if not path.is_dir():
        print("Path not found.")
        return None
    files = sorted(
        [p for p in path.iterdir() if p.is_file() and p.suffix.lower() in VIDEO_EXTENSIONS],
        key=lambda p: p.name.lower(),
    )
    if not files:
        print("No common video files found in that directory.")
        return None
    for i, path_item in enumerate(files[:200], 1):
        print(f" {i:3d}. {path_item.name}")
    try:
        idx = int(input("Select number: ").strip()) - 1
    except ValueError:
        print("Invalid selection.")
        return None
    if 0 <= idx < min(200, len(files)):
        return files[idx]
    print("Invalid selection.")
    return None


def choose_encoder(
    c: Config,
    encoders: list[EncoderInfo],
    audio_available: set[str],
    ffmpeg: str,
) -> None:
    print("\n=== Legacy / classic encoders detected from current FFmpeg ===")
    for i, item in enumerate(encoders, 1):
        caps = item.caps
        pix = ",".join(caps.supported_pix_fmts) if caps and caps.supported_pix_fmts else "?"
        marker = " <" if c.encoder and item.name == c.encoder.name else ""
        print(f" {i:3d}. {item.name:14s} [{item.family:18s}] {item.description}{marker}")
        print(f"       pixel formats: {pix}")
    raw = input("Select encoder number (r=refresh): ").strip().lower()
    if raw == "r":
        return
    try:
        idx = int(raw) - 1
        if not (0 <= idx < len(encoders)):
            raise ValueError
        chosen = encoders[idx]
        if chosen.caps is None:
            chosen.caps = probe_encoder_caps(ffmpeg, chosen)
        c.encoder = chosen

        base_fps, exact = choose_base_fps(c.fps, chosen.caps)
        if not exact:
            print(f"{chosen.name}: requested {c.fps:g} fps is unsupported; using {base_fps:g} fps.")
            c.fps = base_fps
        c.fps_levels = select_levels(c.fps, None)
        c.width, c.height, size_exact = normalize_dimensions(chosen.name, c.width, c.height)
        if not size_exact:
            print(f"{chosen.name}: adjusted output size to {c.width}x{c.height}.")
        c.qv = default_quality(chosen.name)
        c.audio_encoder = choose_audio_encoder(chosen.format_name, audio_available)
        if chosen.name == "amv":
            print("AMV: fixed audio is 22050 Hz IMA-ADPCM; video FPS is limited to 10/14/15/18/21/25/30.")
            c.audio_encoder = "adpcm_ima_amv" if "adpcm_ima_amv" in audio_available else c.audio_encoder
            c.audio_rate = 22050
        elif chosen.name == "roqvideo":
            if "roq_dpcm" in audio_available:
                c.audio_encoder = "roq_dpcm"
                c.audio_rate = 22050
                c.audio_channels = min(max(c.audio_channels, 1), 2)
                print("RoQ: roq_dpcm requires 22050 Hz and mono/stereo audio; settings adjusted accordingly.")
            else:
                c.audio_encoder = None
                print("RoQ: roq_dpcm is unavailable; output will be video-only.")
            warning = validate_roq_dimensions(c.width, c.height)
            if warning:
                print(f"WARNING: {warning}")
        if c.audio_encoder is None:
            print(f"{chosen.name}: container {chosen.format_name} will be video-only.")
        if chosen.name != "libx264":
            c.x264_preset = "medium"
            c.x264_tune = ""
            c.x264_profile = ""
            c.x264_level = ""
        if c.output_is_auto and c.input_path:
            c.output_path = default_output(c.input_path, chosen)
    except (ValueError, RuntimeError) as exc:
        print(f"Invalid selection: {exc}")


def edit_settings(c: Config, available_filters: set[str], available_scale_algorithms: set[str]) -> None:
    deint_options = ["off", *[name for name in DEINTERLACE_FILTERS if name in available_filters]]
    while True:
        enc = c.encoder.name if c.encoder else "(none)"
        print("\n=== Parameters ===")
        print(f" 1. limit (video kbps)           = {c.limit:g}")
        print(f" 2. recover (video kbps)         = {c.recover:g}")
        print(f" 3. window (seconds)             = {c.window:g}")
        print(f" 4. base FPS                     = {c.fps:g}")
        print(f" 5. motion FPS levels            = {','.join(f'{x:g}' for x in c.fps_levels)}")
        print(f" 6. {'CRF' if enc == 'libx264' else 'q:v'}                         = {c.qv}")
        print(f" 7. threads                      = {c.threads}")
        print(f" 8. capture buffer (frames)     = {c.buffer_frames}")
        print(f" 9. size                         = {c.width}x{c.height}")
        print(f"10. crop 4:3                    = {'yes' if c.crop_4_3 else 'no'}")
        print(f"11. scale algorithm             = {c.scale_algo}")
        print(f"12. deinterlace filter          = {c.deinterlace}")
        print(f"13. deinterlace mode            = {c.deint_mode}")
        print(f"14. field parity                = {c.deint_parity}")
        print(f"15. deinterlace marked only     = {'yes' if c.deint_marked_only else 'no'}")
        print(f"16. audio bitrate               = {c.audio_kbps}k")
        print(f"17. audio channels              = {c.audio_channels}")
        print(f"18. audio sample rate           = {c.audio_rate or 'auto'}")
        print(f"19. CUDA decode                 = {'yes' if c.cuda else 'no'}")
        print(f"20. audio encoder               = {c.audio_encoder or 'none'}")
        if enc == "libx264":
            print(f"21. x264 preset                 = {c.x264_preset}")
            print(f"22. x264 tune                   = {c.x264_tune or 'none'}")
            print(f"23. x264 profile                = {c.x264_profile or 'auto'}")
            print(f"24. x264 level                  = {c.x264_level or 'auto'}")
        print(f"25. segment start (HH:MM:SS)    = {format_timecode(c.start_time_seconds)}")
        print(f"26. segment end (HH:MM:SS)      = {format_timecode(c.end_time_seconds)}")
        print(f"    available deinterlacers     = {', '.join(deint_options)}")
        print(" 0. Back")
        pick = input("Select parameter: ").strip()

        try:
            if pick == "0":
                return
            if pick == "1":
                c.limit = float(input(f"limit [{c.limit:g}]: ").strip() or c.limit)
            elif pick == "2":
                c.recover = float(input(f"recover [{c.recover:g}]: ").strip() or c.recover)
            elif pick == "3":
                c.window = float(input(f"window [{c.window:g}]: ").strip() or c.window)
            elif pick == "4":
                c.fps = float(input(f"base FPS [{c.fps:g}]: ").strip() or c.fps)
                if c.encoder and c.encoder.caps:
                    c.fps, exact = choose_base_fps(c.fps, c.encoder.caps)
                    if not exact:
                        print(f"Adjusted to supported FPS: {c.fps:g}")
                c.fps_levels = select_levels(c.fps, None)
            elif pick == "5":
                raw = input(f"levels [{','.join(f'{x:g}' for x in c.fps_levels)}]: ").strip()
                if raw:
                    c.fps_levels = select_levels(c.fps, [float(x) for x in raw.split(",")])
            elif pick == "6":
                c.qv = int(input(f"quality [{c.qv}]: ").strip() or c.qv)
            elif pick == "7":
                c.threads = int(input(f"threads [{c.threads}]: ").strip() or c.threads)
            elif pick == "8":
                c.buffer_frames = int(input(f"buffer frames [{c.buffer_frames}]: ").strip() or c.buffer_frames)
            elif pick == "9":
                raw = input(f"size [{c.width}x{c.height}]: ").strip()
                if raw:
                    m = re.fullmatch(r"(\d+)x(\d+)", raw.lower())
                    if not m:
                        raise ValueError("use WIDTHxHEIGHT")
                    c.width, c.height = int(m.group(1)), int(m.group(2))
            elif pick == "10":
                c.crop_4_3 = not c.crop_4_3
            elif pick == "11":
                raw = input(f"scale algorithm [{c.scale_algo}] ({', '.join(sorted(available_scale_algorithms))}): ").strip().lower()
                if raw:
                    canonical = canonical_scale_algo(raw)
                    if canonical not in available_scale_algorithms:
                        raise ValueError(f"scale algorithm '{canonical}' is not exposed by this FFmpeg build")
                    c.scale_algo = canonical
            elif pick == "12":
                raw = input(f"deinterlace [{' / '.join(deint_options)}] [{c.deinterlace}]: ").strip().lower()
                if raw:
                    if raw not in deint_options:
                        raise ValueError(f"choose one of: {', '.join(deint_options)}")
                    c.deinterlace = raw
            elif pick == "13":
                raw = input(f"deinterlace mode [send_frame/send_field] [{c.deint_mode}]: ").strip().lower()
                if raw:
                    if raw not in {"send_frame", "send_field"}:
                        raise ValueError("choose send_frame or send_field")
                    c.deint_mode = raw
            elif pick == "14":
                raw = input(f"parity [auto/tff/bff] [{c.deint_parity}]: ").strip().lower()
                if raw:
                    if raw not in {"auto", "tff", "bff"}:
                        raise ValueError("choose auto, tff, or bff")
                    c.deint_parity = raw
            elif pick == "15":
                c.deint_marked_only = not c.deint_marked_only
            elif pick == "16":
                c.audio_kbps = int(input(f"audio kbps [{c.audio_kbps}]: ").strip() or c.audio_kbps)
            elif pick == "17":
                c.audio_channels = int(input(f"audio channels [{c.audio_channels}]: ").strip() or c.audio_channels)
            elif pick == "18":
                raw = input(f"audio sample rate [{c.audio_rate or 'auto'}]: ").strip()
                c.audio_rate = int(raw) if raw else None
            elif pick == "19":
                c.cuda = not c.cuda
            elif pick == "20":
                raw = input(f"audio encoder [{c.audio_encoder or 'none'}]: ").strip().lower()
                c.audio_encoder = raw or None
            elif enc == "libx264" and pick == "21":
                c.x264_preset = input(f"preset [{c.x264_preset}]: ").strip() or c.x264_preset
            elif enc == "libx264" and pick == "22":
                c.x264_tune = input(f"tune [{c.x264_tune or 'none'}]: ").strip()
            elif enc == "libx264" and pick == "23":
                c.x264_profile = input(f"profile [{c.x264_profile or 'auto'}]: ").strip()
            elif enc == "libx264" and pick == "24":
                c.x264_level = input(f"level [{c.x264_level or 'auto'}]: ").strip()
            elif pick == "25":
                raw = input(
                    f"start time [{format_timecode(c.start_time_seconds)}] (HH:MM:SS[.mmm]): "
                ).strip()
                if raw:
                    new_start = parse_timecode(raw)
                    if c.end_time_seconds is not None and c.end_time_seconds <= new_start:
                        raise ValueError("end time must be later than start time")
                    c.start_time_seconds = new_start
            elif pick == "26":
                raw = input(
                    f"end time [{format_timecode(c.end_time_seconds)}] "
                    "(HH:MM:SS[.mmm], or 'end' for source end): "
                ).strip()
                if raw:
                    new_end = (
                        None if raw.lower() in {"end", "none", "eof"}
                        else parse_timecode(raw)
                    )
                    if new_end is not None and new_end <= c.start_time_seconds:
                        raise ValueError("end time must be later than start time")
                    c.end_time_seconds = new_end
            else:
                print("Invalid selection or x264-only option is not active.")
        except ValueError as exc:
            print(f"Invalid value: {exc}")

        try:
            validate_common_config(c)
        except ValueError as exc:
            print(f"Invalid configuration: {exc}")


def print_config(
    c: Config,
    ffmpeg: str,
    btbn: bool,
    encoder_count: int,
    available_filters: set[str],
    available_scale_algorithms: set[str],
) -> None:
    print("\n=== Current configuration ===")
    print(f"FFmpeg          : {ffmpeg}")
    print(f"Source          : {'BtbN GPL preferred build' if btbn else 'PATH/custom'}")
    print(f"Legacy found    : {encoder_count}")
    print(f"Input           : {c.input_path or '(not selected)'}")
    print(
        f"Time range      : {format_timecode(c.start_time_seconds)} - "
        f"{format_timecode(c.end_time_seconds)}"
    )
    print(f"Output          : {c.output_path or '(auto)'}")
    print(f"Output mode     : {'auto' if c.output_is_auto else 'custom'}")
    print(f"Video codec     : {c.encoder.name if c.encoder else '(not selected)'}")
    if c.encoder:
        print(f"Container       : {c.encoder.format_name} (.{c.encoder.extension})")
        if c.encoder.caps:
            print(f"Pixel formats   : {', '.join(c.encoder.caps.supported_pix_fmts) or '(auto)'}")
            print(
                f"FPS support     : {', '.join(f'{x:g}' for x in c.encoder.caps.supported_rates[:16]) or '(not declared)'}"
            )
    print(f"Quality         : {'CRF' if c.encoder and c.encoder.name == 'libx264' else 'q:v'} {c.qv}")
    print(f"FPS             : {c.fps:g} -> {','.join(f'{x:g}' for x in c.fps_levels)}")
    print(f"Limit           : {c.limit:g} kbps video")
    print(f"Recover         : {c.recover:g} kbps video")
    print(f"Window          : {c.window:g} s")
    print(f"Resolution      : {c.width}x{c.height}")
    print(f"Crop 4:3        : {'yes' if c.crop_4_3 else 'no'}")
    print(f"Scale algorithm : {canonical_scale_algo(c.scale_algo)}")
    print(f"Deinterlace     : {c.deinterlace}")
    print(f"Deint mode      : {c.deint_mode}, parity={c.deint_parity}, marked_only={c.deint_marked_only}")
    print(f"Audio           : {c.audio_encoder or 'none'} / {c.audio_kbps}k / {c.audio_channels}ch / {c.audio_rate or 'auto'}")
    if c.encoder and c.encoder.name == "roqvideo":
        warning = validate_roq_dimensions(c.width, c.height)
        if warning:
            print(f"RoQ note        : {warning}")
        if c.audio_encoder == "roq_dpcm":
            print("RoQ audio       : requires 22050 Hz, mono/stereo; 22050 Hz is enforced when set to auto")
    print(f"Threads         : {c.threads}")
    print(f"Buffer          : {c.buffer_frames} frames")
    print(f"CUDA decode     : {'yes' if c.cuda else 'no'}")
    print(f"Deint available : {', '.join(name for name in DEINTERLACE_FILTERS if name in available_filters) or 'none'}")
    print(f"Scale available : {', '.join(sorted(available_scale_algorithms))}")
    print("Impairments      : use frame_damage.py (separate post-processing tool)")
    if c.encoder and c.encoder.name == "libx264":
        print(f"x264 preset     : {c.x264_preset}")
        print(f"x264 tune       : {c.x264_tune or 'none'}")
        print(f"x264 profile    : {c.x264_profile or 'auto'}")
        print(f"x264 level      : {c.x264_level or 'auto'}")


def print_filter_list(ffmpeg: str, available_filters: set[str], available_scale_algorithms: set[str]) -> None:
    print("\n=== Relevant FFmpeg video filters ===")
    print(f"FFmpeg: {ffmpeg}")
    for name in DEINTERLACE_FILTERS:
        print(f" {'OK ' if name in available_filters else '---'} {name}")
    print("\nScaling algorithms exposed by scale/libswscale:")
    print(" " + ", ".join(sorted(available_scale_algorithms)))


def run_cui(ffmpeg: str, ffprobe: str, btbn: bool, available_filters: set[str], available_scale_algorithms: set[str]) -> int:
    version, _, likely_gpl = ffmpeg_build_info(ffmpeg)
    encoders = detect_legacy_encoders(ffmpeg)
    audio_available = detect_audio_encoders(ffmpeg)

    print("\nLive Archive Encoder CUI v6.3.3")
    print("============================")
    print(f"FFmpeg          : {version}")
    print(f"Executable      : {ffmpeg}")
    print(f"BtbN default    : {'yes' if btbn else 'fallback/custom'}")
    print(f"GPL build       : {'yes' if likely_gpl else 'not detected'}")
    print(f"Legacy video    : {len(encoders)} detected from ffmpeg -encoders")
    print("Impairments     : handled separately by frame_damage.py")
    print(f"Deinterlacers   : {', '.join(name for name in DEINTERLACE_FILTERS if name in available_filters) or 'none'}")
    print(f"Scalers         : {', '.join(sorted(available_scale_algorithms))}")

    c = Config()
    while True:
        print("\n=== Main menu ===")
        print(f" 1. Input file          = {c.input_path or '(not selected)'}")
        print(f" 2. Output file         = {c.output_path or '(auto)'}")
        print(f" 3. Video encoder       = {c.encoder.name if c.encoder else '(not selected)'}")
        print(" 4. Parameters")
        print(" 5. Rescan encoders")
        print(" 6. Show FFmpeg filters")
        print(" 7. Show configuration")
        print(" 8. Start encoding")
        print(" 0. Exit")
        pick = input("Select: ").strip()

        if pick == "0":
            return 0
        if pick == "1":
            selected = input_file_menu()
            if selected:
                c.input_path = selected
                if c.encoder and c.output_is_auto:
                    c.output_path = default_output(selected, c.encoder)
        elif pick == "2":
            raw = input("Output file path (blank=auto): ").strip().strip('"')
            if raw:
                c.output_path = Path(raw)
                c.output_is_auto = False
            elif c.input_path and c.encoder:
                c.output_path = default_output(c.input_path, c.encoder)
                c.output_is_auto = True
        elif pick == "3":
            choose_encoder(c, encoders, audio_available, ffmpeg)
        elif pick == "4":
            edit_settings(c, available_filters, available_scale_algorithms)
        elif pick == "5":
            try:
                encoders = detect_legacy_encoders(ffmpeg)
                print(f"Rescan complete: {len(encoders)} legacy video encoders detected.")
            except Exception as exc:
                print(f"ERROR: {exc}")
        elif pick == "6":
            print_filter_list(ffmpeg, available_filters, available_scale_algorithms)
        elif pick == "7":
            print_config(c, ffmpeg, btbn, len(encoders), available_filters, available_scale_algorithms)
        elif pick == "8":
            if not c.input_path or not c.input_path.is_file():
                print("Select a valid input video first.")
                continue
            if not c.encoder:
                print("Select a video encoder first.")
                continue
            if c.output_path is None:
                c.output_path = default_output(c.input_path, c.encoder)
            c.output_path.parent.mkdir(parents=True, exist_ok=True)
            if c.output_path.resolve() == c.input_path.resolve():
                print("Output must differ from input.")
                continue
            try:
                validate_output_extension(c.output_path, c.encoder)
            except ValueError as exc:
                print(f"ERROR: {exc}")
                continue
            if c.encoder.caps is None:
                c.encoder.caps = probe_encoder_caps(ffmpeg, c.encoder)
            try:
                validate_common_config(c)
                if c.deinterlace != "off" and c.deinterlace not in available_filters:
                    raise ValueError(f"FFmpeg filter '{c.deinterlace}' is not available")
                if canonical_scale_algo(c.scale_algo) not in available_scale_algorithms:
                    raise ValueError(f"scale algorithm '{c.scale_algo}' is not exposed by this FFmpeg build")
                if c.audio_encoder and c.audio_encoder not in audio_available:
                    raise ValueError(f"audio encoder '{c.audio_encoder}' is not available")
            except ValueError as exc:
                print(f"ERROR: {exc}")
                continue
            print_config(c, ffmpeg, btbn, len(encoders), available_filters, available_scale_algorithms)
            confirm = input("\nStart encoding? [Y/n]: ").strip().lower()
            if confirm not in ("", "y", "yes"):
                continue
            try:
                result = realtime_encode(
                    ffmpeg=ffmpeg,
                    ffprobe=ffprobe,
                    src=c.input_path,
                    dst=c.output_path,
                    encoder=c.encoder,
                    fps=c.fps,
                    fps_levels=c.fps_levels,
                    quality=c.qv,
                    threads=c.threads,
                    limit_kbps=c.limit,
                    recover_kbps=c.recover,
                    window=c.window,
                    buffer_frames=c.buffer_frames,
                    audio_encoder=c.audio_encoder,
                    audio_kbps=c.audio_kbps,
                    audio_channels=c.audio_channels,
                    audio_rate=c.audio_rate,
                    hwaccel_cuda=c.cuda,
                    width=c.width,
                    height=c.height,
                    crop_4_3=c.crop_4_3,
                    scale_algo=c.scale_algo,
                    deinterlace=c.deinterlace,
                    deint_mode=c.deint_mode,
                    deint_parity=c.deint_parity,
                    deint_marked_only=c.deint_marked_only,
                    x264_preset=c.x264_preset if c.encoder.name == "libx264" else None,
                    x264_tune=c.x264_tune if c.encoder.name == "libx264" else None,
                    x264_profile=c.x264_profile if c.encoder.name == "libx264" else None,
                    x264_level=c.x264_level if c.encoder.name == "libx264" else None,
                    start_time_seconds=c.start_time_seconds,
                    end_time_seconds=c.end_time_seconds,
                    available_filters=available_filters,
                    available_scale_algorithms=available_scale_algorithms,
                    verbose=True,
                )
                print(f"\nDone: {c.output_path}")
                print(f"Time range         : {format_timecode(result['start_time_seconds'])} - {format_timecode(result['end_time_seconds'])}")
                print(f"Duration           : {result['duration']:.2f}s")
                print(f"Source pix fmt     : {result['source_pix_fmt']}")
                print(f"Transport pix fmt  : {result['transport_pix_fmt']}")
                print(f"Output resolution  : {result['output_width']}x{result['output_height']}")
                print(f"Video average      : {result['avg_video_kbps']:.1f} kbps")
                print(f"Total average      : {result['avg_total_kbps']:.1f} kbps")
                print(f"Fresh frames       : {result['fresh']}")
                print(f"Repeated slots     : {result['repeated']}")
            except KeyboardInterrupt:
                print("\nInterrupted.")
            except Exception as exc:
                print(f"\nERROR: {exc}", file=sys.stderr)
        else:
            print("Invalid selection.")


def cli_main(
    args: argparse.Namespace,
    ffmpeg: str,
    ffprobe: str,
    available_filters: set[str],
    available_scale_algorithms: set[str],
) -> int:
    encoders = {item.name: item for item in detect_legacy_encoders(ffmpeg)}
    if args.encoder not in encoders:
        print(
            f"ERROR: {args.encoder} is not detected as a legacy/classic video encoder.",
            file=sys.stderr,
        )
        return 2
    encoder = encoders[args.encoder]
    encoder.caps = probe_encoder_caps(ffmpeg, encoder)

    src = Path(args.input)
    if not src.is_file():
        print(f"ERROR: input not found: {src}", file=sys.stderr)
        return 2
    dst = Path(args.output) if args.output else default_output(src, encoder)
    if dst.resolve() == src.resolve():
        print("ERROR: output must differ from input", file=sys.stderr)
        return 2
    try:
        validate_output_extension(dst, encoder)
    except ValueError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    base_fps, exact = choose_base_fps(args.fps, encoder.caps)
    if not exact:
        print(f"NOTE: {encoder.name} does not support {args.fps:g} fps; using {base_fps:g} fps.")
    width, height, size_exact = normalize_dimensions(encoder.name, args.width, args.height)
    if not size_exact:
        print(f"NOTE: {encoder.name} requires a special frame size; using {width}x{height}.")
    levels = select_levels(
        base_fps,
        [float(x) for x in args.fps_levels.split(",")] if args.fps_levels else None,
    )
    recover = args.recover if args.recover is not None else args.limit * 0.75
    audio_available = detect_audio_encoders(ffmpeg)
    audio_encoder = args.audio_encoder or choose_audio_encoder(encoder.format_name, audio_available)
    if encoder.format_name in NO_AUDIO_FORMATS:
        audio_encoder = None
    if audio_encoder and audio_encoder not in audio_available:
        print(f"ERROR: audio encoder not found: {audio_encoder}", file=sys.stderr)
        return 2
    audio_rate = args.audio_rate
    audio_channels = args.audio_channels
    if encoder.name == "amv":
        audio_encoder = "adpcm_ima_amv" if "adpcm_ima_amv" in audio_available else audio_encoder
        audio_rate = 22050
    elif encoder.name == "roqvideo" and audio_encoder == "roq_dpcm":
        audio_rate = 22050 if audio_rate is None else audio_rate
        if audio_channels not in (1, 2):
            print("ERROR: roq_dpcm only supports mono or stereo; use --audio-channels 1 or 2", file=sys.stderr)
            return 2
        if audio_rate != 22050:
            print("ERROR: roq_dpcm requires --audio-rate 22050 (or leave it unset for automatic correction)", file=sys.stderr)
            return 2
    deinterlace = args.deinterlace.lower()
    if deinterlace != "off" and deinterlace not in available_filters:
        print(f"ERROR: FFmpeg filter '{deinterlace}' is not available in this build", file=sys.stderr)
        return 2

    try:
        scale_algo = canonical_scale_algo(args.scale_algo)
        if scale_algo not in available_scale_algorithms:
            raise ValueError(
                f"scale algorithm '{scale_algo}' is not exposed by this FFmpeg build"
            )
        config = Config(
            input_path=src,
            output_path=dst,
            start_time_seconds=args.start_time,
            end_time_seconds=args.end_time,
            encoder=encoder,
            audio_encoder=audio_encoder,
            fps=base_fps,
            fps_levels=levels,
            limit=args.limit,
            recover=recover,
            window=args.window,
            qv=args.qv if args.qv is not None else default_quality(encoder.name),
            threads=args.threads,
            buffer_frames=args.buffer_frames,
            audio_kbps=args.audio_kbps,
            audio_channels=audio_channels,
            audio_rate=audio_rate,
            width=width,
            height=height,
            crop_4_3=not args.no_crop,
            scale_algo=scale_algo,
            deinterlace=deinterlace,
            deint_mode=args.deint_mode,
            deint_parity=args.deint_parity,
            deint_marked_only=args.deint_marked_only,
            cuda=not args.no_cuda,
            x264_preset=args.x264_preset,
            x264_tune=args.x264_tune,
            x264_profile=args.x264_profile,
            x264_level=args.x264_level,
        )
        validate_common_config(config)
        if config.end_time_seconds is not None and config.end_time_seconds <= config.start_time_seconds:
            raise ValueError("end time must be later than start time")
    except ValueError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    dst.parent.mkdir(parents=True, exist_ok=True)
    result = realtime_encode(
        ffmpeg=ffmpeg,
        ffprobe=ffprobe,
        src=src,
        dst=dst,
        encoder=encoder,
        fps=base_fps,
        fps_levels=levels,
        quality=config.qv,
        threads=config.threads,
        limit_kbps=config.limit,
        recover_kbps=config.recover,
        window=config.window,
        buffer_frames=config.buffer_frames,
        audio_encoder=audio_encoder,
        audio_kbps=config.audio_kbps,
        audio_channels=config.audio_channels,
        audio_rate=config.audio_rate,
        hwaccel_cuda=config.cuda,
        width=config.width,
        height=config.height,
        crop_4_3=config.crop_4_3,
        scale_algo=config.scale_algo,
        deinterlace=config.deinterlace,
        deint_mode=config.deint_mode,
        deint_parity=config.deint_parity,
        deint_marked_only=config.deint_marked_only,
        x264_preset=config.x264_preset if encoder.name == "libx264" else None,
        x264_tune=config.x264_tune if encoder.name == "libx264" else None,
        x264_profile=config.x264_profile if encoder.name == "libx264" else None,
        x264_level=config.x264_level if encoder.name == "libx264" else None,
        start_time_seconds=config.start_time_seconds,
        end_time_seconds=config.end_time_seconds,
        available_filters=available_filters,
        available_scale_algorithms=available_scale_algorithms,
        verbose=not args.quiet,
    )
    if not args.quiet:
        print(f"Done: {dst}")
        print(f"Time range         : {format_timecode(result['start_time_seconds'])} - {format_timecode(result['end_time_seconds'])}")
        print(f"Duration           : {result['duration']:.2f}s")
        print(f"Source pix fmt     : {result['source_pix_fmt']}")
        print(f"Transport pix fmt  : {result['transport_pix_fmt']}")
        print(f"Output resolution  : {result['output_width']}x{result['output_height']}")
        print(f"Video average      : {result['avg_video_kbps']:.1f} kbps")
        print(f"Total average      : {result['avg_total_kbps']:.1f} kbps")
        print(f"Fresh frames       : {result['fresh']}")
        print(f"Repeated slots     : {result['repeated']}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Live-style adaptive archival encoder (frame/packet impairment is handled by frame_damage.py)")
    parser.add_argument("input", nargs="?")
    parser.add_argument("output", nargs="?")
    parser.add_argument(
        "--start-time", "--start", dest="start_time", type=parse_timecode_arg,
        default=0.0, metavar="HH:MM:SS",
        help="segment start time, e.g. 00:12:30 (default: 00:00:00)",
    )
    parser.add_argument(
        "--end-time", "--end", dest="end_time", type=parse_timecode_arg,
        default=None, metavar="HH:MM:SS",
        help="exclusive segment end time, e.g. 00:18:00 (default: end of source)",
    )
    parser.add_argument("--encoder", default="rv20")
    parser.add_argument("--audio-encoder", default=None)
    parser.add_argument("--limit", type=float, default=200.0)
    parser.add_argument("--recover", type=float, default=None)
    parser.add_argument("--window", type=float, default=2.0)
    parser.add_argument("--fps", type=float, default=DEFAULT_FPS)
    parser.add_argument("--fps-levels", default=DEFAULT_FPS_LEVELS)
    parser.add_argument(
        "--qv", type=int, default=None,
        help="video quality: CRF for libx264, q:v for other codecs",
    )
    parser.add_argument("--threads", type=int, default=16)
    parser.add_argument("--buffer-frames", type=int, default=6)
    parser.add_argument("--audio-kbps", type=int, default=32)
    parser.add_argument("--audio-channels", type=int, default=1)
    parser.add_argument("--audio-rate", type=int, default=None)
    parser.add_argument("--width", type=int, default=WIDTH)
    parser.add_argument("--height", type=int, default=HEIGHT)
    parser.add_argument("--no-crop", action="store_true")
    parser.add_argument(
        "--scale-algo", "--scale-flags", dest="scale_algo", default="neighbor",
        help="scaler kernel; cubic is an alias for bicubic",
    )
    parser.add_argument("--deinterlace", choices=["off", *DEINTERLACE_FILTERS], default="off")
    parser.add_argument("--deint-mode", choices=["send_frame", "send_field"], default="send_frame")
    parser.add_argument("--deint-parity", choices=["auto", "tff", "bff"], default="auto")
    parser.add_argument("--deint-marked-only", action="store_true")
    parser.add_argument("--x264-preset", default="medium")
    parser.add_argument("--x264-tune", default="")
    parser.add_argument("--x264-profile", default="")
    parser.add_argument("--x264-level", default="")
    parser.add_argument("--no-cuda", action="store_true")
    parser.add_argument("--ffmpeg", default=None)
    parser.add_argument("--ffprobe", default=None)
    parser.add_argument("--list-filters", action="store_true")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--no-cui", action="store_true")
    return parser


def main() -> int:
    if sys.version_info < (3, 13):
        print(
            f"ERROR: Python 3.13+ is required; found {sys.version_info.major}.{sys.version_info.minor}",
            file=sys.stderr,
        )
        return 2

    args = build_parser().parse_args()
    try:
        ffmpeg, btbn = find_ffmpeg(args.ffmpeg)
        ffprobe = find_ffprobe(args.ffprobe, ffmpeg)
        available_filters = detect_video_filters(ffmpeg)
        available_filters = verify_named_filters(
            ffmpeg, available_filters, {"scale", *DEINTERLACE_FILTERS}
        )
        available_scale_algorithms = detect_scale_algorithms(ffmpeg)
    except (FileNotFoundError, RuntimeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    if args.list_filters:
        print_filter_list(ffmpeg, available_filters, available_scale_algorithms)
        return 0
    if not args.input and not args.no_cui:
        return run_cui(ffmpeg, ffprobe, btbn, available_filters, available_scale_algorithms)
    if not args.input:
        print("ERROR: input is required in --no-cui mode", file=sys.stderr)
        return 2
    try:
        return cli_main(args, ffmpeg, ffprobe, available_filters, available_scale_algorithms)
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        return 130
    except (FileNotFoundError, RuntimeError, ValueError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
