# 第三方组件说明 / Third-Party Notices

本项目使用或引用了以下第三方组件，各自遵循其原始许可证。

| 组件 | 用途 | 许可证 | 来源 |
|---|---|---|---|
| NAudio 2.2.1 | WASAPI 音频采集与播放 | MIT | https://github.com/naudio/NAudio |
| Microsoft.ML.OnnxRuntime | 运行 Silero VAD 模型 | MIT | https://github.com/microsoft/onnxruntime |
| Silero VAD（`models/silero_vad.onnx`） | 语音活动检测 | MIT | https://github.com/snakers4/silero-vad |
| System.Security.Cryptography.ProtectedData | DPAPI 加密保存 API Key | MIT | https://github.com/dotnet/runtime |
| .NET 运行时（安装包内置） | 运行环境 | MIT | https://github.com/dotnet/runtime |
| Inno Setup | 制作安装包（仅构建时使用） | Inno Setup License | https://jrsoftware.org/isinfo.php |
| Inno Setup 简体中文语言文件（`installer/ChineseSimplified.isl`） | 安装界面中文 | 随 Inno Setup 分发 | https://github.com/jrsoftware/issrc |

## 不随本项目分发的软件

- **VB-CABLE Virtual Audio Device、Hi-Fi Cable & ASIO Bridge**（© Vincent Burel / VB-Audio Software）：捐赠软件，不包含在本仓库和安装包中。
  软件里的“安装环境”按钮会在用户点击时，从 VB-Audio 官网（`download.vb-audio.com`）下载官方安装包，
  校验 Authenticode 签名（必须有效且由 VB-Audio 作者签名）后再安装。授权说明见 https://vb-audio.com/Services/licensing.htm 。
- **阿里云百炼（DashScope）**：云端识别、翻译、合成服务，需要用户自己的账号和 API Key，按阿里云的服务条款与价格计费。

## 商标

Call of Duty、Modern Warfare 是 Activision Publishing, Inc. 的商标；KOOK、Discord 及阿里云等名称归各自所有者。本项目与上述公司均无关联，提及它们仅为说明兼容的使用场景。
