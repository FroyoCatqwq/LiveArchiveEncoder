# 插件契约（草稿，v8.0 / M3 实现依据）

> 状态：**草稿**。M3 照它写代码；M5 冻结。
> 冻结之前每一处都可以改，改了就同步改这里——`Lae.Plugins` 的测试要能指着这篇文档说话。
>
> 读者：要写插件的人，以及三个月后实现宿主的人（很可能是同一个人）。

## 0. 一句话

插件是**独立进程**。宿主不加载 DLL、不共享内存、不传对象：只拼一条命令行、读它的
stdout、转发它的 stderr、然后校验它写出来的文件。崩溃的插件代价是一个退出码，不是整次编码。

## 1. 三个位置（placement）

只有三个，定义在 `Lae.Core/Plugins/PluginStages.cs`：

| placement | 什么时候跑 |
| --- | --- |
| `post-encode` | 录完之后，二次压缩链之前 |
| `between-passes` | 压缩链的遍与遍之间 |
| `after-chain` | 整条链跑完之后（压缩链关闭时，这就是录完之后） |

**没有 `pre-encode`。** 直播采集的源是流，不是文件；编码开始之前不存在一个"成品"可以交给
插件。协议里放一个永远跑不到的位置，只会让写插件的人以为它有效。

### 1.1 链条关闭时 `between-passes` 没有位置

`ChainSettings.Enabled = false` 时没有"遍与遍之间"。此时声明了 `between-passes` 的 hop
**不运行**，宿主在报告里写一句"链条关闭，N 个 between-passes 的 hop 未运行"。
静默跳过会让"我配了插件但它没动"变成一个查不出原因的现象。

### 1.2 一处命名歧义（实现时照这张表，别凭直觉）

`stage` 这个词在协议里指两件不同的东西，`PluginRegistration.Stages` 的名字也不帮忙：

| 出现在哪里 | 指的是 |
| --- | --- |
| 设置 `[[plugins]] stages = [...]` | **placement**（宿主什么时候调它） |
| 握手 `stages[].placement` | **placement** |
| 握手 `stages[].id` | **插件自己的子命令名**（`run --stage <id>` 的那个 `<id>`） |
| 执行 `run --stage <id>` | **插件自己的子命令名** |

一个插件可以只有一个子命令却有三种 placement（`lae-damage` 就是这样）。反过来，
一个插件可以有多个子命令（`damage` / `blur`），各自的 placement 可以不同。

`PluginRegistration.Stages` 实际是 placement；这个名字在 M3 期间不改（改了要动设置迁移），
但**宿主内部一律用 `placement` 这个词**，只在读写设置时翻译。

## 2. 握手

每次运行前调一次，结果缓存在本次进程内。多次 encode 共用一个进程时（`repl`、将来的
菜单）不会重复调用。

```
<plugin> --lae-handshake
```

stdout 的第一行就是全部握手，其余内容一律忽略。**一行是最稳的写法**：

- 第一行能单独解析 → 就用它，后面的日志随便打。
- 第一行解析不了 → 宿主再试整段 stdout 一次。所以下面这个缩进的多行示例也能用，
  但多行写法**后面不能再跟日志**，否则整段不是 JSON。

两种都合法；一行的那种子在任何情况下都不会因为日志而失效。

