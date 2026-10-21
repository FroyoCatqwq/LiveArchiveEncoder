#!/usr/bin/env python3
"""Two-phase runner for the end-to-end suite (tests/e2e).

The suite has 155 cases and, on this machine, took 794 s when everything ran
one test at a time. Most of that is realtime encoding: each case burns the
wall-clock seconds it claims to encode, so the only way to go faster is to run
independent cases at once.

Not all of them are independent. Two groups have to run alone:

* ``timing`` (compare_matrix, ab_duration) compares what the adaptive bitrate
  controller chose. It reacts to how busy the machine is, so a parallel worker
  changes the measurement rather than merely slowing it down.
* ``shared_config`` (cui_smoke) reads and writes the real settings file under
  the user profile -- the same file every encode writes on its way out, so a
  concurrent CLI run can land between two of its assertions.

So this runner does two phases:

    phase 1  the exclusive modules, one pytest process, nothing else running
    phase 2  everything else under pytest-xdist, one test per worker

Measured on a 32-core box: 794 s -> 492 s (397 s exclusive + 95 s parallel), and
the exclusive phase is now the long pole because it *has* to be.

    python tools/e2e.py                     # both phases
    python tools/e2e.py --phase serial      # only the exclusive modules
    python tools/e2e.py --phase parallel    # only the rest
    python tools/e2e.py --workers 4         # fewer hands
    python tools/e2e.py -k era_flv          # extra args go to pytest
    python tools/e2e.py -- --maxfail 1      # everything after -- is passthrough
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXCLUSIVE = "timing or shared_config"
SHARED = "not (timing or shared_config)"

# pytest's "no tests were collected" is a success for a phase that a -k filter
# emptied out; anything else non-zero is a real failure.
OK_CODES = {0, 5}


def cpu_budget() -> int:
    """How many parallel workers to use by default.

    ``os.process_cpu_count`` (3.13+) respects CPU affinity and cgroup limits,
    which matters on the Linux CI boxes where this runs in a container. Each
    encode is paced against the wall clock -- it sleeps most of its life -- so
    the useful limit is higher than the core count; the cap of 8 keeps the
    boxes' ffprobe bursts from fighting each other.
    """
    count = getattr(os, "process_cpu_count", None)
    total = (count() if count else None) or os.cpu_count() or 2
    return max(2, min(8, total - 1))


def run_phase(name: str, argv: list[str]) -> int:
    print(f"\n=== {name} ===\n  {' '.join(argv[3:])}", flush=True)
    started = time.monotonic()
    code = subprocess.call(argv, cwd=ROOT)
    elapsed = time.monotonic() - started
    verdict = "ok" if code in OK_CODES else f"FAILED (exit {code})"
    print(f"  {name}: {verdict} in {elapsed:.0f}s", flush=True)
    return 0 if code in OK_CODES else code


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description="Run the e2e suite in two phases (exclusive, then parallel).",
        epilog="Arguments this parser does not recognise are passed to pytest.")
    parser.add_argument("--phase", choices=["all", "serial", "parallel"], default="all",
                        help="run only one phase (default: both)")
    parser.add_argument("--workers", type=int, default=None,
                        help=f"parallel workers for phase 2 (default: {cpu_budget()})")
    parser.add_argument("--no-xdist", action="store_true",
                        help="run phase 2 serially (for bisecting a flaky case)")
    args, extra = parser.parse_known_args(argv)
    if extra and extra[0] == "--":
        extra = extra[1:]

    base = [sys.executable, "-m", "pytest", "-q", *extra]
    workers = args.workers or cpu_budget()
    phases: list[tuple[str, list[str]]] = []

    if args.phase in ("all", "serial"):
        phases.append(("phase 1: exclusive", [*base, "-m", EXCLUSIVE]))
    if args.phase in ("all", "parallel"):
        if args.no_xdist:
            phases.append(("phase 2: serial", [*base, "-m", SHARED]))
        else:
            phases.append(("phase 2: parallel", [*base, "-n", str(workers), "-m", SHARED]))

    failed = [name for name, cmd in phases if run_phase(name, cmd) != 0]
    print()
    if failed:
        print(f"e2e FAILED: {', '.join(failed)}")
        return 1
    print("e2e passed: both phases green")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        raise SystemExit(130) from None
