"""The settings file: TOML, hand-editable, and never reformatted behind the user's back.

These tests exercise the three promises the format change was made for, through the
real binary. They are deliberately about the *file on disk*, not the parsed document:
a unit test can prove the store round-trips, only an end-to-end run proves the program
writes a file a person would be willing to edit.
"""

from __future__ import annotations

from conftest import Work

HAND_WRITTEN = """\
# 那年的播放器缓冲只有几十秒，200 kbps 是能连续播的上限
[defaults]
quality = 21
limitKbps = 200.0
"""


def test_a_new_install_writes_a_file_it_can_read_back(work: Work):
    result = work.run("preset", "add", "portal-2006", "--encoder", "mpeg4", "--size", "320x240")
    assert result.returncode == 0, result.stderr

    settings = work.config / "settings.toml"
    assert settings.exists(), result.stdout

    # Read it back through the program rather than by parsing here: the point is
    # that this build accepts its own output, which is precisely what broke the
    # first time the store was written.
    shown = work.run("config", "show")
    assert shown.returncode == 0, shown.stderr
    assert "customPresets = 1" in shown.stdout
    assert "defaults.encoder = mpeg4" not in shown.stdout, "a preset must not become the default"


def test_a_comment_survives_a_preset_being_added(work: Work):
    settings = work.config / "settings.toml"
    settings.write_text(HAND_WRITTEN, encoding="utf-8")

    result = work.run("preset", "add", "portal-2006", "--encoder", "mpeg4", "--quality", "6")
    assert result.returncode == 0, result.stderr

    after = settings.read_text(encoding="utf-8")

    # The comment and the hand-arranged table are the reason for choosing TOML at
    # all; a save that reformats the file has already lost the argument.
    assert "那年的播放器缓冲只有几十秒" in after
    assert "quality = 21" in after
    assert "[[customPresets]]" in after
    assert "portal-2006" in after

    shown = work.run("config", "show")
    assert "defaults.quality = 21" in shown.stdout
    assert "customPresets = 1" in shown.stdout


def test_a_misspelled_key_stops_a_save_instead_of_being_dropped(work: Work):
    settings = work.config / "settings.toml"
    settings.write_text("[defaults]\nqualityy = 5\n", encoding="utf-8")

    result = work.run("preset", "add", "portal-2006", "--encoder", "mpeg4")

    assert result.returncode == 1, result.stdout
    assert "could not be read" in result.stderr
    # And the typo is still there to be fixed: the program did not "help" by
    # rewriting the file with defaults.
    assert settings.read_text(encoding="utf-8") == "[defaults]\nqualityy = 5\n"
