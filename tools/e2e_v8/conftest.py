"""Shared fixtures for the v8 end-to-end suite.

The suite drives the real executable: no test doubles, no in-process calls. That
is the point of it. The unit tests already cover the decisions; what only an
end-to-end run can answer is whether the decisions survive contact with argv, the
console, the filesystem and two FFmpeg children.

Layout note: this is a subfolder suite, not a rewrite of v7's tools/e2e.py. v7's
harness is calibrated against v7's behaviour, and it has to keep working until
v8.0.0 deletes that tree. What dies here is the temptation to grow one suite that
knows two programs.
"""

from __future__ import annotations

import json
import os
import shutil
import signal
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
EXE = "LiveArchiveEncoder.exe" if os.name == "nt" else "LiveArchiveEncoder"
CLI = ROOT / "src" / "Lae.Cli" / "bin" / "Release" / "net11.0" / EXE
V7_CLI = ROOT / "publish" / "jit" / EXE

FFMPEG = shutil.which("ffmpeg")
FFPROBE = shutil.which("ffprobe")


def pytest_configure(config):  # noqa: ARG001 - pytest hook signature
    if not CLI.exists():
        pytest.exit(
            f"the v8 CLI is not built: {CLI}\n"
            f"build it with: dotnet build LiveArchiveEncoder.V8.slnx -c Release",
            returncode=1,
        )


@pytest.fixture()
def needs_ffmpeg():
    if not FFMPEG or not FFPROBE:
        pytest.skip("no FFmpeg on PATH")


@pytest.fixture()
def needs_v7():
    if not V7_CLI.exists():
        pytest.skip(f"no v7 build at {V7_CLI}")


@dataclass
class Work:
    """One test's private folder and settings directory."""

    root: Path
    config: Path

    def run(self, *args: str, timeout: int = 300) -> subprocess.CompletedProcess:
        env = dict(os.environ)
        env["LAE_CONFIG_DIR"] = str(self.config)
        return subprocess.run(
            [str(CLI), *args],
            capture_output=True, timeout=timeout, env=env, encoding="utf-8", errors="replace",
        )

    def run_v7(self, *args: str, timeout: int = 300) -> subprocess.CompletedProcess:
        env = dict(os.environ)
        env["LAE_CONFIG_DIR"] = str(self.config / "v7")
        return subprocess.run(
            [str(V7_CLI), *args],
            capture_output=True, timeout=timeout, env=env, encoding="utf-8", errors="replace",
        )

    def source(self, name: str = "src.avi", seconds: float = 3.0,
               size: str = "320x240", rate: int = 25, audio: bool = True) -> str:
        path = self.root / name
        args = [FFMPEG, "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                "-i", f"testsrc2=size={size}:rate={rate}:duration={seconds}"]
        if audio:
            args += ["-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}",
                     "-c:a", "libmp3lame", "-b:a", "64k"]
        args += ["-c:v", "mpeg4", "-q:v", "4", "-shortest", "-y", str(path)]
        subprocess.run(args, check=True, capture_output=True)
        return str(path)

    def probe(self, path: str) -> dict:
        out = subprocess.run(
            [FFPROBE, "-v", "error", "-show_streams", "-show_format", "-of", "json", path],
            capture_output=True, text=True, check=True,
        ).stdout
        return json.loads(out)

    def parts(self, folder: Path | None = None) -> list[Path]:
        return sorted((folder or self.root).glob("*.part.*"))


@pytest.fixture()
def work(tmp_path: Path) -> Work:
    config = tmp_path / "cfg"
    config.mkdir()
    return Work(root=tmp_path, config=config)


def interrupt(process: subprocess.Popen, delay: float) -> None:
    """Let a run get going, then interrupt it the way a user would."""
    time.sleep(delay)
    if os.name == "nt":
        process.send_signal(signal.CTRL_BREAK_EVENT)
    else:
        process.send_signal(signal.SIGINT)


def start(work: Work, *args: str) -> subprocess.Popen:
    env = dict(os.environ)
    env["LAE_CONFIG_DIR"] = str(work.config)
    kwargs = {}
    if os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    return subprocess.Popen(
        [str(CLI), *args],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env, **kwargs,
    )


def stream(probe: dict, kind: str) -> dict:
    for item in probe["streams"]:
        if item["codec_type"] == kind:
            return item
    raise AssertionError(f"no {kind} stream in {probe}")


def duration(probe: dict) -> float:
    return float(probe["format"]["duration"])
