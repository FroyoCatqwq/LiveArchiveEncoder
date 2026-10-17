#!/usr/bin/env python3
"""Error-path and CUI smoke tests: both implementations must behave identically."""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MEDIA = ROOT / "tests" / "media"
SRC = MEDIA / "test_src.mp4"
PY = ROOT / "live_archive_encoder.py"
# CS_EXE lets the same suite be run against the NativeAOT build.
CS = Path(os.environ.get("CS_EXE")
          or (ROOT / "src" / "LiveArchiveEncoder" / "bin" / "Release" / "net10.0"
              / "LiveArchiveEncoder.exe"))
WORK = ROOT / "tests" / "out"
WORK.mkdir(parents=True, exist_ok=True)

CASES = [
    ("unknown_encoder",  ["--encoder", "not_a_codec"]),
    ("bad_extension",    ["--encoder", "rv20", "--output-override", "x"]),
    ("missing_input",    []),
    ("bad_timecode",     ["--encoder", "rv20", "--start-time", "99:99:99"]),
    ("end_before_start", ["--encoder", "rv20", "--start-time", "00:00:05", "--end-time", "00:00:02"]),
    ("start_past_eof",   ["--encoder", "rv20", "--start-time", "00:30:00"]),
    ("bad_scale_algo",   ["--encoder", "rv20", "--scale-algo", "not_a_kernel"]),
    ("bad_recover",      ["--encoder", "rv20", "--limit", "100", "--recover", "500"]),
    ("roq_bad_size",     ["--encoder", "roqvideo", "--width", "250", "--height", "190"]),
    ("roq_bad_audio_rate", ["--encoder", "roqvideo", "--audio-rate", "44100"]),
    ("unknown_arg",      ["--encoder", "rv20", "--bogus-flag"]),
]


def run(exe: list[str], args: list[str]) -> tuple[int, str]:
    p = subprocess.run([*exe, *args], capture_output=True, text=True,
                       encoding="utf-8", errors="replace", timeout=180)
    combined = (p.stdout + "\n" + p.stderr).strip()
    return p.returncode, combined


def main() -> int:
    print(f"{'case':22} {'py_rc':6} {'cs_rc':6} {'match':6}  first error line")
    print("-" * 110)
    failures = 0
    for name, extra in CASES:
        out = WORK / f"err_{name}"
        args = []
        if name == "missing_input":
            args = [str(WORK / "does_not_exist.mp4"), str(out) + ".rm", "--encoder", "rv20"]
        elif name == "bad_extension":
            args = [str(SRC), str(out) + ".mp4", "--encoder", "rv20"]
        else:
            ext = {"roqvideo": ".roq"}.get(
                next((extra[i + 1] for i, v in enumerate(extra) if v == "--encoder"), "rv20"), ".rm")
            args = [str(SRC), str(out) + ext, "--end-time", "00:00:02", *extra]

        py_rc, py_out = run([sys.executable, str(PY)], args)
        cs_rc, cs_out = run([str(CS)], args)

        def first_error(text: str) -> str:
            for line in text.splitlines():
                if "ERROR" in line or "Traceback" in line:
                    return line.strip()[:70]
            return (text.splitlines() or [""])[0].strip()[:70]

        py_msg = first_error(py_out)
        cs_msg = first_error(cs_out)
        rc_match = py_rc == cs_rc
        # Message bodies differ in wording for Python tracebacks; compare that both
        # produced a diagnostic and agree on the return code.
        both_error = py_rc != 0 and cs_rc != 0
        ok = rc_match if both_error else (py_rc == 0 and cs_rc == 0)
        if not ok:
            failures += 1
        print(f"{name:22} {py_rc:<6} {cs_rc:<6} {'OK' if ok else 'DIFF':6}  py: {py_msg}")
        print(f"{'':22} {'':6} {'':6} {'':6}  cs: {cs_msg}")

    print("-" * 110)
    print(f"total={len(CASES)} fail={failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
