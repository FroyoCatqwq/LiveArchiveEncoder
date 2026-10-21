"""Interactive-shell (CUI) smoke test.

Drives the CUI through piped stdin and asserts on what it prints and what it
leaves behind in the settings file. The real settings file is used (that is the
point of the test) but it is backed up first and restored afterwards -- see the
cui_config fixture.

The eight runs below are a state machine: run 1 writes settings that run 2
reads back, run 3 overrides them, and so on. They therefore depend on
definition order and must not be sharded, reordered or parallelised. Inside a
run, every check is evaluated even if an earlier one failed -- the same
report-everything behaviour the standalone script had.

    pytest tests/e2e/test_cui_smoke.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe pytest tests/e2e/test_cui_smoke.py
"""

from __future__ import annotations

import os

import pytest

import harness

# Every run of the product writes the real settings file on its way out, and
# these assertions read that same file. Running beside other encodes would let
# someone else's write land between two of ours, so this module is exclusive
# too (tools/e2e.py phase 1) -- it is only 11 s, so the serialisation is free.
pytestmark = pytest.mark.shared_config


class Checks:
    """Collects check results so one failure does not hide the rest of a run."""

    def __init__(self):
        self.failures: list[str] = []

    def check(self, name: str, ok, detail: str = ""):
        if not ok:
            self.failures.append(name + (f" -- {detail}" if detail else ""))

    def finish(self):
        assert not self.failures, "failing checks:\n  " + "\n  ".join(self.failures)


def test_run1_fresh_start(exe, cui_config):
    """Fresh start: no stored settings. Switch to Chinese, set 4 parallel
    shards, then exit. Turbo was removed outright in v7.9 -- no menu entry, no
    flag, no settings key -- and the audio encoder has its own row."""
    if cui_config.exists():
        cui_config.unlink()
    p1 = harness.cui(exe, [], "6\n10\nzh\n11\n4\n0\n")

    c = Checks()
    c.check("exit code 0", p1.returncode == 0, f"rc={p1.returncode}")
    c.check("turbo gone from the menu", "Turbo" not in p1.stdout)
    c.check("start encoding moved to 9", " 9. Start encoding" in p1.stdout)
    c.check("audio encoder row present", " 6. Audio encoder" in p1.stdout)
    # With no video encoder chosen there is no container to match against.
    c.check("audio scan needs a video encoder",
            "Choose a video encoder first" in p1.stdout)
    c.check("language switched to zh", "zh" in p1.stdout)
    c.check("parallel menu present", "11." in p1.stdout)
    c1 = harness.read_config()
    c.check("settings file written", c1 is not None)
    if c1:
        c.check("turbo not persisted at all", "Turbo" not in c1, str(c1.get("Turbo")))
        c.check("language persisted", c1.get("Language") == "zh", str(c1.get("Language")))
        c.check("parallel persisted", c1.get("Parallel") == 4, str(c1.get("Parallel")))
    c.finish()


def test_run2_settings_come_back(exe, cui_config):
    """Settings must come back, and the banner has to be Chinese already."""
    p2 = harness.cui(exe, [], "0\n")

    c = Checks()
    c.check("exit code 0", p2.returncode == 0, f"rc={p2.returncode}")
    c.check("reports restored settings", "已恢复上次配置" in p2.stdout)
    c.check("banner is chinese", "交互界面" in p2.stdout)
    c.check("menu is chinese", "开始编码" in p2.stdout and "退出" in p2.stdout)
    c.check("chinese menu keeps start at 9", " 9. 开始编码" in p2.stdout)
    c.check("chinese menu has the audio row", " 6. 音频编码器" in p2.stdout)
    c.check("parallel shows 4", "并行分片数" in p2.stdout and "4" in p2.stdout)
    c.finish()


def test_run3_explicit_lang_wins(exe, cui_config):
    """--lang must win over the stored preference, and it must not be
    clobbered by the CLI-style path either."""
    p3 = harness.cui(exe, ["--lang", "en"], "0\n")

    c = Checks()
    c.check("--lang en overrides stored zh", "Start encoding" in p3.stdout,
            "--lang did not reach the menu")
    # An explicit --lang is a preference, not a one-off: it sticks, the same
    # way every other menu change does.
    c3 = harness.read_config()
    c.check("--lang en is remembered", c3 and c3.get("Language") == "en",
            str(c3.get("Language") if c3 else None))
    c.finish()


