"""v7.9.6 #1: writing the picture in a chosen pixel format and colour layout.

The unit tests pin the frame-size table and the argument construction; these
tests prove the two halves meet a real encoder:

  * a requested format is what the output file reports (10-bit stays 10-bit),
  * a palette format in a container that cannot store a palette is refused
    instead of producing a black picture,
  * colour tags reach the bitstream, not just the command line,
  * a format the encoder does not accept is an argument error (exit 2),
  * --list-pix-fmts marks what the selected encoder declares.
"""

from __future__ import annotations

import re
from pathlib import Path

import harness
from harness import probe, run


def _video(path: Path) -> dict:
    info = probe(path) or {}
    streams = [s for s in info.get("streams", []) if s.get("codec_type") == "video"]
    assert streams, f"no video stream in {path}"
    return streams[0]


def test_explicit_pal8_is_what_the_file_reports(exe, src, workdir):
    """msrle only writes pal8; asking for it explicitly must be a no-op, not a
    different format. This is the pairing that came out black before v7.9.4 and
    is the reason the transport table exists at all."""
    out = workdir("picture")
    dst = out / "pal8_msrle.avi"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                "--encoder", "msrle", "--pix-fmt", "pal8", "--dither", "bayer",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert proc.returncode == 0
    assert "pal8" in proc.stdout, proc.stdout[-400:]

    video = _video(dst)
    assert video.get("pix_fmt") == "pal8", video

    stats = run(["ffmpeg", "-hide_banner", "-i", str(dst), "-vf",
                 "signalstats,metadata=print:key=lavfi.signalstats.YAVG",
                 "-frames:v", "3", "-f", "null", "-"], timeout=120)
    readings = [float(line.rsplit("=", 1)[1]) for line in stats.stderr.splitlines()
                if "lavfi.signalstats.YAVG=" in line]
    assert readings, f"no luma readings: {stats.stderr[-300:]}"
    assert min(readings) > 40.0, f"pal8 came out black: YAVG {readings}"


def test_palette_format_in_a_palette_lossy_container_is_refused(exe, src, workdir):
    """Matroska stores an unknown VfW codec without its palette, so the same clip
    is YAVG 123 in AVI and YAVG 16 (black) after one hop through .mkv. Asking
    for pal8 in mkv has to be refused up front -- and before v7.9.6 that check
    only existed inside the shard planner, so a plain run wrote a black file."""
    out = workdir("picture")
    dst = out / "pal8_mkv.mkv"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                "--encoder", "zmbv", "--pix-fmt", "pal8",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)

    assert proc.returncode == 2, f"expected an argument error, got {proc.returncode}"
    assert "palette" in (proc.stderr + proc.stdout).lower(), proc.stderr[-400:]
    assert not dst.exists() or dst.stat().st_size == 0, "a refused run wrote a file"


def test_ten_bit_output_keeps_its_depth(exe, src, workdir):
    """8-bit in, 10-bit out: the pipe carries 16-bit samples (3 bytes/pixel, not
    1.5) and the encoder has to write them back out at the same depth."""
    out = workdir("picture")
    dst = out / "h264_10bit.mkv"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                "--encoder", "libx264", "--pix-fmt", "yuv420p10le", "--qv", "28",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")

    video = _video(dst)
    assert video.get("codec_name") == "h264", video
    assert video.get("pix_fmt") == "yuv420p10le", video


def test_colour_tags_reach_the_bitstream(exe, src, workdir):
    """Measured 2026-10-24: this build's libx264 ignores -color_primaries and
    -color_trc and writes "unknown" unless the values also go through
    -x264-params. The file is what counts, not the command line."""
    out = workdir("picture")
    dst = out / "colour_tags.mp4"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                "--encoder", "libx264", "--qv", "28",
                "--color-range", "tv", "--colorspace", "bt2020nc",
                "--color-primaries", "bt2020", "--color-trc", "smpte2084",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")

    video = _video(dst)
    assert video.get("color_range") == "tv", video
    assert video.get("color_space") == "bt2020nc", video
    assert video.get("color_primaries") == "bt2020", video
    assert video.get("color_transfer") == "smpte2084", video


def test_a_format_the_encoder_cannot_take_is_an_argument_error(exe, src, workdir):
    """mpeg4 declares yuv420p only. The refusal has to name both sides."""
    out = workdir("picture")
    dst = out / "mpeg4_10bit.avi"

    proc = run([str(exe), str(src), str(dst), "--end", "00:00:04",
                "--encoder", "mpeg4", "--pix-fmt", "yuv420p10le",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)

    assert proc.returncode == 2, f"expected an argument error, got {proc.returncode}"
    message = proc.stderr.lower()
    assert "yuv420p10le" in message and "mpeg4" in message, proc.stderr[-400:]


def test_list_pix_fmts_marks_the_selected_encoder(exe):
    """The list is the measured table with the encoder's own column, so a user
    can see what --pix-fmt will accept before typing it."""
    proc = run([str(exe), "--list-pix-fmts", "--encoder", "msrle",
                "--ffmpeg-source", "path"], timeout=120)

    assert proc.returncode == 0, proc.stderr[-300:]
    assert "pal8" in proc.stdout, proc.stdout[:400]
    assert "Encoder msrle declares: pal8" in proc.stdout, proc.stdout[:400]
    # pal8 is the one format msrle can take, so it is the one marked OK. The
    # hint line also starts with "OK", hence the format column in the pattern.
    marked = [line for line in proc.stdout.splitlines()
              if re.match(r"^\s+OK\s+\S+\s+\d", line)]
    assert len(marked) == 1 and "pal8" in marked[0], marked


def test_dither_is_only_emitted_for_a_low_colour_target(exe, src, workdir):
    """-sws_dither on a yuv420p run would be a no-op with a misleading log line,
    so the flag is only passed where it can change the picture."""
    out = workdir("picture")

    reduced = out / "pal_reduced.avi"
    proc = run([str(exe), str(src), str(reduced), "--end", "00:00:03",
                "--encoder", "msrle", "--pix-fmt", "pal8", "--dither", "bayer",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)
    assert proc.returncode == 0, proc.stderr[-300:]
    assert re.search(r"^dither\s+:", proc.stdout, re.MULTILINE), proc.stdout[-400:]

    plain = out / "plain_yuv.avi"
    proc = run([str(exe), str(src), str(plain), "--end", "00:00:03",
                "--encoder", "mpeg4", "--dither", "bayer",
                "--fps", "15", "--ffmpeg-source", "path"], timeout=180)
    assert proc.returncode == 0, proc.stderr[-300:]
    assert not re.search(r"^dither\s+:", proc.stdout, re.MULTILINE), proc.stdout[-400:]
