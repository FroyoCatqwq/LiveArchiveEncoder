"""v7.9.8.5: the program updates itself from a release feed.

The interesting part is that none of this needs GitHub. The feed is just JSON in
GitHub's release shape, so a local server can stand in for it -- which is what
makes the whole path (check -> download -> digest -> unpack -> verify -> swap)
testable without a network, without publishing a release, and without touching
the binary the rest of the suite runs.

The swap itself is the part worth watching: the process that replaces the
executable cannot be the one being replaced, so the program hands over to the
downloaded build running from a staging directory. These tests therefore poll
for the result instead of assuming it is there when the parent exits.
"""

from __future__ import annotations

import hashlib
import http.server
import json
import os
import shutil
import socket
import tarfile
import threading
import time
import zipfile
from pathlib import Path

import pytest

import harness
from harness import EXE_NAME, run

# The suite runs on x64 machines; the flavour is detected from the file set
# rather than assumed, because CS_EXE can point at the NativeAOT build.
_ARCH = "x64"

_JIT_FILES = [
    "LiveArchiveEncoder.dll",
    "LiveArchiveEncoder.deps.json",
    "LiveArchiveEncoder.runtimeconfig.json",
]

# Starts with LiveArchiveEncoder so the installer's file filter picks it up. It
# is how a test proves the files were copied: the binaries are byte-identical to
# the ones already installed, so their presence proves nothing.
MARKER = "LiveArchiveEncoder.e2e-marker"


def _files_of(exe: Path) -> list[str]:
    """The files that make up this build: one exe for AOT, four for JIT."""
    names = [exe.name]
    if (exe.parent / _JIT_FILES[0]).is_file():
        names += _JIT_FILES
    return [name for name in names if (exe.parent / name).is_file()]


def _suffix(files: list[str]) -> str:
    aot = _JIT_FILES[0] not in files
    platform = "win" if os.name == "nt" else "linux"
    extension = "zip" if os.name == "nt" else "tar.gz"
    return f"-{platform}-{_ARCH}{'-aot' if aot else ''}.{extension}"


def _is_jit(files: list[str]) -> bool:
    return _JIT_FILES[0] in files


def _write_archive(source: Path, files: list[str], target: Path, marker: bool) -> None:
    """Pack a build the way a release does: flat, with an optional marker file."""
    payload = {name: (source / name).read_bytes() for name in files}
    if marker:
        payload[MARKER] = b"copied by the updater\n"

    if target.suffix == ".zip":
        with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data in payload.items():
                archive.writestr(name, data)
        return

    import io

    with tarfile.open(target, "w:gz") as archive:
        for name, data in payload.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mode = 0o755
            archive.addfile(info, io.BytesIO(data))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


class _RangeHandler(http.server.SimpleHTTPRequestHandler):
    """A file server that honours Range requests.

    Not a nicety: the tool downloads with aria2c when it is installed, and aria2c
    opens sixteen ranged connections and then retries for a minute when the
    server ignores the header.
    """

    def log_message(self, format: str, *args: object) -> None:  # noqa: A002 - base class API
        pass

    def send_head(self):  # type: ignore[override]
        header = self.headers.get("Range")
        if not header or not header.startswith("bytes="):
            return super().send_head()

        path = self.translate_path(self.path)
        if not os.path.isfile(path):
            self.send_error(404)
            return None

        size = os.path.getsize(path)
        spec = header[len("bytes="):].split(",")[0].strip()
        first, _, last = spec.partition("-")
        start = int(first) if first else 0
        end = int(last) if last else size - 1
        end = min(end, size - 1)
        if start > end:
            self.send_error(416)
            return None

        handle = open(path, "rb")
        handle.seek(start)
        self.send_response(206)
        self.send_header("Content-Type", self.guess_type(path))
        self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
        self.send_header("Content-Length", str(end - start + 1))
        self.send_header("Accept-Ranges", "bytes")
        self.end_headers()
        self._range = (start, end)
        return handle

    def copyfile(self, source, outputfile):  # type: ignore[override]
        bounds = getattr(self, "_range", None)
        if bounds is None:
            return super().copyfile(source, outputfile)
        remaining = bounds[1] - bounds[0] + 1
        while remaining > 0:
            chunk = source.read(min(65536, remaining))
            if not chunk:
                break
            outputfile.write(chunk)
            remaining -= len(chunk)