def test_run4_corrupt_settings(exe, cui_config):
    """A corrupt settings file must never stop the program."""
    cui_config.write_text("{ this is not json", encoding="utf-8")
    p4 = harness.cui(exe, [], "0\n")

    c = Checks()
    c.check("survives corrupt settings", p4.returncode == 0, f"rc={p4.returncode}")
    c.check("falls back to english", "Main menu" in p4.stdout)
    c.finish()


def test_run5_invalid_menu_input(exe, cui_config):
    """Invalid menu input must not crash or hang."""
    cui_config.unlink()
    p5 = harness.cui(exe, [], "99\n\n7\n0\n")

    c = Checks()
    c.check("survives invalid input", p5.returncode == 0, f"rc={p5.returncode}")
    c.check("reports invalid selection", "Invalid selection" in p5.stdout)
    c.finish()


def test_run6_toolchain_menu(exe, cui_config):
    """FFmpeg toolchain menu: listing must work without touching the network
    (option 1 is the read-only one) and the choice must be remembered."""
    p6 = harness.cui(exe, [], "12\n1\n0\n0\n")

    c = Checks()
    c.check("exit code 0", p6.returncode == 0, f"rc={p6.returncode}")
    c.check("toolchain menu offered", "12." in p6.stdout and "FFmpeg toolchain" in p6.stdout)
    # gyan.dev publishes no Linux build, so the Linux catalogue is
    # deliberately shorter. Asking for "gyan" there would fail for a correct
    # reason -- the menu must not offer a source it cannot install.
    expected = (("btbn", "gyan", "avbuild", "nonfree") if os.name == "nt"
                else ("btbn", "avbuild", "nonfree"))
    missing = [s for s in expected if s not in p6.stdout]
    c.check("lists every source", not missing, f"missing={missing}")
    if os.name != "nt":
        c.check("hides windows-only sources", "gyan" not in p6.stdout)
    c.check("marks manual sources", "manual download" in p6.stdout)
    c6 = harness.read_config()
    c.check("ffmpeg source persisted", c6 and c6.get("FfmpegSource") == "btbn",
            str(c6.get("FfmpegSource") if c6 else None))
    c.finish()


def test_run7_playback_menu(exe, cui_config):
    """Playback menu: choosing ffplay must stick."""
    p7 = harness.cui(exe, [], "13\nffplay\n0\n")

    c = Checks()
    c.check("exit code 0", p7.returncode == 0, f"rc={p7.returncode}")
    c.check("playback menu offered", "13." in p7.stdout and "Playback" in p7.stdout)
    c7 = harness.read_config()
    c.check("play mode persisted", c7 and c7.get("PlayMode") == "ffplay",
            str(c7.get("PlayMode") if c7 else None))
    c.finish()


def test_run8_play_mode_off(exe, cui_config):
    """...and "off" must be reachable again afterwards, with a bad value
    rejected in between."""
    p8 = harness.cui(exe, [], "13\nbogus\n13\noff\n0\n")

    c = Checks()
    c.check("rejects a bad play mode", "Choose off, ffplay or external" in p8.stdout)
    c8 = harness.read_config()
    c.check("play mode back to off", c8 and c8.get("PlayMode") == "off",
            str(c8.get("PlayMode") if c8 else None))
    c.finish()


def test_run9_stream_pick(exe, cui_config, multi_src):
    """v7.9: the streams menu lists the source's tracks and remembers the pick.

    The index the user types is the one ffprobe assigns, so the menu has to
    print them rather than expect the user to know. A blank answer clears the
    pick again -- "first of each type" is the v7.8 behaviour and stays reachable.
    """
    # 1 = input, then the path; 16 = streams; 2 = the second video track,
    # 3 = the second audio track; 0 = exit. (v7.9.1 removed the watermark
    # entry, which shifted the menu down by one.)
    p9 = harness.cui(exe, [], f"1\n{multi_src}\n16\n2\n3\n0\n")

    c = Checks()
    c.check("exit code 0", p9.returncode == 0, f"rc={p9.returncode}")
    c.check("streams menu offered", "16." in p9.stdout and "Video/audio stream" in p9.stdout)
    c.check("lists the streams before asking", "streams in this source:" in p9.stdout.lower())
    c.check("asks for a video index", "Video stream index" in p9.stdout)
    c.check("asks for an audio index", "Audio stream index" in p9.stdout)
    c.check("menu shows the pick", "video 2 / audio 3" in p9.stdout)
    c9 = harness.read_config()
    c.check("video pick persisted", c9 and c9.get("VideoStream") == 2,
            str(c9.get("VideoStream") if c9 else None))
    c.check("audio pick persisted", c9 and c9.get("AudioStream") == 3,
            str(c9.get("AudioStream") if c9 else None))
    c.finish()


