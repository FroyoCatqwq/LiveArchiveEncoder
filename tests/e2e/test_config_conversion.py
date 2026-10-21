"""Settings-file conversion e2e (v7.9.1).

The two directions of the conversion, exercised against the real binary:

    command line --save-config FILE--> settings file
    settings file --emit-args=FILE--> command line

Both exits happen before any encode, so these runs are cheap; the point is
that the plumbing (parse, resolve, capture, emit, re-parse) agrees with
itself. The emitter's own logic is unit-tested in CliArgsTests; what is
checked here is that a run really produces a file that really emits a line
that really parses back into the same settings.

    pytest tests/e2e/test_config_conversion.py
"""

from __future__ import annotations

import json
import time

import harness
from harness import run


def test_save_config_writes_and_skips_the_encode(exe, src, workdir):
    """The conversion must not cost an encode: no output file may appear."""
    out = workdir("options")
    cfg = out / "convert_saved.json"
    dst = out / "convert_saved.avi"
    since = time.time()

    proc = run([str(exe), str(src), str(dst), "--encoder", "mpeg4", "--fps", "25",
                "--save-config", str(cfg), "--ffmpeg-source", "path"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    assert cfg.is_file() and harness.fresh_output(cfg, since), "settings file not written"
    assert "Settings written to" in proc.stdout, "no confirmation line"
    assert not dst.exists(), "--save-config must not encode"


def test_emit_args_reads_a_file_without_ffmpeg(exe, workdir):
    """A hand-written settings file becomes a command line, no FFmpeg needed."""
    out = workdir("options")
    cfg = out / "convert_emit.json"
    cfg.write_text(json.dumps({
        "Version": 4,
        "InputPath": "in.rm",
        "OutputPath": "out.avi",
        "Encoder": "mpeg4",
        "Fps": 25.0,
        "Limit": 900.0,
        "Recompress": {"Enabled": True, "Args": "-c:v libx264 -crf 28", "Container": "mp4"},
    }), encoding="utf-8")

    proc = run([str(exe), "--emit-args=" + str(cfg)])

    assert proc.returncode == 0, proc.stderr[-300:]
    line = proc.stdout.strip()
    assert "in.rm" in line and "out.avi" in line, line
    assert "--encoder mpeg4" in line, line
    assert "--fps 25" in line, line
    assert "--limit 900" in line, line
    assert "--recompress-args" in line, line
    # Defaults stay out of the line.
    assert "--width" not in line, line
    assert "--threads" not in line, line


def test_emit_args_missing_file_is_an_error(exe):
    proc = run([str(exe), "--emit-args=no-such-settings.json"])

    assert proc.returncode == 2
    assert "settings file not found" in (proc.stdout + proc.stderr)


def test_command_line_round_trips_through_the_file(exe, src, workdir):
    """args -> file -> args -> file: the two snapshots must agree."""
    out = workdir("options")
    first = out / "convert_rt1.json"
    second = out / "convert_rt2.json"
    dst = out / "convert_rt.avi"

    proc = run([str(exe), str(src), str(dst), "--encoder", "mpeg4", "--fps", "25",
                "--width", "320", "--height", "240", "--limit", "900",
                "--save-config", str(first), "--ffmpeg-source", "path"])
    assert proc.returncode == 0, proc.stderr[-300:]

    proc = run([str(exe), "--emit-args=" + str(first)])
    assert proc.returncode == 0, proc.stderr[-300:]
    line = proc.stdout.strip()
    assert "--encoder mpeg4" in line and "--fps 25" in line, line
    # The run used the default ladder: the resolver prepends the base rate to
    # the stored level list, and that derived entry must not come back as a
    # --fps-levels choice (it would invent a rung for the base rate).
    assert "--fps-levels" not in line, line

    # The emitted line is a command line: feed it back in. (The repository
    # paths carry no spaces, so a plain split is a faithful tokenisation.)
    proc = run([str(exe)] + line.split() + ["--save-config", str(second)])
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")

    a = json.loads(first.read_text(encoding="utf-8"))
    b = json.loads(second.read_text(encoding="utf-8"))
    a.pop("SavedAt", None)
    b.pop("SavedAt", None)
    assert a == b, f"round trip drifted:\n{a}\n{b}"
