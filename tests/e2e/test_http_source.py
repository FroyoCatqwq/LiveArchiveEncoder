"""HTTP/HTTPS source end-to-end (pytest).

v7.9 goal 6: an input may be an http:// or https:// URL, "initial support" in
the sense of a progressive file over a Range-capable server -- which is what
archive.org and every CDN are. FFmpeg does the fetching; what these tests pin
down is the plumbing around it:

    pytest tests/e2e/test_http_source.py

The server below is a deliberately minimal Range-capable file server on
127.0.0.1. It has to speak Range because the encoder seeks the input (-ss
before -i), and a server that ignores Range would make ffmpeg fail on the seek
rather than on anything this program does. python's stdlib http.server does
*not* do that, hence the handler.
"""

from __future__ import annotations

import re
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

import harness
from harness import run

MEDIA_DIR = harness.TESTS / "media"
CLIP = "test_src.mp4"


class _RangeHandler(BaseHTTPRequestHandler):
    """GET one file out of MEDIA_DIR, with single-range 206 support.

    Everything ffmpeg's http protocol asks for on a progressive download:
    plain GET, one `Range: bytes=a-b` (or `bytes=a-`) per request, 206 with
    Content-Range when honoured, 404 otherwise. Suffix ranges (`bytes=-N`)
    are answered too, though ffmpeg does not use them.
    """

    protocol_version = "HTTP/1.1"

    def log_message(self, *args):  # keep pytest output readable
        pass

    def _resolve(self) -> Path | None:
        name = self.path.split("?", 1)[0].lstrip("/")
        if not name or "/" in name or "\\" in name:
            return None
        path = (MEDIA_DIR / name).resolve()
        return path if path.is_file() and MEDIA_DIR.resolve() in path.parents else None

    def _send_range(self, path: Path, spec: str | None):
        size = path.stat().st_size
        start, end = 0, size - 1
        partial = spec is not None
        if spec:
            m = re.fullmatch(r"(\d*)-(\d*)", spec)
            if not m:
                self.send_error(416, "bad range")
                return
            first, last = m.group(1), m.group(2)
            if first:
                start = int(first)
                end = int(last) if last else size - 1
                # RFC 9110 14.1.2: a last-byte-pos past the end is clamped, not
                # honoured. Without this a client asking for 2 MiB of an 800 KiB
                # file was promised a Content-Length it could never receive, and
                # then hung waiting for the rest -- which is exactly the hang
                # NetworkProbe's budget now protects against, but a server that
                # speaks Range has no business creating it.
                end = min(end, size - 1)
            elif last:  # suffix: the final `last` bytes
                start = max(0, size - int(last))
            if start > end or start >= size:
                self.send_error(416, "range not satisfiable")
                return
        length = end - start + 1
        self.send_response(206 if partial else 200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(length))
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.end_headers()
        with path.open("rb") as fh:
            fh.seek(start)
            remaining = length
            while remaining > 0:
                chunk = fh.read(min(65536, remaining))
                if not chunk:
                    break
                self.wfile.write(chunk)
                remaining -= len(chunk)

    def do_GET(self):
        path = self._resolve()
        if path is None:
            self.send_error(404, "not found")
            return
        spec = None
        range_header = self.headers.get("Range")
        if range_header and range_header.startswith("bytes="):
            spec = range_header[len("bytes="):].split(",")[0].strip()
        self._send_range(path, spec)

    def do_HEAD(self):
        path = self._resolve()
        if path is None:
            self.send_error(404, "not found")
            return
        size = path.stat().st_size
        self.send_response(200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(size))
        self.end_headers()


@pytest.fixture(scope="module")
def server():
    """A Range-capable file server; yields (base_url, stop)."""
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), _RangeHandler)
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()
    yield f"http://127.0.0.1:{httpd.server_address[1]}"
    httpd.shutdown()
    httpd.server_close()
    thread.join(timeout=5)


@pytest.fixture
def dead_port() -> str:
    """A port nothing is listening on (bind, learn the number, let go)."""
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return f"http://127.0.0.1:{s.getsockname()[1]}"


class _StallHandler(BaseHTTPRequestHandler):
    """Advertise a body, send a taste, then go quiet -- the NAS that hangs.

    This is not a contrived server: a share that promises Content-Length and
    then stops sending is an ordinary way for a network source to misbehave,
    and the client-side probe used to wait for the rest of the body forever.
    """

    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(200 * 1024 * 1024))
        self.end_headers()
        try:
            self.wfile.write(b"\0" * 4096)
            self.wfile.flush()
            time.sleep(300)
        except OSError:      # the client gave up, which is the point
            pass


@pytest.fixture(scope="module")
def stalling_server():
    httpd = ThreadingHTTPServer(("127.0.0.1", 0), _StallHandler)
    httpd.daemon_threads = True
    thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    thread.start()
    yield f"http://127.0.0.1:{httpd.server_address[1]}"
    httpd.shutdown()
    httpd.server_close()
    thread.join(timeout=5)


def _encode(exe, workdir, name, url, extra, ext="avi"):
    out = workdir("http")
    dst = out / f"{name}.{ext}"
    proc = run([str(exe), url, str(dst), *extra, "--ffmpeg-source", "path"])
    return proc, dst