def test_run10_stream_pick_clears(exe, cui_config, multi_src):
    """Blank answers must return to "first of each type"."""
    p10 = harness.cui(exe, [], f"1\n{multi_src}\n16\n\n\n0\n")

    c = Checks()
    c.check("exit code 0", p10.returncode == 0, f"rc={p10.returncode}")
    c.check("reset confirmed", "reset to the first stream" in p10.stdout)
    c10 = harness.read_config()
    c.check("video pick cleared", c10 and c10.get("VideoStream") is None,
            str(c10.get("VideoStream") if c10 else None))
    c.check("audio pick cleared", c10 and c10.get("AudioStream") is None,
            str(c10.get("AudioStream") if c10 else None))
    c.finish()


def test_run11_subtitle_menu(exe, cui_config):
    """v7.9.1: the CUI exposes the subtitle options the CLI already had.

    The fixture MKV carries its SubRip track as stream 2, so the menu lists it
    the way ffprobe numbers it. Picking a sidecar clears an embedded pick --
    the encoder refuses both at once -- and a cleared pick leaves the mode
    alone but disables the stage.
    """
    mkv = harness.TESTS / "media" / "test_subs.mkv"
    srt = harness.TESTS / "media" / "test_src.srt"
    if not mkv.is_file() or not srt.is_file():
        pytest.skip("subtitle fixtures missing (see test_subtitles.py)")

    # A: input the MKV, open subtitles, pick the embedded track, come back.
    # 17 = subtitles (v7.9.1 removed the watermark entry, which shifted the
    # menu down by one); 3 = embedded stream; 2 = the SubRip track's index.
    pa = harness.cui(exe, [], f"1\n{mkv}\n17\n3\n2\n0\n0\n")
    # B: switch the mode to copy, then name a sidecar (clears the stream pick).
    pb = harness.cui(exe, [], f"17\n1\n2\n{srt}\n0\n0\n")
    # C: clear the sidecar; the mode stays copy but nothing is picked.
    pc = harness.cui(exe, [], f"17\n2\n-\n0\n0\n")

    c = Checks()
    for name, p in (("A", pa), ("B", pb), ("C", pc)):
        c.check(f"run {name} exit code 0", p.returncode == 0, f"rc={p.returncode}")
    c.check("subtitle menu offered", "17." in pa.stdout and "Subtitles" in pa.stdout)
    c.check("lists the streams before asking",
            "streams in this source:" in pa.stdout.lower())
    c.check("asks for a subtitle index", "Subtitle stream index" in pa.stdout)
    c.check("main menu shows the burn pick", "burn: stream 2" in pa.stdout)
    c.check("mode cycles to copy", "Mode (burn / copy / off)" in pb.stdout
            and "= copy" in pb.stdout)
    c.check("sidecar replaces the stream pick",
            "External subtitle file" in pb.stdout and "test_src.srt" in pb.stdout)
    c.check("main menu shows the copy pick", "copy: test_src.srt" in pb.stdout)
    c.check("sidecar cleared", "External subtitle file cleared." in pc.stdout)
    c.check("nothing picked reads as off", "Subtitles" in pc.stdout and "= off" in pc.stdout)
    cfg = harness.read_config()
    c.check("subtitles persisted", cfg is not None and "Subtitles" in cfg,
            str(sorted(cfg) if cfg else None))
    if cfg:
        subs = cfg.get("Subtitles") or {}
        c.check("mode persisted as copy", subs.get("Mode") == "copy", str(subs.get("Mode")))
        c.check("files cleared in the file too", subs.get("Files") == [], str(subs.get("Files")))
        c.check("stream pick cleared in the file too", subs.get("Stream") is None,
                str(subs.get("Stream")))
    c.finish()
