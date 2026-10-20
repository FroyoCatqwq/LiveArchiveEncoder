"""Option-path smoke test (pytest).

The encoder matrix (test_compare_matrix.py) covers "does this codec work"; this
one covers "do the option paths still work" -- turbo, sharding, the split audio
pipeline, deinterlacing, cropping and time ranges. Each case only has to produce
a structurally valid file; bit-exactness is the matrix's job.

    pytest tests/e2e/test_option_smoke.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe pytest tests/e2e/test_option_smoke.py
"""

from __future__ import annotations

import pytest

import harness
from harness import run

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
                           "SRC", "OUT/rc_bad.avi"],      2, "not allowed here"),
    ("rc_trailing_output", ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--recompress-args", "-c:v libx264 out.mp4",
                           "SRC", "OUT/rc_trail.avi"],    2, "output file name"),
    ("wm_position_bad",   ["--lang", "en", "--no-cui", "--watermark", "x",
                           "--watermark-position", "middle",
                           "SRC", "OUT/wm_bad.mp4"],      2, "watermark-position"),
    ("era_help_lists",    ["--completion", "bash"],                 0, "--era"),
    # v7.7 #5: a motion level may never be faster than the base rate. The ladder
    # only throttles downwards, so 25 with --fps 15 is a typo, not a setting.
    ("level_too_fast",    ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--fps", "15", "--fps-levels", "25,10",
                           "SRC", "OUT/lv_fast.avi"], 2, "faster than the base FPS"),
    # A bad threshold is a parse error, caught before any encode starts.
    ("level_bad_spec",    ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--fps-levels", "12:128:130",
                           "SRC", "OUT/lv_bad.avi"],  2, "must be below"),
    # ---- v7.8 #3: an explicit sample rate the codec cannot honour is refused
    # before FFmpeg starts. ac3 takes 32/44.1/48 kHz and nothing else.
    ("audio_rate_bad",    ["--lang", "en", "--no-cui", "--encoder", "rv20",
                           "--audio-encoder", "ac3", "--audio-rate", "16000",
                           "SRC", "OUT/ar_bad.rm"],   2, "does not support 16000 Hz"),
    # ...and a codec the container cannot hold is refused the same way.
    ("audio_nomux",       ["--lang", "en", "--no-cui", "--encoder", "rv20",
                           "--audio-encoder", "libvorbis",
                           "SRC", "OUT/am_bad.rm"],   2, "cannot be muxed"),
    # ---- v7.8 #4: only containers the encoder can write are accepted; a wrong
    # one is an error rather than a silent fallback to another extension.
    ("container_bad",     ["--lang", "en", "--no-cui", "--encoder", "rv20",
                           "--container", "mkv",
                           "SRC", "OUT/ct_bad.mkv"],  2, "cannot write"),
    # ---- v7.8 #5/#6: fit modes and anchors are validated at parse time.
    ("fit_bad",           ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--fit", "squash",
                           "SRC", "OUT/fit_bad.avi"], 2, "--fit must be one of"),
    ("anchor_bad",        ["--lang", "en", "--no-cui", "--encoder", "mpeg4",
                           "--crop-anchor", "sideways",
                           "SRC", "OUT/an_bad.avi"],  2, "--crop-anchor must be one of"),
    ("completion_new_opts", ["--completion", "bash"],             0, "--container"),
]


def _expand(args: list[str], out, src) -> list[str]:
    """OPTION_CHECKS carries SRC/OUT placeholders; bind them per run."""
    return [str(src) if a == "SRC" else str(out / a[len("OUT/"):]) if a.startswith("OUT/") else a
            for a in args]


@pytest.mark.parametrize(("name", "extra", "ext"), CASES, ids=[c[0] for c in CASES])
def test_option_case(exe, src, workdir, name, extra, ext):
    out = workdir("options")
    dst = out / f"{name}.{ext}"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                *extra, "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert dst.exists() and dst.stat().st_size > 0, "no output"

    info = harness.video_summary(dst)
    assert info is not None, "ffprobe cannot read the output"
    assert not info.get("no_video"), "no video stream"
    if name == "quiet":
        assert not proc.stdout.strip(), "--quiet still printed output"

    # The live product exists; now the stage products.
    for rel, want_codec in POST_PRODUCTS.get(name, []):
        staged = out / rel
        assert staged.exists() and staged.stat().st_size > 0, f"missing stage product {rel}"
        sinfo = harness.probe(staged) or {}
        sv = [s for s in sinfo.get("streams", []) if s.get("codec_type") == "video"]
        assert sv and sv[0].get("codec_name") == want_codec, f"{rel} is not {want_codec}"


@pytest.mark.parametrize(("name", "args", "want_rc", "want_text"), OPTION_CHECKS,
                         ids=[c[0] for c in OPTION_CHECKS])
def test_option_check(exe, src, workdir, name, args, want_rc, want_text):
    proc = run([str(exe), *_expand(args, workdir("options"), src)])
    blob = proc.stdout + proc.stderr
    assert proc.returncode == want_rc, f"rc={proc.returncode} want {want_rc}"
    assert want_text in blob, f"missing {want_text!r} in output"
