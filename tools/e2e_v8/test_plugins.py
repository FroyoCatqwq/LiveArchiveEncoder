"""Plugin hops, end to end: a real plugin, a real encode, a real product.

The fixture in ``tools/fixtures`` is a small Python script that copies its input --
the least a plugin can do and still pass the three product checks. What is being
tested here is the wiring around it: discovery from the settings folder, the
handshake, the command line the host builds, and the promotion of the part product
once verification has passed.

A plugin hop can only be written in a settings file, which is why every test here
uses ``--recompress-only`` with a stored chain. That is also the only path that
takes a chain from the settings at all -- an ordinary encode deliberately does not,
because v7.9.8.6 shipped a release where every encode silently recompressed itself.
"""

from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

from conftest import Work, duration

FIXTURES = Path(__file__).resolve().parent.parent / "fixtures"

PLAIN_HOP = """[[defaults.recompress.passes]]
plugin = "lae-fake"
stage = "copy"
"""

# A hop whose plugin says it succeeded and writes nothing. This is the failure the
# whole verification layer exists for, and the one a human would not catch.
EMPTY_HOP = """[[defaults.recompress.passes]]
plugin = "lae-fake"
stage = "copy"

[defaults.recompress.passes.params]
mode = "empty"
"""


def install(work: Work, mode: str | None = None) -> Path:
    """Put the fixture where the program looks for installed plugins."""
    folder = work.config / "plugins" / "lae-fake"
    folder.mkdir(parents=True)

    shutil.copyfile(FIXTURES / "fake-plugin.py", folder / "fake-plugin.py")

    # The interpreter is named in the manifest and the script relatively, which is
    # the shape a real plugin in a scripting language would use -- and it only works
    # because the host runs the plugin from its own folder.
    (folder / "plugin.json").write_text(
        json.dumps({"id": "lae-fake", "command": [sys.executable, "fake-plugin.py"]}),
        encoding="utf-8",
    )

    if mode:
        (folder / "mode.txt").write_text(mode, encoding="utf-8")

    return folder


def store_chain(work: Work, steps: str) -> None:
    (work.config / "settings.toml").write_text(
        "[defaults.recompress]\nenabled = true\n\n" + steps,
        encoding="utf-8",
    )


def test_a_plugin_hop_runs_and_keeps_its_product(work: Work, needs_ffmpeg):
    source = work.source(seconds=2)
    install(work)
    store_chain(work, PLAIN_HOP)

    result = work.run("encode", source, "--recompress-only")

    assert result.returncode == 0, result.stderr

    product = work.root / "src_web.avi"
    assert product.exists(), result.stdout + result.stderr

    # The plugin copied its input, so the product is the same length as the
    # recording. A hop that lost content would also have failed verification, which
    # is why this is asserted on the file rather than trusted.
    assert abs(duration(work.probe(str(product))) - 2.0) < 0.6

    # The recording is untouched: the chain works on copies, always.
    assert Path(source).exists()
    assert "web" not in Path(source).name


def test_a_plugin_that_succeeds_with_an_empty_product_fails_verification(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    install(work)
    store_chain(work, EMPTY_HOP)

    result = work.run("encode", source, "--recompress-only")

    # Exit 5, not 1: the plugin did not fail, it lied.
    assert result.returncode == 5, (result.returncode, result.stdout, result.stderr)
    assert "empty" in result.stderr.lower()

    # Nothing was promoted, and nothing is left wearing the real name.
    assert not (work.root / "src_web.avi").exists()


def test_a_plugin_that_will_not_describe_itself_is_refused(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    install(work, mode="schema")
    store_chain(work, PLAIN_HOP)

    result = work.run("encode", source, "--recompress-only")

    assert result.returncode == 4, (result.returncode, result.stdout, result.stderr)
    assert "schema 99" in result.stderr


def test_json_events_puts_nothing_but_json_on_standard_output(work: Work, needs_ffmpeg):
    source = work.source(seconds=1)
    install(work)
    store_chain(work, PLAIN_HOP)

    result = work.run("encode", source, "--recompress-only", "--json-events")

    assert result.returncode == 0, result.stderr

    lines = [line for line in result.stdout.splitlines() if line.strip()]
    assert lines, "the stream is empty"

    events = [json.loads(line) for line in lines]

    # Every line parses. If the human report had stayed on stdout, the first
    # "written:" line would fail here -- which is exactly the mistake this guards.
    assert all("event" in event for event in events), result.stdout

    kinds = {event["event"] for event in events}
    assert "done" in kinds, kinds
    assert "progress" in kinds or "stage" in kinds, kinds
