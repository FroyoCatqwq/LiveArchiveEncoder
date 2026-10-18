# Live Archive Encoder v7 — migration test report

> Archived report. Paths quoted below are from before the repo was reorganised:
> the Python harnesses now live in `tests/e2e/`, the build helper in `tools/`,
> and the v6.3.3 baseline in `reference/`. The numbers are unaffected.

Scope: verify that the .NET 10 / C# 14 port is functionally equivalent to
`live_archive_encoder.py` v6.3.3, and measure what the port actually changes in
controller cost.

Environment: Windows x64, .NET 10 SDK 10.0.402, Python 3.13.14,
FFmpeg `N-127579-g5cb9fcd211-20261016` (BtbN, GPL).
Fixture: `tests/media/test_src.mp4` — 320x240, h264 + aac, 20 s, generated with lavfi.

---

## 1. Build status

```
dotnet build src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release
-> 0 warnings, 0 errors
```

The delivered skeleton did not compile. It had no ffprobe probing, no encoder
capability probing, no filter/scaler detection, no preflight, no timecode
parsing, no audio path, no CLI parameters, and a frame buffer that returned
`ArrayPool` buffers while the controller still held a reference to them
(use-after-return). It was replaced with a complete port; see README.md.

---

## 2. Functional equivalence — 12 encoder / parameter cases

`tests/compare_matrix.py` runs both implementations on the same material with
identical parameters and compares the produced media with ffprobe. Structural
fields must match exactly: codec, width, height, pix_fmt, r_frame_rate,
duration, audio codec, stream count, container. 5 s segment per case.

| case | encoder → container | result | codec | size | fps | dur | audio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| rv20_default | rv20 → rm | PASS | rv20 | 256x192 | 13/1 | 5.0 | ac3 |
| mpeg4_avi | mpeg4 → avi | PASS | mpeg4 | 256x192 | 13/1 | 5.077 | mp3 |
| libx264_mp4 | libx264 → mp4 | PASS | h264 | 256x192 | 13/1 | 5.0 | aac |
| mpeg1video_mpg | mpeg1video → mpeg | PASS | mpeg1video | 256x192 | 48000/1001 | 4.942 | mp2 |
| wmv2_asf | wmv2 → asf | PASS | wmv2 | 256x192 | 13/1 | 5.046 | wmav2 |
| mjpeg_avi | mjpeg → avi | PASS | mjpeg | 256x192 | 13/1 | 5.077 | mp3 |
| roqvideo_roq | roqvideo → roq | PASS | roq | 256x192 | 13/1 | — | roq_dpcm |
| h261_special | h261 → h261 | PASS | h261 | 176x144 | 30000/1001 | — | none |
| rv20_timerange | rv20, 00:00:02–00:00:05 | PASS | rv20 | 256x192 | 13/1 | 3.0 | ac3 |
| mpeg4_nocrop_bicubic | mpeg4, 320x240, no-crop | PASS | mpeg4 | 320x240 | 13/1 | 5.077 | mp3 |
| mpeg4_yadif | mpeg4 + yadif | PASS | mpeg4 | 256x192 | 13/1 | 5.077 | mp3 |
| mpeg4_lowfps | mpeg4 @ 8 fps | PASS | mpeg4 | 256x192 | 8/1 | 5.125 | mp3 |

**12 / 12 pass.** Notable behaviours reproduced exactly:

* h261 snapped to a legal 176x144 frame size (both sides).
* mpeg1video snapped to 48000/1001 fps (both sides).
* roqvideo selected `roq_dpcm` at 22050 Hz (both sides).
* h261 produced a video-only file (container is in `NO_AUDIO_FORMATS`).

### Frame selection

9 of 12 cases produced **identical** fresh/repeated counts. Example, libx264:
`fresh=61 repeat=4` on both sides; mjpeg: `fresh=24 repeat=41` on both sides.

The remaining 3 drifted by 1–9 slots out of 65. This is inherent to the
adaptive controller, not a porting defect — it reacts to measured output bytes,
and the measured bytes depend on frame content and wall-clock jitter. Measured
directly, v6.3.3 run three times over the same 10 s segment:

```
PY run1: fresh=37   PY run2: fresh=67   PY run3: fresh=64
CS run1: fresh=64   CS run2: fresh=39   CS run3: fresh=64
```

To prove the frame pipeline itself is equivalent, the controller was run with
`--limit 99999` (throttling disabled, so the state machine never switches):

```
PY run1: fresh=64 repeated=1      PY run2: fresh=64 repeated=1
CS run1: fresh=64 repeated=1      CS run2: fresh=64 repeated=1
```

Identical. With the adaptive path removed, the two implementations agree
slot-for-slot.

---

## 3. Real material, 60 s segment

Source: `HK-MCT1-PART-encoded.avi` (320x240, h264 + aac, 377 s), encoder
`mpeg4` → AVI, `--end-time 00:01:00`.

| | Python v6.3.3 | C# v7 |
| --- | --- | --- |
| wall time | 62.018 s | 62.025 s |
| duration | 60.00 s | 60.00 s |
| output | mpeg4 256x192 13/1, 60.076923 s | identical |
| audio | mp3, 60.084 s | identical |
| video average | 148.8 kbps | 148.6 kbps |
| total average | 180.8 kbps | 180.6 kbps |
| fresh / repeated | 653 / 127 | 642 / 138 |
| total slots | 780 | 780 |
| file size | 1 355 718 B | 1 354 470 B |

Same slot count (780), same stream layout, same duration, average bitrate within
0.13 %, file size within 0.09 %. The 11-frame fresh/repeated difference is the
same adaptive-controller jitter described above, over 13x more slots.

---

## 4. Error-path equivalence — 11 cases

`tests/error_paths.py`. Both sides were required to produce the same exit code
and a diagnostic.

| case | py rc | cs rc | result |
| --- | --- | --- | --- |
| unknown encoder | 2 | 2 | OK |
| output extension contradicts muxer | 2 | 2 | OK |
| missing input file | 2 | 2 | OK |
| invalid timecode | 2 | 2 | OK |
| end before start | 2 | 2 | OK |
| start past EOF | 2 | 2 | OK |
| unknown scale algorithm | 2 | 2 | OK |
| recover >= limit | 2 | 2 | OK |
| roqvideo non-16-divisible size | 1 | 1 | OK |
| roq_dpcm wrong sample rate | 2 | 2 | OK |
| unrecognized argument | 2 | 2 | OK |

**11 / 11.** Messages are reproduced verbatim, e.g.
`ERROR: roqvideo requires width and height to be divisible by 16; got 250x190`.

---

## 5. CUI smoke test

`printf '6\n7\n5\n0\n' | LiveArchiveEncoder.exe` — banner, filter list,
configuration dump and encoder rescan all render; 35 legacy encoders detected;
clean exit on `0`.

---

## 6. Performance

`tests/benchmark.ps1` measures the **controller** process (Python interpreter vs
.NET host). The FFmpeg children are byte-identical in both runs, so this is the
only part the port can change. 20 s source, rv20, defaults.

| run | wall (s) | controller CPU (s) | peak MB | avg working MB |
| --- | --- | --- | --- | --- |
| python v6.3.3 | 21.649 | 0.188 | 19.4 | 15.5 |
| csharp v7 (first impl, spin-wait tail) | 21.486 | 1.406 | 65.9 | 60.8 |
| csharp v7 (sleep-based timer) | 21.523 | 0.766 | 65.7 | 60.9 |
| csharp v7 (sleep-based, 2nd) | 21.489 | 0.484 | 64.3 | 59.6 |
| csharp v7 (runtime-trimmed) | 21.353 | 0.688 | 65.9 | 60.9 |
| csharp v7 (runtime-trimmed, 2nd) | 21.348 | 0.641 | 65.6 | 61.0 |

5 s segment, for comparison:

| run | wall (s) | controller CPU (s) |
| --- | --- | --- |
| python v6.3.3 | 6.702 | 0.219 |
| csharp v7 | 6.516 | 0.656 |

Reading of these numbers:

* **Wall time is essentially unchanged** (21.35 s vs 21.65 s, ~1 %). It is
  bounded by the deliberate realtime pacing, not by the controller.
* **Controller CPU is a fixed startup cost.** 5 s of media costs 0.656 s and
  20 s of media costs 0.48–0.77 s — it does not grow with the number of slots.
  The persistent ~0.45 s gap over Python is JIT + runtime initialisation.
