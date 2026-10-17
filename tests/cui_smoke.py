#!/usr/bin/env python3
"""Interactive-shell smoke test.

Drives the CUI through a piped stdin and asserts on what it prints and what it
leaves behind in the settings file. The real settings file is used (that is the
point of the test) but it is backed up first and restored afterwards.

    python tests/cui_smoke.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe python tests/cui_smoke.py
"""

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_EXE = ROOT / "publish" / "jit" / "LiveArchiveEncoder.exe"
EXE = Path(os.environ.get("CS_EXE", DEFAULT_EXE))
CONFIG = Path(os.environ.get("LOCALAPPDATA", "")) / "LiveArchiveEncoder" / "config.json"
BACKUP = CONFIG.with_suffix(".json.bak")

PASS, FAIL = [], []


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f"  -- {detail}" if detail else ""))


def cui(args, keys, timeout=180):
    """Feed `keys` (a string of newline separated menu choices) to the shell."""
    proc = subprocess.run([str(EXE), *args], input=keys, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout)
    return proc


def read_config():
    if not CONFIG.exists():
        return None
    try:
        return json.loads(CONFIG.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return None


def main():
    if not EXE.is_file():
        sys.exit(f"missing binary: {EXE}")

    had_config = CONFIG.exists()
    if had_config:
        shutil.copyfile(CONFIG, BACKUP)
    else:
        CONFIG.parent.mkdir(parents=True, exist_ok=True)

    try:
        # ---------------------------------------------------------------- run 1
        # Fresh start: no stored settings. Switch to Chinese, turn turbo on,
        # set 4 parallel shards, then exit.
        if CONFIG.exists():
            CONFIG.unlink()
        p1 = cui([], "10\nzh\n9\n11\n4\n0\n")
        check("run1 exit code 0", p1.returncode == 0, f"rc={p1.returncode}")
        check("run1 menu offers turbo", "9." in p1.stdout and "Turbo" in p1.stdout)
        check("run1 language switched to zh", "zh" in p1.stdout)
        check("run1 parallel menu present", "11." in p1.stdout)
        c1 = read_config()
        check("run1 settings file written", c1 is not None)
        if c1:
            check("run1 turbo persisted", c1.get("Turbo") is True, str(c1.get("Turbo")))
            check("run1 language persisted", c1.get("Language") == "zh", str(c1.get("Language")))
            check("run1 parallel persisted", c1.get("Parallel") == 4, str(c1.get("Parallel")))

        # ---------------------------------------------------------------- run 2
        # Settings must come back, and the banner has to be Chinese already.
        p2 = cui([], "0\n")
        check("run2 exit code 0", p2.returncode == 0, f"rc={p2.returncode}")
        check("run2 reports restored settings", "已恢复上次配置" in p2.stdout)
        check("run2 banner is chinese", "交互界面" in p2.stdout)
        check("run2 menu is chinese", "开始编码" in p2.stdout and "退出" in p2.stdout)
        check("run2 turbo still on", "开启（无实时节流）" in p2.stdout)
        check("run2 parallel shows 4", "并行分片数" in p2.stdout and "4" in p2.stdout)

        # ---------------------------------------------------------------- run 3
        # --lang must win over the stored preference, and it must not be
        # clobbered by the CLI-style path either.
        p3 = cui(["--lang", "en"], "0\n")
        check("run3 --lang en overrides stored zh", "Start encoding" in p3.stdout,
              "--lang did not reach the menu")
        # An explicit --lang is a preference, not a one-off: it sticks, the same
        # way every other menu change does.
        c3 = read_config()
        check("run3 --lang en is remembered", c3 and c3.get("Language") == "en",
              str(c3.get("Language") if c3 else None))

        # ---------------------------------------------------------------- run 4
        # A corrupt settings file must never stop the program.
        CONFIG.write_text("{ this is not json", encoding="utf-8")
        p4 = cui([], "0\n")
        check("run4 survives corrupt settings", p4.returncode == 0, f"rc={p4.returncode}")
        check("run4 falls back to english", "Main menu" in p4.stdout)

        # ---------------------------------------------------------------- run 5
        # Invalid menu input must not crash or hang.
        CONFIG.unlink()
        p5 = cui([], "99\n\n7\n0\n")
        check("run5 survives invalid input", p5.returncode == 0, f"rc={p5.returncode}")
        check("run5 reports invalid selection", "Invalid selection" in p5.stdout)

    finally:
        if had_config:
            shutil.copyfile(BACKUP, CONFIG)
            BACKUP.unlink()
        elif CONFIG.exists():
            CONFIG.unlink()

    print()
    print(f"total={len(PASS) + len(FAIL)} pass={len(PASS)} fail={len(FAIL)}")
    if FAIL:
        print("failing: " + ", ".join(FAIL))
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
