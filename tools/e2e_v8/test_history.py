"""The run history, through the real binary.

A unit test can prove a row maps to a record; only an end-to-end run proves the
row appears at all -- that the session actually told the store, that the database
lives where `history path` says it does, and that a second run does not overwrite
the first.
"""

from __future__ import annotations

from conftest import Work


def test_a_finished_encode_is_remembered(work: Work):
    source = work.source(seconds=2)
    output = work.root / "out.avi"

    result = work.run("encode", source, "--to", "2", "--out", str(output))
    assert result.returncode == 0, result.stderr

    listed = work.run("history", "list")
    assert listed.returncode == 0, listed.stderr

    # One row, and it is about this run: the source name, the encoder, and the
    # outcome. The exact numbers belong to the unit tests; what matters here is
    # that the write happened at all.
    assert "out.avi" in listed.stdout
    assert "mpeg4" in listed.stdout
    assert " ok " in listed.stdout
    assert "1 run(s) recorded" in listed.stdout


def test_the_history_accumulates_and_lives_beside_the_settings(work: Work):
    source = work.source(seconds=1)

    for name in ("first.avi", "second.avi"):
        result = work.run("encode", source, "--to", "1", "--out", str(work.root / name))
        assert result.returncode == 0, result.stderr

    listed = work.run("history", "list", "--limit", "5")
    assert "second.avi" in listed.stdout
    assert "first.avi" in listed.stdout
    assert "2 run(s) recorded" in listed.stdout

    # Newest first: the second run has to be printed above the first.
    assert listed.stdout.index("second.avi") < listed.stdout.index("first.avi")

    where = work.run("history", "path")
    assert where.returncode == 0
    assert where.stdout.strip().endswith("history.db")

    # And the database really is in the isolated settings folder this run used,
    # not in the developer's own profile.
    assert (work.config / "history.db").exists()
