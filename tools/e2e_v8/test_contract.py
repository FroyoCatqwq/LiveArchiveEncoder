"""The command's contract: exit codes, output shape, and what it refuses.

Nothing here encodes anything for real, so these run in under a second each and
are the ones to run first when something breaks: a wrong exit code is what a
script sees, and it is cheaper to check than a wrong frame.
"""

from __future__ import annotations

import pytest

from conftest import Work


def test_version_prints_one_line_and_succeeds(work: Work):
    result = work.run("version")

    assert result.returncode == 0
    assert result.stdout.strip() == "8.0.0-alpha.0"
    assert result.stderr == ""


def test_no_arguments_prints_usage_and_succeeds(work: Work):
    result = work.run()

    assert result.returncode == 0
    assert "encode" in result.stdout


def test_an_unknown_command_is_a_usage_error(work: Work):
    result = work.run("no-such-command")

    assert result.returncode == 2
    assert result.stderr.strip().startswith("ERROR:")


def test_config_path_points_inside_the_configured_folder(work: Work):
    result = work.run("config", "path")

    assert result.returncode == 0
    assert str(work.config) in result.stdout


def test_config_show_is_greppable(work: Work):
    result = work.run("config", "show")

    assert result.returncode == 0
    assert "defaults.encoder =" in result.stdout
    assert "defaults.size = " in result.stdout


def test_setup_reports_the_toolchain(work: Work, needs_ffmpeg):
    result = work.run("setup")

    assert result.returncode == 0
    assert "Ready." in result.stdout


def test_an_unreadable_source_is_a_failure_not_a_usage_error(work: Work, needs_ffmpeg):
    result = work.run("encode", str(work.root / "nothing-here.avi"), "--out", str(work.root / "out.avi"))

    assert result.returncode == 1
    assert "cannot read" in result.stderr


def test_a_malformed_size_is_a_usage_error(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    result = work.run("encode", source, "--out", str(work.root / "out.avi"), "--size", "320")

    assert result.returncode == 2
    assert "WIDTHxHEIGHT" in result.stderr


def test_an_unknown_encoder_is_a_usage_error(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    result = work.run("encode", source, "--out", str(work.root / "out.avi"), "--encoder", "notacodec")

    assert result.returncode == 2
    assert "no encoder named" in result.stderr


def test_a_dry_run_prints_two_commands_and_writes_nothing(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--out", str(output), "--dry-run")

    assert result.returncode == 0
    assert "decoder:" in result.stdout
    assert "encoder:" in result.stdout
    assert "pipe:1" in result.stdout
    assert "pipe:0" in result.stdout
    assert not output.exists()
    assert work.parts() == []


def test_the_dry_run_is_pasteable(work: Work, needs_ffmpeg):
    """Every printed command line must be a command that exists, with its quotes."""
    source = work.source(seconds=1)
    result = work.run("encode", source, "--out", str(work.root / "out.avi"), "--dry-run")

    lines = [line.strip() for line in result.stdout.splitlines() if line.startswith("  ")]
    assert len(lines) == 2
    for line in lines:
        assert "ffmpeg" in line.lower()
        assert line.count('"') % 2 == 0, f"unbalanced quoting: {line}"
