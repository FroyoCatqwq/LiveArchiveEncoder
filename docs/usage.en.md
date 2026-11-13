# Live Archive Encoder v7 (.NET 10 / .NET 11 / C#) — user guide

[English](usage.en.md) · [中文](usage.md)

The operation manual. For migration details and measurements see
[`../README.md`](../README.md) and [`test-report-v7.en.md`](test-report-v7.en.md);
per-version history is in [`CHANGELOG.en.md`](CHANGELOG.en.md); the index of every
document (both languages) is in [`README.md`](README.md).

> **In one sentence**: take a modern video and re-encode it the way a live recording
> was archived that year — realtime paced, into RV20 / RoQ / MPEG-1 / MPEG-4 / WMV2
> and other legacy encoders, with adaptive bitrate control. **Dropped frames,
> corruption and glitch effects are out of scope** — this tool only encodes; those
> effects belong to a different project, not to a hidden switch here.
>
> This English edition covers every option, menu, constraint and error path. The
> Chinese edition additionally keeps the long measurement narratives; both are
> maintained together.

---

## 1. Install and build

### Platforms

| | Windows | Linux |
| --- | --- | --- |
| Status | Primary development platform | Fully supported since v7.5 |
| Build | `tools\build.ps1` (JIT / AOT) | `tools/build.sh` (JIT, optional `--aot`) |
| NativeAOT | Yes — needs MSVC + Windows SDK | Yes, shipped since v7.9.1; building needs `clang` + `zlib1g-dev` |
| SMB | UNC paths (`\\host\share`) | CIFS mount points (`/mnt/…`), needs `cifs-utils` |
| Player | `ffplay`, or the `start` association | `ffplay` or `xdg-open` |
| Managed FFmpeg | Yes | Yes |

### Download a release

Every version publishes four assets, on GitHub and Codeberg:

| File | Platform | Note |
| --- | --- | --- |
| `…-win-x64.zip` | Windows | JIT; needs the .NET 10 **or** .NET 11 runtime (`RollForward=Major`) |
| `…-win-x64-self-contained.zip` | Windows | Bundled runtime, no installation needed |
| `…-linux-x64.tar.gz` | Linux | JIT; needs the .NET 10 **or** .NET 11 runtime |
| `…-linux-x64-self-contained.tar.gz` | Linux | Bundled runtime |

Linux packages are ready after `tar xzf … && ./LiveArchiveEncoder` (the executable
bit is set). The self-contained build links glibc dynamically, so the target must not
be older than the build machine (glibc 2.28 since v7.9.6.3); older distributions are
better served by the JIT package. Both still need FFmpeg/ffprobe on `PATH`, or let the
tool install its own (§7.10).

Full OS / CPU / memory / disk / font / permission requirements, measured environments
and performance expectations are in [`requirements.en.md`](requirements.en.md).

Cross-platform rules (follow these in scripts):

- Executable name: `LiveArchiveEncoder.exe` on Windows, `LiveArchiveEncoder` on Linux;
  the e2e suite handles it through `EXE_NAME`.
- Managed FFmpeg directory: `%LOCALAPPDATA%\LiveArchiveEncoder\ffmpeg\<id>\bin` on
  Windows, `~/.local/share/LiveArchiveEncoder/ffmpeg/<id>/bin` on Linux.
- The settings file sits next to it: `config.json`.
- Both separators are accepted; UNC is `\\host\share`, a Linux mount is a plain path.

### Dependencies

| Need | Detail |
| --- | --- |
| FFmpeg + ffprobe | On `PATH`, or given with `--ffmpeg` / `--ffprobe`; the tool can also install its own (§7.10) |
| .NET SDK | Build only; .NET 10 or .NET 11 (`winget install Microsoft.DotNet.SDK.Preview`, on Linux `mise use -g dotnet@11.0.100-rc.2`) |
| Runtime | JIT needs .NET 10 **or** .NET 11; the self-contained build needs nothing |
| Linux extras | `xz-utils` (unpack a managed `.tar.xz`), `cifs-utils` (SMB mounts), `samba` (if you serve a share) |

