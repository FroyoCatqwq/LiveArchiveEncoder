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
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXCLUSIVE = "timing or shared_config"
SHARED = "not (timing or shared_config)"

# v7.9.8.10: pytest's exit 5 ("no tests collected") used to be treated as
# success unconditionally. A broken marker, a renamed module, a typo'd -k or
# xdist workers that all collected nothing would print "both phases green"
# without a single test having run. An empty phase is only legitimate when the
# caller explicitly filtered with -k; otherwise both phases MUST collect items.


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


def _collected(argv: list[str]) -> int:
    """How many test items this phase can see. -n is dropped for the probe."""
    # A -q passed through to pytest stacks on top of ours. Two -q's make
    # --collect-only print per-file counts instead of node ids, so normalise
    # to exactly one -q here rather than trusting the caller's argv.
    tail = [a for a in argv[3:] if a != "-q"]
    if "-n" in tail:
        i = tail.index("-n")
        del tail[i:i + 2]
    probe = [*argv[:3], "--collect-only", "-q", *tail]
    try:
        done = subprocess.run(probe, cwd=ROOT, capture_output=True, timeout=180)
    except subprocess.TimeoutExpired:
        return -1
    text = done.stdout.decode("utf-8", "replace") + done.stderr.decode("utf-8", "replace")
    if done.returncode != 0:
        return -1
    items = sum(1 for line in text.splitlines() if "::" in line)
    if items == 0:
        # pytest can also summarise ("196/225 tests collected"). Accept that
        # form so a display-format change never looks like an empty phase.
        for line in text.splitlines():
            found = re.search(r"(\d+)(?:/\d+)?\s+tests? collected", line)
            if found:
                return int(found.group(1))
    return items


def run_phase(name: str, argv: list[str], filtered: bool) -> int:
    print(f"\n=== {name} ===\n  {' '.join(argv[3:])}", flush=True)
    expected = _collected(argv)
    started = time.monotonic()
    code = subprocess.call(argv, cwd=ROOT)
    elapsed = time.monotonic() - started

    allowed_empty = filtered and expected == 0
    ok = code == 0 or (code == 5 and allowed_empty)
    if expected < 0 or (expected == 0 and not allowed_empty):
        ok = False
    verdict = "ok" if ok else f"FAILED (exit {code}, collected {expected})"
    print(f"  {name}: {verdict} in {elapsed:.0f}s", flush=True)
    return 0 if ok else (code or 1)


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

    filtered = any(a == "-k" or a.startswith("-k=") for a in extra)
    failed = [name for name, cmd in phases if run_phase(name, cmd, filtered) != 0]
    print()
    if failed:
        print(f"e2e FAILED: {', '.join(failed)}")
        return 1
    if not phases:
        print("e2e FAILED: no phases selected")
        return 1
    print("e2e passed: both phases green")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        raise SystemExit(130) from None
