# LiveArchiveEncoder v7.9.8.8

v7 的补丁版。主题是**把 OpenH264 的两个旋钮接上**：一个从来没被拧过（二次压制），
一个一直拧错了方向（码率模式）。

产物格式没变。但用 `libopenh264` 的二次压制**体积会明显变小**——那是修好的证据，不是退化。

## 1. 二次压制选 `libopenh264` 时，参数是空的

不给 `--recompress-quality` 时（它本来也没有质量旋钮），生成的命令是：

```
-c:v libopenh264 -pix_fmt yuv420p
```

没有码率、没有模式。OpenH264 落在自带的 2 Mbps 目标上——实测 640x480 testsrc2：
**要 300 kbps，产出 2002 kbps，6.7 倍**。用户看到的是"预设应用了"，拿到的是一个大七倍的
文件。

现在命令里一定有码率和模式：

```
-c:v libopenh264 -b:v 300k -rc_mode bitrate -pix_fmt yuv420p
```

码率用新旗标 `--recompress-video-kbps`（可重复，默认 300）。真素材
`[晚街与灯][Re Zero 4th - 04]` 20 秒片段实测：默认 346 kbps、显式 700k 时 622 kbps。

同一类"静默吞掉"还修了两处，现在都会**报错退出 2**：

- `--recompress-quality` 给一条全是 `libopenh264` 的链：它没有质量旗标，那个数字只会被丢掉；
- `--recompress-video-kbps` 给一条全是 `-crf` 编码器的链：码率同样不生效。

两种编码器混在一条链上时（`libx264` 出观看版、`libopenh264` 出站点版），两个数字**各落到
用得上的一遍**，用不上的那遍打印一行 NOTE。不报错是因为这种链否则写不出来：一个数字没法
跳过某一遍，写两遍也一样。

## 2. 码率模式可选

| 模式 | 含义 | 实测（640x480，目标 300k） |
| --- | --- | --- |
| `bitrate`（默认） | 严格按码率 | 354 kbps |
| `quality` | 优先画质，`-b:v` 只作目标 | 362 kbps |
| `timestamp` | 按时间戳分配（实时/可变帧率） | 358 kbps |

`buffer` 和 `off` **被拒绝**，理由带实测数字：

- `buffer` 官方文档自己写着 "no bitrate control"；实测要 300 kbps 给 **3.1 Mbps**。
- `off` 把 QP 锁死在库内部的 26，而 FFmpeg 没有提供改它的入口。

CLI 是 `--openh264-rc <bitrate|quality|timestamp>`；CUI 在参数菜单 32/33 行，
只在编码器是 `libopenh264` 时出现。

## 3. 为什么没有 QP：`-qp` 在这个编码器上不存在

网上流传 `-rc_mode off -qp 23` 的写法。查 `libavcodec/libopenh264enc.c`：选项表里没有
`qp`，`svc_encode_init` 也从没读过 `avctx->global_quality`。FFmpeg 会把这句话说得很清楚：

```
AVOption qp (Quantization parameter (luma)) has not been used for any stream.
```

`-qmin/-qmax` 倒是会被写进 `param.iMinQp/iMaxQp`，但在 `RC_OFF_MODE` 下不起作用——实测
qmin = qmax 取 10、26、51，产出**字节完全相同**（1840204 B）。所以这个编码器的"质量"
只能是码率的函数，`-rc_mode quality` 是它唯一接近质量控制的模式。

## 4. 可选跳帧

`-allow_skip_frames 1` 是码率真正落地的前提。源码 `param.bEnableFrameSkip = skip_frames`，
OpenH264 自己也在日志里说："bitrate can't be controlled ... without enabling skip frame"。
实测 300 kbps：不开 354 kbps，开 302 kbps。

默认**不开**：帧率由自适应阶梯掌握，让编码器再偷偷丢一层，阶梯会看到一个自己没造成的
码率下降，原因就查不清了。要严格码率时用 `--openh264-skip-frames`。

## 顺带

- **`BuildProfile.SchemaVersion` 3 → 4**。`EncoderCaps` 多了一个字段，而它是磁盘缓存的
  一部分：旧缓存没有 `HasRcMode`，反序列化出来是 `false`，程序就会认定所有人的 FFmpeg
  都不支持 `-rc_mode` —— 这个功能会**只在升级过的机器上静默失效**。代价是升级后第一次
  运行重扫一次 FFmpeg（四次进程启动）。