Legacy encoders (rv20, roqvideo, amv …) exist only in a full FFmpeg build.
[BtbN GPL builds](https://github.com/BtbN/FFmpeg-Builds/releases) are recommended; the
CUI banner shows `BtbN default` and `GPL build` so you can confirm which one is in use.

> **First start asks which build to install (v7.9.6.3).** With no FFmpeg at all the
> program no longer exits with `Cannot find ffmpeg`; it lists the sources it can
> download for this platform (`btbn` / `gyan` / `nonfree`, filtered per platform) and
> continues the run after installing. It only asks when **someone is at the keyboard**
> (stdin/stdout not redirected) and no `--ffmpeg` / `--ffprobe` was given — guessing a
> distribution inside a script is worse than failing. The script route is
> `--ffmpeg-download` (no value means `btbn`, §7.10).

> **Beware the distribution FFmpeg.** Many Linux distributions strip rv20, roqvideo
> and friends for licence reasons, and even desktop Ubuntu's `ffmpeg` may come from a
> restricted repository. Use `--list-filters`, menu item 3, or simply let this tool
> download a full build.

### Build (Windows)

```powershell
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
tools\build.ps1            # -> publish\jit\LiveArchiveEncoder.exe
tools\build.ps1 -Aot       # -> publish\aot\LiveArchiveEncoder.exe
```

### Target framework: one artefact, two runtimes (v7.8.1)

The default target is **`net10.0`** with `RollForward=Major`, so a single
framework-dependent build starts on a machine that only has .NET 11. To compile
explicitly for .NET 11, use the same switch (`LaeTfm`, defined in
`Directory.Build.props`):

```powershell
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release -p:LaeTfm=net11.0
```

NativeAOT is self-contained and unaffected by the TFM. Do not assume `net11.0` in
scripts: `src\LiveArchiveEncoder\bin\Release\` contains `net10.0`.

### Build (Linux / macOS)

```bash
tools/build.sh              # -> publish/jit/LiveArchiveEncoder
tools/build.sh --aot        # experimental, needs clang + zlib1g-dev
```

The `DOTNET` environment variable overrides which `dotnet` is called — useful with
mise/asdf shims, which a non-interactive shell does not put on `PATH`:

```bash
DOTNET="$HOME/.local/share/mise/shims/dotnet" bash tools/build.sh
```

NativeAOT on Linux is **experimental**: ILCompiler links against system libraries
whose layout differs between distributions. Prefer the JIT build there.

### Only two artefacts: JIT and AOT (since v7.8.1)

v7.8.1 stopped producing a third AOT flavour with `-p:IlcInstructionSet=avx2`. The
instruction-set baseline only affects the C# side, while the encoding happens in the
FFmpeg subprocess, which runtime-dispatches its own SIMD. Measured: AOT starts about
0.07 s faster than JIT (no JIT warm-up), AVX2 versus AOT is inside the noise, and the
cost was an exe that only runs on AVX2 CPUs — so it is no longer built.

NativeAOT needs Visual Studio's **Desktop development with C++** workload (it uses
`link.exe`). If `vcvarsall.bat` cannot locate the Windows SDK, the link fails with
`LNK1181: cannot open input file 'advapi32.lib'`; pass the SDK import library
directories explicitly (`/p:IlcLinkerArgs` or an environment with `LIB` set).

---

## 2. Quick start

```powershell
# The typical case: a modern clip into RealVideo 20
LiveArchiveEncoder.exe input.mp4 output.rm --encoder rv20

# First ten seconds only
LiveArchiveEncoder.exe input.mp4 out.rm --encoder rv20 --end-time 00:00:10

# Output may be omitted -> <name>_live.<container extension>
LiveArchiveEncoder.exe input.mp4 --encoder mpeg4

# No arguments at all -> interactive menu
LiveArchiveEncoder.exe
```

**Set your expectations**: this is a realtime simulation, not an offline transcode.
A 60-second clip takes about 60 seconds plus a few seconds of start-up and teardown.
That is deliberate — the capture thread reads frames at the source cadence and the slot
loop emits one output frame per `1/fps`, which is the only way to reproduce how a live
encode behaved. **Do not expect it to be fast.**

Since v7.9.8.6 every encode prints this first (not with `--quiet`), because the progress
line only shows frames and kbps:

```
Note: live pacing -- the encode follows the source's own duration; a 10-minute
clip waits about 10 minutes (--parallel N shortens it at the cost of shards).
The output keeps '.part' in its name until it passes validation.
```

The `.part` sentence is for first-time users too: the product is written as
`name.part.ext` and only renamed with `File.Move` after validation (§7.12 and
`Core/OutputFile`), so a `.part.xxx` during a run is not a failed output — Ctrl+C
deletes it.

---

## 3. Command line (CLI)

### Syntax

```
LiveArchiveEncoder.exe [options] [input] [output]
```

Input and output are positional and ordered; the output is derived when omitted.

### Option reference

| Option | Default | Meaning |
| --- | --- | --- |
| `--start-time` / `--start` | `00:00:00` | Segment start, `HH:MM:SS[.mmm]` |
| `--end-time` / `--end` | end of source | Segment end, **exclusive**, same format |
| `--encoder` | `rv20` | Video encoder name; must be a legacy encoder the current FFmpeg detects |
| `--audio-encoder` | per container | See §5 |
| `--limit` | `200` | Bitrate ceiling in kbps; above it the frame rate steps down |
| `--recover` | `limit × 0.75` | Bitrate floor; below it the frame rate may step back up |
| `--window` | `2.0` | Rolling statistics window in seconds |
| `--fps` | `13` | Base frame rate |
| `--fps-levels` | `13,12,10,8,5` | **v7.9.9.2**: the default ladder now follows what a 13–15 fps site of the period used (same as `EraPreset.SiteLadder()`), not the Python baseline's 13/8/5/3; an old `config.json` still carrying the old value is migrated. Comma-separated, high to low; `none` (or `off`) removes the ladder so the base rate holds. With `--era` and no explicit value, the preset supplies it |
| `--qv` | per encoder | Quality, **lower is better**, but **each encoder reads its own scale**: libx264/libx265/VP9/AV1 take CRF (0–51), mpeg4/flv/wmv2/rv10/rv20 take `q:v` (2–31), libopenh264 takes neither and only honours bitrate. The same 9 is 2.9× the bitrate across the two scales, and a number is converted at equal bitrate when the encoder changes (v7.9.8.2) |
| `--threads` | `16` | Thread count passed to FFmpeg |
| `--buffer-frames` | `6` | Capture buffer depth in frames |
| `--audio-kbps` | `32` | Audio bitrate; out of range for the encoder/sample rate is an error (§5) |
| `--audio-channels` | `1` | Channel count |
| `--audio-rate` | follow source | Sample rate in Hz; refused if the encoder or container rejects it (§5) |
| `--video-stream` | first | **v7.9**: which video stream to encode (absolute index from ffprobe) |
| `--audio-stream` | first | **v7.9**: which audio stream to keep (use `--list-streams`) |
| `--list-streams` | — | **v7.9**: print every stream in the source and exit |
| `--subtitle` | none | **v7.9**: external subtitle file (`.ass/.ssa/.srt/.vtt`); repeatable (`clip.sc.ass` + `clip.tc.ass`) |
| `--subtitle-stream` | none | **v7.9**: use an embedded subtitle track instead |
| `--subtitle-mode` | `burn` | **v7.9**: `burn` into the picture / `copy` as a track / `off` |
| `--subtitle-codec` | per container | **v7.9**: subtitle encoder for `copy` mode |
| `--subtitle-font-dir` | the program's `fonts` | **v7.9.5.1**: additional font directory for burn-in |
| `--subtitle-font` | none | **v7.9.5.1**: force a font family, overriding the subtitle's own |
| `--subtitle-aspect` | `source` | **v7.9.8.3**: layout aspect for subtitles `source` / `4:3` / `16:9` (§7.16) |
| `--font-import` | — | **v7.9.5.1**: copy a font file (or every font in a directory) into `fonts`, then exit |
| `--list-subtitle-fonts` | — | **v7.9.5.1**: list the families a subtitle asks for, whether they exist and where, then exit |
| `--font-pack` | FILE | **v7.9.8.13**: unpack a `.zip/.7z/.rar` font pack with 7-Zip and import per the subtitle's advice or your own picks |
| `--auto-subtitle` | — | **v7.9.8.13**: find `X.sc.ass` / `X.tc.ass` next to the source; otherwise ask for a file |
| `--width` `--height` | `256` `192` | Output resolution (16:9 presets in §7.15) |
| `--container` | encoder default | Container switch (§7.14) |
| `--fit` | `crop` | `crop` fills and cuts / `fit` keeps everything and pads / `stretch` distorts (§7.15) |
| `--crop-anchor` | `center` | Which side survives `crop`: `center` / `top` / `bottom` / `left` / `right` |
| `--pad-color` | `black` | Padding colour for `fit`: any FFmpeg colour name or `0xRRGGBB` |
| `--no-crop` | crop | Legacy spelling, equivalent to `--fit stretch` |
| `--scale-algo` / `--scale-flags` | `neighbor` | Scaling kernel; `cubic` is an alias for `bicubic` |
| `--pix-fmt` | `auto` | **v7.9.6**: output pixel format (`pal8` / `rgb8` / `rgb555le` / `rgb565le` / `gray` / `rgb48le` / `yuv420p10le` … §7.17). `auto` keeps the source format. A request is either honoured or exits 2 — no silent downgrade |
| `--list-pix-fmts` | — | **v7.9.6**: list all 86 measured pixel formats, marking the ones the current encoder declares, then exit |
| `--color-range` | follow source | **v7.9.6**: `tv` / `pc` / `mpeg` / `jpeg` / `unspecified` |
| `--colorspace` | follow source | **v7.9.6**: matrix tag (`bt709` / `bt470bg` / `smpte170m` / `bt2020nc` …) |
| `--color-primaries` | follow source | **v7.9.6**: primaries tag (`bt709` / `bt470bg` / `bt2020` / `smpte431` …) |
| `--color-trc` | follow source | **v7.9.6**: transfer tag (`bt709` / `iec61966-2-1` for sRGB / `smpte2084` for PQ / `arib-std-b67` for HLG …) |
| `--dither` | `auto` | **v7.9.6**: dithering when reducing colour depth (`auto` / `none` / `bayer` / `ordered` / `random` / `error_diffusion`); only emitted when the target format really loses depth |
| `--deinterlace` | `off` | `bwdif` / `yadif` / `w3fdif` / `nnedi` (**v7.9.8.3**, needs the NNEDI3 weights) |
| `--deint-mode` | `send_frame` | `send_frame` or `send_field` |
| `--deint-parity` | `auto` | `auto` / `tff` / `bff` |
| `--deint-marked-only` | off | Deinterlace only frames flagged interlaced |
| `--nnedi-update` | — | **v7.9.8.3**: download or refresh the NNEDI3 weights the `nnedi` filter needs, then exit |
| `--nnedi-status` | — | **v7.9.8.3**: print the installed weights' version, size, sha256 and install time |
| `--nnedi-weights` | installed copy | **v7.9.8.3**: use this file (or unpacked directory) instead of the managed install |
| `--gh-proxy` / `--no-gh-proxy` | auto-detect | **v7.9.8.3**: route GitHub downloads through a front (`https://gh-proxy.com/` by default); `--no-gh-proxy` turns it off and remembers that. Since **v7.9.8.5** the program's own updates use it too |
| `--version` | — | Print just the version and exit |
| `--update-check` | — | **v7.9.8.5**: ask the release feed once and exit (**0** = current, **3** = newer exists, **2** = unreadable) |
| `--update` | — | **v7.9.8.5**: download and install the latest version in place, then exit (§7.18) |
| `--update-feed URL` | GitHub | **v7.9.8.5**: query this feed instead; it must be GitHub-release-shaped JSON (mirrors, intranet servers, local test services) |
| `--no-update-check` | on | **v7.9.8.5**: skip the check for this launch |
| `--x264-preset` | `medium` | libx264 only |
| `--x264-tune` / `--x264-profile` / `--x264-level` | empty | libx264 only; empty means automatic |
| `--hwaccel` | `off` | Hardware decoding: `off` / `auto` / any name from `ffmpeg -hwaccels` (cuda, vaapi, qsv, d3d11va …). **Off by default**; enabling really decodes a frame first to validate driver and GPU, falling back to software when it fails |
| `--no-cuda` | — | Legacy spelling of `--hwaccel off` |
| `--ffmpeg` / `--ffprobe` | found on PATH | Explicit executables |
| `--list-filters` | — | Print the deinterlace filters and scaling algorithms this FFmpeg has, then exit |
| `--quiet` | off | No progress banner and no result summary |
| `--no-cui` | off | With no arguments, fail instead of entering the menu (for scripts) |
| `--audio-mode` | `inline` | `inline` / `separate` / `auto` (§7) |
| `--recompress` | off | Run a "video site" transcode after encoding (§7.12) |
| `--recompress-args` | empty | The FFmpeg middle arguments for that pass; `-i` and the output are generated. Since **v7.9.8.3** repeatable: the Nth occurrence is the Nth pass |
| `--recompress-container` | from the codec | Container extension of the recompressed product |
| `--recompress-codec` | none | **v7.9.5.1**: second-pass video encoder (libx265 / libvpx-vp9 / libsvtav1 / flv …); the container follows it |
| `--recompress-quality` | codec default | **v7.9.5.1**: `-crf` (x264/x265/vp9/av1) or `-q:v` (MPEG family). **v7.9.8.4** repeatable, paired positionally with `--recompress-codec`; a single value applies to the whole chain |
| `--recompress-audio-codec` | recommended by the video codec | **v7.9.5.1**: `aac` / `libmp3lame` / `libopus` / `libvorbis` / `ac3` / `flac` / `copy`. Repeatable |
| `--recompress-audio-kbps` | codec default | **v7.9.5.1**: second-pass audio bitrate. Repeatable |
| `--recompress-video-kbps` | 300 | **v7.9.8.8**: video bitrate for encoders whose only knob is bitrate (`libopenh264`). Repeatable. Without it the command used to omit `-b:v` entirely and the encoder fell back to its own 2 Mbps |
| `--openh264-rc` | `bitrate` | **v7.9.8.8**: OpenH264 rate control `bitrate` / `quality` / `timestamp`. `buffer` and `off` are refused (the former ignores the bitrate — 3.1 Mbps when asked for 300k; the latter pins internal QP 26) |
| `--openh264-skip-frames` | off | **v7.9.8.8**: let OpenH264 drop frames to hit the target. Off means "target", on means "ceiling" (measured at 300k: 354 vs 302 kbps). Off by default because the adaptive ladder owns the frame rate |
| `--recompress-only` | off | **v7.9.5**: skip the live pass and recompress an existing file (§7.12) |
| `--no-recompress` | — | Turn the stage off (wins over the two above) |
| `--era ID` | none | **v7.6 / v7.9.7**: apply a preset whose id is `site/year` (`youku/2007`, `bilibili/2012`, `generic/1998`); old ids still work and print a rename note (§7.12) |
| `--tearing MODE` | `off` | **v7.9.9.10**: reproduce a dropped vertical sync — the top of the picture lags one frame. `horizontal` splits at the middle, `random` walks the split down the frame; `off` (default) leaves the picture alone. CUI parameter row `39.` |
| `--disc ID` | none | **v7.9.9.9**: a **shorthand** for `--era disc/<ID>`, i.e. `--era dvd` / `--era vcd` / `--era svcd` / `--era cd` (CD-i), see §7.19. The four targets are rows of the era table and pin codec, container, resolution, frame rate, bitrate, GOP and audio. The flag survives so a v7.9.9.8 script keeps working; new scripts use `--era` |
| `--era-modern` | off | **v7.9.5.1**: use libx264 + AAC in mp4 for the second pass instead of the site's period encoder |
| `--era-native` | on | **v7.9.5.1**: explicitly request the site's own encoder (the default; for self-documenting scripts) |
| `--era-aspect` | `source` | **v7.9.6.3**: preset aspect `source` / `4:3` / `16:9`, only sizes the encoder really supports (§7.12) |
| `--h264` | follows the preset | **v7.9.7**: `libx264` or `libopenh264`; presets from 2010 on default to OpenH264, switching both the live layer and the second pass (§7.12) |
| `--no-era` | — | **v7.9.9**: do not use an era preset and delete the saved one from the written `config.json`; the live pass uses explicit parameters only. CUI: "Era preset → `x.` none"; recompress menu `9.` deletes the whole chain |
| `--no-realtime` | — | **v7.9.9.1**: do not replay the live cadence; encode as fast as the machine allows (direct compression). The output is identical, it just does not wait; the run is forced to a single rung. CUI: "Parameters → `34.` live simulation". **v7.9.9.2 fixed an A/V desync** (65 slots used to carry only 3 real frames); one frame per slot now matches the realtime durations field by field |
| `--recompress-maxrate` / `--recompress-minrate` / `--recompress-bufsize` | — | **v7.9.9.2**: the VBV triple for the second pass, in kbps. **Must accompany `-b:v`** (`--recompress-video-kbps` or a hand-written `-b:v`), otherwise FFmpeg silently ignores them; `minrate > maxrate` is refused |
| `--recompress-extra` | — | **v7.9.9.2**: free-form FFmpeg args appended to the first recompress pass, e.g. `"-preset slow -profile:v high -ac 1 -b:a 64k"`. Changing the codec no longer wipes them |
| `--extra-encoder-args` | — | **v7.9.9.2**: free-form FFmpeg args appended to the **live encoder** command, e.g. `"-maxrate 300k -bufsize 600k"`; parsed with the same quoting as `--recompress-args`. Remembered and emitted by `--emit-args` |
| `--net-test PATH\|URL` | — | **v7.9.6.3**: measure the link and print the sharding verdict without encoding (§7.8.4) |
| `--iperf3 HOST[:PORT]` | — | **v7.9.6.3**: measure the link itself with iperf3 as a reference; needs a peer server |
| `--emit-args` / `--emit-args=FILE` | — | **v7.9.1**: print the settings as an equivalent command line and exit; `=FILE` reads them from a config file (§7.6) |
| `--save-config` | — | **v7.9.1**: write this run's final settings to the config file without encoding (§7.6) |
| `--help` / `-h` | — | Print usage and exit 0 |
| `--cpu-info` | — | Print the runtime-detected instruction sets (diagnostics) |
| `--parallel` | `1` | Split the range into N shards, encode in parallel, join (§7.5) |
| `--lang` | `en` | UI language: `en` or `zh` (§7.6) |
| `--completion` | — | Print a shell completion script and exit: `bash` / `zsh` / `fish` / `powershell` (§7.7) |

`--start-time` / `--end-time` accept `HH:MM:SS` and `HH:MM:SS.mmm`. The end must be
later than the start and must not exceed the source duration.

**v7.9.9.3**: `--recompress-maxrate/-minrate/-bufsize/-recompress-extra` and
`--extra-encoder-args` also have CUI rows, so a whole fragment no longer has to be
hand-written — recompress menu `b.`–`g.` (`-maxrate/-minrate/-bufsize/-preset/
-profile:v/-ac`) and parameter menu `36.`–`38.` (live-side VBV) plus `35.` (free-form
args). See §7.12.

### Exit codes

| Code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Failure while encoding (corrupt input, FFmpeg failure, timeout) |
| `2` | Command line / configuration / environment problem (bad value, ffmpeg missing, extension mismatch) |
| `3` | `--update-check` found a newer release (not an error) |
| `130` | Interrupted by Ctrl+C |

Ctrl+C is taken over rather than killing the process: the encode loop is cancelled and
the FFmpeg children are reaped in `finally`, **leaving no orphans**. The interactive
menu survives an interrupt — adjust and encode again; every encode takes a fresh
cancellation token, so a previous interrupt cannot leak into the next run.

---

## 4. Interactive menu (CUI)

Running with no arguments enters it. Main menu:

```
 1. Input file         pick the source (a directory lists video files to choose from)
 2. Output file        empty = auto-name <name>_live.<ext>; a directory = put it there
 3. Video encoder      choose from the legacy encoders detected in FFmpeg
 4. Parameters         parameter page (below)
 5. Rescan encoders    re-probe encoders (v7.9.5: also rebuilds the capability cache)
 6. Audio encoder      only those the current video encoder + container accept (§7.13)
 7. Show FFmpeg filters
 8. Show configuration
 9. Start encoding
10. Language           en / zh
11. Parallel shards    1 = off (§7.5)
12. FFmpeg toolchain   switch / download / remove builds (§7.10)
13. Playback after run what to play when the encode finishes (§7.11)
14. Era preset         site/year presets; n/e/d maintain custom ones, m toggles
                       native/modern rendering (§7.12)
15. Recompress         1 on/off, 2 argument fragment, 3 container, 4 recompress an
                       existing file, 5/6/7 second-pass video codec / quality or
                       bitrate / audio codec, a audio bitrate, b-g generic args (§7.12)
16. Streams            video/audio stream selection (§7.16)
17. Subtitles          burn / mux / off (§7.16)
18. Command line       print the current settings as an equivalent command (§7.6)
19. Config files       named profiles: load / save / delete (§7.6)
20. Picture format     pixel format / colour tags / dithering (§7.17)
21. Update             self-update: check / install / auto-check on start (§7.18)
 0. Exit
```

Items 6–17, 20 and 21 write to the settings file immediately; item 18 is read-only.
Picking an encoder does three things automatically: snaps unsupported frame rates to a
legal one, normalises the size to what the encoder supports, and chooses a default
audio encoder for the container. AMV and RoQ additionally force their audio parameters
(AMV: 22050 Hz IMA-ADPCM; RoQ: `roq_dpcm` at 22050 Hz mono/stereo) and say why.

**Parameter page**:

```
 1. limit            2. recover          3. window           4. base FPS
 5. FPS levels       6. CRF / q:v        7. threads          8. capture buffer
 9. size (+ aspect) 10. picture fit     11. scale algorithm 12. deinterlace
13. deint mode      14. field parity    15. deint marked only
16. audio bitrate   17. audio channels  18. audio sample rate
19. hardware decode 20. audio encoder
21. x264 preset     22. x264 tune       23. x264 profile    24. x264 level
                    (21–24 only for libx264)
25. segment start   26. segment end     27. level bitrates
28. container       29. crop anchor     30. bar colour      31. resolution presets
32. OpenH264 rc     33. OpenH264 skip frames   (only for libopenh264)
34. live simulation  35. extra encoder args
36. maxrate         37. minrate         38. bufsize
 0. Back
```

The page also shows the current **picture format** (pixel format + pinned colour tags +
dithering); change it through item 20. Every entry is "Enter keeps the current value";
15, 19 and 34 are toggles. `26` accepts `end` / `none` / `eof` for "to the end of the
source". 16/17/18 are auto-corrected to the audio encoder's capability (asking for
22050 Hz with ac3 becomes 32000 with an explanation; 16 kbps at 44.1 kHz for mp2
becomes 32), and 20 only lists encoders the current container accepts.

