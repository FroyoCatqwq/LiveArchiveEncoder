#!/usr/bin/env python3
"""Option-path smoke test.

The encoder matrix (tests/e2e/feature_sweep.py) covers "does this codec work"; this
one covers "do the option paths still work" -- turbo, sharding, the split audio
pipeline, deinterlacing, cropping and time ranges. Each case only has to produce
a structurally valid file; bit-exactness is the matrix's job.

    python tests/e2e/option_smoke.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe python tests/e2e/option_smoke.py
"""

import json
import os
import subprocess
import sys
import time
from pathlib import Path
# The apphost carries no extension on Unix; every path in this suite goes
# through EXE_NAME so the same script runs on Windows and on Linux.
EXE_NAME = "LiveArchiveEncoder.exe" if os.name == "nt" else "LiveArchiveEncoder"


ROOT = Path(__file__).resolve().parent.parent.parent  # tests/e2e -> project root
DEFAULT_EXE = ROOT / "publish" / "jit" / EXE_NAME
EXE = Path(os.environ.get("CS_EXE", DEFAULT_EXE))
SRC = ROOT / "tests" / "media" / "test_src.mp4"
OUT = ROOT / "tests" / "out" / "options"

# name, extra args, output extension. Every case names its encoder explicitly:
# the default is rv20 (rm), and the extension has to match the muxer.
CASES = [
    ("baseline",        ["--encoder", "rv20"],                  "rm"),
    ("turbo",           ["--encoder", "rv20", "--turbo"],       "rm"),
    ("turbo_mpeg4",     ["--encoder", "mpeg4", "--turbo"],      "avi"),
    ("parallel4",       ["--encoder", "mpeg4", "--turbo", "--parallel", "4"], "avi"),
    ("parallel_roq",    ["--encoder", "roqvideo", "--turbo", "--parallel", "4"], "roq"),
    ("parallel_rv20",   ["--encoder", "rv20", "--turbo", "--parallel", "4"],     "rm"),
    ("audio_separate",  ["--encoder", "mpeg4", "--audio-mode", "separate"], "avi"),
    ("audio_auto",      ["--encoder", "mpeg4", "--audio-mode", "auto"],     "avi"),
    ("audio_inline",    ["--encoder", "mpeg4", "--audio-mode", "inline"],   "avi"),
    ("quiet",           ["--encoder", "mpeg4", "--quiet"],      "avi"),
    ("deint_yadif",     ["--encoder", "mpeg4", "--deinterlace", "yadif"],  "avi"),
    ("deint_bwdif",     ["--encoder", "mpeg4", "--deinterlace", "bwdif",
                         "--deint-mode", "send_field"],         "avi"),
    ("nocrop",          ["--encoder", "mpeg4", "--no-crop", "--scale-algo", "bicubic"], "avi"),
    ("timerange",       ["--encoder", "mpeg4", "--start-time", "00:00:02",
                         "--end-time", "00:00:08"],             "avi"),
    ("fps_levels",      ["--encoder", "mpeg4", "--fps", "25",
                         "--fps-levels", "25,15,10,5"],         "avi"),
    # v7.7 #6: per-rung bitrate thresholds. 10 fps is entered at 120 kbps and
    # 5 fps at 150 kbps, instead of the multiples of --limit the ladder used.
    ("level_kbps",      ["--encoder", "mpeg4", "--turbo",
                         "--fps-levels", "10:120:110,5:150"],   "avi"),
    # v7.7 #1: 19.010s at 15 fps is 285.15 slots, so the slot grid reaches
    # 19.067s -- past the end of the source. This case failed with
    # "end time ... exceeds the source duration" before the planner clamped the
    # last boundary to the end of the range.
    ("shard_rounding",  ["--encoder", "mpeg4", "--turbo", "--parallel", "4",
                         "--end-time", "00:00:19.010"],         "avi"),
    ("x264",            ["--encoder", "libx264", "--qv", "23",
                         "--x264-preset", "faster"],            "mp4"),
    ("amv_forced",      ["--encoder", "amv"],                   "amv"),
    ("high_limit",      ["--encoder", "mpeg4", "--limit", "99999",
                         "--recover", "90000"],                 "avi"),
    # "path" is the escape hatch back to the system FFmpeg: discovery must skip
    # any managed build and still produce a normal encode.
    ("source_path",     ["--encoder", "mpeg4", "--turbo", "--ffmpeg-source", "path"], "avi"),
    # --play off must be a no-op, or scripting an encode would pop a window.
    ("play_off",        ["--encoder", "mpeg4", "--turbo", "--play", "off"], "avi"),

    # ---- v7.6: era preset + post-processing stages ----
    # An era preset rewrites the encoder, frame size and bitrate ceiling, so the
    # output container follows the preset's encoder, not the old default.
    ("era_flv",         ["--era", "flv07", "--turbo"],                    "flv"),
    # The watermark stage writes <stem>_wm.mp4 next to the main product.
    ("watermark_only",  ["--encoder", "mpeg4", "--turbo", "--watermark", "测试"], "avi"),
    # Both stages: watermark -> recompress, and --recompress-keep keeps the
    # intermediate so both can be probed below.
    ("post_full_chain", ["--encoder", "mpeg4", "--turbo", "--watermark", "测试",
                         "--recompress", "--recompress-keep",
                         "--recompress-args", "-vf scale=320:240,fps=15 "
                                              "-c:v libx264 -crf 28 -ac 1 -b:a 32k"], "avi"),

    # ---- v7.8 #4: container choice ----
    # mpeg1video/mpeg2video/mpeg4/libxvid/libx264 gain AVI, libx264 gains MKV.
    # The default container is unchanged, which is why every case above still
    # names the same extension it always did.
    ("container_m1v_avi",  ["--encoder", "mpeg1video", "--container", "avi",
                            "--turbo"],                                       "avi"),
    ("container_m2v_avi",  ["--encoder", "mpeg2video", "--container", "avi",
                            "--turbo"],                                       "avi"),
    ("container_xvid_mp4", ["--encoder", "libxvid", "--container", "mp4",
                            "--turbo"],                                       "mp4"),
    ("container_x264_avi", ["--encoder", "libx264", "--container", "avi",
                            "--turbo"],                                       "avi"),
    ("container_x264_mkv", ["--encoder", "libx264", "--container", "mkv",
                            "--turbo"],                                       "mkv"),

    # ---- v7.8 #5/#6: 16:9 output and the three fit modes ----
    ("size_169",        ["--encoder", "libx264", "--width", "640", "--height", "360",
                         "--turbo"],                                          "mp4"),
    ("fit_letterbox",   ["--encoder", "mpeg4", "--width", "320", "--height", "240",
                         "--fit", "fit", "--turbo"],                           "avi"),
    ("fit_pad_colour",  ["--encoder", "mpeg4", "--width", "320", "--height", "240",
                         "--fit", "fit", "--pad-color", "0x404040", "--turbo"], "avi"),
    ("fit_stretch",     ["--encoder", "mpeg4", "--fit", "stretch", "--turbo"],  "avi"),
    ("fit_crop_left",   ["--encoder", "mpeg4", "--fit", "crop",
                         "--crop-anchor", "left", "--turbo"],                   "avi"),

    # ---- v7.8 #3: audio encoders matched to the container ----
    # ac3 in RealMedia at the source's own 44.1 kHz: valid, and it used to be
    # the pair that broke when a preset asked for 16 kHz.
    ("audio_ac3",       ["--encoder", "rv20", "--audio-encoder", "ac3",
                         "--turbo"],                                           "rm"),
    ("audio_mp2",       ["--encoder", "mpeg2video", "--container", "avi",
                         "--audio-encoder", "mp2", "--turbo"],                 "avi"),
]