- `EncoderCaps.HasRcMode`：从 `ffmpeg -h encoder=NAME` 读 `-rc_mode` 是否存在。这个选项
  在源码里，但**官方手册的 libopenh264 一节没有记载**；探测到没有就不发，退回旧参数，
  而不是让整个运行死在 "Unrecognized option"。
- 改正一条撒谎的注释：`ArgsBuilder` 曾说 OpenH264 "reads neither `-maxrate` nor
  `-bufsize`"。实际是 `iMaxBitrate = FFMAX(rc_max_rate, bit_rate)`——它读，只是当
  `maxrate == bitrate` 时两者相同，那个旗标没有信息量。
- `--openh264-rc` / `--openh264-skip-frames` 给了别的编码器现在**报错退出 2**，
  `--recompress-only` 这种没有直播编码器的路径也一样，不再静默忽略。
- CUI 第二遍第 6 行：`libopenh264` 时标题是"码率（kbps）"、问的是码率；改音频编码器这类
  无关操作也不会再把已设的码率抹回默认值。

## 装哪个包

| 平台 | 文件 | 要求 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-v7.9.8.8-win-x64.zip` | **.NET 10 运行时** |
| Linux x64 | `LiveArchiveEncoder-v7.9.8.8-linux-x64.tar.gz` | **.NET 10 运行时** |

这一版只出依赖版（JIT）；AOT 产物这次没有构建（本机缺少出 Windows AOT 的工具链）。

Linux 包这次只做了**结构核对**：ELF 头、可执行位 755、`runtimeconfig` 要求 `Microsoft.NETCore.App
10.0.0`、清单与 v7.9.8.7 完全一致，且 dll 里能找到 `7.9.8.8`（4 处）与 `-rc_mode`（2 处）。
没有真跑——本机 WSL 里没有 .NET 运行时，`lae-linux` 测试机这次连不上（Tailscale 超时）。
Windows 包解包后跑过 `--version`。

## 校验

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LiveArchiveEncoder-v7.9.8.8-win-x64.zip` | 372,323 | `47dce036004ea1b847b9d178feda8e4ae03be7c9778d69ff5d61381d7aa87ca4` |
| `LiveArchiveEncoder-v7.9.8.8-linux-x64.tar.gz` | 333,137 | `07959d275bac98a6f68b5a0e22e91a24af56af864314a8121d67d36f9ad79ad8` |

## 这一版怎么验的

单测 **639**（638 通过 / 1 跳过）、零警告。本轮新增的 7 组断言各做了一次变异验证
（缓存 schema、混合链、码率保留、目录默认值、0 码率、旗标守卫），每组都变红并指名。

- `TheModeReachesTheCommandLine`：三种模式各自的命令行。
- `BufferAndOffAreRefusedWithTheMeasurement`：拒绝理由里带着实测数字。
- `OpenH264AlwaysGetsBothABitrateAndAMode`：不给码率时也必须有 `-b:v 300k`。
- `EveryBitrateOnlyCodecCarriesADefault`：规则化断言——任何没有质量旗标的编码器都必须
  能自己产出码率，以后新增编码器不会重犯。
- `AnOlderSchemaIsAMissRatherThanAPartialAnswer`：schema 3 的旧缓存必须当缓存未命中，
  而不是带着 `HasRcMode = false` 继续用。
- `NumbersApplyWhereTheyApplyInAMixedChain`：混合链上两个数字各归各遍，单值也不至于
  让另一遍裸奔。
- `EditingAnUnrelatedFieldKeepsTheBitrate`：改音频编码器不得重置已设的码率。
- 真素材：`[晚街与灯][Re Zero 4th - 04]` 20 秒片段，二次压制默认 346 kbps、显式 700k
  时 622 kbps。
- 真命令：`--recompress-only --recompress-codec libx264 --recompress-codec libopenh264`
  打出来的第二条是
  `-c:v libopenh264 -b:v 300k -rc_mode bitrate -pix_fmt yuv420p`（这是原来那条空命令修好
  后的样子），并带一行 NOTE 说明质量号不属于这一遍。
- 拒绝路径：`--openh264-rc buffer`、`--recompress-quality` 给 `libopenh264`、
  `--openh264-rc` 给 `libx264`、`--recompress-only` 配 `--openh264-rc`，均 exit 2 且
  错误信息可读。