class _Server:
    def __init__(self, directory: Path) -> None:
        self._httpd = http.server.ThreadingHTTPServer(
            ("127.0.0.1", 0), lambda *a, **kw: _RangeHandler(*a, directory=str(directory), **kw))
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)

    def __enter__(self) -> str:
        self._thread.start()
        port = self._httpd.server_address[1]
        return f"http://127.0.0.1:{port}"

    def __exit__(self, *exc: object) -> None:
        self._httpd.shutdown()
        self._httpd.server_close()
        self._thread.join(timeout=5)


class Feed:
    """A served release, alive for the duration of a test."""

    def __init__(self, root: Path, exe: Path, **kw: object) -> None:
        self.root = root
        self.exe = exe
        self.kw = kw
        self._server: _Server | None = None

    def __enter__(self) -> str:
        files = _files_of(self.exe)
        tag = str(self.kw.pop("tag", "v9.9.9"))
        marker = bool(self.kw.pop("marker", True))
        digest = self.kw.pop("digest", None)
        suffix = self.kw.pop("asset_suffix", None) or _suffix(files)
        if self.kw:
            raise AssertionError(f"unexpected keys: {self.kw}")

        archive_name = f"LiveArchiveEncoder-{tag}{suffix}"
        archive = self.root / archive_name
        _write_archive(self.exe.parent, files, archive, marker)

        # The server has to come up before the feed is written: the asset URL is
        # absolute, and the port is only known once it is listening.
        self._server = _Server(self.root)
        base = self._server.__enter__()

        document = {
            "tag_name": tag,
            "name": f"{tag} - a test release",
            "body": "notes",
            "html_url": "https://example.invalid/releases/tag/" + tag,
            "assets": [{
                "name": archive_name,
                "size": archive.stat().st_size,
                "browser_download_url": f"{base}/{archive_name}",
                "digest": "sha256:" + (str(digest) if digest else _sha256(archive)),
            }],
        }
        (self.root / "release.json").write_text(json.dumps(document), encoding="utf-8")
        return base + "/release.json"

    def __exit__(self, *exc: object) -> None:
        if self._server is not None:
            self._server.__exit__(*exc)


def _install_copy(exe: Path, target: Path) -> Path:
    """A private install directory: an update replaces files next to the exe."""
    target.mkdir(parents=True, exist_ok=True)
    for name in _files_of(exe):
        shutil.copy2(exe.parent / name, target / name)
    return target / exe.name


def _env(tmp: Path) -> dict[str, str]:
    # A private config root: the update check writes update-state.json, and a
    # test must not disturb the real one.
    return os.environ | {"LAE_CONFIG_DIR": str(tmp)}


