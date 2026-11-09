"""RealMedia (.rm) end-to-end (pytest).

v7.9.8.1 gives the rm container its own audio codec. The video half already
worked -- `--encoder rv10` wrote a .rm before this version -- but the audio was
whatever the rm muxer defaults to (ac3), which no RealMedia player of the period
ever decoded, and the second pass did not know rv10/rv20 at all, so
`--recompress-codec rv20` fell back to libx264 in MP4 without saying so.

    pytest tests/e2e/test_realmedia.py
"""

from __future__ import annotations

import shutil

import harness
from harness import run


def _streams(path):
    info = harness.probe(path) or {}
    return [(s.get("codec_type"), s.get("codec_name")) for s in info.get("streams", [])]


def _details(path):
    info = harness.probe(path) or {}
    return {s.get("codec_type"): s for s in info.get("streams", [])}


def test_rm_defaults_to_ac3_with_a_real_timeline(exe, workdir):
    """v7.9.8.12: real_144 leaves every RM audio packet at PTS 0, so the audio
    stream reports duration 0 and the file cannot stay in sync. ac3 must be the
    default and must produce an audio timeline as long as the video."""
    # v7.9.9.5: each case gets its own scratch directory. Under xdist the four
    # cases ran in different workers but shared tests/out/realmedia, so the
    # stale-part sweep and the rmtree of the deliberate "directory in the way"
    # collided with files another worker was still writing (SHFileOperationW
    # 0x20 / sharing violation). The suite is meant to be parallel-safe.
    out = workdir("realmedia/ac3")
    dst = out / "rv10.rm"
    proc = run([str(exe), str(harness.SRC), str(dst), "--encoder", "rv10",
                "--end", "00:00:04", "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    streams = _streams(dst)
    assert ("video", "rv10") in streams, f"expected rv10 video, got {streams}"
    assert ("audio", "ac3") in streams, f"expected ac3 audio, got {streams}"

    details = _details(dst)
    video_duration = float(details["video"].get("duration") or 0)
    audio_duration = float(details["audio"].get("duration") or 0)
    assert audio_duration > video_duration * 0.8, (video_duration, audio_duration)

    decode = run(["ffmpeg", "-v", "error", "-i", str(dst), "-f", "null", "-"])
    assert decode.returncode == 0 and not decode.stderr.strip(), \
        f"the .rm does not decode cleanly: {decode.stderr[:300]}"


def test_rv10_snaps_a_non_multiple_of_16(exe, workdir):
    """v7.9.8.12: rv10enc.c:69 rejects (width | height) & 15. A 320x180
    request must be snapped to 320x176 instead of failing the preflight."""
    out = workdir("realmedia/snap")
    dst = out / "rv10_320x180.rm"
    proc = run([str(exe), str(harness.SRC), str(dst), "--encoder", "rv10",
                "--width", "320", "--height", "180",
                "--end", "00:00:02", "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    video = _details(dst)["video"]
    assert (video.get("width"), video.get("height")) == (320, 176), video


def test_recompress_to_realmedia(exe, workdir):
    """The second pass can deliver RealVideo 2.0 instead of silently writing H.264."""
    out = workdir("realmedia/recompress")
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
    destination is occupied by a directory, so the rename has to fail. The
    occupant must be untouched. v7.9.8.10 changed the third expectation: the
    encode DID finish, so the finished .part is now kept (and its path printed)
    instead of being deleted by the catch that used to Discard() it on any error.
    """
    out = workdir("realmedia/stages")
    live = out / "stage.avi"
    for stale in out.glob("*.part*"):
        if stale.is_file():
            stale.unlink()
    first = run([str(exe), str(harness.SRC), str(live), "--encoder", "mpeg4",
                 "--end", "00:00:03", "--ffmpeg-source", "path"])
    assert first.returncode == 0, first.stderr[-300:]

    blocked = out / "stage_web.mp4"
    # The scratch directory survives between runs on purpose, so a previous run
    # left this very directory here; rmdir it instead of tripping over it.
    if blocked.is_dir():
        shutil.rmtree(blocked)
    elif blocked.exists():
        blocked.unlink()
    blocked.mkdir()                      # File.Move cannot land on a directory

    proc = run([str(exe), str(harness.SRC), str(live), "--encoder", "mpeg4",
                "--end", "00:00:03", "--recompress", "--recompress-codec", "libx264",
                "--ffmpeg-source", "path"])

    assert proc.returncode != 0, "a rename that cannot succeed must fail the run"
    assert blocked.is_dir(), "the occupant was removed by the failed pass"
    parts = [p for p in out.glob("*.part*") if p.is_file()]
    assert parts, "the finished encode was discarded when only its rename failed"
    assert any(p.stat().st_size > 0 for p in parts), "the kept part is empty"
    combined = (proc.stdout + proc.stderr).lower()
    assert "kept" in combined, "the user was not told where the finished file is"
