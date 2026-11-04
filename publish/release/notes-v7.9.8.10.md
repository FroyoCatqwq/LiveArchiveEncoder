# LiveArchiveEncoder v7.9.8.10

v7 的补丁版，**没有新功能**。这一版是对整个项目做了一次五路只读审查
（正确性 / 资源与生命周期 / CLI·CUI·文档契约 / 死代码 / 测试有效性），
逐条复核后修掉了"绿着但其实坏了"和"只在老用户、长任务上暴露"的问题，
再清掉确认为零引用的资产。

## 1. CUI 选编码器时吞掉异常，还跳过半段同步

菜单第 3 项选一个多容器编码器（`libx264`、`mpeg4` …）时，`encoder.containers`
模板要两个参数，代码只给了一个。`string.Format` 抛 `FormatException`，被
`ChooseEncoder` 的 catch 打成一行 "Invalid selection" —— 而此时编码器**已经换掉**，
后面的 fps、尺寸、质量、音频编码器、默认输出路径全都没跑。菜单显示新编码器，
配置里却是旧编码器的数字。`audio.replaced` 是同一个错误。两处都补齐，
并加了三条回归防线：两种语言的占位符集合必须一致、每个带参模板都能被格式化、
CUI e2e 真的去选一次编码器。

## 2. 编码成功、只差改名时，唯一的好成品被删掉

目标被播放器占用、共享盘拒绝替换、路径过长，都只会让最后的 `File.Move` 失败，
编码其实已经完整结束。三处调用方的 catch 却一律 `Discard(part)`，几十分钟的
实时录制就这样消失，只剩一句 "could not put ... in place"。现在把"编码失败"和
"改名失败"分开：后者保留 part 并打印完整路径，你可以手工改名。二次压缩那条同理。

顺带把 `.part` 临时名加上进程 ID + 随机标签：固定的 `clip.part.mp4` 可能正好是
用户自己的成品，Windows 上只有大小写不同的两个输出也会共用同一个 part 互相截断。
分片合并从 `%TEMP%` 跨搬到其他盘时加了 copy 回退。

## 3. ⚠ 自更新的信任边界（行为变化）

- `LAE_UPDATE_TOKEN` 以前会发给 `--update-feed` 指向的**任意主机**；现在只有
  GitHub 官方域名才附带凭据。
- 资产 URL 现在只接受 https（本地测试用的回环 http 保留）。
- **发布没有 SHA-256 摘要时，现在直接失败（exit 1）**，不再"只校验大小"就解压、
  执行下载物的 `--version` 并把程序交出去，而脚本还拿到 exit 0。size 和 URL 出自
  同一份不可信 JSON，不构成完整性证明。
- `TryReplace` 的注释声称"中途失败会留下完整的旧版本"，实现却相反：前 N-1 个
  文件已被覆盖且没有备份。现在先把每个目标复制进 staging 的 `.rollback`，
  任一文件失败就从副本恢复；也不再只复制 `LiveArchiveEncoder*` 前缀的文件。

> GitHub 的 release API 会返回 `digest`，所以 **v7.9.8.9 及 7.9.8.8 都能照常
> 自动更新到这一版**。如果你把 `--update-feed` 指向不提供 digest 的第三方镜像，
> 更新会被拒绝；这时请手动下载，或换回官方发布源。

## 4. 参数与命令的一致性

- `--era-native` 以前解析了却没人读，`--era-modern --era-native` 会反向生效；
  现在显式的 native 优先。
- `AudioCatalog.KbpsBand(codec, null)` 以前返回**最后**（最窄）那一档，与"最宽
  band"的注释相反：`--audio-kbps 16` 配 `mp2` / `libvorbis` 被判非法或悄悄改成 32。
  rate 未知时现在取所有 band 的并集；显式 rate 仍按窄档校验。
- CUI 应用 era 预设时少调了一步 `AudioCatalog.Effective`，内置 rv20/rm 预设写下
  16000 Hz 而 `real_144` 只认 8000，点"开始编码"会拒绝**程序自带的预设**，CLI 却能跑。