# ---------------------------------------------------------------------------
# The happy paths
# ---------------------------------------------------------------------------

def test_http_segment(exe, server, workdir):
    """The specced shape: a URL plus a time segment.

    Input seeking (-ss before -i) is what makes the server need Range, so this
    case fails on a non-conforming server before it tests anything else.
    """
    proc, dst = _encode(exe, workdir, "segment", f"{server}/{CLIP}",
                        ["--encoder", "mpeg4",
                         "--start-time", "00:00:02", "--end-time", "00:00:06"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    info = harness.video_summary(dst)
    assert info is not None and not info.get("no_video"), "ffprobe cannot read the output"
    # 4 seconds requested; the live pace means the product is that long, give
    # or take the slot grid.
    assert 3.0 <= info["duration"] <= 6.0, f"unexpected duration {info['duration']}"


def test_http_full_read(exe, server, workdir):
    """No seek, no Range: a plain progressive GET from the first byte."""
    proc, dst = _encode(exe, workdir, "full", f"{server}/{CLIP}",
                        ["--encoder", "mpeg4", "--end", "00:00:03"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    info = harness.video_summary(dst)
    assert info is not None and not info.get("no_video")


def test_http_audio_second_read(exe, server, workdir):
    """The audio track is a second network read of the same URL.

    mpeg4 in AVI carries libmp3lame by default, so the encode opens the URL a
    second time for the audio pipeline -- the path most likely to forget the
    protocol options.
    """
    proc, dst = _encode(exe, workdir, "audio", f"{server}/{CLIP}",
                        ["--encoder", "mpeg4", "--end", "00:00:03"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    streams = (harness.probe(dst) or {}).get("streams", [])
    assert any(s.get("codec_type") == "audio" for s in streams), "no audio stream in the output"


def test_http_parallel_shards(exe, server, workdir):
    """Sharding reads the URL once per shard, concurrently."""
    proc, dst = _encode(exe, workdir, "shards", f"{server}/{CLIP}",
                        ["--encoder", "mpeg4", "--parallel", "3", "--end", "00:00:03"])

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    info = harness.video_summary(dst)
    assert info is not None and not info.get("no_video")


def test_http_default_output_name(exe, server, workdir):
    """No output argument: the product lands in the working directory under
    the name the URL ends with (v7.9 Cli.DefaultOutput)."""
    scratch = workdir("http")
    url = f"{server}/{CLIP}"

    proc = run([str(exe), url, "--encoder", "mpeg4", "--end", "00:00:03",
                "--ffmpeg-source", "path"], cwd=str(scratch))

    assert proc.returncode == 0, (
        f"exit={proc.returncode}: {(proc.stderr.strip().splitlines() or [''])[-1][:200]}")
    product = scratch / "test_src_live.avi"
    assert product.is_file() and product.stat().st_size > 0, \
        f"expected {product.name} in the working directory, found: " \
        f"{sorted(p.name for p in scratch.iterdir())}"


# ---------------------------------------------------------------------------
# The unhappy paths -- a URL must fail like a URL, not hang or mislead
# ---------------------------------------------------------------------------

def test_http_404(exe, server, workdir):
    """A wrong path on a working server is a 404, reported as such."""
    proc, dst = _encode(exe, workdir, "missing", f"{server}/no-such-clip.mp4",
                        ["--encoder", "mpeg4", "--end", "00:00:03"])

    assert proc.returncode != 0
    assert not dst.exists(), "a failed run must not leave a product behind"
    assert "404" in (proc.stdout + proc.stderr), \
        f"the 404 should reach the user, got: {(proc.stdout + proc.stderr)[-200:]}"


def test_http_connection_refused(exe, dead_port, workdir):
    """Nothing listening: fail fast, not after the socket timeout."""
    proc, dst = _encode(exe, workdir, "dead", f"{dead_port}/{CLIP}",
                        ["--encoder", "mpeg4", "--end", "00:00:03"])

    assert proc.returncode != 0
    assert not dst.exists()


def test_http_stalled_body_does_not_hang(exe, stalling_server, workdir):
    """A server that stops mid-body must produce a verdict, not a hang.

    The link probe behind `--parallel` used to block on the rest of an
    advertised body forever: HttpClient.Timeout stops applying once the headers
    are in (ResponseHeadersRead hands back a live stream), so there was nothing
    left to give up. A run then sat in silence with no output at all until the
    caller killed it. The probe now carries its own deadline, and the only
    acceptable outcomes here are "measured, so no sharding" and "could not
    measure" -- both fail the run fast and say so.
    """
    started = time.monotonic()
    proc, dst = _encode(exe, workdir, "stall", f"{stalling_server}/stall.mp4",
                        ["--encoder", "mpeg4", "--parallel", "3", "--end", "00:00:03"])
    elapsed = time.monotonic() - started

    # ffprobe's own -rw_timeout is 30 s, the probe budget 15 s, plus startup.
    assert elapsed < 150, f"the run took {elapsed:.0f}s, i.e. it hung"
    assert proc.returncode != 0, "a source that never delivers must fail"
    assert not dst.exists(), "a failed run must not leave a product behind"
