# 实测能力矩阵（v7.9.5）

`ffmpeg -h encoder=NAME` 声明的东西不一定是真的。这个项目已经因此栽过三次
（v7.9.4 的 mpeg1video 帧率、pal8 帧长，v7.9.5 的 RealMedia 整数帧率）。所以
v7.9.5 不再相信声明表，改用 `tools/capability_sweep.py` 把每个编码器的**容器**
和**帧率**都跑一遍。

## 怎么测的

```bash
python tools/capability_sweep.py --json /tmp/capsweep.json
```

对每个 legacy 编码器：

1. **容器**：用该编码器的默认容器 + avi / matroska / mov / mp4 / mpeg / flv /
   asf / ogg / rm / mpegts / webm 各编一个 0.6 秒的 `testsrc`，然后三重校验：
   - muxer 接受（退出码 0，产物非空）；
   - `ffprobe` **用编码器自己的名字读回来**（`codec_name` 必须一致）；
   - 抽一帧量亮度/色度（`signalstats` 的 YAVG/UAVG），确认画面还在。

   第 2、3 条是必须的。只测「muxer 接受」会得到一张假的表：

   | 组合 | 只测 mux | 真相 |
   | --- | --- | --- |
   | `rv20 → avi` | 成功 | 文件里存的是 `rawvideo`（AVI 没有 RV20 的 fourcc），probe 出来根本不是 rv20 |
   | `mpeg4 → rm` | 成功 | rm 把 codec tag 写成 `rv20`，载荷却是 MPEG-4，播放器按 RealVideo 解 → 花屏 |
   | `h261 → mpeg` | 成功 | probe 出 `codec=unknown`、`nb_frames` 对不上 |
   | `msrle → matroska` | 成功 | **画面全黑**（YAVG 16，AVI 是 123）——Matroska 用 `V_MS/VFW/FOURCC` + `BITMAPINFOHEADER` 存 VfW 编码器，那个头里没有调色板 |
   | `msrle → asf` | 成功 | 同上，也全黑 |

2. **帧率**：13 个取值（5 / 10 / 12 / 15 / 20 / 23.976 / 24 / 25 / 29.97 / 30 /
   50 / 59.94 / 60），在默认容器和 AVI 上各编一遍，读 `avg_frame_rate`。
   带小数的请求如果被改写成别的值，记为 `x->y`。

   用 `avg_frame_rate` 而不是 `r_frame_rate`：MPEG-1/2 的 `r_frame_rate` 是
   **场率**（25 fps 的文件读成 50/1），那是 ffprobe 对 MPEG-1 的启发式，不是编码器
   干的事。

3. 原始 `.h261` / `.h263` / `.a64` 容器**不存时间戳**，所以帧率只在 AVI 上有意义
   —— 这也是 `Constants.RateLessFormats` 存在的原因（工具会打印一条 note）。

环境：Windows 11，`ffmpeg version N-127941-g303510f9d1-20261022`（BtbN GPL 构建），
2026-10-23。

## 容器（三重校验全部通过的对）

