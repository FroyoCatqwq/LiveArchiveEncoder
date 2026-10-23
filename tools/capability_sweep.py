"""Empirical encoder x container x fps sweep.

Asks the FFmpeg in use what it *really* does, instead of what
`ffmpeg -h encoder=NAME` claims. Two declared-vs-real mismatches were already
found by hand (mpeg1video's unofficial 5/10/12/15, pal8's extra palette byte);
this script exists so the rest can be found the same way, in one pass.

    python tools/capability_sweep.py              # human table
    python tools/capability_sweep.py --json out.json

Output sections:

  containers  encoder -> {container: OK / NO: reason}
  fps         encoder -> {requested: delivered or FAIL}

"delivered" is what ffprobe reports as r_frame_rate on the produced file, so a
silently retimed rate (mpeg1video 20 -> 23.976) shows up as "20->23.976".
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

# The legacy/classic set this project exposes (mirrors FFmpegDiscovery's
# LegacyEncoderName). Kept as a regex so a build that drops one simply omits it.
LEGACY = re.compile(
    r"^(?:a64multi5?|amv|asv[12]|cinepak|cljr|flashsv2?|flv|h261|h263p?"
    r"|mjpeg|msmpeg4v2?|msrle|msvideo1|mpeg[124]video|mpeg4|qtrle|roqvideo"
    r"|rpza|rv(?:10|20)|smc|snow|svq1|wmv[12]|zmbv|libtheora|libx264|libxvid|vivo)$",
    re.IGNORECASE,
)

# Candidate containers per encoder family, by *muxer* name -- `-f mkv` is not a
# muxer ("Requested output format 'mkv' is not known"), `-f matroska` is. The
# first entry of DEFAULT_CONTAINER is this project's default; the rest are what
# a user might plausibly ask for.
COMMON_CONTAINERS = ["avi", "matroska", "mov", "mp4", "mpeg", "flv", "asf", "ogg", "rm", "mpegts", "webm"]

DEFAULT_CONTAINER = {
    "a64multi": "a64", "a64multi5": "a64", "amv": "amv",
    "wmv1": "asf", "wmv2": "asf", "rv10": "rm", "rv20": "rm",
    "mpeg1video": "mpeg", "mpeg2video": "mpeg", "mpeg4": "avi", "libxvid": "avi",
    "libtheora": "ogg", "libx264": "mp4", "flv": "flv", "flashsv": "flv",
    "flashsv2": "flv", "h261": "h261", "h263": "h263", "h263p": "avi",
    "mjpeg": "avi", "qtrle": "mov", "rpza": "mov", "smc": "mov", "svq1": "mov",
    "roqvideo": "roq",
}

EXT = {
    "a64": "a64", "amv": "amv", "asf": "wmv", "avi": "avi", "flv": "flv",
    "h261": "h261", "h263": "h263", "mpeg": "mpg", "mp4": "mp4",
    "matroska": "mkv", "mov": "mov", "ogg": "ogv", "rm": "rm", "roq": "roq",
    "mpegts": "ts", "webm": "webm",
}

# Sizes an encoder actually accepts. Anything else gets 320x240.
SIZE = {
    "h261": "352x288",
    "h263": "352x288",
    "roqvideo": "320x240",
    "amv": "320x240",
    "a64multi": "320x200",
    "a64multi5": "320x200",
    "msrle": "320x240",
    "vivo": "160x120",
    "snow": "320x240",
}

FPS_PROBE = ["5", "10", "12", "15", "20", "23.976", "24", "25", "29.97", "30", "50", "59.94", "60"]


def run(cmd: list[str], timeout: int = 60) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout,
                          errors="replace")


def encoders(ffmpeg: str) -> list[str]:
    out = run([ffmpeg, "-hide_banner", "-encoders"]).stdout
    names = []
    for line in out.splitlines():
        m = re.match(r"^\s*V[\.A-Z]{5}\s+(\S+)\s", line)
        if m and LEGACY.match(m.group(1)):
            names.append(m.group(1))
    return sorted(set(names))


def probe_rate(ffprobe: str, path: str) -> tuple[str | None, str | None, int, str | None]:
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        return None, None, 0, None
    p = run([ffprobe, "-v", "error", "-select_streams", "v:0", "-count_packets",
             "-show_entries", "stream=r_frame_rate,avg_frame_rate,codec_name,nb_read_packets",
             "-of", "default=nw=1", path])
    rate = avg = codec = None
    frames = 0
    for line in p.stdout.splitlines():
        if line.startswith("r_frame_rate="):
            rate = line.split("=", 1)[1].strip()
        elif line.startswith("avg_frame_rate="):
            avg = line.split("=", 1)[1].strip()
        elif line.startswith("codec_name="):
            codec = line.split("=", 1)[1].strip()
        elif line.startswith("nb_read_packets="):
            try:
                frames = int(line.split("=", 1)[1])
            except ValueError:
                frames = 0
    return rate, codec, frames, avg


def rate_value(text: str | None) -> float | None:
    if not text or "/" not in text:
        return None
    num, den = text.split("/", 1)
    try:
        n, d = float(num), float(den)
    except ValueError:
        return None
    return n / d if d else None


def try_encode(ffmpeg: str, encoder: str, container: str, fps: str, out: str,
               explicit_container: bool) -> tuple[bool, str]:
    size = SIZE.get(encoder, "320x240")
    w, h = size.split("x")
    cmd = [
        ffmpeg, "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", f"testsrc=size={size}:rate={fps}:duration=0.6",
        "-frames:v", "8",
        "-c:v", encoder, "-an", "-s:v", f"{w}x{h}",
    ]
    if encoder == "amv":
        # The AMV muxer refuses a video-only file ("AMV files only support 2
        # streams"), so the sweep has to carry an audio track for it.
        cmd = [
            ffmpeg, "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", f"testsrc=size={size}:rate={fps}:duration=0.6",
            "-f", "lavfi", "-i", f"sine=frequency=440:sample_rate=22050:duration=0.6",
            "-frames:v", "8", "-c:v", encoder, "-c:a", "adpcm_ima_amv",
            "-ac", "1", "-ar", "22050", "-block_size:a", str(int(22050 / float(fps))),
        ]
    if explicit_container:
        cmd += ["-f", container]
    cmd += ["-y", out]
    try:
        p = run(cmd)
    except subprocess.TimeoutExpired:
        return False, "timeout"
    if p.returncode != 0:
        first = next((x.strip() for x in p.stderr.splitlines() if x.strip()), "")
        return False, first[:160]
    if not os.path.exists(out) or os.path.getsize(out) == 0:
        return False, "no output"
    return True, ""


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ffmpeg", default=shutil.which("ffmpeg") or "ffmpeg")
    ap.add_argument("--ffprobe", default=shutil.which("ffprobe") or "ffprobe")
    ap.add_argument("--json", default="")
    ap.add_argument("--only", default="", help="comma-separated encoder list")
    args = ap.parse_args()

    ffmpeg, ffprobe = args.ffmpeg, args.ffprobe
    version = run([ffmpeg, "-hide_banner", "-version"]).stdout.splitlines()
    print(f"# {version[0] if version else 'ffmpeg unknown'}", file=sys.stderr)

    names = encoders(ffmpeg)
    if args.only:
        want = {x.strip() for x in args.only.split(",") if x.strip()}
        names = [n for n in names if n in want]
    print(f"# {len(names)} legacy encoders: {', '.join(names)}", file=sys.stderr)

    tmp = tempfile.mkdtemp(prefix="capsweep_")
    report: dict[str, dict] = {}

    for encoder in names:
        default = DEFAULT_CONTAINER.get(encoder, "avi")
        entry: dict = {"default": default, "containers": {}, "fps": {},
                       "fps_by_container": {}, "size": SIZE.get(encoder, "320x240")}

        # ---- containers: default first, then the rest ----
        working = []
        expected = None
        order = [default] + [c for c in COMMON_CONTAINERS if c != default]
        for container in order:
            ext = EXT.get(container, container)
            out = os.path.join(tmp, f"{encoder}.{ext}")
            try:
                os.remove(out)
            except OSError:
                pass
            ok, why = try_encode(ffmpeg, encoder, container, "25", out, explicit_container=True)
            if ok:
                rate, codec, frames, avg = probe_rate(ffprobe, out)
                if expected is None:
                    expected = codec
                # Muxing is not the same as storing: AVI accepts an RV20 stream
                # and ffprobe then reads it back as "rawvideo", and the rm muxer
                # writes a codec tag RealMedia can actually advertise instead of
                # the one we asked for. Both are files no player will decode, so
                # a container only counts as usable when the codec survives.
                if codec != expected:
                    entry["containers"][container] = {
                        "ok": False, "why": f"stored as {codec}, expected {expected}",
                    }
                else:
                    entry["containers"][container] = {
                        "ok": True, "codec": codec, "frames": frames,
                        "rate": rate, "avg": avg,
                    }
                    working.append(container)
            else:
                entry["containers"][container] = {"ok": False, "why": why}

        # ---- fps: only meaningful on a container that works ----
        # The raw containers (.h261/.h263/.a64/.roq) carry no timestamps at all,
        # so a rate probed out of them says nothing about what the encoder did.
        # AVI does carry a rate, which is why it is swept alongside whenever it
        # is a legal target.
        base = working[0] if working else default
        containers_to_sweep = [base]
        if "avi" in working and base != "avi":
            containers_to_sweep.append("avi")

        for container in containers_to_sweep:
            ext = EXT.get(container, container)
            table: dict[str, dict] = {}
            for fps in FPS_PROBE:
                out = os.path.join(tmp, f"{encoder}_fps.{ext}")
                try:
                    os.remove(out)
                except OSError:
                    pass
                ok, why = try_encode(ffmpeg, encoder, container, fps, out, explicit_container=False)
                if not ok:
                    table[fps] = {"ok": False, "why": why}
                    continue
                rate, codec, frames, avg = probe_rate(ffprobe, out)
                # avg_frame_rate is the honest one here: ffprobe reports the field
                # rate as r_frame_rate for MPEG-1/2 (a 25 fps file reads 50/1),
                # which is a heuristic about the codec, not something the encoder did.
                value = rate_value(avg) or rate_value(rate)
                table[fps] = {"ok": True, "rate": rate, "avg": avg, "frames": frames,
                              "value": value}
            entry["fps_by_container"][container] = table
            entry["fps"] = table
        entry["fps_container"] = base
        report[encoder] = entry
        print(f"  {encoder:12s} containers={len(working)} fpsok="
              f"{sum(1 for v in entry['fps'].values() if v['ok'])}", file=sys.stderr)

    # ---- human table ----
    print()
    print("## containers")
    for encoder, entry in report.items():
        ok = [c for c, v in entry["containers"].items() if v["ok"]]
        print(f"{encoder:12s} default={entry['default']:6s} ok={','.join(ok) or '-'}")

    print()
    print("## fps (per container, avg_frame_rate; 'x->y' means the encoder retimed)")
    for encoder, entry in report.items():
        for container, table in entry["fps_by_container"].items():
            cells = []
            for fps in FPS_PROBE:
                v = table[fps]
                if not v["ok"]:
                    cells.append(f"{fps}:FAIL")
                else:
                    value = v.get("value")
                    requested = float(fps)
                    if value is None:
                        cells.append(f"{fps}:?{v.get('avg') or v.get('rate')}")
                    elif abs(value - requested) < 0.02:
                        cells.append(f"{fps}:ok")
                    else:
                        cells.append(f"{fps}->{value:.3f}")
            print(f"{encoder:12s} [{container:8s}] {' '.join(cells)}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=1, ensure_ascii=False)
        print(f"\nwrote {args.json}", file=sys.stderr)

    shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