```json
{
  "schema": 1,
  "protocol": 1,
  "id": "lae-damage",
  "version": "0.1.0",
  "media": ["video"],
  "deterministic": true,
  "stages": [
    {
      "id": "damage",
      "placement": ["post-encode", "between-passes", "after-chain"],
      "params": [
        { "name": "intensity", "type": "float", "min": 0, "max": 1, "default": 0.25 },
        { "name": "seed", "type": "int", "default": 0 }
      ]
    }
  ]
}
```

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `schema` | 是 | 握手 JSON 的形状版本。宿主不认识的更高版本 → 拒绝并说明 |
| `protocol` | 是 | 调用协议版本（`run` 的参数怎么拼）。`1` 是本文档描述的这套 |
| `id` | 是 | 与设置里的 `[[plugins]] id` 一致；不一致 → 拒绝（配错了插件） |
| `version` | 否 | 只进报告，不参与比较 |
| `media` | 否 | 默认 `["video"]`。`audio` / `image` 留给以后 |
| `deterministic` | 否 | 默认 `true`。`false` 时宿主在报告里注明"这一步不可复现" |
| `stages[].id` | 是 | 插件自己的子命令名 |
| `stages[].placement` | 是 | 必须至少含一个已知 placement；含未知值的忽略并在警告里列出 |
| `stages[].params[].type` | 是 | `float` / `int` / `string` / `bool` |

**握手失败的处置**：超时（默认 10 秒）、非零退出、JSON 不合法、`schema`/`protocol` 不认识
——任一情况都不运行这个插件，报告里一句话说清是哪一种，退出码 `4`（`PluginError`）。
**不降级、不猜**：一个握手说不清自己是什么的插件，跑起来只会写出一个说不清的文件。

## 3. 执行

```
<plugin> run --stage <id> --input <abs> --output <abs.part> --param k=v
```

- 只认 `--key value` 和上面 `--param k=v` 两种形式。**不让插件解析字符串**：路径由宿主用
  `ArgumentList` 拼，含空格和中文的路径不需要引号。
- `--input` / `--output` 一律是**绝对路径**。
- 工作目录是宿主建的临时目录，插件往里写临时文件不必自己清理（宿主会删）。
- `--output` 指向的是 `.part`：**插件成功返回不等于产物可用**，见 §4。
- 插件可以在 stdout 上发事件（§6）；不发也合法。

### 3.1 参数校验

宿主按握手的 `params` 校验设置里的值：未知参数名 → 拒绝；超出 `min`/`max` → 拒绝；
缺省时用 `default`。校验发生在**开跑之前**，不是调用插件的时候——错误应该在一毫秒内出现，
而不是录完二十分钟之后。

## 4. 产物校验（三层）

v7 的验收是"`.part` 存在且长度 > 0"，对黑盒插件不够：一个截断的 MP4 也满足。三层：

| 层 | 检查 | 失败时 |
| --- | --- | --- |
| 1 | `.part` 存在且 > 0 字节 | 视为失败 |
| 2 | `ffprobe` 读得出容器，且至少一条视频流 | 视为失败 |
| 3 | 时长与输入相差 ≤ 1 秒 | 视为失败 |

任一层失败 → 退出码 `5`（`VerificationFailed`），**保留 `.part` 供诊断，不 Promote**。
`PostChainRun` 已经把"失败保留 part"这条策略写好了，插件 hop 直接沿用。

第 3 层防的是"只处理了前 10% 就返回 0"——这是黑盒插件最可能的坏法，也是最难靠肉眼发现的。

## 5. 发现顺序

```
1. --plugin <path>                      显式（调试）
2. chain 里 inline 写 command           临时
3. LAE_PLUGINS 环境变量                  CI
4. <exe 目录>/plugins/<id>/              便携分发主路径
5. <配置目录>/plugins/<id>/              用户级安装
6. PATH 上的 lae-<id>
```

第 4 条要用 `AppContext.BaseDirectory` 的**原始**路径。AOT 单文件在运行时会解压到一个临时
目录，`Assembly.Location` / 解压路径都会指错地方——v7 在 `Toolchain.Home` 上踩过一次，
M3 要为这条单独写测试。

发现阶段的错误（目录里有 `plugin.json` 但命令不存在）要报出来，不是跳过。

## 6. 插件事件（可选）

`--json-events` 传给插件时，插件可以在 stdout 上逐行发 JSON：

```json
{"event":"progress","percent":0.42}
{"event":"log","level":"warn","message":"frame 1200 looks like a duplicate"}
```

