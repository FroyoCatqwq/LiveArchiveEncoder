"""RealMedia (.rm) end-to-end (pytest).

v7.9.8.1 gives the rm container its own audio codec. The video half already
worked -- `--encoder rv10` wrote a .rm before this version -- but the audio was
whatever the rm muxer defaults to (ac3), which no RealMedia player of the period
ever decoded, and the second pass did not know rv10/rv20 at all, so
`--recompress-codec rv20` fell back to libx264 in MP4 without saying so.

    pytest tests/e2e/test_realmedia.py
"""

from __future__ import annotations

import harness
from harness import run


def _streams(path):
    info = harness.probe(path) or {}
    return [(s.get("codec_type"), s.get("codec_name")) for s in info.get("streams", [])]


def _details(path):
    info = harness.probe(path) or {}
    return {s.get("codec_type"): s for s in info.get("streams", [])}


def test_rm_carries_real_audio(exe, workdir):
    """rv10 + real_144, in a file ffmpeg can read back and decode."""
    out = workdir("realmedia")
    dst = out / "rv10.rm"
    proc = run([str(exe), str(harness.SRC), str(dst), "--encoder", "rv10",
                "--end", "00:00:04", "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    streams = _streams(dst)
    assert ("video", "rv10") in streams, f"expected rv10 video, got {streams}"
    assert ("audio", "ra_144") in streams, f"expected ra_144 audio, got {streams}"

    # The codec has exactly one mode; a 44.1 kHz stereo request must land on it.
    audio = _details(dst)["audio"]
    assert audio.get("sample_rate") == "8000", audio
    assert audio.get("channels") == 1, audio

    decode = run(["ffmpeg", "-v", "error", "-i", str(dst), "-f", "null", "-"])
    assert decode.returncode == 0 and not decode.stderr.strip(), \
        f"the .rm does not decode cleanly: {decode.stderr[:300]}"


def test_recompress_to_realmedia(exe, workdir):
    """The second pass can deliver RealVideo 2.0 instead of silently writing H.264."""
    out = workdir("realmedia")
    live = out / "rc.avi"
    proc = run([str(exe), str(harness.SRC), str(live), "--encoder", "mpeg4",
                "--end", "00:00:05", "--recompress", "--recompress-codec", "rv20",
                "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    web = out / "rc_web.rm"
    assert web.is_file(), f"expected {web.name}, found {sorted(p.name for p in out.iterdir())}"

    streams = _streams(web)
    assert ("video", "rv20") in streams, f"expected rv20 video, got {streams}"
    assert ("audio", "ra_144") in streams, f"expected ra_144 audio, got {streams}"
    probe = harness.probe(web) or {}
    assert (probe.get("format") or {}).get("format_name") == "rm"


def test_recompress_stages_and_only_then_promotes(exe, workdir):
    """The second pass writes beside the delivery, never into it.

    v7.9.8 gave the live pass the ".part" scheme but missed this stage: ffmpeg -y
    truncates its destination as it opens it, so a second pass that failed
    halfway left a short file under the real name and the previous delivery gone.

    The check here is the seam itself rather than a mid-encode failure: the
    destination is occupied by a directory, so the rename has to fail. What must
    happen is a clean error, the occupant untouched, and no ".part" left behind --
    the three things that used to be "the product is gone and a half file is in
    its place".
    """
    out = workdir("realmedia")
    live = out / "stage.avi"
    first = run([str(exe), str(harness.SRC), str(live), "--encoder", "mpeg4",
                 "--end", "00:00:03", "--ffmpeg-source", "path"])
    assert first.returncode == 0, first.stderr[-300:]

    blocked = out / "stage_web.mp4"
    if blocked.exists():
        blocked.unlink()
    blocked.mkdir()                      # File.Move cannot land on a directory

    proc = run([str(exe), str(harness.SRC), str(live), "--encoder", "mpeg4",
                "--end", "00:00:03", "--recompress", "--recompress-codec", "libx264",
                "--ffmpeg-source", "path"])

    assert proc.returncode != 0, "a rename that cannot succeed must fail the run"
    assert blocked.is_dir(), "the occupant was removed by the failed pass"
    assert not (out / "stage_web.part.mp4").exists(), "a .part file was left behind"