* **The first implementation burned a full core** because the high-resolution
  timer spun for the sub-millisecond tail of every slot (1.406 s CPU). Replacing
  the spin with sleep-to-deadline cut it to ~0.65 s with no measurable change in
  output cadence.
* **Memory is higher** (~61 MB vs ~15 MB working set) — .NET runtime floor. The
  frame pool itself is 8 x 72 KB, so it is not the frames.

### What was not achievable at the time — and what changed later

ReadyToRun / NativeAOT were originally blocked: they need NuGet runtime packs and
the restore failed behind the local proxy (`NU1301: proxy tunnel request ... 502`).
Only offline runtime switches were applied (`InvariantGlobalization`,
`UseSystemResourceKeys`, `MetadataUpdaterSupport=false`,
`HttpActivityPropagationSupport=false`, `DebuggerSupport=false`,
`System.GC.Conserve=7`); they did not move the needle measurably, which is
consistent with the cost being JIT itself.

With a working proxy the AOT publish was retried and now succeeds — see §8. It
removes ~50 ms of process start, not the ~0.45 s of controller CPU, because that
cost is spread over the run rather than concentrated at startup.

---

## 7. Split audio pipeline (encode video, add audio afterwards)

`--audio-mode separate` encodes the video alone into a staging file while a second
FFmpeg process encodes the audio track, then joins the two with `-c copy`.
`--audio-mode auto` does the same but silently falls back to the single-process
pipeline when the muxer rejects the split.

### 7.1 Why it exists, and the A/V alignment argument

The idea was that skipping audio during the paced video pass would let the video
finish sooner. It does not, because the video pass is **paced against the wall
clock**: it costs `duration` seconds whether or not audio is in the same process.
The audio pass then has to happen anyway, plus an extra remux.

Alignment is not guesswork, though. Both stages are cut from the same source with
`-ss <start>` **on the input**, so both output streams begin at PTS 0; the video
pass is additionally bounded by `-t <duration>`, and the audio pass by the same
`-t`. Verified with ffprobe on a 5 s mpeg4/avi result:

| | start_pts | duration | frames |
| --- | --- | --- | --- |
| video | 0 | 5.076923 s | 66 |
| audio | 0 | 5.041633 s | 193 |

The 35 ms difference is one AAC/MP3 frame granularity and is identical to what
the inline path produces.

### 7.2 Safety net (because several legacy muxers lie)

Three failure modes were found and are now handled:

1. **The muxer accepts the encode and drops the video stream.** The RoQ muxer
   wrote an audio-only file with exit code 0: 2.5 s of audio, `Video 271.7 >
   Total 178.2`. Fixed by `CanSplitMux`, which does a 3-frame synthetic
   encode + mux + probe *before* the real pass, and by `VerifyMuxedOutput`, which
   re-probes the final file for both streams and a plausible duration.
2. **A "successful" mux that produces a corrupt file.** rv20/RealMedia came back
   with `HEADER ERROR` and a duration of 0.065 s instead of 5.0 s. `VerifyMuxedOutput`
   now also decodes the result (`ffmpeg -v error -i out -f null -`) and rejects
   any stderr output.
3. **Preflight that was too strict / too lax.** The 3-frame preflight clip is not
   duration-checked (too short to be meaningful), but the final mux is.

### 7.3 Measured verdict — the split path is slower and less faithful

`tests/ab_duration.py`, 5 s segment, 4 runs per variant:

| case | python v6.3.3 | cs inline | cs separate | cs auto |
| --- | --- | --- | --- | --- |
| mpeg1video — duration | 4.942 ×4 | 4.942 ×4 | 4.901 / 4.984 (unstable) | 4.901 / 4.984 |
| mpeg1video — fresh/repeat | 56/64, 54/66 | 54–56 / 64–66 | 31–42 / 78–89 | 32–42 / 78–88 |
| wmv2 — duration | 5.046 ×4 | 5.046 ×4 | 5.015 ×4 | 5.015 ×4 |
| wmv2 — fresh/repeat | 33/32 ×4 | 33/32 | 33/32 | 33/32 |
| wall (mpeg1video) | 6.61 s | **6.44 s** | 7.22 s | 7.12 s |
| wall (wmv2) | 6.63 s | **6.45 s** | 7.17 s | 7.13 s |