| 编码器 | 默认 | 通过 | 说明 |
| --- | --- | --- | --- |
| `mpeg1video` | mpeg | mpeg, avi, **matroska, mov, mp4**, asf, mpegts | v7.9.5 之前只给 mpeg + avi |
| `mpeg2video` | mpeg | 同上 | 同上 |
| `mpeg4` | avi | avi, **matroska, mov**, mp4, mpeg, asf, mpegts | v7.9.5 之前只给 avi + mp4 |
| `libxvid` | avi | avi, **matroska, mov**, mp4, mpeg, asf, mpegts | 同上 |
| `libx264` | mp4 | mp4, avi, **matroska, mov, flv**, mpeg, asf, mpegts | v7.9.5 之前只给 mp4 + avi + mkv |
| `libtheora` | ogg | ogg, **matroska, avi**, mov, asf | |
| `flv` | flv | flv, **avi, matroska**, mov, asf | |
| `mjpeg` | avi | avi, **matroska, mov, mp4**, asf | |
| `h261` | h261 | h261, avi, **matroska**, mov, asf | 原始流没有音轨，AVI/MKV 才有 |
| `h263` | h263 | h263, avi, **matroska**, mov, asf | 同上 |
| `h263p` | avi | avi, **matroska**, mov, asf | |
| `msrle` | avi | avi, **mov** | **不能进 Matroska / ASF**：pal8 调色板会丢，画面全黑 |
| `msvideo1` | avi | avi, matroska, mov, asf | 像素格式是 `rgb555le`，不是索引色，所以进 Matroska 也不丢东西 |
| `cinepak` / `asv1` / `asv2` / `cljr` / `snow` / `zmbv` / `msmpeg4v2` | avi | avi, matroska, mov, asf | |
| `flashsv` | flv | flv, avi, matroska, mov, asf | |
| `flashsv2` | flv | flv | 只有 flv 能用 |
| `qtrle` | mov | mov | `-c copy` 到 avi 会变成 rawvideo |
| `roqvideo` | roq | roq | |
| `rv10` / `rv20` | rm | rm | AVI 会存成 rawvideo，ASF 直接拒写，MP4/MOV 找不到 tag |
| `smc` | mov | mov, matroska | |
| `svq1` | mov | mov, avi, asf | |
| `rpza` | mov | mov, avi, matroska, asf | |
| `wmv1` / `wmv2` | asf | asf, avi, matroska, mov | |
| `a64multi` / `a64multi5` | a64 | a64, avi, matroska, mov, asf | |

工具**只提供其中一部分**（`Constants.ContainerChoicesByEncoder`）。区别在于
"能存"和"有人这么用"：`mpeg4 → mpeg` 能存，但把 MPEG-4 塞进 MPEG-PS 是没人要的
组合；`a64multi → mov` 更是没有任何播放器会预期的东西。表里收进去的标准是
（a）三重校验通过，（b）播放器/容器规范里说得通，（c）pal8 编码器不进丢调色板的
容器。

## 帧率

| 编码器 | 不接受的取值 | 会被改写 | 结论 |
| --- | --- | --- | --- |
| `mpeg1video` | 5, 10, 12, 15 | 20 → 23.976 | 声明表里的 5/10/12/15 是 libmpeg3 的 unofficial rates；真正可用只有八个 `frame_rate_code`。v7.9.4 已吸附 |
| `mpeg2video` | — | — | 有 frame rate extension，1–60 全收 |
| `rv10` / `rv20` | — | 23.976 → 23.000，29.97 → 29.000，59.94 → 59.000 | **RealMedia 头里 fps 是整数**。v7.9.5：改为四舍五入到 24/30/60，文件与报告一致 |
| `roqvideo` | 23.976, 29.97, 59.94 | — | **RoQ 头里 fps 也是整数**，小数直接拒编。v7.9.5：同样四舍五入 |
| `amv` | 12, 20, 24, 29.97, 59.94, 60 | — | 真实规则是 `22050 % fps == 0`（5/10/14/15/18/21/25/30/50 都行），工具的表只列常见档位 |
| `flv` / `flashsv` / `flashsv2` / `wmv1` / `wmv2` | — | 29.97 → 29.917，60 → 59.940 | 容器时间基是 1/1000，误差 0.18%，可以当已知非故障 |
| 其余（asv, cinepak, mpeg4, libx264, mjpeg, msrle, msvideo1, qtrle, smc, snow, svq1, zmbv, libtheora, libxvid, h263p …） | — | — | 13 个取值全收 |

原始容器（`.h261` / `.h263` / `.a64`）在**任何**帧率下都读回 25/1（ffprobe 的缺省
猜测）：文件里根本没有帧率字段。画面没问题，只是帧率不随文件走 —— 工具会打印
note，并建议改用 AVI。

## 回归

这些结论都有护栏，不是文档里的口头约定：

- `tests/LiveArchiveEncoder.Tests/EncoderRateAndContainerTests.cs`
  —— 整数帧率规则、容器表的自洽性（扩展名 ↔ muxer 名）、pal8 不进丢调色板的容器、
  只 mux 不通的组合仍被拒；
- `tests/LiveArchiveEncoder.Tests/PaletteAndRateTests.cs` —— pal8 帧长、mpeg1 的
  八个真帧率；
- `tests/e2e/test_option_smoke.py` —— 新容器逐对出片、小数帧率落成整数、
  msrle 非黑。
