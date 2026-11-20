# 游戏双向语音翻译 · 第一周验证版

只在自己电脑上安装：你说中文，队友听到英文；队友说英文，你在耳机里听到中文，游戏音效照常。
首个目标场景：**Call of Duty: Modern Warfare 4（DMZ）** 与 **Discord**。

```
发送：真实麦克风 →（按住说话）→ 中文识别 → 中译英 → 英文合成 → VB-CABLE → 游戏把 CABLE Output 当麦克风
接收：游戏“语音聊天输出设备”= Hi-Fi Cable → 语音检测 → 英文识别 → 英译中 → 中文合成 ┐
                                       └→ 队友原声（可压低 / 按住放大）────────────┴→ 耳机
游戏音效：直接走游戏自己的“游戏音效设备”到耳机，不经过本程序。
```

云服务全部使用阿里云百炼（北京）：`fun-asr-realtime` 识别、`qwen-mt-flash` 翻译（可切 `qwen-flash`）、`cosyvoice-v3-flash` 合成。
按官方价目粗估，**一小时游戏约 1–3 元**；开发测试阶段基本在免费额度内。

---

## 一、一次性准备

### 1. 安装虚拟声卡（两条免费的 VB-Audio 虚拟线）
VB-CABLE A+B 需要付费购买后才能下载，所以改用两条免费的 VB-Audio 虚拟线，效果相同：

