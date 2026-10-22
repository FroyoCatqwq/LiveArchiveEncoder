"""Option-path smoke test (pytest).

The encoder matrix (test_compare_matrix.py) covers "does this codec work"; this
one covers "do the option paths still work" -- sharding, the split audio
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
# v7.9 removed --turbo; these cases run at the realtime pace, which is the only
# pace there is now.
CASES = [
    ("baseline",        ["--encoder", "rv20"],                  "rm"),
    ("parallel4",       ["--encoder", "mpeg4", "--parallel", "4"], "avi"),
    ("parallel_roq",    ["--encoder", "roqvideo", "--parallel", "4"], "roq"),
    ("parallel_rv20",   ["--encoder", "rv20", "--parallel", "4"],     "rm"),
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
    ("level_kbps",      ["--encoder", "mpeg4",
                         "--fps-levels", "10:120:110,5:150"],   "avi"),
    # v7.7 #1: 19.010s at 15 fps is 285.15 slots, so the slot grid reaches
    # 19.067s -- past the end of the source. This case failed with
    # "end time ... exceeds the source duration" before the planner clamped the
    # last boundary to the end of the range.
    ("shard_rounding",  ["--encoder", "mpeg4", "--parallel", "4",
                         "--end-time", "00:00:19.010"],         "avi"),
    ("x264",            ["--encoder", "libx264", "--qv", "23",
                         "--x264-preset", "faster"],            "mp4"),
    ("amv_forced",      ["--encoder", "amv"],                   "amv"),
    ("high_limit",      ["--encoder", "mpeg4", "--limit", "99999",
                         "--recover", "90000"],                 "avi"),
    # "path" is the escape hatch back to the system FFmpeg: discovery must skip
    # any managed build and still produce a normal encode.
    ("source_path",     ["--encoder", "mpeg4", "--ffmpeg-source", "path"], "avi"),
    # --play off must be a no-op, or scripting an encode would pop a window.
    ("play_off",        ["--encoder", "mpeg4", "--play", "off"], "avi"),

    # ---- v7.6: era preset + post-processing stages ----
    # An era preset rewrites the encoder, frame size and bitrate ceiling, so the
    # output container follows the preset's encoder, not the old default.
    ("era_flv",         ["--era", "flv07"],                              "flv"),
    # v7.9.1 removed the watermark stage; recompress is the one that is left.
    # The stage writes <stem>_web.mp4 next to the main product.
    ("post_web",        ["--encoder", "mpeg4", "--recompress",
                         "--recompress-args", "-vf scale=320:240,fps=15 "
                                              "-c:v libx264 -crf 28 -ac 1 -b:a 32k"], "avi"),

    # ---- v7.8 #4: container choice ----
    # mpeg1video/mpeg2video/mpeg4/libxvid/libx264 gain AVI, libx264 gains MKV.
    # The default container is unchanged, which is why every case above still
    # names the same extension it always did.
    ("container_m1v_avi",  ["--encoder", "mpeg1video", "--container", "avi"], "avi"),
    ("container_m2v_avi",  ["--encoder", "mpeg2video", "--container", "avi"], "avi"),
    # v7.9.4 #2: the same run without --container, because the extension
    # already says AVI. This used to be rejected with "Output extension '.avi'
    # does not match selected muxer 'mpeg'" -- the check only knew the encoder
    # default. #3 rides along: 15 fps is an mpeg1video rate this build refuses,
    # so the run has to snap it instead of dying in preflight.
    ("container_m1v_byext", ["--encoder", "mpeg1video", "--fps", "15"], "avi"),
    # v7.9.4 #1: an indexed codec (msrle is pal8-only) must come out with a
    # picture, not a black frame -- see test_palette_output_is_not_black.
    ("msrle",              ["--encoder", "msrle"],                            "avi"),
    ("container_xvid_mp4", ["--encoder", "libxvid", "--container", "mp4"],   "mp4"),
    ("container_x264_avi", ["--encoder", "libx264", "--container", "avi"],   "avi"),
    ("container_x264_mkv", ["--encoder", "libx264", "--container", "mkv"],   "mkv"),

    # ---- v7.8 #5/#6: 16:9 output and the three fit modes ----
    ("size_169",        ["--encoder", "libx264", "--width", "640", "--height", "360"], "mp4"),
    ("fit_letterbox",   ["--encoder", "mpeg4", "--width", "320", "--height", "240",
                         "--fit", "fit"],                              "avi"),
    ("fit_pad_colour",  ["--encoder", "mpeg4", "--width", "320", "--height", "240",
                         "--fit", "fit", "--pad-color", "0x404040"],  "avi"),
    ("fit_stretch",     ["--encoder", "mpeg4", "--fit", "stretch"],   "avi"),
    ("fit_crop_left",   ["--encoder", "mpeg4", "--fit", "crop",
                         "--crop-anchor", "left"],                    "avi"),

    # ---- v7.8 #3: audio encoders matched to the container ----
    # ac3 in RealMedia at the source's own 44.1 kHz: valid, and it used to be
    # the pair that broke when a preset asked for 16 kHz.
    ("audio_ac3",       ["--encoder", "rv20", "--audio-encoder", "ac3"], "rm"),
    ("audio_mp2",       ["--encoder", "mpeg2video", "--container", "avi",
                         "--audio-encoder", "mp2"],                  "avi"),

    # ---- v7.9 #5: h261/h263/h263p gain AVI, and with it an audio track ----
    ("container_h261_avi",  ["--encoder", "h261", "--container", "avi"],  "avi"),
    ("container_h263_avi",  ["--encoder", "h263", "--container", "avi"],  "avi"),
    ("container_h263p_avi", ["--encoder", "h263p", "--container", "avi"], "avi"),
]

# v7.6: files the post-processing stages leave next to the main product.
# case name -> [(file that must exist, expected codec)].
POST_PRODUCTS = {
    "post_web":         [("post_web_web.mp4", "h264")],
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
    # v7.9.1: subtitles complete like every other option; the retired
    # watermark flags must not come back with them.
    ("completion_subtitle", ["--completion", "bash"],             0, "--subtitle-mode"),
    # ---- v7.9: Turbo is gone, not deprecated. An old script still passing it
    # must fail loudly rather than silently lose the pacing change it asked for.
    ("turbo_gone",        ["--lang", "en", "--no-cui", "--turbo"], 2, "unrecognized argument"),
    # ---- v7.9.3.1: an unknown --audio-mode is a parse error. It used to be
    # thrown out of the parser, which Program.Main does not catch, so a typo
    # produced an unhandled-exception stack trace instead of "ERROR:" + rc 2.
    ("audio_mode_bad",    ["--lang", "en", "--no-cui", "--audio-mode", "bogus",
                           "SRC", "OUT/amode.avi"],  2, "auto, inline or separate"),
    # ---- v7.9.3.1: a preset is a fallback, not an override (manual §7.12).
    # --emit-args prints the resolved run, so the width the preset would have
    # imposed (352) must not appear where the explicit 320 belongs.
    ("era_explicit_wins", ["--lang", "en", "--no-cui", "--era", "flv07",
                           "--width", "320", "--height", "180", "--emit-args",
                           "SRC", "OUT/era_x.flv"],  0, "--width 320"),
    # ...and with no explicit size the preset still applies.
    ("era_preset_applies", ["--lang", "en", "--no-cui", "--era", "flv07",
                            "--emit-args", "SRC", "OUT/era_p.flv"], 0, "--width 352"),
    # ---- v7.9.3.1: the stream picks and the x264 knobs were missing from every
    # generated completion script, so `--video-str<Tab>` completed nothing.
    ("completion_streams", ["--completion", "bash"], 0, "--video-stream"),
    ("completion_x264",    ["--completion", "zsh"],  0, "--x264-profile"),
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

    # v7.9 #5: the point of h261/h263/h263p in AVI is the audio track the raw
    # .h261/.h263 streams cannot carry, so those cases must actually have one.
    if name.startswith("container_h26"):
        streams = (harness.probe(dst) or {}).get("streams", [])
        assert any(s.get("codec_type") == "audio" for s in streams), \
            f"{name} produced no audio stream"

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


def test_completion_drops_retired_flags(exe):
    """v7.9.1 removed the watermark stage; a stale completion script would
    still offer --watermark* and --recompress-keep to tab-completion."""
    proc = run([str(exe), "--completion", "bash"])
    assert proc.returncode == 0
    for gone in ("watermark", "recompress-keep"):
        assert gone not in proc.stdout, f"retired {gone!r} still completes"


def test_palette_output_is_not_black(exe, src, workdir):
    """v7.9.4 #1: an indexed encoder must produce a picture, not a black frame.

    msrle only writes pal8. Two things used to go wrong at once:
      * the rawvideo pipe cut every frame 1024 bytes short (a pal8 frame is
        w*h + palette, not w*h), which shifted the frame data out from under
        the palette, and
      * the Matroska staging pass that sharding uses drops the palette
        outright -- the same 2 s clip probes YAVG 123 written straight to AVI
        and YAVG 16 (black) after one hop through .mkv.

    So the run has to be a single pass, and the product has to have colour.
    """
    out = workdir("options")
    dst = out / "msrle_black.avi"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                "--encoder", "msrle", "--parallel", "4", "--fps", "15",
                "--ffmpeg-source", "path"], timeout=180)
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert dst.exists() and dst.stat().st_size > 0, "no output"

    # Sharding asks for a Matroska staging pass, which this codec cannot use.
    assert "Sharding skipped" in proc.stdout, proc.stdout[-400:]

    stats = run(["ffmpeg", "-hide_banner", "-i", str(dst), "-vf",
                 "signalstats,metadata=print:key=lavfi.signalstats.YAVG",
                 "-frames:v", "3", "-f", "null", "-"], timeout=120)
    readings = [float(line.rsplit("=", 1)[1]) for line in stats.stderr.splitlines()
                if "lavfi.signalstats.YAVG=" in line]
    assert readings, f"no luma readings: {stats.stderr[-300:]}"
    # 16.0 is black; the fixture's colour bars sit around 120.
    assert min(readings) > 40.0, f"msrle came out black: YAVG {readings}"


def test_avi_header_keeps_the_encoded_rate(exe, src, workdir):
    """v7.9.4: an AVI product must not lie about its frame rate.

    AVI stores the rate in its header, and a stream copied out of the Matroska
    staging file lands there with no codec frame rate, so the muxer invents
    one: 13 fps mpeg4 probed back as r_frame_rate 13/1 but avg_frame_rate
    1000/1, mpeg1video as 48000/1001 / 1000/1. Both are restated now.
    """
    out = workdir("options")

    for name, encoder, fps in (("avi_rate_mpeg4", "mpeg4", "13"),
                               ("avi_rate_m1v", "mpeg1video", "24")):
        dst = out / f"{name}.avi"
        proc = run([str(exe), str(src), str(dst), "--end", "00:00:08",
                    "--encoder", encoder, "--fps", fps, "--parallel", "4",
                    "--ffmpeg-source", "path"], timeout=300)
        assert proc.returncode == 0, (
            f"{name}: exit={proc.returncode}: "
            f"{(proc.stderr.strip().splitlines() or [''])[-1][:200]}")

        info = harness.probe(dst) or {}
        video = [s for s in info.get("streams", []) if s.get("codec_type") == "video"]
        assert video, f"{name}: no video stream"

        def rate(text: str) -> float:
            num, _, den = (text or "0/0").partition("/")
            return float(num) / float(den or 1)

        nominal = rate(video[0].get("r_frame_rate"))
        average = rate(video[0].get("avg_frame_rate"))
        assert abs(nominal - float(fps)) < 0.1, f"{name}: r_frame_rate {nominal}"
        assert abs(average - float(fps)) < 0.1, f"{name}: avg_frame_rate {average}"


def test_parallel_flv_join_has_no_corrupt_frames(exe, src, workdir):
    """v7.9.2: joining FLV shards used to corrupt the picture partway through.

    Reported against a 243 s / 196 Mbit/s rawvideo source cut into five shards:
    the product played sound to the end while the picture stopped at the fourth
    boundary (194.8 s of 243.4 s). 730 of 3651 frames were undecodable with
    "Bad picture start code" -- the shards themselves were perfect, the join was
    not, and the audio kept the format duration long enough to pass the length
    check.

    Shards are staged in Matroska now and remuxed to the requested container
    after the join, so the whole file has to decode.
    """
    out = workdir("options")
    dst = out / "parallel_flv.flv"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:20",
                "--encoder", "flv", "--parallel", "5", "--fps", "15",
                "--ffmpeg-source", "path"], timeout=300)
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}")
    assert dst.exists() and dst.stat().st_size > 0, "no output"

    # Decode the whole thing; a bad join shows up as demuxer complaints on
    # stderr long before the duration check would notice.
    dec = run(["ffmpeg", "-v", "error", "-i", str(dst),
               "-map", "0:v:0", "-f", "null", "-"], timeout=300)
    assert dec.returncode == 0, f"decode failed: {dec.stderr[-300:]}"
    assert not dec.stderr.strip(), f"undecodable frames in the join: {dec.stderr[:400]}"

    info = harness.probe(dst) or {}
    duration = float((info.get("format") or {}).get("duration") or 0.0)
    assert 19.0 <= duration <= 21.0, f"joined duration {duration}"
