using System.Text.Json;
using System.Text.Json.Serialization;

namespace GVT.Core.Config;

/// <summary>
/// 全部可调参数。首次运行时写到 %APPDATA%\GameVoiceTranslator\config.json，之后以该文件为准。
/// 设备名按“包含该子串”匹配，留空表示 Windows 默认设备。
/// </summary>
public sealed class AppConfig
{
    public DashScopeConfig DashScope { get; set; } = new();
    public SendConfig Send { get; set; } = new();
    public ReceiveConfig Receive { get; set; } = new();
    public HotkeyConfig Hotkeys { get; set; } = new();
    public VadConfig Vad { get; set; } = new();
    public BudgetConfig Budget { get; set; } = new();
    public OverlayConfig Overlay { get; set; } = new();
    public DebugConfig Debug { get; set; } = new();
    public GeneralConfig General { get; set; } = new();
    /// <summary>音色设计 / 声音复刻得到的自定义音色。</summary>
    public List<CustomVoiceConfig> CustomVoices { get; set; } = new();

    /// <summary>词库文件路径；相对路径相对于配置目录。</summary>
    public string GlossaryFile { get; set; } = "glossary.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ConfigDirectory
    {
        get
        {
            // GVT_CONFIG_DIR：可选，指定配置目录（便携使用、测试、截图用）
            var custom = Environment.GetEnvironmentVariable("GVT_CONFIG_DIR");
            var dir = !string.IsNullOrWhiteSpace(custom) ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameVoiceTranslator");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
            cfg.Save(path); // 补齐新版本增加的字段
            return cfg;
        }
        var created = new AppConfig();
        created.Save(path);
        return created;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public string ResolveGlossaryPath()
        => Path.IsPathRooted(GlossaryFile) ? GlossaryFile : Path.Combine(ConfigDirectory, GlossaryFile);
}

public sealed class DashScopeConfig
{
    /// <summary>可选：业务空间 ID。填写后使用专属域名 {id}.cn-beijing.maas.aliyuncs.com。</summary>
    public string WorkspaceId { get; set; } = "";
    public string Region { get; set; } = "cn-beijing";

    /// <summary>两个方向没有单独指定时用的识别模型（各方向见 Send.AsrModel / Receive.AsrModel）。</summary>
    public string AsrModel { get; set; } = "fun-asr-realtime";
    /// <summary>音色不在内置列表时使用的合成模型。</summary>
    public string TtsModel { get; set; } = "qwen-audio-3.1-tts-flash";
    public int TtsSampleRate { get; set; } = 48000;

    /// <summary>llm = 通用模型 + 规则提示词（默认，实测改口和主语更准）；mt = qwen-mt 专用翻译模型。</summary>
    public TranslatorKind Translator { get; set; } = TranslatorKind.Llm;
    public string MtModel { get; set; } = "qwen-mt-flash";
    public string LlmModel { get; set; } = "qwen-flash";

    public int RequestTimeoutMs { get; set; } = 8000;
    /// <summary>音频推完后等识别结果的上限。正常 0.3 秒内返回；超时就换新连接重试一次。</summary>
    public int AsrFinishTimeoutMs { get; set; } = 3500;
    /// <summary>把术语表作为识别上下文传给识别服务。实测听不清时模型会把术语表原样吐出来（“撤离，跑路，撤离点……”），默认关闭。</summary>
    public bool UseGlossaryAsAsrContext { get; set; } = false;
    /// <summary>把术语表里标了 Hotword 的游戏词做成识别热词表（只让发音相近时偏向正确写法，不会被吐出来）。</summary>
    public bool UseAsrHotwords { get; set; } = true;

    /// <summary>可选：直接指定 WebSocket 地址（如千问AI平台 wss://cn-beijing.maas.qianwenaiapi.com/api-ws/v1/inference）。</summary>
    public string WebSocketUrlOverride { get; set; } = "";
    /// <summary>可选：直接指定 OpenAI 兼容地址（含 /compatible-mode/v1 之类的前缀）。</summary>
    public string CompatibleBaseUrlOverride { get; set; } = "";

    [JsonIgnore]
    public string WebSocketUrl => !string.IsNullOrWhiteSpace(WebSocketUrlOverride) ? WebSocketUrlOverride : string.IsNullOrWhiteSpace(WorkspaceId)
        ? "wss://dashscope.aliyuncs.com/api-ws/v1/inference"
        : $"wss://{WorkspaceId}.{Region}.maas.aliyuncs.com/api-ws/v1/inference";