def _wait_for(path: Path, timeout: float = 40.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists():
            return True
        time.sleep(0.2)
    return False


# ---- the tests ----

def test_the_version_is_a_number(exe):
    """--version is what the updater trusts the staged build by."""
    proc = run([str(exe), "--version"], timeout=60)

    assert proc.returncode == 0, proc.stderr
    parts = proc.stdout.strip().split(".")
    assert len(parts) >= 3, proc.stdout
    assert all(part.isdigit() for part in parts[:4]), proc.stdout


def test_a_check_finds_a_newer_build(exe, tmp_path):
    with Feed(tmp_path, exe) as url:
        proc = run([str(exe), "--update-check", "--update-feed", url,
                    "--no-gh-proxy", "--lang", "en"],
                   timeout=120, env=_env(tmp_path / "cfg"))

    assert proc.returncode == 3, f"exit={proc.returncode}\n{proc.stdout}\n{proc.stderr}"
    assert "v9.9.9" in proc.stdout, proc.stdout


def test_the_current_build_is_not_offered_as_an_upgrade(exe, tmp_path):
    """A feed older than the running build must answer "up to date", not 3."""
    current = run([str(exe), "--version"], timeout=60).stdout.strip()
    # 0.0.1 is older than everything this project has ever shipped.
    with Feed(tmp_path, exe, tag="v0.0.1") as url:
        proc = run([str(exe), "--update-check", "--update-feed", url,
                    "--no-gh-proxy", "--lang", "en"],
                   timeout=120, env=_env(tmp_path / "cfg"))

    assert proc.returncode == 0, f"exit={proc.returncode}\n{proc.stdout}"
    assert current in proc.stdout, proc.stdout


def test_an_update_replaces_the_files_in_place(exe, tmp_path):
    install = _install_copy(exe, tmp_path / "install")
    marker = install.parent / MARKER

    with Feed(tmp_path, exe) as url:
        proc = run([str(install), "--update", "--update-feed", url,
                    "--no-gh-proxy", "--lang", "en"],
                   timeout=180, input="", env=_env(tmp_path / "cfg"))

    assert proc.returncode == 0, f"exit={proc.returncode}\n{proc.stdout}\n{proc.stderr}"
    assert _wait_for(marker), f"the marker file never appeared in {install.parent}"
    assert marker.read_text(encoding="utf-8").startswith("copied by the updater")

    # The file set is still coherent: the replaced build starts and reports a
    # version, which is the whole point of doing the swap from a second process.
    check = run([str(install), "--version"], timeout=60)
    assert check.returncode == 0, check.stderr
    assert check.stdout.strip()[-1].isdigit()

    # Starting the new build is also when the staging directory goes: it is
    # marked finished by the swap, so the ten-minute safety window does not apply.
    leftovers = sorted(p.name for p in install.parent.glob(".update-*"))
    assert leftovers == [], f"staging left behind: {leftovers}"


def test_a_wrong_digest_stops_the_update(exe, tmp_path):
    install = _install_copy(exe, tmp_path / "install")
    marker = install.parent / MARKER

    with Feed(tmp_path, exe, digest="0" * 64) as url:
        proc = run([str(install), "--update", "--update-feed", url,
                    "--no-gh-proxy", "--lang", "en"],
                   timeout=180, input="", env=_env(tmp_path / "cfg"))

    assert proc.returncode == 1, f"exit={proc.returncode}\n{proc.stdout}\n{proc.stderr}"
    assert "digest" in proc.stderr.lower(), proc.stderr
    time.sleep(1.0)
    assert not marker.exists(), "a download that failed its digest was installed anyway"


def test_a_release_without_an_asset_for_this_build_is_refused(exe, tmp_path):
    with Feed(tmp_path, exe, asset_suffix="-win-arm64.zip") as url:
        proc = run([str(exe), "--update", "--update-feed", url,
                    "--no-gh-proxy", "--lang", "en"],
                   timeout=120, input="", env=_env(tmp_path / "cfg"))

    assert proc.returncode == 1, f"exit={proc.returncode}\n{proc.stdout}\n{proc.stderr}"
    assert "asset for this build" in proc.stderr, proc.stderr


def test_the_handover_can_restart_the_program(exe, tmp_path):
    """The `--restart` half of the handover, driven directly.

    Reached this way rather than through `--update` because a real restart
    depends on stdin being a terminal, and a test's stdin is a pipe -- which is
    the case the other tests already cover. Here the applier is told to restart
    explicitly, and the proof that it did is the menu the relaunched program
    prints: it inherits this pipe, so it is visible in the captured output and
    exits on EOF like any scripted shell run.
    """
    install = _install_copy(exe, tmp_path / "install")
    payload = tmp_path / "payload"
    payload.mkdir()
    for name in _files_of(exe):
        shutil.copy2(exe.parent / name, payload / name)
    (payload / MARKER).write_text("handed over\n", encoding="utf-8")

    # A pid that cannot exist: "the parent is already gone" is the normal case
    # and must not be treated as a failure. And the applier is the DOWNLOADED
    # build, not the installed one -- running from the install directory would
    # mean holding the very files it has to replace.
    applier = payload / exe.name
    proc = run([str(applier), "--self-update-apply", "9999999",
                str(install.parent), str(payload), "--restart"],
               timeout=180, input="", env=_env(tmp_path / "cfg"))

    assert proc.returncode == 0, f"exit={proc.returncode}\n{proc.stdout}\n{proc.stderr}"
    assert (install.parent / MARKER).is_file(), "the payload was never copied"
    assert "Live Archive Encoder" in proc.stdout, (
        "the replacement was not restarted:\n" + proc.stdout[-600:])


def test_the_shell_can_turn_the_start_up_check_off(exe, tmp_path):
    """Menu 21 -> 3. The row and its strings exist; this is the wiring."""
    root = tmp_path / "cfg"
    root.mkdir(parents=True, exist_ok=True)

    proc = run([str(exe)], timeout=180, input="21\n3\n0\n0\n", env=_env(root))

    assert proc.returncode == 0, f"exit={proc.returncode}\n{proc.stderr[-400:]}"
    saved = json.loads((root / "config.json").read_text(encoding="utf-8"))
    # The settings file spells its members the C# way.
    value = saved.get("AutoUpdateCheck", saved.get("autoUpdateCheck"))
    assert value is False, saved