- `--qv 999` / `-q:v 0` 以前只在预检里以一句无关的 "Video preflight failed" 晚失败；
  现在按编码器的质量刻度在校验阶段报出区间。
- 音频码率 snap 后的值现在写回 `RealtimeOptions`，校验、预检、inline 的 `-b:a`、
  分离音轨、码率估算五处同源；预检也终于拿到 `--audio-stream` 选中的轨。
- 预检只限了视频 3 帧，带音轨的长源会把整条音轨编完再撞 120 秒上限；加了 `-t 0.5`。
- 顶层异常处理补上 `IOException` 本身：磁盘满、UNC 断开不再变成带 stack trace 的崩溃。

## 5. 资源、进程与测试基础设施

- `Smb.cs` 重定向了 stderr 却从不读：PowerShell 写满 4 KB 管道就死锁，白等 15 秒后
  共享盘一律显示"未连接"。现在并发抽干两条管道，查询失败时打印原因。
- `ProcessRunner` 超时/取消时丢弃两个读取 Task，FFmpeg 的最后一句诊断全丢，还留下
  未观察异常；现在先收尾再把输出附进异常。
- `tools/e2e.py` 把 pytest 的 exit 5（0 个用例）无条件当成功——marker 写错、模块改名、
  xdist 全挂都会打印 "both phases green" 而一个测试没跑。现在每个 phase 先
  `--collect-only` 核对收集数。
- 4 个交互提示传了可用编码器清单却没显示，现在真的列出来。

## 6. 清理（逐项 grep 复核为零引用）

16 个 en/zh 成对残留的本地化 key、5 个零引用成员、7 个未使用的 using，以及随
"无 digest 仍可更新"一起失效的 `DigestMissing` 字段与文案；`docs/runs/` 下 12 份
历史 harness 输出（含 3 份字节级完全相同的日志）和一份结论推荐已删除 Turbo 的
验证文档；`README.md` 目录树、`archive/README.md` 归档清单、`.slnx` 里悬空的
`Watermark.cs` 一并校正。文档里照抄即失败的命令（`-o`、`-args`、`--size`、
`--list-encoders`）也改成真实写法，`--help` 补上 `--cpu-info`。

## 这一版怎么验的

单测 **654**（653 通过 / 1 跳过）、零警告；完整 v7 e2e 绿。
每条新断言都做过变异验证：把修复改回去，测试必须变红**并指名**
（7 组单测变异 + 2 组 e2e 变异全部符合）。`tools/e2e.py` 的"0 用例判绿"
也用一个不存在的 marker 反向验过，现在返回失败。

**诚实的限制**：网络源默认输出那条仍只有单测与代码审查（本机没有共享盘）；
Linux 包仍只做结构核对（本机 WSL 无 .NET 运行时，`lae-linux` 测试机连不上），
Windows 包解包后跑过 `--version`。

## 装哪个包

| 平台 | 文件 | 运行条件 |
| --- | --- | --- |
| Windows x64 | `LiveArchiveEncoder-v7.9.8.10-win-x64.zip` | **.NET 10 或 .NET 11 运行时** |
| Linux x64 | `LiveArchiveEncoder-v7.9.8.10-linux-x64.tar.gz` | **.NET 10 或 .NET 11 运行时** |

这一版只出依赖版（JIT）；AOT 产物这次没有构建。解压覆盖旧文件即可，
`config.json` 和已有设置不受影响。

## 校验

| 文件 | 字节 | SHA256 |
| --- | --- | --- |
| `LiveArchiveEncoder-v7.9.8.10-win-x64.zip` | 373,667 | `16f1493bedeb2f7f2f3fb5a0eff88ba62cff3a7fb2a00945b9b586ca20c82278` |
| `LiveArchiveEncoder-v7.9.8.10-linux-x64.tar.gz` | 334,589 | `dfa7eacc5768478a42692a148354442449fc04a2b1087276f264dfbd4ad4b2ef` |