Choosing `9` prints the full configuration again and asks `Start encoding? [Y/n]`.

---

## 5. Encoder → container → default audio

**The output extension must match the container**, otherwise the program exits 2. The
table lists **defaults**; since v7.8 some encoders can write other containers
(`--container`, or just the extension). The full measured list is in
[`capability-matrix.en.md`](capability-matrix.en.md).

| Encoder | Default container | Extensions | Also writes (v7.9.5) | Default audio |
| --- | --- | --- | --- | --- |
| `rv10` `rv20` | rm | `.rm` `.rmvb` | — (size must be a multiple of 16) | `ac3` (since v7.9.8.12) |
| `mpeg1video` `mpeg2video` | mpeg | `.mpg` `.mpeg` `.mpe` `.vob` | avi, mp4, mov, mkv | `mp2` |
| `mpeg4` `libxvid` | avi | `.avi` | mp4, mov, mkv | `libmp3lame` |
| `mjpeg` `h263p` | avi | `.avi` | mov, mkv, mp4 (mjpeg only) | `libmp3lame` |
| `msrle` | avi | `.avi` | mov (**not** mkv/asf: palette lost) | `libmp3lame` |
| `wmv1` `wmv2` | asf | `.asf` `.wmv` | avi, mkv, mov | `wmav2` |
| `libx264` | mp4 | `.mp4` `.m4v` | avi, mkv, mov, flv | `aac` |
| `libtheora` | ogg | `.ogg` `.ogv` | mkv, avi | `libvorbis` |
| `flv` | flv | `.flv` | avi, mkv | `aac` |
| `flashsv` `flashsv2` | flv | `.flv` | avi/mkv (flashsv only) | `aac` |
| `qtrle` `rpza` `smc` `svq1` | mov | `.mov` `.qt` | avi/mkv (rpza, smc); avi (svq1) | `aac` |
| `amv` | amv | `.amv` | — | `adpcm_ima_amv` |
| `roqvideo` | roq | `.roq` | — | `roq_dpcm` |
| `h261` | h261 | `.h261` | avi, mkv | **no audio** |
| `h263` | h263 | `.h263` | avi, mkv | **no audio** |
| `a64multi` `a64multi5` | a64 | `.a64` | avi, mkv | **no audio** |