宿主把它翻译成自己的事件流（`IEventSink.Progress` / `Warn`），所以插件进度和录像进度在同
一条流上。不认识的 `event` 忽略；不是 JSON 的行当成普通日志（和 stderr 一样转发）。

## 7. 超时、取消、退出码

| 情况 | 处理 | 退出码 |
| --- | --- | --- |
| 握手超时（10 s） | 不运行该插件 | 4 |
| hop 超时（`max(300 s, 4 × 时长 + 120 s)`，与 FFmpeg hop 同一公式） | 杀进程树，保留 part | 4 |
| 插件退出码 `0` 但产物没通过三层校验 | 不 Promote，保留 part | 5 |
| 插件退出码 `2` | 用法错：宿主拼错了参数，或插件版本不兼容 | 4，并在报告里点名"这是用法错，不是插件失败" |
| 插件退出码其他非零 | 插件失败；stderr 尾部原样带进报告 | 4 |
| Ctrl+C | 杀进程树，丢 part，上抛 `OperationCanceledException` | 130 |

`2` 单独处理是因为它是**宿主**的错，不是插件的错。把它混进"插件失败"会让排查方向从一开始
就错了。

插件 stderr 一律转发（带 `plugin <id>: ` 前缀），**不当错误**：FFmpeg 也往 stderr 写正常信息。

## 8. 写一个插件（任意语言）

| 平台 | 形态 |
| --- | --- |
| Windows | `lae-damage.exe`，或 `.cmd` / `.bat` |
| Linux | 有可执行位的脚本，带 shebang（`#!/usr/bin/env python3`） |
| 都行 | `plugin.json` 里写 `"command": ["python3", "-m", "lae_damage"]`——这样不用管可执行位 |

`plugin.json`（可选，放在插件目录里）：

```json
{
  "id": "lae-damage",
  "command": ["python3", "-m", "lae_damage"]
}
```

有 `plugin.json` 时以它为准，否则 `<id>` 目录里的 `<id>` / `<id>.exe` 就是入口。

最小可用的假插件（M3 的测试夹具，约 20 行）要做四件事：响应 `--lae-handshake`、
接受 `run --stage ...`、把输入复制/截断成输出、可选地返回一个坏产物用来测 §4。

## 9. 已定与未决

### 9.1 已定：hop 是一个联合，不是两个列表

一个 step 要么是 FFmpeg 片段，要么是一次插件调用，由写了的字段决定（`ChainStep.Kind`）：

```toml
[[defaults.recompress.passes]]
args = "-c:v libx264 -crf 28"     # → Ffmpeg
container = "mp4"

[[defaults.recompress.passes]]
plugin = "lae-damage"             # → Plugin
stage = "damage"

[defaults.recompress.passes.params]
intensity = "0.25"
```

两半都写 → `Ambiguous`，**报错而不是按优先级挑一个**：输掉的那一半会留在文件里，
看起来还在起作用。

**键名仍然是 `recompress` 和 `passes`**，虽然类型已经叫 `ChainSettings` / `Steps`。改键名
意味着整体重渲文件，而重渲会丢掉用户写的每一行注释；`TomlSettingsStore` 是外科式回写的，
这条不能破。代码里可以叫得诚实，文件里保持用户的。

插件 hop 目前**在计划阶段被拒**（`plan.error.chainPluginNotReady`，exit 2），不是静默跳过：
跳过会交付一个链没描述过的产物，而运行说"完成"、文件也在，用户没有任何线索。

### 9.2 未决

1. **插件能不能自己 spawn 别的插件**：默认**不能**，组合关系由宿主的 chain 表达。
   蓝图提过一个默认关闭的 `--allow-plugin-spawn` 逃生口，目前不实现。
2. **`between-passes` 在链有 3 遍时插几次**：暂定"每遍之间各一次"（N 遍 → N−1 次），
   首尾用 `post-encode` / `after-chain`。这条要拿 `lae-damage` 真跑一次再确认。