    /// <summary>DashScope 原生 HTTP 接口前缀（/api/v1），与 OpenAI 兼容地址同域名。</summary>
    [JsonIgnore]
    public string ApiBaseUrl => CompatibleBaseUrl.Replace("/compatible-mode/v1", "/api/v1");

    [JsonIgnore]
    public string CompatibleBaseUrl => !string.IsNullOrWhiteSpace(CompatibleBaseUrlOverride) ? CompatibleBaseUrlOverride : string.IsNullOrWhiteSpace(WorkspaceId)
        ? "https://dashscope.aliyuncs.com/compatible-mode/v1"
        : $"https://{WorkspaceId}.{Region}.maas.aliyuncs.com/compatible-mode/v1";
}

public sealed class DebugConfig
{
    /// <summary>把每句识别用的音频和识别结果存到 logs\utterances，用来对比、改进识别模型。只存在本机。</summary>
    public bool SaveUtteranceAudio { get; set; } = false;
}

public sealed class CustomVoiceConfig
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "qwen-audio-3.1-tts-flash";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Language { get; set; } = "en";
}

public enum TranslatorKind { Mt, Llm }

public enum GateMode { PushToTalk, VoiceActivity }

public enum ReceiveListenMode
{
    /// <summary>中文译音为主，原声压低。</summary>
    TranslationFirst,
    /// <summary>原声与译音都保留，译音播放时原声适度压低。</summary>
    Both,
}

public sealed class SendConfig
{
    public bool Enabled { get; set; } = true;
    public string MicDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "CABLE Input (VB-Audio Virtual Cable)";
    public GateMode Gate { get; set; } = GateMode.PushToTalk;
    public string AsrLanguageHint { get; set; } = "zh";
    /// <summary>识别我说的中文的模型。实测 fun-asr-realtime（配游戏热词）在中文上最稳。</summary>
    public string AsrModel { get; set; } = "fun-asr-realtime";
    public string Voice { get; set; } = "Andy_v3.1";
    public VoiceStyle Style { get; set; } = VoiceStyle.Casual;
    /// <summary>自定义说话风格（自然语言），填写后覆盖 Style。</summary>
    public string CustomInstruction { get; set; } = "";
    public double TtsRate { get; set; } = 1.05;
    public TtsVoice ToTts(string fallbackModel) => VoiceCatalog.Resolve(Voice, TtsRate, Style, CustomInstruction, fallbackModel, "en");
    public float OutputVolume { get; set; } = 1.0f;
    /// <summary>按键开始前保留的音频（毫秒），防止吞掉第一个字。</summary>
    public int PreRollMs { get; set; } = 200;
    /// <summary>每段译音前垫一小段低电平噪声，帮助游戏的语音激活及时打开。0 = 关闭。</summary>
    public int LeadInMs { get; set; } = 0;
    public float LeadInLevel { get; set; } = 0.01f;
}

public sealed class ReceiveConfig
{
    public bool Enabled { get; set; } = true;
    public string SourceDevice { get; set; } = "Hi-Fi Cable Output (VB-Audio Hi-Fi Cable)";
    public string HeadphoneDevice { get; set; } = "";
    public string AsrLanguageHint { get; set; } = "en";
    /// <summary>
    /// 识别队友说话的模型。qwen3-asr-flash-realtime 对口音、压缩过的游戏语音明显更准（2026-11-23 录音对比）；
    /// 用它时不指定语种、由模型自动判断，队友里有人说中文时会原样识别成中文（只显示不播报），不会被硬转成英文乱码。
    /// </summary>
    public string AsrModel { get; set; } = "qwen3-asr-flash-realtime";
    public string Voice { get; set; } = "longanyang_v3.1";
    public VoiceStyle Style { get; set; } = VoiceStyle.Casual;
    public string CustomInstruction { get; set; } = "";
    public double TtsRate { get; set; } = 1.15;
    public TtsVoice ToTts(string fallbackModel) => VoiceCatalog.Resolve(Voice, TtsRate, Style, CustomInstruction, fallbackModel, "zh");
    public float TranslationVolume { get; set; } = 1.0f;
    public ReceiveListenMode ListenMode { get; set; } = ReceiveListenMode.TranslationFirst;
    /// <summary>yeah / okay / yep 这类附和语气词和不成句的单个词只显示字幕，不播报。</summary>
    public bool SkipBackchannel { get; set; } = true;
    /// <summary>翻译模型没把握时在中文播报前加的提示。</summary>
    public string LowConfidencePrefix { get; set; } = "可能是，";

