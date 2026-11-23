# 参与贡献

感谢你愿意改进游戏双译！

## 提 Issue
- **Bug**：请写清楚系统版本、软件版本、复现步骤，并附上 `%APPDATA%\GameVoiceTranslator\logs` 里对应时间的 `app-*.log`（发之前检查一下，里面不会有 API Key，但可能有你说过的话）。
- **翻译 / 识别不准**：最好给出原话、识别结果和译文（诊断页或 `latency-*.csv` 里都有）。

## 提 PR
1. Fork 后从 `main` 新建分支。
2. 需要 Windows 10/11 + .NET 10 SDK。提交前请确认：
   ```powershell
   dotnet build
   dotnet test
   ```
3. 改动翻译提示词、术语表或过滤规则时，请跑一下语料回归，并在 PR 里贴出前后对比（需要自己的 API Key）：
   ```powershell
   dotnet run --project src/GVT.Replay -- mt corpus/zh2en.tsv
   dotnet run --project src/GVT.Replay -- mt corpus/en2zh.tsv --dir en2zh
   ```
4. 界面改动请附截图（`GameVoiceTranslator.exe --shot <目录> --demo` 可以只渲染本软件的界面，不会截到桌面）。

## 约定
- 代码注释和界面文字用中文；面向用户的文字尽量口语、说清楚“发生了什么、该怎么做”。
- 不要提交任何 API Key、录音或日志；不要把 VB-Audio 的安装包加进仓库（它不允许再分发）。
- 提交即表示你同意以 GPL-3.0 协议授权你的贡献。