# v7.6: files the post-processing stages leave next to the main product.
# case name -> [(file that must exist, expected codec)].
POST_PRODUCTS = {
    "watermark_only":   [("watermark_only_wm.mp4", "h264")],
    "post_full_chain":  [("post_full_chain_wm.mp4", "h264"),
                         ("post_full_chain_web.mp4", "h264")],
}

# Non-encoding option paths: (name, args, expected rc, substring that must appear).
# Separate from CASES because they produce no media file.
OPTION_CHECKS = [
    ("ffmpeg_list",       ["--ffmpeg-list"],                      0, "btbn"),
    ("ffmpeg_source_bad", ["--ffmpeg-source", "bogus"],           2, "must be one of"),
    ("ffmpeg_manual",     ["--ffmpeg-download", "avbuild"],       1, "downloaded by hand"),
    ("play_bad",          ["--play", "bogus"],                    2, "--play supports"),
    ("completion_new",    ["--completion", "bash"],               0, "--ffmpeg-source"),
    # v7.6: era presets and the recompress fragment validator. The validator has
    # to fire before the live pass -- a bad fragment must never cost a 20 minute
    # encode -- which is why the rejection is rc=2 from argument parsing.
    # --lang en pins the message: the CLI otherwise speaks the language stored in
    # the settings file, which is a user choice, not this script's.
    ("era_unknown",       ["--lang", "en", "--era", "nope"],        2, "must be one of"),
    ("rc_bad_args",       ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--recompress-args", "-i x.mp4",
                           str(SRC), str(OUT / "rc_bad.avi")],      2, "not allowed here"),
    ("rc_trailing_output", ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--recompress-args", "-c:v libx264 out.mp4",
                           str(SRC), str(OUT / "rc_trail.avi")],    2, "output file name"),
    ("wm_position_bad",   ["--lang", "en", "--no-cui", "--watermark", "x",
                           "--watermark-position", "middle",
                           str(SRC), str(OUT / "wm_bad.mp4")],      2, "watermark-position"),
    ("era_help_lists",    ["--completion", "bash"],                 0, "--era"),
    # v7.7 #5: a motion level may never be faster than the base rate. The ladder
    # only throttles downwards, so 25 with --fps 15 is a typo, not a setting.
    ("level_too_fast",    ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--fps", "15", "--fps-levels", "25,10",
                           str(SRC), str(OUT / "lv_fast.avi")], 2, "faster than the base FPS"),
    # A bad threshold is a parse error, caught before any encode starts.
    ("level_bad_spec",    ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--fps-levels", "12:128:130",
                           str(SRC), str(OUT / "lv_bad.avi")],  2, "must be below"),
    # ---- v7.8 #3: an explicit sample rate the codec cannot honour is refused
    # before FFmpeg starts. ac3 takes 32/44.1/48 kHz and nothing else.
    ("audio_rate_bad",    ["--lang", "en", "--no-cui", "--encoder", "rv20",
                           "--audio-encoder", "ac3", "--audio-rate", "16000",
                           str(SRC), str(OUT / "ar_bad.rm")],   2, "does not support 16000 Hz"),
    # ...and a codec the container cannot hold is refused the same way.
    ("audio_nomux",       ["--lang", "en", "--no-cui", "--encoder", "rv20",
                           "--audio-encoder", "libvorbis",
                           str(SRC), str(OUT / "am_bad.rm")],   2, "cannot be muxed"),
    # ---- v7.8 #4: only containers the encoder can write are accepted; a wrong
    # one is an error rather than a silent fallback to another extension.
    ("container_bad",     ["--lang", "en", "--no-cui", "--encoder", "rv20",
                           "--container", "mkv",
                           str(SRC), str(OUT / "ct_bad.mkv")],  2, "cannot write"),
    # ---- v7.8 #5/#6: fit modes and anchors are validated at parse time.
    ("fit_bad",           ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--fit", "squash",
                           str(SRC), str(OUT / "fit_bad.avi")], 2, "--fit must be one of"),
    ("anchor_bad",        ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--crop-anchor", "sideways",
                           str(SRC), str(OUT / "an_bad.avi")],  2, "--crop-anchor must be one of"),
    ("completion_new_opts", ["--completion", "bash"],             0, "--container"),
]


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=300)


