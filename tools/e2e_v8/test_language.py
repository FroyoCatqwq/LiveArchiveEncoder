"""Switching languages, through the real binary.

Three ways in, and all three have to agree: the default (English), `LAE_LANG`, and
`ui.language` in settings.toml. The reason to test the settings path as well as the
variable is that they are different code: one is an environment read at start-up, the
other is a value that arrives from the file the user edits.
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
