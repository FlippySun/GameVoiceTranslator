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
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameVoiceTranslator");
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

    public string AsrModel { get; set; } = "fun-asr-realtime";
    public string TtsModel { get; set; } = "cosyvoice-v3-flash";
    public int TtsSampleRate { get; set; } = 48000;

    /// <summary>mt = qwen-mt 专用翻译模型（支持术语）；llm = 通用模型 + 提示词。</summary>
    public TranslatorKind Translator { get; set; } = TranslatorKind.Mt;
    public string MtModel { get; set; } = "qwen-mt-flash";
    public string LlmModel { get; set; } = "qwen-flash";

    public int RequestTimeoutMs { get; set; } = 8000;

    /// <summary>可选：直接指定 WebSocket 地址（如千问AI平台 wss://cn-beijing.maas.qianwenaiapi.com/api-ws/v1/inference）。</summary>
    public string WebSocketUrlOverride { get; set; } = "";
    /// <summary>可选：直接指定 OpenAI 兼容地址（含 /compatible-mode/v1 之类的前缀）。</summary>
    public string CompatibleBaseUrlOverride { get; set; } = "";

    [JsonIgnore]
    public string WebSocketUrl => !string.IsNullOrWhiteSpace(WebSocketUrlOverride) ? WebSocketUrlOverride : string.IsNullOrWhiteSpace(WorkspaceId)
        ? "wss://dashscope.aliyuncs.com/api-ws/v1/inference"
        : $"wss://{WorkspaceId}.{Region}.maas.aliyuncs.com/api-ws/v1/inference";

    [JsonIgnore]
    public string CompatibleBaseUrl => !string.IsNullOrWhiteSpace(CompatibleBaseUrlOverride) ? CompatibleBaseUrlOverride : string.IsNullOrWhiteSpace(WorkspaceId)
        ? "https://dashscope.aliyuncs.com/compatible-mode/v1"
        : $"https://{WorkspaceId}.{Region}.maas.aliyuncs.com/compatible-mode/v1";
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
    public string Voice { get; set; } = "loongandy_v3";
    public string TtsLanguageHint { get; set; } = "en";
    public double TtsRate { get; set; } = 1.1;
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
    public string Voice { get; set; } = "longanyang";
    public string TtsLanguageHint { get; set; } = "zh";
    public double TtsRate { get; set; } = 1.15;
    public float TranslationVolume { get; set; } = 1.0f;
    public ReceiveListenMode ListenMode { get; set; } = ReceiveListenMode.TranslationFirst;

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
}

public sealed class VadConfig
{
    public string SileroModelPath { get; set; } = "models/silero_vad.onnx";
    public float StartThreshold { get; set; } = 0.5f;
    public float EndThreshold { get; set; } = 0.35f;
    public int MinSpeechMs { get; set; } = 160;
    public int EndSilenceMs { get; set; } = 450;
    public int PreRollMs { get; set; } = 300;
    public int MaxUtteranceMs { get; set; } = 12000;
    /// <summary>Silero 不可用时的能量阈值（dBFS）。</summary>
    public float EnergyThresholdDb { get; set; } = -42f;
}

public sealed class BudgetConfig
{
    public double SessionBudgetYuan { get; set; } = 5.0;
    public double AsrYuanPerSecond { get; set; } = 0.00033;
    public double TtsYuanPer10kChars { get; set; } = 1.0;
    public double MtYuanPerMInputTokens { get; set; } = 0.7;
    public double MtYuanPerMOutputTokens { get; set; } = 1.95;
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
    public int MaxLines { get; set; } = 5;
}