def probe(path):
    p = run(["ffprobe", "-v", "error", "-show_streams", "-show_format",
             "-of", "json", str(path)])
    if p.returncode != 0:
        return None
    try:
        return json.loads(p.stdout)
    except json.JSONDecodeError:
        return None


def main():
    if not EXE.is_file():
        sys.exit(f"missing binary: {EXE}")
    OUT.mkdir(parents=True, exist_ok=True)

    print(f"binary : {EXE}")
    print(f"source : {SRC}")
    print()
    print(f"{'case':<18}{'result':<8}{'size':>9}  {'wall':>6}  detail")
    print("-" * 92)

    fails = 0
    for name, extra, ext in CASES:
        dst = OUT / f"{name}.{ext}"
        t0 = time.time()
        proc = run([str(EXE), str(SRC), str(dst), "--end", "00:00:04",
                    *extra, "--ffmpeg-source", "path"])
        wall = time.time() - t0

        status, detail = "PASS", ""
        if proc.returncode != 0:
            status = "FAIL"
            detail = f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:80]}"
        elif not dst.exists() or dst.stat().st_size == 0:
            status, detail = "FAIL", "no output"
        else:
            info = probe(dst)
            if info is None:
                status, detail = "FAIL", "ffprobe cannot read the output"
            else:
                streams = info.get("streams", [])
                video = [s for s in streams if s.get("codec_type") == "video"]
                audio = [s for s in streams if s.get("codec_type") == "audio"]
                if not video:
                    status, detail = "FAIL", "no video stream"
                else:
                    v = video[0]
                    codec = v.get("codec_name", "?")
                    dur = 0.0
                    try:
                        dur = float((info.get("format") or {}).get("duration") or 0.0)
                    except ValueError:
                        dur = 0.0
                    detail = (f"{codec} {v.get('width')}x{v.get('height')} "
                              f"v={len(video)} a={len(audio)} dur={dur:.2f}s")
                    if name == "quiet" and proc.stdout.strip():
                        status, detail = "FAIL", "--quiet still printed output"

                # The live product exists; now the stage products.
                for rel, want_codec in POST_PRODUCTS.get(name, []):
                    staged = OUT / rel
                    if not staged.exists() or staged.stat().st_size == 0:
                        status, detail = "FAIL", f"missing stage product {rel}"
                        break
                    sinfo = probe(staged)
                    sv = [s for s in (sinfo or {}).get("streams", [])
                          if s.get("codec_type") == "video"]
                    if not sv or sv[0].get("codec_name") != want_codec:
                        status, detail = "FAIL", f"{rel} is not {want_codec}"
                        break

        size = dst.stat().st_size if dst.exists() else 0
        if status == "FAIL":
            fails += 1
        print(f"{name:<18}{status:<8}{size:>9}  {wall:>5.2f}s  {detail}")

    print()
    print(f"{'option':<18}{'result':<8}detail")
    print("-" * 92)
    total = len(CASES)
    for name, args, want_rc, want_text in OPTION_CHECKS:
        proc = run([str(EXE), *args])
        blob = proc.stdout + proc.stderr
        ok = proc.returncode == want_rc and want_text in blob
        if not ok:
            fails += 1
        why = "" if ok else (
            f"rc={proc.returncode} want {want_rc}" if proc.returncode != want_rc
            else f"missing {want_text!r}")
        print(f"{name:<18}{'PASS' if ok else 'FAIL':<8}{why}")
        total += 1

    print("-" * 92)
    print(f"total={total} pass={total - fails} fail={fails}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
