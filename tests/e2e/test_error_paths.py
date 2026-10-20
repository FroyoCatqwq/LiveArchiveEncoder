"""Error-path smoke tests: the C# build and the v6.3.3 baseline must agree.

Both implementations are pushed down the same broken inputs; the contract is
that they fail the same way (same return code, both with a diagnostic). Message
wording differs by design -- Python prints tracebacks -- so only the codes and
"did it complain at all" are compared.
"""

from __future__ import annotations

import pytest

from harness import run

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


def _args_for(name: str, extra: list[str], work, src) -> list[str]:
    out = work / f"err_{name}"
    if name == "missing_input":
        return [str(work / "does_not_exist.mp4"), str(out) + ".rm", "--encoder", "rv20"]
    if name == "bad_extension":
        return [str(src), str(out) + ".mp4", "--encoder", "rv20"]
    ext = ".roq" if next((extra[i + 1] for i, v in enumerate(extra)
                          if v == "--encoder"), "rv20") == "roqvideo" else ".rm"
    return [str(src), str(out) + ext, "--end-time", "00:00:02", *extra]


def _first_error(text: str) -> str:
    for line in text.splitlines():
        if "ERROR" in line or "Traceback" in line:
            return line.strip()[:70]
    return (text.splitlines() or [""])[0].strip()[:70]


@pytest.mark.parametrize(("name", "extra"), CASES, ids=[c[0] for c in CASES])
def test_error_path_matches_the_baseline(exe, src, py_baseline, workdir, name, extra):
    work = workdir("errors")
    args = _args_for(name, extra, work, src)

    py = run([*py_baseline, *args], timeout=180)
    # --ffmpeg-source pins the C# run to the system FFmpeg -- the same binary
    # the Python baseline uses -- so a managed build installed later cannot
    # change these exit codes. Python has no such flag, hence the asymmetry.
    cs = run([str(exe), *args, "--ffmpeg-source", "path"], timeout=180)

    py_msg = _first_error(py.stdout + "\n" + py.stderr)
    cs_msg = _first_error(cs.stdout + "\n" + cs.stderr)
    context = f"\n  py: {py_msg}\n  cs: {cs_msg}"

    both_error = py.returncode != 0 and cs.returncode != 0
    if both_error:
        assert py.returncode == cs.returncode, f"return codes differ{context}"
    else:
        assert py.returncode == 0 and cs.returncode == 0, (
            f"one side failed where the other succeeded{context}")