### Constraints that bite

* **RoQ**: width and height must be divisible by 16, otherwise
  `roqvideo requires width and height to be divisible by 16; got 250x190`. RoQ writes
  only `.roq`, and only integer frame rates.
* **`roq_dpcm`**: accepts 22050 Hz and one or two channels. Any other sample rate is
  refused: `roq_dpcm requires --audio-rate 22050 (or leave it unset for automatic
  correction)`.
* **AMV**: audio is fixed at 22050 Hz IMA-ADPCM and `22050 % fps` must divide evenly,
  so the usable frame rates are `5/10/14/15/18/21/25/30/50`.
* **MPEG-1**: only the eight real frame_rate_codes `23.976 / 24 / 25 / 29.97 / 30 / 50
  / 59.94 / 60`; anything else snaps to the nearest. The `5/10/12/15` in
  `ffmpeg -h encoder=mpeg1video` are libmpeg3's unofficial rates and the encoder
  refuses them (`MPEG-1/2 does not support 15/1 fps`), so the tool validates against
  the eight (v7.9.4). **MPEG-2** has a frame-rate extension and really encodes 5/10/12/
  15/75, so FFmpeg's declared list is used there.
* **RealMedia (`rv10`/`rv20`) and `roqvideo` accept only integer frame rates**
  (v7.9.5): their headers store integer fps. Values are rounded (23.976→24, 29.97→30,
  59.94→60) and the report says "integer only" for them.
* **`.rm` audio defaults back to `ac3`** (v7.9.8.12): `real_144` writes audio packets
  with a PTS of 0, so the audio stream duration becomes 0 and playback desyncs; the
  recompress pass then keeps only ~0.35 s of audio (measured on a 282 s source). It is
  still selectable with `--audio-encoder real_144` but not recommended: 8 kHz mono,
  fixed 14.4 kbps, `-b:a` ignored; 44100 Hz / 2 channels is snapped to 8000 Hz / mono
  with a note for each.
* **`rv10`/`rv20` sizes must be a multiple of 16** (v7.9.8.12): `rv10enc.c` refuses
  anything else. 320×180 becomes 320×176 with a note.
* **`.rm` is single-pass only** (v7.9.8.1): RealVideo has no fourcc in AVI (it reads
  back as `rawvideo`) and Matroska refuses it, and `--parallel` uses exactly those
  containers for shards. `--parallel` on `.rm` is refused and falls back. `--audio-mode
  separate` may work: a 3-frame sample is tried first (video to `.rm`, audio to `.ra`,
  joined with `-c copy` and decoded to verify).
* **Raw `.h261` / `.h263` / `.a64` store no frame rate** (v7.9.5): the bitstream has no
  timestamps, so a 15 fps encode reads back as 25 fps. The picture is fine and a note
  is printed; use `--container avi` or `.mkv` to make the rate travel with the file.
* **Palette encoders (`msrle`) are single-pass only**: their transport format is
  `pal8`, and the Matroska staging used to join shards drops the palette, producing a
  black picture. `--parallel` skips sharding for them and says so (v7.9.4). For the same
  reason they **cannot** write `.mkv` / `.asf`; only AVI and MOV keep the palette.
* **h261 / h263**: sizes are forced to standard rungs (h261: `176x144` or `352x288`).
* **h261 / h263 / a64**: the container carries no audio, so the output is video only.

If unsure which encoders this FFmpeg has, use `--list-filters`, or menu item 3 for the
detected list (including supported pixel formats).

### Audio numbers: sample rates and bitrates (v7.8.2)

Encoders and containers each impose hard audio limits; missing them fails halfway
through FFmpeg and leaves a broken file. v7.8.2 turned these boundaries into a table
(`Core/AudioCatalog.cs`), measured against the toolchain's FFmpeg master rather than
copied from specs. Two regimes:

* **Explicit values** (`--audio-rate` / `--audio-kbps`, or typed in the menu) — an
  error that lists the legal values; the run does not start with a bad number.
* **Values coming from an era preset** (`--era`) — the preset describes "that year",
  not a specific encoder, so they are corrected to the nearest legal value and the log
  says what changed.

| Constraint | Value |
| --- | --- |
| `mp2` / `mp2fixed` sample rate | from 16000 (8/11.025/12 kHz are refused) |
| `mp2` bitrate | MPEG-2 band 8–160 kbps at 16/22.05/24 kHz, MPEG-1 band 32–384 kbps at 32/44.1/48 kHz, and **only standard rates** (36 is refused; 144 is legal only at low rates) |
| `wmav1` / `wmav2` bitrate | minimum 24 kbps |
| `libvorbis` bitrate | the band scales with the rate: 8 kHz 8–40, 22050 16–80, 44.1/48 kHz 32–224 |
| `libopus` bitrate | at most 256 kbps |
| **FLV container + MP3** | only 11025 / 22050 / 44100 / 48000 (FFmpeg: "choose from (44100, 22050, 11025)") |
| **MP4 container + MP3** | below 16 kHz the muxer refuses it ("not standard"); AVI / MKV / MOV / ASF have no such limit |
| `ac3` / `eac3` sample rate | only 32 / 44.1 / 48 kHz |

Two easy traps: `dialup98` (16 kHz / 16 kbps) into FLV is corrected to 11025 Hz, and
into WMV (ASF) to 24 kbps — nearest values, not errors.

