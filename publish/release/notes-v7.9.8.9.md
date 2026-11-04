# LiveArchiveEncoder v7.9.8.9

v7 的补丁版。三件事都是**默认值站错了地方**：网络素材的输出落点、mp4 配 H.264 的音频、
以及写错扩展名时的处理方式。

## 1. 网络驱动器上的素材，默认不再把输出写回那条链路

`_live` 文件一直放在源文件旁边。源在共享盘上时，这条规则变成"读写挤同一条链路，几十 GB
的产物落在那台只负责共享的机器上"。

现在源目录被判定为网络路径（UNC、映射盘符、Linux 的 cifs/nfs 挂载）时，默认输出改到：

```
%LOCALAPPDATA%\LiveArchiveEncoder\output\
```

就是 `config.json` 所在目录（启动横幅一直印着它），单独开一个 `output` 子目录，这样清掉
设置不会连带清掉录制。

- **照样能改**：菜单「2. Output file」填了就是你的（`outputIsAuto=false`，写进设置）；
  命令行第二个位置参数同理。用户给的路径程序再也不重算。
- 没给路径时每次按规则重算，换了输入或编码器都跟着变。
- 打印一行说明它去了哪里，不是悄悄搬家。

## 2. mp4 + H.264 的音频默认是 AAC

`libx264` / `libopenh264` 写 mp4 时，音频默认 AAC。

坑在"记住"上：2007 年的 FLV 预设会把 `libmp3lame` 写进设置，而 **MP3 确实能封进 mp4**，
所以没有任何规则会替换它，之后每次 mp4 都带着 mp3。现在这条规则在三个"确定编码器/容器"
的地方都会跑——启动载入设置、换编码器、换容器——只在**默认**被改时生效，并说明换掉了什么。
手工在「6. Audio encoder」里选的编码器仍然算数。

## 3. 扩展名写错自动更正，而不是拒绝

`out.rm` 配 `libx264` 以前在开跑前报 `Output extension '.rm' does not match selected muxer
'mp4'`（退出码 2）。现在更正成编码器真正能写的后缀：

```
NOTE: '.rm' is not a container libx264 can write; the file is written as .mp4.
```

反方向一样（`out.mp4` 配 `rv20` → `out.rm`）。更正出来的名字**不覆盖任何人**：

- 更正后已存在（`taken.rm` 有了）→ `taken (2).rm`；
- 更正后正好是源文件（`-o clip.rm`，源是 `clip.mp4`）→ `clip (2).mp4`，源文件字节不变。

CLI 与 CUI 走同一条规则。

**这条是有意离开 v6.3.3 基线的**：老 e2e 里 `bad_extension` 是"必须报错"的用例，现在搬家成
`test_output_naming.py` 里的正向断言。

## 顺带

- CLI 写回设置时 `outputIsAuto` 反映"这一次是不是用户给的路径"。以前一律写 `true`，
  手写的输出路径下次会被当成自动路径重算。

## 装哪个包

| 平台 | 文件 | 要求 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-v7.9.8.9-win-x64.zip` | **.NET 10 运行时** |
| Linux x64 | `LiveArchiveEncoder-v7.9.8.9-linux-x64.tar.gz` | **.NET 10 运行时** |

只出依赖版（JIT）。Linux 包这次同样只做**结构核对**：ELF 头、可执行位 755、
`runtimeconfig` 要求 `Microsoft.NETCore.App 10.0.0`、清单与上一版一致，dll 里能找到
`7.9.8.9`。本机 WSL 没有 .NET 运行时，`lae-linux` 测试机连不上。

## 校验

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LiveArchiveEncoder-v7.9.8.9-win-x64.zip` | 373,302 | `55b7105284ce33d64931f57d427337a1cf8752dc29037b8841c0a7681277bd4b` |
| `LiveArchiveEncoder-v7.9.8.9-linux-x64.tar.gz` | 334,106 | `f5f919f425a3d99c3f3de1b6b0699795c92f5bd9f884506f83872124b130a14d` |

## 这一版怎么验的

单测 **645**（644 通过 / 1 跳过）、零警告；完整 v7 e2e 绿（2 skip：没有 `.a64` demuxer）。
新增 6 组断言，5 组做过变异验证（规则关掉 / 网络判定恒假 / 更正失效 / 不避让既有文件 /
不检查 AAC 是否可用），每组都变红并指名。

真机验证（Windows，包内构建）：

- `out.rm` + libx264 → `NOTE ... written as .mp4`，产物 `out.mp4`；
- `out.mp4` 已存在时再来一次 → `out (2).mp4`，原文件未被改；
- `-o src.rm`（源是 `src.mp4`）→ `src (2).mp4`，源文件仍是 110,817 B；
- 一份写着 `libmp3lame` 的设置启动 CUI → 打印换用 AAC，菜单第 6 行是 `aac`，
  退出后设置里存的也是 `aac`。

**网络默认值那条只有单测与代码审查**——本机没有可用的共享盘，做不了真机验证。
它走的是 `Remote.Smb.IsRemotePath`（UNC / 映射盘符 / Linux 挂载），与启动横幅判断
"网络源"用的是同一个函数。
