#!/usr/bin/env python3
"""A plugin that exists to be wrong in specific ways.

The host's interesting paths are all failure paths: a plugin that will not
describe itself, one that exits 2 because the host built a bad command line, one
that returns success with a truncated file. Each is selected here by a parameter
or an environment variable, so a test can ask for exactly one of them.

It is deliberately small. A fixture that shares code with the thing under test
proves that the share works, not that the contract does.

  <plugin> --lae-handshake
  <plugin> run --stage copy --input <in> --output <out> --param mode=<mode>

Modes: copy (default), truncate, empty, garbage, fail, usage, slow.
"""

import json
import os
import shutil
import sys
import time

PLUGIN_ID = "lae-fake"


def folder() -> str:
    return os.path.dirname(os.path.abspath(__file__))


def mode() -> str:
    """How this copy should misbehave, from a file beside it.

    A file rather than an environment variable because tests run in parallel in one
    process: a variable set by one would apply to every other plugin started at the
    same moment. A file is per-copy, and each test gets its own copy.
    """
    path = os.path.join(folder(), "mode.txt")
    if not os.path.exists(path):
        return ""
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read().strip()


def handshake() -> None:
    behaviour = mode()

    if behaviour == "hang":
        time.sleep(120)
        return

    if behaviour == "silent":
        return

    if behaviour == "schema":
        print(json.dumps({"schema": 99, "protocol": 1, "id": PLUGIN_ID, "stages": []}))
        return

    if behaviour == "wrongid":
        print(json.dumps({
            "schema": 1,
            "protocol": 1,
            "id": "someone-else",
            "stages": [{"id": "copy", "placement": ["post-encode"]}],
        }))
        return

    print(json.dumps({
        "schema": 1,
        "protocol": 1,
        "id": PLUGIN_ID,
        "version": "0.0.1",
        "media": ["video"],
        "deterministic": behaviour != "nondeterministic",
        "stages": [{
            "id": "copy",
            "placement": ["post-encode", "between-passes", "after-chain"],
            "params": [
                {"name": "mode", "type": "string", "default": "copy"},
                {"name": "percent", "type": "float", "min": 0, "max": 100, "default": 100},
            ],
        }],
    }))


def run(argv: list[str]) -> int:
    stage = None
    source = None
    destination = None
    params: dict[str, str] = {}

    index = 0
    while index < len(argv):
        token = argv[index]
        if token == "--stage" and index + 1 < len(argv):
            stage = argv[index + 1]
            index += 2
        elif token == "--input" and index + 1 < len(argv):
            source = argv[index + 1]
            index += 2
        elif token == "--output" and index + 1 < len(argv):
            destination = argv[index + 1]
            index += 2
        elif token == "--param" and index + 1 < len(argv):
            key, _, value = argv[index + 1].partition("=")
            params[key] = value
            index += 2
        else:
            index += 1

    if stage != "copy":
        print(f"unknown stage: {stage}", file=sys.stderr)
        return 2

    if not source or not destination:
        print("missing --input or --output", file=sys.stderr)
        return 2

    mode = params.get("mode", "copy")

    # A plugin's own commentary goes to stderr and the host forwards it. The
    # default mode prints something, so a test can prove forwarding happened.
    print(f"fake-plugin: {mode} {os.path.basename(source)}", file=sys.stderr)

    if mode == "fail":
        print("fake-plugin: refusing on purpose", file=sys.stderr)
        return 3

    if mode == "usage":
        print("fake-plugin: --mode is not a thing I accept", file=sys.stderr)
        return 2

    if mode == "empty":
        open(destination, "wb").close()
        return 0

    if mode == "garbage":
        with open(destination, "wb") as out:
            out.write(b"this is not a video and never was")
        return 0

    if mode == "slow":
        # Longer than any budget a test gives it, so the only way this returns is
        # if the host failed to kill it.
        time.sleep(30)

    if mode == "truncate":
        percent = float(params.get("percent", "10"))
        size = os.path.getsize(source)
        with open(source, "rb") as src, open(destination, "wb") as dst:
            dst.write(src.read(int(size * percent / 100.0)))
        return 0

    shutil.copyfile(source, destination)
    return 0


def main() -> int:
    args = sys.argv[1:]

    if "--lae-handshake" in args:
        handshake()
        return 0

    if args and args[0] == "run":
        return run(args[1:])

    print("fake-plugin: unknown invocation", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
