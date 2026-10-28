"""Fixtures for the LiveArchiveEncoder end-to-end suite.

Two groups of modules must not share the machine (see pytest.ini for the full
explanation): the ones marked ``timing`` measure wall-clock pacing, and the
``shared_config`` one reads and writes the real settings file that every encode
also writes. Everything else is free to run in parallel.

``tools/e2e.py`` is the sanctioned way to get that split (exclusive phase first,
then the rest under xdist). The guard below refuses a blanket ``pytest -n`` so
an accidental full-suite xdist run fails loudly instead of producing flaky
timing numbers.
"""

from __future__ import annotations

import shutil
from pathlib import Path

import pytest

import harness


def pytest_configure(config: pytest.Config) -> None:
    workers = getattr(config.option, "numprocesses", None)
    if not workers:
        return
    marks = (config.option.markexpr or "").replace(" ", "")
    if "timing" in marks and "shared_config" in marks:
        return
    raise pytest.UsageError(
        "timing / shared_config modules must run alone: use tools/e2e.py, or "
        '-m "not (timing or shared_config)" when passing -n')


@pytest.fixture(scope="session")
def exe() -> Path:
    """The binary under test (CS_EXE overrides the default publish/jit build)."""
    path = harness.resolve_exe()
    if not path.is_file():
        raise pytest.UsageError(
            f"missing binary: {path}\n"
            "build it first (tools/build.ps1 or tools/build.sh), "
            "or point CS_EXE at an existing build")
    return path


@pytest.fixture(scope="session")
def src() -> Path:
    if not harness.SRC.is_file():
        raise pytest.UsageError(f"missing source clip: {harness.SRC}")
    return harness.SRC


@pytest.fixture(scope="session")
def multi_src() -> Path:
    """A source with two video and two audio tracks (v7.9 stream selection)."""
    if not harness.MULTI.is_file():
        raise pytest.UsageError(f"missing multi-stream source: {harness.MULTI}")
    return harness.MULTI


@pytest.fixture(scope="session")
def py_baseline() -> list[str]:
    """The v6.3.3 reference implementation as an argv prefix."""
    if not harness.PY_BASELINE.is_file():
        raise pytest.UsageError(f"missing python baseline: {harness.PY_BASELINE}")
    return harness.py_command()


@pytest.fixture
def workdir():
    """Factory for a suite's scratch directory (kept between runs, like before)."""
    made: list[Path] = []

    def _make(name: str) -> Path:
        path = harness.scratch(name)
        made.append(path)
        return path

    return _make


@pytest.fixture(scope="session")
def cui_config():
    """The settings file the CUI tests write and read back.

    v7.9.8.6: harness.config_path() is the suite's own LAE_CONFIG_DIR now, not the
    developer's %LOCALAPPDATA%, so this is a private file. The backup/restore stays
    because the suite rewrites it repeatedly and a later phase expects a clean
    start; it costs one copy.
    """
    path = harness.config_path()
    backup = path.with_suffix(".json.bak")
    had_config = path.exists()
    if had_config:
        shutil.copyfile(path, backup)
    else:
        path.parent.mkdir(parents=True, exist_ok=True)
    try:
        yield path
    finally:
        if had_config:
            shutil.copyfile(backup, path)
            backup.unlink()
        elif path.exists():
            path.unlink()
