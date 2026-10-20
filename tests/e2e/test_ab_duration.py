"""Duration drift isolation: python baseline vs the C# build.

compare_matrix proves the two implementations mux equivalent frame sequences,
but MPEG-PS/TS stamp packets from when the pipe bytes actually arrive, so the
container duration wobbles by about a frame run to run even when both sides mux
an identical frame sequence. This suite runs the two encoders whose container
duration drifted historically (mpeg1video, wmv2) through four variants --
python, and the C# build with inline / separate / auto audio -- four times each
and checks the run-to-run medians against the baseline:

    mpeg (mpg)  wall-clock stamped: |median(py) - median(cs)| <= 0.25 s
                (Linux measures up to 0.167 s, four frames at 23.976 fps)
    others      medians must match exactly (wmv/asf stamps from frame count)
    cs separate the opt-in split pipeline, which remuxes: <= 0.25 s
                (measured on wmv2: 5.015 vs 5.046 on all four runs)

The tolerance and the format list are shared with compare_matrix
(harness.PACING_DURATION_FORMATS / harness.DURATION_TOL -- the comment there
explains where 0.25 s comes from). fresh/repeat counters
and wall times are printed for diagnostics but never asserted: they react to
how busy the machine was, which is also why this suite must not run under
parallel load (see conftest.py -- no pytest-xdist).

    pytest tests/e2e/test_ab_duration.py
    CS_EXE=publish/aot/LiveArchiveEncoder.exe pytest tests/e2e/test_ab_duration.py
    # one encoder only:
    pytest tests/e2e/test_ab_duration.py -k wmv2
"""

from __future__ import annotations

import statistics
import time

import pytest

import harness
from harness import parse_summary, probe, run

CASES = [("mpeg1video", "mpg"), ("wmv2", "wmv")]
RUNS = 4
END = "00:00:05"

# Non-pacing containers are stamped from the frame counter, so the medians must
# agree exactly; the epsilon only absorbs float noise, never a real drift
# (one frame at 25 fps is 0.04 s, five times larger).
EXACT_EPS = 1e-6


def stream_duration(path) -> float | None:
    """ffprobe video-stream duration in seconds (None when unreadable)."""
    info = probe(path)
    if info is None:
        return None
    for s in info.get("streams", []):
        if s.get("codec_type") == "video" and s.get("duration"):
            try:
                return float(s["duration"])
            except ValueError:
                return None
    return None


@pytest.mark.parametrize(("encoder", "ext"), CASES, ids=[c[0] for c in CASES])
def test_duration_medians_match_the_baseline(exe, src, py_baseline, workdir,
                                             encoder, ext):
    work = workdir("ab")
    # --ffmpeg-source is deliberately not pinned here (same as the standalone
    # script this replaces): the point is the C# default resolution path.
    variants = [
        ("py", [*py_baseline]),
        ("cs inline", [str(exe), "--audio-mode", "inline"]),
        ("cs separate", [str(exe), "--audio-mode", "separate"]),
        ("cs auto", [str(exe)]),
    ]

    rows: list[tuple[str, list[float | None], list[str], list[float]]] = []
    errors: list[str] = []
    container: str | None = None

    for label, base in variants:
        tag = label.replace(" ", "_")
        durs: list[float | None] = []
        fresh: list[str] = []
        walls: list[float] = []
        for i in range(RUNS):
            out = work / f"s_{encoder}_{tag}_{i}.{ext}"
            started = time.time()
            proc = run([*base, "--encoder", encoder, "--end-time", END,
                        str(src), str(out)])
            walls.append(time.time() - started)
            if proc.returncode != 0:
                errors.append(f"{label} run{i}: rc={proc.returncode} "
                              f"{proc.stderr.strip()[-200:]}")
                durs.append(None)
                fresh.append("?/?")
                continue
            if not harness.fresh_output(out, started):
                errors.append(f"{label} run{i}: rc=0 but {out.name} was not rewritten")
            dur = stream_duration(out)
            if dur is None:
                errors.append(f"{label} run{i}: rc=0 but ffprobe read no video "
                              f"duration from {out.name}")
            durs.append(dur)
            summary = parse_summary(proc.stdout, proc.stderr, proc.returncode)
            fresh.append(f"{summary['fresh'] or '?'}/{summary['repeated'] or '?'}")
            if container is None:
                info = probe(out)
                container = (info.get("format") or {}).get("format_name") if info else None
        rows.append((label, durs, fresh, walls))

    # The diagnostic table is always printed: on failure it rides along in the
    # assertion message, on success it documents what the medians were built
    # from (per-run jitter is expected, not a defect).
    lines = [f"case {encoder} ({ext}) -- {RUNS} runs per variant, "
             f"segment 0-{END}, container={container or '?'}"]
    medians: dict[str, float] = {}
    for label, durs, fresh, walls in rows:
        valid = [d for d in durs if d is not None]
        if valid:
            medians[label] = statistics.median(valid)
        lines.append(f"  {label:<11} dur={'/'.join(f'{d:.3f}' if d is not None else 'FAIL' for d in durs)}"
                     f"  median={medians.get(label, float('nan')):.3f}")
        lines.append(f"  {' ' * 11} fresh/repeat={'  '.join(fresh)}"
                     f"  wall={' '.join(f'{w:.2f}' for w in walls)}")
    print("\n".join(lines))

    assert not errors, "run failures:\n  " + "\n  ".join(errors) + "\n" + "\n".join(lines)

    py_med = medians["py"]
    # --audio-mode separate is the opt-in split pipeline: audio is encoded on
    # its own and the result is remuxed with -c copy, which rewrites the
    # duration field of some containers even though the frame sequence is
    # identical. Measured on wmv2: 5.046 -> 5.015, the same number every run,
    # i.e. systematic rather than jitter -- it is the documented behaviour in
    # docs/usage.md §7. The default paths (inline / auto) stay on the exact rule.
    SPLIT_PIPELINE_VARIANT = "cs separate"

    diffs: list[str] = []
    for label, med in medians.items():
        if label == "py":
            continue
        delta = abs(med - py_med)
        if label == SPLIT_PIPELINE_VARIANT:
            ok = delta <= harness.DURATION_TOL
            rule = (f"|delta| <= {harness.DURATION_TOL}s (split pipeline remuxes; "
                    f"see docs/usage.md)")
        elif container in harness.PACING_DURATION_FORMATS:
            ok = delta <= harness.DURATION_TOL
            rule = (f"|delta| <= {harness.DURATION_TOL}s (container {container} "
                    f"is wall-clock stamped)")
        elif container is None:
            ok = delta <= harness.DURATION_TOL
            rule = f"|delta| <= {harness.DURATION_TOL}s (container unknown)"
        else:
            ok = delta <= EXACT_EPS
            rule = f"exact match required (container {container})"
        if label == SPLIT_PIPELINE_VARIANT and delta > 0:
            lines.append(f"  note: {label} is {delta:.3f}s off the single-process "
                         f"path -- the documented split-pipeline behaviour")
        if not ok:
            diffs.append(f"{label}: median {med:.3f}s vs py {py_med:.3f}s "
                         f"(delta {delta:.3f}s, {rule})")
    assert not diffs, "duration drift:\n  " + "\n  ".join(diffs) + "\n" + "\n".join(lines)
