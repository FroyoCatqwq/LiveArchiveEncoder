#!/usr/bin/env python3
"""Probe the audio limits behind Core/AudioCatalog.cs.

Every sample-rate list and bitrate band in the catalogue was measured, not
looked up: this script encodes a short sine with each codec, at each rate, at
each bitrate, and records what the FFmpeg build actually accepts. Re-run it
after a toolchain update and diff the output; the catalogue is the thing that
keeps a bad --audio-rate/--audio-kbps from aborting an encode halfway through.

    python tools/probe_audio_limits.py            # all rounds
    python tools/probe_audio_limits.py flv        # one round by name

Rounds: rates, kbps, flv, round2, round3, round4 (see ROUNDS below).
The FFmpeg on PATH is used; the app's own build works fine.
"""
import subprocess
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "tests" / "out" / "probe"
OUT.mkdir(parents=True, exist_ok=True)

# codec -> container that muxes it (mirrors AudioCatalog.ContainerCodecs)
CODEC_CONT = {
    "ac3": "mpeg", "eac3": "mpeg", "aac": "mp4", "libmp3lame": "mp4",
    "mp3_mf": "asf", "mp2": "mpeg", "mp2fixed": "mpeg",
    "wmav1": "asf", "wmav2": "asf", "libvorbis": "ogg", "vorbis": "ogg",
    "libopus": "ogg", "opus": "ogg", "adpcm_ima_amv": "amv",
    "adpcm_ima_wav": "avi", "roq_dpcm": "roq", "flac": "matroska",
    "pcm_s16le": "matroska", "pcm_u8": "matroska",
}
RATES = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 96000]
BITRATES = [8, 12, 16, 20, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128,
            160, 192, 224, 256, 320, 384, 448, 512, 640]


def run(codec, cont, rate, kbps=None, channels=1, duration=0.3, extra=None):
    """One encode. True when FFmpeg accepted the combination."""
    out = OUT / f"{codec}_{cont}_{rate}_{kbps}.{cont}"
    cmd = ["ffmpeg", "-hide_banner", "-nostdin", "-v", "error", "-y",
           "-f", "lavfi", "-i", f"sine=frequency=440:duration={duration}:sample_rate={rate}",
           "-ac", str(channels), "-c:a", codec, "-ar", str(rate)]
    if kbps is not None:
        cmd += ["-b:a", f"{kbps}k"]
    if extra:
        cmd += extra
    cmd += ["-f", cont, str(out)]
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    err = (p.stderr or "").strip().replace("\n", " | ")
    return p.returncode == 0 and out.exists() and out.stat().st_size > 0, err


def rates_round():
    """Which rates each codec accepts at a moderate bitrate."""
    for codec, cont in CODEC_CONT.items():
        good = []
        for r in RATES:
            ok, err = run(codec, cont, r, 128)
            if not ok and r <= 16000:
                ok, err = run(codec, cont, r, 32)  # a low rate may have no 128k slot
            if ok:
                good.append(r)
            else:
                print(f"    {codec} @{r}: {err[:100]}")
        print(f"  {codec:16} rates={good}")


def kbps_round():
    """Contiguous bitrate band at the low / mid / high supported rate."""
    for codec, cont in CODEC_CONT.items():
        ok_rates = [r for r in RATES if run(codec, cont, r, 128)[0] or run(codec, cont, r, 32)[0]]
        if not ok_rates:
            continue
        for r in sorted(set([ok_rates[0], ok_rates[len(ok_rates) // 2], ok_rates[-1]])):
            first = last = None
            for b in BITRATES:
                if run(codec, cont, r, b)[0]:
                    first = first if first is not None else b
                    last = b
                elif first is not None:
                    break
            print(f"  {codec:16} rate={r:6} band={first}-{last}")


def flv_round():
    """Container-level rate limits: FLV and MP4 vs the codec's own list."""
    for codec in ["aac", "libmp3lame", "mp3_mf"]:
        good = [r for r in RATES if run(codec, "flv", r, 128)[0]]
        print(f"  flv {codec:12} rates={good}")
    for cont in ["mp4", "matroska", "mov", "asf", "avi"]:
        good = [r for r in RATES if run("libmp3lame", cont, r, 64)[0]]
        print(f"  {cont:10} libmp3lame rates={good}")


def round2():
    """Discrete bitrate bands (mp2) and the CBR/experimental codecs."""
    for r in [16000, 22050, 24000, 32000, 44100, 48000]:
        oks = [b for b in BITRATES if run("mp2", "mpeg", r, b)[0]]
        print(f"  mp2 rate={r:6} ok={oks}")
    for codec, cont in [("libvorbis", "ogg"), ("libopus", "ogg")]:
        for r in [8000, 22050, 44100, 48000]:
            oks = [b for b in BITRATES if run(codec, cont, r, b)[0]]
            print(f"  {codec} rate={r:6} ok={oks}")


def round3():
    """Validity, not just exit code: a file FFmpeg wrote but ffprobe rejects."""
    for codec, cont in [("ac3", "mpeg"), ("aac", "mp4"), ("wmav2", "asf")]:
        for r in [32000, 48000]:
            oks = []
            for b in [8, 12, 16, 24, 32, 64, 128]:
                ok, _ = run(codec, cont, r, b, duration=0.5)
                if not ok:
                    continue
                f = OUT / f"{codec}_{cont}_{r}_{b}.{cont}"
                q = subprocess.run(["ffprobe", "-v", "error", "-show_entries",
                                    "format=duration", "-of", "csv=p=0", str(f)],
                                   capture_output=True, text=True)
                try:
                    if float(q.stdout.strip()) >= 0.25:
                        oks.append(b)
                except ValueError:
                    pass
            print(f"  {codec} rate={r:6} valid={oks}")


ROUNDS = {
    "rates": rates_round,
    "kbps": kbps_round,
    "flv": flv_round,
    "round2": round2,
    "round3": round3,
}

if __name__ == "__main__":
    wanted = sys.argv[1:] or list(ROUNDS)
    for name in wanted:
        print(f"== {name} ==", flush=True)
        ROUNDS[name]()
    print("DONE")