| 用途 | 驱动 | 播放端（往里放声音） | 录音端（从里面取声音） |
|---|---|---|---|
| 发送：英文译音 → 游戏麦克风 | VB-CABLE（[VBCABLE_Driver_Pack46.zip](https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip)） | `CABLE Input` | `CABLE Output` |
| 接收：游戏队友语音 → 本程序 | Hi-Fi Cable（[HiFiCableAsioBridgeSetup_v1007.zip](https://download.vb-audio.com/Download_CABLE/HiFiCableAsioBridgeSetup_v1007.zip)） | `Hi-Fi Cable Input` | `Hi-Fi Cable Output` |

安装：以管理员身份运行 `VBCABLE_Setup_x64.exe -i -h` 和 `HiFiCableAsioBridgeSetup.exe -i -h`。
**安装后 Windows 会把默认播放设备切到虚拟线，必须改回耳机**，可以用下面的命令，或者在 Windows 声音设置里手动改：
`dotnet run --project src/GVT.Replay -- set-default render "你的耳机名称"`
用 `... -- cable-test "CABLE Input (VB-Audio Virtual Cable)" "CABLE Output (VB-Audio Virtual Cable)"` 可以验证虚拟线是否能传声音。

### 2. Windows 声音设置（重要）
- 控制面板 → 声音 →“通信”选项卡 → 选 **“不执行任何操作”**。否则 Windows 一检测到“通话”就会把游戏音量压低 80%，脚步声会变小。
- 建议使用有线或 2.4G 耳机。蓝牙耳机开启麦克风后会切到通话模式，音质和采样率都会明显下降。

### 3. 百炼 API Key（必须是“按量付费”Key）
1. 在千问AI平台 / 百炼控制台的“按量付费”区域点“创建 API Key”（sk-ws- 开头）。**Token Plan / Coding Plan 个人版的 sk-sp- Key 不能用**：它只允许在 Cursor、Claude Code 这类开发工具里交互使用，也不包含实时语音识别和合成。
2. 在模型广场确认以下模型可用：`fun-asr-realtime`、`qwen-mt-flash`、`cosyvoice-v3-flash`（可选：`qwen-flash`）。
3. 打开本程序，把 Key 粘贴进第 1 栏并保存。Key 用 Windows 当前用户加密保存，不会以明文写到磁盘上，也可以改用环境变量 `DASHSCOPE_API_KEY`。

### 4. MW4 设置（设置 → 音频）
| 项目 | 选择 |
|---|---|
| 游戏音效设备（Speakers/Headphones Game Sound Device） | 你的耳机（不变） |
| **语音聊天输出设备（Voice Chat Output Device）** | **Hi-Fi Cable Input** |
| **麦克风输入设备（Microphone Input Device）** | **CABLE Output** |
| 语音录制方式 | **开放麦克风**（按住说话由本程序负责；虚拟麦克风里只有译音，开放麦克风是安全的） |

> 显示模式请用**无边框全屏**，否则字幕小窗可能被游戏盖住。快捷键在独占全屏下也能用。

### 5. Discord 设置（用 Discord 和朋友测试时）
- 输入设备：`CABLE Output`；输出设备：`Hi-Fi Cable Input`
- 输入模式：语音激活，并**关闭“自动确定输入灵敏度”**，手动把门限调低
- 关闭降噪（Krisp）、回声消除、自动增益控制（它们可能削弱合成语音）
- 关闭“语音 → 衰减”

---

## 二、使用

1. 运行 `GameVoiceTranslator.exe`（开发时：`dotnet run --project src/GVT.App`）。
2. 检查四个设备选择，点 **开始翻译**。
3. 左侧状态区有两个电平条：说话时左边的条应该动，队友说话时右边的条应该动。

| 快捷键 | 作用 |
|---|---|
| 鼠标前侧键（XButton2），按住 | 说话。松开后，已说完的这句会继续翻译并完整发出 |
| F7 | **取消本句**：丢弃正在录的句子和尚未发出的英文。已经播出去的无法撤回 |
| F8 | **停止/恢复发送**：立即静音虚拟麦克风并清空待发内容 |
| F9，按住 | **听原声**：暂停中文播报，队友原声恢复到正常音量 |
| F10 | 在自己耳机里重播上一句中文（不会发给队友） |
| F11 | 停止/恢复中文播报（原声照常） |
| F6 | **编辑浮窗**：可拖动位置、拖右下角改大小、滚轮改字号；再按一次锁定并保存 |

快捷键可在 `%APPDATA%\GameVoiceTranslator\config.json` 的 `Hotkeys` 中修改；按键名使用 WPF Key 名称，如 `F7`、`CapsLock`、`OemTilde`，鼠标侧键为 `XButton1`/`XButton2`。

### 声音（“声音”页可试听）
- 默认使用 `qwen-audio-3.1-tts-flash`，英文 Andy、中文龙安洋，风格“开黑口语”：通过自然语言指令让合成语音更像朋友语音聊天，而不是播音腔。
- 可选风格：开黑口语 / 冷静报点 / 标准；也可以填自定义英文风格描述。修改后下一句立即生效。
- 旧版 CosyVoice 播报腔音色保留为“Andy（旧版）”，用于对比。

**安全设计：**
- 发送方向出任何故障，虚拟麦克风都只会静音，**绝不会自动改发中文原声**。
- 本程序意外崩溃时，守护进程 `GVT.Watchdog.exe` 会弹出一个窗口，并把队友原声直接转到耳机。重新打开本程序后，它会自动退出。
- 不注入游戏进程，不 Hook DirectX；快捷键通过 Raw Input 读取。
- 注意：COD 有 AI 语音审核。你的英文译音会以你的账号身份被审核。

---

## 三、第一周验证清单

| # | 验证 | 怎么做 | 通过标准 |
|---|---|---|---|
| 1 | 翻译质量与速度（不需要声卡） | `dotnet run --project src/GVT.Replay -- mt corpus/zh2en.tsv`，再加 `--translator llm` 跑一遍对比 | 关键词检查（否定、方向、数量、改口）全部通过 |
| 2 | 英译中质量 | `... -- mt corpus/en2zh.tsv --dir en2zh` | 同上 |
| 3 | 完整链路延迟（不需要声卡） | `... -- synth corpus/zh2en.tsv --out samples/zh`，然后 `... -- run samples/zh` | 句末到开始播放 P50 ≤ 1.5 s |
| 4 | 本机接线 | 开始翻译后，用 Windows“录音机”录 `CABLE Output`，按住侧键说一句中文 | 录音里只有英文译音，没有你的中文原声 |
| 5 | Discord 双向 | 手机登录另一个 Discord 账号，和电脑在同一语音频道 | 手机听到英文；对手机说英文，电脑耳机里听到中文；双方都停止说话后完全安静 |
| 6 | MW4 实测 | 按上表设置后进一局 DMZ | 队友能听到译音；中文播报期间脚步声、枪声正常 |
| 7 | 故障 | 打一局中途拔网线、拔耳机，或在任务管理器结束主程序 | 状态提示清楚；崩溃后能听到队友原声 |

每句话的延迟都会写到 `%APPDATA%\GameVoiceTranslator\logs\latency-*.csv`，各列依次是：句末到识别完成、到翻译完成、到合成首包、到开始播放（毫秒）。

### 可调参数（config.json）
- `Send.TtsRate` / `Receive.TtsRate`：译音语速（0.5–2.0）
- `Send.Voice`：英文音色，默认 `loongandy_v3`（美式男声），可改为 `loongabby_v3` 等；`Receive.Voice`：中文音色，默认 `longanyang`
- `Receive.ListenMode` 及 `OriginalGain*`：原声在各种状态下的音量
- `Vad.EndSilenceMs`：队友停顿多久算一句话结束（越小越快，但越容易把一句话切断）
- `Send.LeadInMs`：如果游戏吞掉了译音的第一个音节，把它改成 150 左右试试
- `Budget.SessionBudgetYuan`：单次会话预算，达到后两个方向都会自动暂停
- `Budget.MaxSlotAgeSeconds`：排队超过该秒数仍未播放的译音会被丢弃（过时指令不发）

### 词库
`%APPDATA%\GameVoiceTranslator\glossary.json`（首次运行时从 `src/GVT.App/glossary.default.json` 复制）。
词条同时用于两件事：一是提升识别率（作为识别上下文，最多约 380 字），二是约束翻译（作为 qwen-mt 的术语干预）。暂不影响合成发音。
别名不要用太泛的词，比如“后面”“东西”，否则正常句子也会被强制翻成游戏术语。

---

## 四、项目结构

```
src/GVT.Core      核心：音频（采集/重采样/混音/有序播放队列/VAD）、阿里云客户端、双通道调度、取消机制
src/GVT.App       WPF 主界面、游戏内字幕小窗（鼠标穿透）、Raw Input 全局快捷键
src/GVT.Watchdog  崩溃守护：主程序意外退出时恢复队友原声
src/GVT.Replay    离线回放测试：翻译语料、合成测试音频、完整链路延迟
tests/GVT.Tests   单元测试（不需要网络和声卡），重点覆盖取消、顺序、回声、过时内容
corpus/           中英关键语义测试语料
models/           Silero VAD 模型
```

取消机制：每个方向维护一个 epoch。取消时 epoch 加一，之前所有句子的识别、翻译、合成结果，在之后的每一步都会因为 epoch 不匹配而被丢弃；播放队列中的句子（包括正在播放的）也会立即停止。所以迟到、重试或重连得到的旧结果都不可能再播出来。

## 五、已知限制 / 下一步
- 设备断开后不会自动重连，需要点“停止”再“开始”。这是有意的：不会悄悄换成别的设备。
- 自己的译音试听、紧急短语、文字转语音、战斗模式还没做（P1）。
- 多人同时说话时，接收端是混在一起识别的，效果需要专项测试。
- 端到端同传模型（豆包同传 2.0、qwen-livetranslate）暂未接入，留作对照组。