The **experimental** FFmpeg encoders named `vorbis` / `opus` (without the `lib` prefix)
need `-strict`; the tool does not pass it, so choosing them fails with a hint to use
`libvorbis` / `libopus`, and OGG's default list has offered only those two since v7.8.2.

---

## 6. Tuning adaptive bitrate control

1. The capture thread feeds frames into a bounded buffer (6 frames by default);
2. the slot loop emits one output slot per `1/fps` — a new frame if the buffer has one,
   otherwise it **repeats the previous frame**;
3. in parallel it tracks the output bytes over the last `window` seconds and converts
   them to kbps;
4. above `limit` it steps to the next rung of the ladder; below `recover`, and only
   after sustaining it, it steps back up.

Hence `Fresh frames` and `Repeated slots`: **repeats are a feature**, simulating the
picture freezing when bandwidth runs out.

| Goal | Adjustment |
| --- | --- |
| Freeze harder (worse "bad network" look) | lower `--limit` (e.g. 80), widen the ladder `--fps-levels 13,6,3,1` |
| Keep the picture smooth | raise `--limit` (e.g. 500), narrow the ladder `--fps-levels 13,11` |
| Statistics too jumpy, frame rate oscillates | raise `--window` (e.g. 4.0) |
| Source is already soft/simple | lower `--qv` (smaller = better quality, higher bitrate) |
| Disable throttling for a baseline | `--limit 99999` (`--recover` becomes limit×0.75) |

`--recover` must be greater than 0 and less than `--limit`, otherwise
`recover must be > 0 and < limit`.

### Per-rung bitrate thresholds (v7.7)

A rung can carry its own entry and exit threshold by adding colons:

| Form | Meaning |
| --- | --- |
| `12` | Rate only; thresholds derive from `--limit` / `--recover` (the pre-v7.6 behaviour) |
| `12:128` | Step down to 12 fps at **128 kbps**; step up uses `--recover` |
| `12:128:115` | Step down at 128 kbps, step back up only below 115 kbps |

```bash
# base 15 fps; drop to 12 fps at 128 kbps, to 5 fps at 140 kbps, back up at 115/130
LiveArchiveEncoder src.mp4 out.avi --encoder mpeg4 --fps 15 \
    --fps-levels 12:128:115,5:140:130
```

The CUI writes the same thing in **Parameters → 5 (FPS levels)**; **Parameters → 27
(level bitrates)** is a per-rung editor that first lists the derived values.

Rules and boundaries:

- **A rise threshold must stay below its drop threshold**, or the ladder oscillates;
  this is rejected at input.
- **A rung may not exceed the base rate**: `20` with a base of 15 is a typo and is
  refused in the menu, and exits 2 when `--fps-levels` is given explicitly. Without
  `--fps-levels` the check is skipped — the default ladder starts at 13 while mpeg2video
  snaps the base to 12, which is normal.
- Thresholds are remembered **by frame-rate value**, not by position, so reordering or
  deleting a rung cannot move a threshold to another rung; rungs that fall above a
  lowered base rate are dropped together with their thresholds.
- Omitting thresholds keeps the old behaviour: rung 1 `--limit`×1.00, rung 2 ×1.25,
  rung 3 ×1.60, with `--recover` for every rise.

---

## 7. Audio path: `--audio-mode`

| Mode | Behaviour |
| --- | --- |
| `inline` (default) | One FFmpeg process writes video and audio together, exactly like v6.3.3 |
| `separate` | Video is encoded alone into a staging file while a second FFmpeg encodes the audio, then the two are joined with `-c copy` |
| `auto` | Try `separate` with a 3-frame rehearsal; fall back to `inline` when the muxer or the join fails |

`separate` exists because some legacy muxers (RoQ, RealMedia) mishandle an inline audio
track. It is **slower and less faithful** (an extra remux, and timestamps drift), so it
is never the default. Its safety net is the rehearsal: 3 frames are encoded both ways,
the join is decoded, and only then is the real run allowed — otherwise it silently
falls back to a single process.

---

## 7.5 Speed: `--parallel`

Split the range into N shards, encode each with its own FFmpeg, then join. Each shard is
written to a staging container the encoder really supports (AVI or Matroska), so shards
never go into `.rm` or a palette-dropping container.

**Measured reality: sharding is currently a net loss.** A whitelisted encoder's marginal
cost is 0.06–0.26 s for a 20 s source, while every shard pays about 1.5 s of start-up, so
`--parallel 4` on a 20 s source is slower than one pass. The only encoder with a marginal
cost high enough to be worth splitting is **roqvideo (8.3 s)**, and RoQ cannot be joined.
The interface stays for the day the fixed cost comes down.

Where the fixed cost goes: one `ffmpeg` start is about **210 ms** (10 runs of
`ffmpeg -version` totalled 2.10 s on this machine). Measured on the same 2 s clip: 10
process starts before the change, 8 on the first run after and 3 on later ones — the
remaining three are pre-flight, decode and encode.

**Capability cache (v7.9.5)**: the answers to `ffmpeg -encoders`, `-filters`,
`-h filter=scale`, `-h encoder=NAME` and `-version` depend only on that binary, so they
are cached per binary under `<settings>/cache/`. Delete the directory to force a rescan
(`LAE_NO_CAPS_CACHE=1` does it for one run).

For the fastest possible file, use the AOT build or `--no-realtime`.

---

## 7.6 Settings persistence and languages

### Automatic saving

The CUI writes its settings to JSON **on exit, whenever a setting such as language or
shard count changes, and before every encode**, so the next launch restores them (the
banner says `Restored settings`). Location:
`%LOCALAPPDATA%\LiveArchiveEncoder\config.json`, or `~/.local/share/LiveArchiveEncoder/
config.json` on Linux (`LAE_CONFIG_DIR` overrides it).

Restoring validates: a missing input file is ignored, an encoder name the current FFmpeg
does not have is ignored (switching FFmpeg cannot restore a broken state), and the shard
count is clamped to 1..8. A corrupt file falls back to defaults instead of blocking
start-up. Writes are atomic (`.tmp` then move), so a crash cannot leave half a file.

The **CLI does not read the settings file** — script arguments are explicit, and having
an old config silently change them would be dangerous. But every CLI run **writes this
run's parameters back**, so opening the menu afterwards shows the last CLI settings.
Language is the exception: without `--lang` the stored language is kept, so one CLI run
does not reset a Chinese UI to English.

### Settings ↔ command line (v7.9.1)

Both flags **only convert, they never encode**:

```powershell
LiveArchiveEncoder.exe --emit-args                      # settings -> one command line
LiveArchiveEncoder.exe --emit-args=profile.json         # that file -> command line
LiveArchiveEncoder.exe src.mp4 --emit-args --encoder rv20
LiveArchiveEncoder.exe --save-config                    # this run's params -> settings
```

### See the command line in the menu (v7.9.3, item 18)

Item 18 prints one line generated by the same emitter as `--emit-args`, and that line is
also printed automatically **just before the confirmation prompt** of a real encode: you
see the settings summary and the equivalent command at once, press Enter to accept or
copy it for your notes.

### Named profiles (v7.9.3, item 19)

`config.json` is "the current one"; `configs/*.json` are switchable **named profiles**.
Item 19 lists them (by name, with modification times), then offers load / save / delete.
Loading writes the result back to the active settings, like any other menu item.

### Language

`--lang en|zh`, or CUI item 10. `en` / `zh` / `zh-CN` / `中文` are all accepted.
Priority: **`--lang` > stored setting > English default**.

Translated: the main menu, up to 33 parameter rows, the encoder picker, `Show
configuration`, the result summary, progress-banner labels, SMB notes and the subtitle
menus. **FFmpeg's own messages and the `ERROR:` prefix are never translated** — those
are meant to be searched, and they are what the Python comparison looks at.

---

## 7.7 Shell completion

The program is its own completion source: encoder names come from the live FFmpeg, not
from a hard-coded list.

```bash
LiveArchiveEncoder --completion bash        > /etc/bash_completion.d/livearchiveencoder
LiveArchiveEncoder --completion zsh         > ~/.zfunc/_LiveArchiveEncoder
LiveArchiveEncoder --completion fish        > ~/.config/fish/completions/LiveArchiveEncoder.fish
LiveArchiveEncoder --completion powershell  | Out-File -Encoding utf8 $PROFILE.LiveArchiveEncoder.ps1
```

Options, values, encoder names, pixel formats, containers, era ids and file paths are
completed. Fish registers every option name first and then its values, so a
half-typed `--net-test` does not swallow the next token.

---

## 7.8 Remote sources (SMB shares / HTTP links)

