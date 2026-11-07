"""v7.9.8.13: sidecar subtitles and .zip font packs."""

from __future__ import annotations

import os
import zipfile

import harness
from harness import run

ASS_TEXT = """[Script Info]
ScriptType: v4.00+

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour
Style: Default,FOT-Matisse ProN B,40,&H00FFFFFF

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,hello
"""


def _tiny_source(path):
    run([
        "ffmpeg", "-v", "error",
        "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=13:duration=1",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050:duration=1",
        "-map", "0:v", "-map", "1:a", "-c:v", "mpeg4", "-c:a", "aac",
        "-shortest", str(path), "-y",
    ], timeout=90)


def test_font_pack_imports_the_fonts_the_ass_asks_for(exe, workdir, tmp_path):
    out_dir = workdir("fontpack")
    src = out_dir / "clip.mkv"
    _tiny_source(src)
    (out_dir / "clip.sc.ass").write_text(ASS_TEXT, encoding="utf-8")

    pack = out_dir / "Fonts.zip"
    with zipfile.ZipFile(pack, "w") as archive:
        archive.writestr("Fonts/FOT-MatisseProN-B.otf", b"fake-font")
        archive.writestr("Fonts/Unrelated.otf", b"fake-font")

    font_dir = tmp_path / "fonts"
    env = {
        **os.environ,
        "LAE_CONFIG_DIR": str(tmp_path / "cfg"),
        "LAE_FONT_DIR": str(font_dir),
    }
    dst = out_dir / "out.avi"
    proc = run([
        str(exe), str(src), str(dst),
        "--encoder", "mpeg4", "--ffmpeg-source", "path",
        "--auto-subtitle", "--font-pack", str(pack),
        "--subtitle-mode", "off",
        "--end", "00:00:01", "--lang", "en",
    ], input="", env=env, timeout=180)

    assert proc.returncode == 0, (
        f"stdout:\n{proc.stdout}\nstderr:\n{proc.stderr}")
    assert "Subtitle: clip.sc.ass" in proc.stdout, proc.stdout
    assert (font_dir / "FOT-MatisseProN-B.otf").exists(), sorted(font_dir.glob("*"))
    # The ASS names only one family; an unrelated font in the same pack is
    # not copied unless the user picks everything.
    assert not (font_dir / "Unrelated.otf").exists(), sorted(font_dir.glob("*"))