    public float OriginalGainTranslationFirst { get; set; } = 0.35f;
    public float OriginalGainTranslationFirstDucked { get; set; } = 0.12f;
    public float OriginalGainBoth { get; set; } = 1.0f;
    public float OriginalGainBothDucked { get; set; } = 0.5f;

    /// <summary>把中文译音偏到原声所在的一侧（定向队伍语音）。</summary>
    public bool FollowSourcePan { get; set; } = true;

    /// <summary>与最近发出的英文相似度超过该值则视为回声（对方外放把我们的译音收了回来）。</summary>
    public double EchoSimilarityThreshold { get; set; } = 0.6;
    public int EchoWindowSeconds { get; set; } = 12;
}

public sealed class HotkeyConfig
{
    public string PushToTalk { get; set; } = "XButton2";
    public string CancelSentence { get; set; } = "F7";
    public string ToggleSend { get; set; } = "F8";
    public string HoldOriginal { get; set; } = "F9";
    public string ReplayLast { get; set; } = "F10";
    public string ToggleReceive { get; set; } = "F11";
    /// <summary>切换浮窗编辑模式（可拖动、调整大小）。</summary>
    public string OverlayEdit { get; set; } = "F6";
}

public sealed class VadConfig
{
    public string SileroModelPath { get; set; } = "models/silero_vad.onnx";
    public float StartThreshold { get; set; } = 0.5f;
    public float EndThreshold { get; set; } = 0.35f;
    public int MinSpeechMs { get; set; } = 160;
    public int EndSilenceMs { get; set; } = 450;
    /// <summary>识别中间结果停在半截（the / of / to…）时，多等到这么久的静音才断句。</summary>
    public int UnfinishedEndSilenceMs { get; set; } = 1000;
    public int PreRollMs { get; set; } = 300;
    public int MaxUtteranceMs { get; set; } = 12000;
    /// <summary>Silero 不可用时的能量阈值（dBFS）。</summary>
    public float EnergyThresholdDb { get; set; } = -42f;
}

public sealed class BudgetConfig
{
    /// <summary>每次点“开始”后的花费上限（元）；0 = 不限。</summary>
    public double SessionBudgetYuan { get; set; } = 5.0;
    public double AsrYuanPerSecond { get; set; } = 0.00033;
    public double TtsYuanPer10kChars { get; set; } = 1.0;
    public double MtYuanPerMInputTokens { get; set; } = 0.7;
    public double MtYuanPerMOutputTokens { get; set; } = 1.95;
    /// <summary>qwen-audio-3.1-asr-flash-message / -streaming：输入 6 元/百万 token、输出 4.5 元/百万 token。</summary>
    public double AsrYuanPerMInputTokens { get; set; } = 6.0;
    public double AsrYuanPerMOutputTokens { get; set; } = 4.5;
    /// <summary>同一句最多重试次数（目前仅用于翻译 HTTP 请求）。</summary>
    public int MaxRetries { get; set; } = 1;
    /// <summary>排队超过该秒数仍未开始播放的译音会被丢弃。</summary>
    public double MaxSlotAgeSeconds { get; set; } = 10;
}

public sealed class OverlayConfig
{
    public bool Visible { get; set; } = true;
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 220;
    public double Width { get; set; } = 560;
    public double FontSize { get; set; } = 18;
    public double Opacity { get; set; } = 0.85;
    public int MaxLines { get; set; } = 4;
    public double Height { get; set; } = 220;
    /// <summary>字幕在浮窗里停留多久后淡出（秒）。</summary>
    /// <summary>每条字幕停留多少秒后淡出；0 = 一直保留（直到被新字幕挤出浮窗）。</summary>
    public double HoldSeconds { get; set; } = 25;
    public bool ShowOriginal { get; set; } = true;
    /// <summary>“我说的”译文颜色（队友听到的英文）。</summary>
    public string MeTextColor { get; set; } = "#A6E6FF";
    /// <summary>“队友说的”译文颜色（我听到的中文）。</summary>
    public string MateTextColor { get; set; } = "#FFE2A8";
    /// <summary>原文（小字）颜色。</summary>
    public string OriginalTextColor { get; set; } = "#B8BEC9";
}

public enum CloseAction { Ask, MinimizeToTray, Exit }

public sealed class GeneralConfig
{
    /// <summary>点右上角关闭时：每次询问 / 最小化到托盘 / 彻底退出。</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;
    /// <summary>第一次最小化到托盘时弹一次系统通知，告诉用户程序还在后台。</summary>
    public bool TrayHintShown { get; set; }
}
