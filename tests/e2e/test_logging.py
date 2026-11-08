"""v7.9.9: structured run logs must exist without changing user-visible output.

The harness points LAE_LOG_DIR at the suite-private config root, so nothing is
written into the developer's %LOCALAPPDATA% / ~/.local/share.
"""
from __future__ import annotations

import os
import re

import harness


def _env(level: str | None) -> dict[str, str]:
    """A private config + log root, optionally raising the log threshold."""
    env = {**os.environ,
           "LAE_CONFIG_DIR": str(harness.CONFIG_ROOT),
           "LAE_LOG_DIR": str(harness.CONFIG_ROOT / "logs")}
    if level is not None:
        env["LAE_LOG_LEVEL"] = level
    return env


def test_version_run_leaves_a_correlated_log(exe):
    # v7.9.9.1: the default threshold is Error, so the full trace (including
    # the UI mirror) has to be asked for explicitly.
    proc = harness.run([str(exe), "--version", "--lang", "en"], env=_env("verbose"))

    assert proc.returncode == 0, proc.stderr
    # The Console mirror must not add, remove or re-encode a single character:
    # --version has always printed exactly one dotted version number.
    version = proc.stdout.strip()
    assert re.fullmatch(r"\d+(?:\.\d+)+", version), (
        f"user-visible output changed: {proc.stdout!r}"
    )

    # v7.9.9.1: size rolling, so the name is lae.log (plus lae_001.log ...);
    # there is no date suffix to match any more.
    logs = sorted((harness.CONFIG_ROOT / "logs").glob("lae*.log"))
    assert logs, "no rolling log file was written"
    text = logs[-1].read_text(encoding="utf-8", errors="replace")
    assert "run.start" in text, text[:400]
    assert "run.end" in text or "process.exit" in text, text[:400]
    assert f"ui.stdout {version}" in text, text[:400]
    # Correlation ids must be on every line.
    assert re.search(r"\[INF\] \S+/startup run\.start", text), text[:400]


def test_the_default_threshold_writes_errors_only(exe):
    """v7.9.9.1: an ordinary run must not grow a log."""
    log_dir = harness.CONFIG_ROOT / "logs-default"
    env = {**os.environ,
           "LAE_CONFIG_DIR": str(harness.CONFIG_ROOT),
           "LAE_LOG_DIR": str(log_dir)}
    env.pop("LAE_LOG_LEVEL", None)

    proc = harness.run([str(exe), "--version", "--lang", "en"], env=env)
    assert proc.returncode == 0, proc.stderr

    logs = sorted(log_dir.glob("lae*.log")) if log_dir.is_dir() else []
    if logs:
        text = "".join(p.read_text(encoding="utf-8", errors="replace") for p in logs)
        assert "[INF]" not in text, f"default level leaked info records:\n{text[:400]}"
        assert "[WRN]" not in text, text[:400]
    # No [ERR] was produced, so "no file" and "an empty file" are both correct.


def test_no_era_does_not_break_a_plain_encode(exe, workdir):
    """--no-era is a 'do not use' switch, not an error or a new preset."""
    out = workdir("logging")
    dst = out / "no_era.avi"
    proc = harness.run([str(exe), str(harness.SRC), str(dst),
                        "--encoder", "mpeg4", "--end", "00:00:01",
                        "--no-era", "--no-recompress",
                        "--ffmpeg-source", "path", "--lang", "en", "--quiet"])
    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:300]}"
    )
    assert dst.is_file() and dst.stat().st_size > 0