Three findings:

* **Inline is exact.** Duration is deterministic and matches v6.3.3 on every run;
  fresh/repeat falls inside the same jitter band as Python.
* **Separate perturbs the controller.** The parallel audio process competes with
  the capture thread, so mpeg1video loses ~20 fresh slots out of 120 and its
  container duration stops being stable. Running the audio pass at
  `BelowNormal` priority did not fix this.
* **Separate changes container metadata even when the frames are identical.**
  wmv2/ASF has identical fresh/repeat (33/32) but a duration of 5.015 instead of
  5.046 — the extra `-c copy` remux rewrites the ASF duration field.
* **Separate is ~0.7 s slower**, exactly as predicted: the video pass is pinned to
  the wall clock, so the audio pass and the remux are pure additional cost.

### 7.4 Decision

The default is **`--audio-mode inline`**, i.e. v6.3.3 behaviour. `separate` and
`auto` remain available — the pipeline is complete, A/V-aligned and guarded — but
they are opt-in, and the report says plainly why: they are slower and they move
the output.

The split path itself is verified working: `--audio-mode separate` produces a
file with both streams and a sane duration for mpeg4/avi, mpeg1video/mpg,
roqvideo/roq (the muxer that used to silently drop the video stream) and
rv20/rm.

---

## 8. NativeAOT

AOT is opt-in so the default `dotnet build` stays a plain framework-dependent
app:

