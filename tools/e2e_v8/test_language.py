"""Switching languages, through the real binary.

Three ways in, and all three have to agree: the default (English), `LAE_LANG`, and
`ui.language` in settings.toml. The reason to test the settings path as well as the
variable is that they are different code: one is an environment read at start-up, the
other is a value that arrives from the file the user edits.

The second half of the file is about the boundary rather than the switch: what a
translation is allowed to touch. Wording changes, arguments do not -- a Chinese run
must hand FFmpeg the same command line an English one does, byte for byte, or the
"translation" has changed what the program does.
"""

from __future__ import annotations

import os
import subprocess

from conftest import CLI, Work


def run_with(work: Work, *args: str, lang: str | None = None) -> subprocess.CompletedProcess:
    env = dict(os.environ)
    env["LAE_CONFIG_DIR"] = str(work.config)
    if lang is None:
        env.pop("LAE_LANG", None)
    else:
        env["LAE_LANG"] = lang

    return subprocess.run(
        [str(CLI), *args], capture_output=True, timeout=120, env=env,
        encoding="utf-8", errors="replace",
    )


def test_english_is_the_default(work: Work):
    result = run_with(work, "history", "list")

    assert result.returncode == 0, result.stderr
    assert "No runs recorded yet." in result.stdout


def test_the_environment_variable_switches_to_chinese(work: Work):
    result = run_with(work, "history", "list", lang="zh")

    assert result.returncode == 0, result.stderr
    assert "还没有任何运行记录。" in result.stdout
    assert "No runs recorded yet." not in result.stdout


def test_the_settings_file_switches_too(work: Work):
    (work.config / "settings.toml").write_text('[ui]\nlanguage = "zh"\n', encoding="utf-8")

    result = run_with(work, "history", "list")

    assert result.returncode == 0, result.stderr
    assert "还没有任何运行记录。" in result.stdout


def test_a_regional_tag_is_accepted(work: Work):
    result = run_with(work, "history", "list", lang="zh-CN")

    assert result.returncode == 0, result.stderr
    assert "还没有任何运行记录。" in result.stdout


def test_help_is_translated(work: Work):
    result = run_with(work, "--help", lang="zh")

    assert result.returncode == 0, result.stderr
    assert "以归档节奏重新编码一个素材。" in result.stdout

    # The help option belongs to the argument parser, so its sentence is the one
    # place a Chinese screen would otherwise fall back to English in the middle.
    assert "显示帮助和用法" in result.stdout
    assert "Re-encode one source at archive pace." not in result.stdout


def test_a_usage_error_is_translated(work: Work):
    # --size is refused before the toolchain is probed, so this needs no FFmpeg.
    result = run_with(work, "encode", "whatever.mkv", "--size", "bad", lang="zh")

    assert result.returncode == 2
    assert "宽x高" in result.stderr
    assert "WIDTHxHEIGHT" not in result.stderr


def test_a_dry_run_reports_in_chinese(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)

    result = run_with(
        work, "encode", source, "--out", str(work.root / "out.avi"), "--dry-run", lang="zh")

    assert result.returncode == 0, result.stderr
    assert "来源" in result.stdout
    assert "解码:" in result.stdout
    assert "source     " not in result.stdout


def test_both_languages_print_the_same_command_lines(work: Work, needs_ffmpeg):
    """A translation may change the words around the plan, never the plan.

    Compared with the output name folded out, because that is the one thing the
    two runs were told to differ in.
    """
    source = work.source(seconds=1)

    english = run_with(work, "encode", source, "--out", str(work.root / "a.avi"), "--dry-run")
    chinese = run_with(
        work, "encode", source, "--out", str(work.root / "b.avi"), "--dry-run", lang="zh")

    assert english.returncode == 0, english.stderr
    assert chinese.returncode == 0, chinese.stderr

    def commands(result: subprocess.CompletedProcess) -> list[str]:
        return [line for line in result.stdout.splitlines()
                if line.startswith("  ") and "ffmpeg" in line]

    encoded = [line.replace("a.avi", "OUT") for line in commands(english)]
    translated = [line.replace("b.avi", "OUT") for line in commands(chinese)]

    assert len(encoded) == 2, english.stdout
    assert encoded == translated