An input may be a UNC path (`\\host\share\…`), an `smb://` URL, a mapped Windows drive
or a mounted CIFS path, an `http(s)://` link, or a local file. The tool only detects the
shape and adds the right resilience options; the OS does the mounting.

| Kind | Handling |
| --- | --- |
| Local file / drive | No network options at all |
| Mapped network drive | Detected as remote even though it looks local (v7.9.1) |
| UNC / `smb://` / CIFS mount | Remote; reads get `-rw_timeout 30000000` |
| `http(s)://` | Remote; the FFmpeg build needs the `http` protocol |

**Write side**: no `-stimeout` or `-reconnect*` is ever emitted; the only write-side
option is `-rw_timeout`, and the writability probe leaves no file behind.

### 7.8.4 Why v7.9.6.3 stopped forcing a single pass

N shards each opening the same shared connection is the most fragile shape in this
project — a slow link makes the picture collapse mid-file. Since v7.9.6.3 `NetworkProbe`
measures the link and decides: a link that cannot sustain the shards falls back to a
single pass and says so. Two ways to look at it without encoding:

```powershell
LiveArchiveEncoder.exe --net-test \\nas\archive\record.mp4
LiveArchiveEncoder.exe --net-test https://archive.org/download/.../clip.mp4
LiveArchiveEncoder.exe --iperf3 192.168.1.10        # iperf3 -c HOST -t 3 -J as reference
```

---

## 7.10 FFmpeg toolchain (switch / download / remove)

| Option | Meaning |
| --- | --- |
| `--ffmpeg-list` | List the sources and which ones are installed |
| `--ffmpeg-source ID` | Use that source; also accepts `path` (never resolves to a managed build) |
| `--ffmpeg-download [ID]` | Download and install a source (no value = `btbn`), then exit |
| `--ffmpeg-delete ID` | Remove an installed managed build |
| `--ffmpeg-archive FILE` | Install from a local archive instead of the network |
| `--ffmpeg-env SHELL` | Print the shell snippet that puts the managed build on `PATH` |
| `--proxy URL` | Proxy for downloads; reused from the config when omitted |
| `--gh-proxy` / `--no-gh-proxy` | Route GitHub downloads through a front (default `https://gh-proxy.com/`) |

Six sources, filtered per platform: `btbn`, `nonfree`, `ytdlp`, `autobuild` (Windows
only), `gyan` (Windows only, manual-friendly) and `avbuild` (manual download because
SourceForge intercepts automation). Daily / rolling assets are preferred; a fixed
stable asset is only a fallback. Both platforms use the official `ip7z/7zip` build, so
`.zip` / `.7z` / `.rar` (including Rar5) all extract without a system p7zip.

Lookup order: `--ffmpeg` / `--ffprobe` → managed build → `PATH` → the first-run installer.
After installing, the managed `bin` is published to the shell `PATH` (v7.9.8.11).

`--nnedi-update` / `--nnedi-status` / `--nnedi-weights` manage the NNEDI3 weights the
`nnedi` deinterlacer needs (GitHub only, about 13.5 MB; honours `--gh-proxy`).

Details and measured asset facts: [`toolchain-builds.en.md`](toolchain-builds.en.md).

---

## 7.11 Playing the result

| `--play` | Behaviour |
| --- | --- |
| `off` (default) | Do nothing |
| `ffplay` | Play with `ffplay -autoexit`, closing on exit |
| `external` | Hand the file to the OS association (`start` on Windows, `xdg-open` on Linux) |

`--player PATH` overrides the executable. CUI item 13 sets the default for future runs.
A missing player or a missing file is reported, never a crash. Players the tool starts
are tracked so the next encode can close exactly that file's player if it holds the
output (§10, "output is in use").

---

## 7.12 Post-processing: recompress (v7.6; watermark removed in v7.9.1)

The idea: a live stream from 2007 was re-encoded by the site into its own delivery
format. This stage replays that second encode. It is **off by default** and changes
nothing unless enabled.

### CLI

```powershell
# one extra pass, site-style
LiveArchiveEncoder.exe in.mp4 out.flv --encoder flv --recompress \
    --recompress-codec libx264 --recompress-quality 24 --recompress-container mp4

# several passes; the Nth --recompress-args is the Nth pass
LiveArchiveEncoder.exe in.mp4 --recompress \
    --recompress-args "-c:v libx264 -crf 26" --recompress-args "-c:v libx264 -crf 30"

# generic parameters (VBV triple must accompany -b:v)
LiveArchiveEncoder.exe in.mp4 --recompress --recompress-codec libx264 \
    --recompress-video-kbps 250 --recompress-maxrate 300 --recompress-minrate 100 \
    --recompress-bufsize 600 --recompress-extra "-preset slow -ac 1 -b:a 64k"

# recompress an existing file, no live pass
LiveArchiveEncoder.exe clip.avi --recompress-only --recompress-codec libx264
```

`--recompress-args` must contain the middle only: `-i` and the output path are
generated. A bare path anywhere in the fragment is rejected—otherwise FFmpeg treats it as
an extra output and can overwrite an unrelated file.

### CUI (menu 15)

```
 1. Enable recompress            on / off
 2. FFmpeg args                  the first pass's fragment
 3. Output container
 4. Recompress one existing file
 5. Video encoder                rewrites -c:v
 6. Quality                      rewrites -crf / -q:v
 7. Audio encoder                rewrites -c:a
 a. Audio bitrate                -b:a
 b. VBV maxrate                  -maxrate
 c. VBV minrate                  -minrate
 d. VBV bufsize                  -bufsize
 e. Preset                       -preset
 f. Profile:v                    -profile:v
 g. Audio channels               -ac
 h. Video bitrate (-b:v)         -b:v (v7.9.9.7)
 8. Further passes (chain)       2nd pass onwards (NOT the CLI's --recompress-extra)
 9. Disable recompress           delete the whole chain
 0. Back
```

`b`–`g` and parameter menu `36.`–`38.` (live-side VBV) are new in v7.9.9.3. They write
into the **same fragment** as the CLI options (second pass into the fragment of item 2,
live side into `ExtraEncoderArgs`), so `--emit-args`, changing the encoder and changing
the quality all keep them. `none` deletes a flag; an empty answer keeps it.

**For the recompress pass, VBV needs `-b:v`**: use row `h.`,
`--recompress-video-kbps`, or a `-b:v` inside the fragment, otherwise FFmpeg silently
ignores the triple. The CUI checks this before writing back and refuses
`minrate > maxrate`. Rows `h.` and `6` are mutually exclusive: setting a bitrate removes
`-crf`/`-q:v`, and editing quality removes `-b:v`.

**v7.9.9.7**: FFmpeg's MPEG-video-family encoders (flv1, h261, …) additionally require
`-maxrate` and `-bufsize` **as a pair**; giving only one makes `avcodec_open2` return
EINVAL (-22) mid-encode. On the live side (parameter menu 35/36/37/38 and
`--extra-encoder-args`) the missing half is now **completed automatically**: a missing
buffer becomes two seconds of maxrate, a missing maxrate becomes half the buffer. The
3-frame preflight and the real encode share the same normalisation, so these
combinations no longer die halfway through. libx264 / mpeg4 / libopenh264 do accept a
half-pair, so valid configurations are not rejected; `minrate > maxrate` is still
refused at exit 2 before encoding. Legacy encoders use `-q:v`, not a generated
`-b:v` — the earlier claim that the live command always emits `-b:v` was wrong.

Item 8 lists the existing passes 2..N in one prompt: **a fragment appends a pass**, **a
number deletes that pass**, **`-` clears them all**. The container of a new pass is
derived from its encoder (`-c:v flv` → `.flv`), the same rule as the command line.

### Era presets