```powershell
tests\build.ps1 -Aot
# equivalent to:
#   dotnet publish src/LiveArchiveEncoder/LiveArchiveEncoder.csproj -c Release `
#       -r win-x64 -p:PublishAot=true -o publish\aot
```

### 8.1 The link failure and its fix

The first publish failed with

```
LINK : fatal error LNK1181: cannot open input file 'advapi32.lib'
```

`Microsoft.NETCore.Native.Windows.targets` runs `findvcvarsall.bat`, which calls
`vcvarsall.bat` and echoes back `%LIB%`. On this machine `vcvarsall`'s Windows
SDK discovery goes through `reg.exe`, which is blocked in the build sandbox, so
`LIB` came back containing only the two MSVC directories and no
`Windows Kits\10\Lib\...\um\x64` / `ucrt\x64`. `link.exe` therefore found
neither `advapi32.lib` nor `ucrt.lib`.

Fix: pass the SDK import-library directories explicitly through the documented
`AdditionalNativeLibraryDirectories` extension point, overridable at build time:

```xml
<WindowsSdkLibRoot Condition="'$(WindowsSdkLibRoot)' == ''">C:\Program Files (x86)\Windows Kits\10\Lib</WindowsSdkLibRoot>
<WindowsSdkLibVersion Condition="'$(WindowsSdkLibVersion)' == ''">10.0.26100.0</WindowsSdkLibVersion>
```

On a normal developer box (or from a VS developer prompt) that block can be
deleted; `vcvarsall` supplies the same directories by itself.

### 8.2 What AOT changes

| build | size | notes |
| --- | --- | --- |
| JIT, framework-dependent | 162 KB exe + shared framework | default `dotnet build` |
| NativeAOT | **3.76 MB, single file, no dependencies** | needs the C++ workload to link |

AOT produces a self-contained executable that no longer needs the shared
framework. It does not change what the tool outputs: the AOT build passes the
same 12-case media matrix and the same 11 error paths as the JIT build.

### 8.3 AOT and instruction sets

`--cpu-info` is a new diagnostic flag that prints what the runtime reports:

| build | `Vector<byte>.Count` | `Avx2` | `Avx512F` | `Fma` |
| --- | --- | --- | --- | --- |
| JIT | 32 bytes | True | True | True |
| NativeAOT (default) | **16 bytes** | **False** | False | **False** |
| NativeAOT `-p:IlcInstructionSet=avx2` | 32 bytes | True | False | True |

NativeAOT compiles for the **baseline** x86-64 instruction set (SSE2), so
`Avx2.IsSupported` reports `False` and the vectorised runtime helpers (including
`Buffer.MemoryCopy` / `Span.CopyTo`) run at SSE2 width. The JIT dispatches at
runtime and gets AVX2. Raising the AOT baseline with
`-p:IlcInstructionSet=avx2` restores it — at the cost of producing an exe that
requires a CPU with those instructions. `tests\build.ps1 -Aot` takes the value
from `-IlcInstructionSet` if you want to opt in.

---

## 9. SIMD: there is nothing to switch on

Encoding happens entirely inside the FFmpeg child processes, which dispatch on
the CPU at runtime. Forcing a level with `-cpuflags` changes nothing: `+avx2`
matches the default, `+avx512` is if anything slightly worse, and only
*disabling* SIMD (`-cpuflags 0`) makes a difference — roughly 1.2–1.5x slower on
the legacy encoders. In other words SSE4.2/AVX2 are already in use and there is
no flag to gain from.

On the .NET side there is no vectorisable workload to begin with: the controller
never touches pixels. It reads a raw frame from the decoder's stdout pipe into a
pooled buffer and writes it to the encoder's stdin pipe.

The one real knob is `IlcInstructionSet` for the AOT build (§8.3).

---

## 11. Bug fix: 中断一次之后再也编不动了

**现象**：交互式菜单里 Ctrl+C 中断一次编码，回到菜单后无论怎么改参数，之后
每一次 `8. Start encoding` 都在打印完 `Filters :` 之后立刻 `Interrupted.`，
一秒活都不干。

**根因**：`Program` 里只有一个 `static readonly CancellationTokenSource`。Ctrl+C
的 `CancelKeyPress` 处理器把它取消掉之后**从不重置**，而 CUI 在中断后并不退出，
于是同一次会话里后续所有编码都拿到一个"已取消"的令牌，进入执行循环第一行的
`ThrowIfCancellationRequested()` 就抛出。579 秒的活一秒没干，正是这个特征。

排查过程中确认了取消只有这一个来源：`Execute()` 内部那个 linked CTS 只用来停止
diagnostics 读线程，不会反向取消父令牌；`ThrowIfCancellationRequested()` 用的也
是父令牌。

**修法**：新增 `Core/Interrupt.cs`，把 Ctrl+C 的处理收在一处，并让令牌**按操作
发放**——`BeginOperation()` 在返回前先清掉上一次的取消状态。CUI 每次开始编码和
CLI 每次运行都调一次。

**验证**：把真实的 `Interrupt.cs` 编进一个独立测试程序，模拟
`开始 → Ctrl+C → 再开始`：

```
[PASS] first op starts with a live token
[PASS] Ctrl+C cancels the running op
[PASS] ...and it throws OperationCanceledException
[PASS] next op gets a FRESH token (the bug)
[PASS] ...and does not throw immediately
[PASS] a third op also starts clean
```

另外用 stdin 驱动了一次真实的 CUI 会话，连续两次编码都完成
（`Duration: 2.00s`，各 `Fresh 15 / Repeated 11`）。

---

## 10. Conclusion

Functional equivalence is established, and re-verified after every change made
in this round:

| check | JIT build | NativeAOT build |
| --- | --- | --- |
| 12-case media matrix (`compare_matrix.py`) | 12 / 12 | 12 / 12 |
| 11-case error paths (`error_paths.py`) | 11 / 11 | 11 / 11 |

both against `live_archive_encoder.py` v6.3.3, plus identical frame counts when
the adaptive state machine is taken out of the loop, and identical CLI/CUI
semantics.

Three things were added this round and all three are behaviour-preserving by
default:

* **NativeAOT** is available via `tests\build.ps1 -Aot`. It was blocked by
  `LNK1181: cannot open input file 'advapi32.lib'` until the Windows SDK import
  library directories were supplied explicitly (§8.1). The AOT binary passes the
  full matrix and error-path suite.
* **`--audio-mode separate`** exists and works, but is **not** the default: it is
  slower and it shifts the output (§7). Default is `inline`, i.e. v6.3.3.
* **`--help` / `--cpu-info`** close the last CLI gaps against argparse.

SIMD needs no work: the FFmpeg children already dispatch on the CPU at runtime
and the C# controller has nothing vectorisable to begin with (§9).