`--era site/year` (menu 14) fills size, frame rate, bitrate ceiling, quality, window,
buffer depth and deinterlacing for that site in that year, and turns the recompress
stage's second pass into the site's own encoder. `--era-modern` swaps the second pass to
libx264 + AAC in mp4; `--era-native` is the default; `--era-aspect` changes the frame.
`--h264 libx264|libopenh264` switches both the live and the second pass (presets from
2010 on default to OpenH264 because x264's tuning looks too clean for the period).
`--no-era` ignores the preset for a run and deletes the saved one.

The numbers, their evidence grades and the corrections are in
[`era-presets.en.md`](era-presets.en.md).

### `.part` and the report

Every product is written as `name.part.ext` and only renamed after validation, so an
interrupted run leaves no half file and a `.part` during a run is not a failure. If the
final name is held by another program, the file is saved as `name (2).ext`, the report
names **that** file, and later passes of a chain read it. Since **v7.9.9.2** the pre-flight
also refuses a locked target before the encode starts and removes stale `.part`
siblings from earlier crashed runs.

---

## 7.13 Audio encoders: scan, match, validate (v7.8)

Menu item 6 lists **only the audio encoders the current video encoder and container can
actually hold** — `aac` in avi, `wmav2` in mp4, `libvorbis` in asf and similar dead ends
are filtered out before you see them. The same rule guards the CLI: an explicit
`--audio-encoder` the container cannot take is an error (exit 2), not a surprise at
muxing time.

Every audio number is validated against the measured boundaries in §5 (sample rate,
bitrate, channel count). Explicit values are hard errors; values arriving from an era
preset are corrected to the nearest legal one and logged.

---

## 7.14 Container switching (v7.8; rewritten from measurements in v7.9.5)

`--container NAME` (or just the output extension) selects the container. Only pairs that
passed all three checks — muxer accepts, ffprobe reads the codec back under its own name,
and a frame is still visible — are offered. `ffmpeg -h muxer=` declaring support is not
enough: `rv20 → avi` silently stores `rawvideo`, `mpeg4 → rm` writes an `rv20` tag over
MPEG-4 payload, and `msrle → matroska` produces an entirely black picture.

The full measured table is in [`capability-matrix.en.md`](capability-matrix.en.md).
Constraints that follow from it:

- palette encoders (`msrle`) never go into Matroska or ASF;
- `.rm` is single-pass and 16-divisible;
- raw `.h261` / `.h263` / `.a64` carry no timestamps.

---

## 7.15 Picture ratio: 16:9 and three fit modes (v7.8)

| `--fit` | Behaviour |
| --- | --- |
| `crop` (default) | Fill the frame and cut the overflow; `--crop-anchor` chooses which side survives |
| `fit` | Keep the whole picture and pad the remainder with `--pad-color` |
| `stretch` | Distort the picture to the frame (`--no-crop` is the legacy spelling) |

16:9 presets are ordinary width/height pairs (`640x360`, `1280x720`, …); `--width` /
`--height` take anything, and the encoder then snaps to a size it supports (h261 to
176x144 / 352x288, rv10/rv20 to a multiple of 16, RoQ to a multiple of 16).

---

## 7.16 Multiple streams and subtitles (v7.9)

```powershell
LiveArchiveEncoder.exe in.mkv --list-streams
LiveArchiveEncoder.exe in.mkv out.mkv --video-stream 0 --audio-stream 2
LiveArchiveEncoder.exe in.mkv out.mp4 --audio-stream none
```

`--video-stream` / `--audio-stream` take the absolute indices ffprobe prints; `none`
drops the audio. With a single stream of each, nothing needs to be said.

### Subtitles

| Mode | Behaviour |
| --- | --- |
| `burn` (default) | Render into the picture through libass |
| `copy` | Mux as a subtitle track (`--subtitle-codec` picks the encoder) |
| `off` | Drop subtitles entirely |

Sources: `--subtitle file` (repeatable, e.g. `clip.sc.ass` + `clip.tc.ass`), or
`--subtitle-stream N` for an embedded track, or `--auto-subtitle` to find sibling files
named after the source.

**Fonts come only from the tool's own `fonts` directory** (next to `ffmpeg/`), which is
why burning Chinese subtitles without a Chinese font shows tofu boxes. `--font-import`
copies a file or a directory in, `--font-pack` unpacks `.zip/.7z/.rar` packs (through
7-Zip) and imports only the families the subtitle actually names,
`--list-subtitle-fonts` reports what a subtitle asks for and whether this machine has it,
and `--subtitle-font` forces a family. `--subtitle-aspect` decides the layout aspect
(`source` / `4:3` / `16:9`) — relevant when the ASS was authored for a 4:3 canvas.

---

## 7.17 Output pixel format and colour (v7.9.6)

`--pix-fmt` accepts 86 formats, each with a frame length measured directly
(`Core/PixelFormatCatalog.cs`, pinned by `PixelFormatCatalogTests`). A request is either
honoured or rejected with exit 2 — there is no silent downgrade. `--list-pix-fmts` shows
all of them, marking the ones the current encoder declares.

Colour tags: `--color-range`, `--colorspace`, `--color-primaries`, `--color-trc`, all
"follow the source" unless given. `--dither` applies only when the target format really
loses colour depth.

Two measured surprises worth knowing:

- `libx264 -pix_fmt gray` produces `yuvj420p` (the encoder accepts gray as input and
  writes 4:2:0); the tool reads the result back and prints a note.
- `libx264` colour tags only reach the bitstream when passed through `-x264-params`; the
  tool does that automatically and verifies with ffprobe afterwards.

`pal8`-style formats can only go into AVI and MOV (Matroska and ASF drop the palette and
the picture goes black).

---

## 7.18 Self-update (v7.9.8.5)

| Command | Effect |
| --- | --- |
| `--update-check` | Ask once: exit **0** (current), **3** (newer exists), **2** (unreadable) |
| `--update` | Download and install the latest release in place |
| `--update-feed URL` | Query a different GitHub-shaped release feed |
| `--no-update-check` | Skip the automatic check for this launch |

The startup check has a 3-second budget and a 24-hour cache, and fails silently.

The asset is chosen by runtime flavour (`-self-contained`, `-aot`, or no suffix), so a
framework-dependent install never receives a self-contained payload. The replacement
walks **every file in the payload recursively** (since v7.9.9.3 — the old top-level-only
walk dropped `runtimes/` and satellite resources), backs up what it overwrites, and
rolls back on failure.

`LAE_UPDATE_TOKEN` (environment only, never written to disk) is used when the release
page answers 401/403/404. Both `--proxy` and `--gh-proxy` apply, exactly as for
toolchain downloads.

---

## 7.19 Disc images and disc targets (v7.9.9.8)

**ISO input**: `*.iso` / `*.img` can be used directly, but only two layouts are
accepted — **DVD-Video** (`VIDEO_TS/` plus `*.IFO`, read straight by FFmpeg's
`dvdvideo` demuxer, which follows the IFO across VOB parts) and **Video CD**
(`MPEGAV/*.DAT`, unpacked to a temp directory with 7-Zip). Any other image (a data
disc, a Blu-ray) is refused with exit 2. The product keeps the ISO's name.

**Disc targets**: `--disc dvd|vcd|svcd|cd` (CUI parameter row `39.`) pins:

| Target | Codec | PAL / NTSC | Video | Audio |
| --- | --- | --- | --- | --- |
| `dvd` | mpeg2video | 720×576@25 / 720×480@30 | 6000k, maxrate 8000, bufsize 1835k, GOP 15 | ac3 384k 48 kHz 2ch |
| `vcd` | mpeg1video | 352×288@25 / 352×240@30 | 1150k CBR, bufsize 327k, GOP 15 | mp2 224k 44.1 kHz 2ch |
| `svcd` | mpeg2video | 480×576@25 / 480×480@30 | 2520k, bufsize 917k, GOP 15 | mp2 224k 44.1 kHz 2ch |
| `cd` | mpeg1video | as VCD | as VCD | as VCD |

* The standard follows `--fps` (29.97/30/23.976 = NTSC, 25 = PAL); without it the
  disc defaults to PAL, so a 720×480 NTSC image needs `--fps 29.97`.
* The disc is a **hard constraint**: it overrides `--encoder`, `--container`, size,
  frame rate, bitrate and audio, and prints what it changed. `-maxrate/-bufsize/-g`
  go through `ExtraEncoderArgs`, the same path the VBV pairing uses.
* A multi-title DVD exposes only the **first** title; extract another VOB with an
  external tool if you need it.

```powershell
# re-author a DVD to DVD-Video (first minute)
LiveArchiveEncoder.exe D:\discs\movie.iso out.mpg --disc dvd --end-time 00:01:00

# an NTSC disc
LiveArchiveEncoder.exe D:\discs\ntsc.iso out.mpg --disc dvd --fps 29.97

# rebuild a VCD from its DAT
LiveArchiveEncoder.exe MPEGAV\AVSEQ01.DAT out.mpg --disc vcd
```

## 8. Useful command lines

```powershell
# a 2007 Youku-style clip: live layer + the site's own second pass
LiveArchiveEncoder.exe in.mp4 out.flv --era youku/2007 --recompress

# a "remaster": the same preset but a modern second pass
LiveArchiveEncoder.exe in.mp4 out.mp4 --era bilibili/2012 --era-modern --recompress

# burn external subtitles with a Chinese font already imported
LiveArchiveEncoder.exe in.mkv out.mp4 --encoder libx264 --subtitle clip.sc.ass

# fixed CBR-ish delivery for a strict player
LiveArchiveEncoder.exe in.mp4 out.mp4 --encoder libx264 --recompress \
    --recompress-codec libx264 --recompress-video-kbps 800 \
    --recompress-maxrate 900 --recompress-bufsize 1800

# no live waiting, same output
LiveArchiveEncoder.exe in.mp4 out.avi --encoder mpeg4 --no-realtime

# convert a menu setup into a script
LiveArchiveEncoder.exe --emit-args > run.ps1
```

---

## 9. Reading the output

```
Slot  42/65  fresh=42 repeated=0  motion=13fps  video=197 kbps  audio=32 kbps
```

- `fresh` — slots filled with a newly decoded frame; `repeated` — slots that repeated the
  previous one. Repeats mean the bitrate ceiling forced the ladder down; they are the
  feature, not a fault (§6).
- `motion` — the frame rate the controller currently emits.
- `video` / `audio` — rolling bitrate over the statistics window.

The final summary lists the output path, size, duration, stream counts, the fresh /
repeated split and, when a recompress ran, where each pass landed. `--quiet` suppresses
both the banner and the summary.

---

## 10. Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| `Cannot find ffmpeg` | Not on `PATH` and nothing installed | Run once interactively and accept the installer, or `--ffmpeg-download`, or `--ffmpeg PATH` |
| `encoder 'xxx' is not available` | The FFmpeg build lacks it | Switch to a full GPL build (`--ffmpeg-source btbn`), or check `--list-filters` |
| `the output extension does not match the container` | `.mp4` for an `rv20` encode, etc. | Use the encoder's default extension or `--container` |
| Encoding looks frozen | Realtime pacing plus a low `--limit` | Raise `--limit`, or use `--no-realtime` |
| `recover must be > 0 and < limit` | Contradictory throttling values | Lower `--recover` below `--limit` |
| `roqvideo requires width and height to be divisible by 16` | Non-conforming size | Pick a 16-divisible size |
| Audio-only or wrong-duration file | A legacy muxer mishandling inline audio | `inline` avoids it; with `--audio-mode separate` the 3-frame rehearsal catches it and falls back |
| Chinese subtitles are **tofu boxes** | The font family the ASS names is not in `fonts` | Put a CJK font in `%LOCALAPPDATA%\LiveArchiveEncoder\fonts\` (Linux `~/.local/share/...`), or `--font-import`; `LAE_FONT_DIR` overrides |
| `recompress` says `'-i' is not allowed here` | Input or output written into the fragment | The fragment is the middle only; `-i` and the output are generated |
| `--parallel 4` "does nothing" | The source is on a network drive | Deliberate single-pass fallback (§7.8.4); copy the source locally |
| `pal8 frames cannot be stored in 'matroska'` | Palette format in a palette-dropping container | Use `.avi` / `.mov`, or `--pix-fmt yuv420p` |
| `encoder 'xxx' cannot take yuv420p10le` | The encoder does not declare that format | `--list-pix-fmts --encoder xxx` |
| Requested `gray`, file says `yuvj420p` | The encoder accepts gray as input, writes 4:2:0 | A note, not an error; use a native gray encoder (`cinepak`, `qtrle`) for real grayscale |
| Picture gets blurry mid-recording on a remote source | Network jitter made the controller step down | An honest reaction to the link; use a stable link or a local source |
| `ERROR: output is in use by another program` | A player or AV scanner holds the target | Close it, or pick another name; players this tool started are closed automatically |

### Run log (v7.9.9 / v7.9.9.1)

Logging defaults to **`error`**: an ordinary run produces no log file at all. When
something fails, the log carries the last 200 context events (`ctx[...]` lines) before the
error — attach those when reporting a bug.

| Variable | Effect |
| --- | --- |
| `LAE_LOG_DIR` | Log directory, default `<data root>/logs` (`lae.log`, size-rolled 16 MB × 7) |
| `LAE_LOG_LEVEL=verbose` | Full trace, including every console line (`ui.stdout` / `ui.stderr`) |
| `LAE_LOG_LEVEL=information` | Structured events plus console output |
| `LAE_LOG_LEVEL=warning` | Warnings and errors only |
| `LAE_LOG=0` | Disable logging |
| `LAE_LOG_MIRROR=0` | Do not mirror console output |

Line format `<time> [level] <RunId>/<OperationId> <message>`. Reading order:
`run.start` → `ffmpeg.discovered` → `child.spawn` / `child.exit` (full argv, exit code,
elapsed, last 400 chars of stderr) → `output.promoted` → `process.exit`. Start-up prints
the **actual log path**, so there is nothing to guess.

### Path memory and file locks (v7.9.9.2)

* **Recent directories**: every run records the input's directory and the output's
  directory in `RecentDirectories` (at most 8, newest first; missing, relative and
  duplicate entries are dropped on read). CUI item 2 lists them as `r1.`, `r2.`… — type
  `rN` to put the product there.
* **Directory completion**: an `--output` (or CUI output) that is an **existing directory**
  names the product `<source stem>_live.<ext>` inside it. A plain file path behaves as
  before.
* **Pre-flight lock check**: the target is probed with `FileShare.None`, and stale
  `<name>.<tag>.part<ext>` siblings from earlier crashed runs are removed. All four
  encode paths do this, so a long run cannot discover the problem at the end.
* **When it is locked**: players **this program started** are closed first, then the probe
  is retried; if it is still held, the run stops with a clear message. Programs the user
  opened elsewhere are never killed.
* **Locked mid-run**: after four retries the file is saved as `name (2).ext`, and the
  report and every later pass follow that real path. On Windows a sharing violation and
  an ACL refusal raise the same exception type; since v7.9.9.2 both are retried, so the
  alternate save can no longer be bypassed.

---

## 11. Relationship to the Python version (v6.3.3)

> **Since v7.6** the promise is no longer "identical to v6.3.3" — the C# version grows its
> own features (the post-processing pipeline in §7.12). All original behaviour is kept and
> every new feature is opt-in, so with no flags the two are still equivalent; the media
> matrix moved from "equivalence criterion" to "regression check".

C# v7 is a **behaviour-preserving port** of v6.3.3: the FFmpeg command line (including
where `-threads` lands), the adaptive state machine's thresholds and hysteresis, the frame
selection rule, the rolling bitrate algorithm, every default and every table were copied
item by item. Verified: 12 encoder/parameter cases match on structural fields, 11 error
paths match on exit code and message, and frame counts match exactly with the throttling
disabled.

So the same command produces equivalent results in both implementations and the two can be
compared side by side. C# adds `--parallel` (§7.5), settings persistence and languages
(§7.6), completion (§7.7), remote sources (§7.8), toolchain management (§7.10), playback
(§7.11), recompress and era presets (§7.12), audio/container/aspect/subtitle/pixel-format
work (§7.13–§7.17) and self-update (§7.18) — all of them off by default or purely
additive.

The v6.3.3 baseline's six known defects (AMV produced 0 bytes, CUDA on by default, two
wrong SMB options, audio limits checked only for sample rate, fonts never resolved,
subtitle filters never detected) are listed with their fix releases in
[`CHANGELOG-archive.en.md`](CHANGELOG-archive.en.md) — those are **deliberate deviations**,
not differences.

---

## 12. Self-test

| Layer | Tool | Covers | Command |
| --- | --- | --- | --- |
| Unit | **TUnit** (`tests/LiveArchiveEncoder.Tests`) | pure logic: SMB options, CLI parsing, config round-trips, localisation completeness, toolchain metadata | `dotnet test --solution LiveArchiveEncoder.V7.slnx` |
| Equivalence | **pytest** (`tests/e2e/test_*.py`) | product equivalence against v6.3.3, error paths, encoder sweeps, CUI smoke, duration drift | `python tools/e2e.py` |

Neither needs a special environment for the unit layer; the e2e suite needs a full FFmpeg
and uses a private `LAE_CONFIG_DIR`, so it never touches your real settings.

```powershell
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release
dotnet test --solution LiveArchiveEncoder.V7.slnx -c Release -p:LaeTfm=net11.0
python tools\e2e.py                 # two phases
python tools\e2e.py --phase serial  # only the exclusive 25
python tools\e2e.py --workers 4
CS_EXE=publish\aot\LiveArchiveEncoder.exe pytest   # test the AOT product instead
```

The suite is split by marker because 25 cases cannot run in parallel:

| marker | Who | Why not parallel |
| --- | --- | --- |
| `timing` | `compare_matrix`, `ab_duration` | They assert **which frames** the adaptive controller chose; a busy machine changes the choice — parallel workers do not slow it down, they make it wrong |
| `shared_config` | `cui_smoke` | They exercise the real settings file, and every CLI run writes it on exit |

`tools/e2e.py` runs those modules alone first, then hands the rest to xdist, one worker per
case. The e2e suite is also the acceptance evidence for the port: see
[`test-report-v7.en.md`](test-report-v7.en.md).

For the full list of what each unit-test class pins, the Chinese edition keeps the
class-by-class breakdown (§12); the summary above is the contract.

