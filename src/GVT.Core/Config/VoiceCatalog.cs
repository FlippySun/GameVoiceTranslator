namespace GVT.Core.Config;

/// <summary>一次合成所需的全部参数。</summary>
public sealed record TtsVoice(string Model, string Voice, double Rate, string? LanguageHint, string? Instruction);

public enum VoiceStyle
{
    /// <summary>开黑口语：放松、自然，像和朋友语音聊天（默认）。</summary>
    Casual,
    /// <summary>冷静报点：清楚、简短、语速快。</summary>
    Callout,
    /// <summary>标准：不加风格指令，即音色原本的播音风格。</summary>
    Standard,
}

public sealed record VoiceInfo(string Id, string Model, string Name, string Description, string Language, bool SupportsStyle);

/// <summary>
/// 可选音色。qwen-audio-3.1-tts-flash 与 CosyVoice 走同一套接口，但支持用自然语言指令控制说话风格，
/// 听起来更像真人聊天；CosyVoice v3 的英文音色偏“营销播报”，保留作对比。
/// </summary>
public static class VoiceCatalog
{
    public const string QwenAudio = "qwen-audio-3.1-tts-flash";
    public const string CosyV3 = "cosyvoice-v3-flash";
    public const string Qwen3 = "qwen3-tts-flash";

    public static readonly IReadOnlyList<VoiceInfo> English = new List<VoiceInfo>
    {
        new("Ryan", Qwen3, "Ryan", "Qwen3 · 美式男声 · 戏感真实（推荐试听）", "en", false),
        new("Aiden", Qwen3, "Aiden", "Qwen3 · 美语大男孩", "en", false),
        new("Ethan", Qwen3, "Ethan", "Qwen3 · 阳光活力男声", "en", false),
        new("Kai", Qwen3, "Kai", "Qwen3 · 温和男声", "en", false),
        new("Moon", Qwen3, "Moon", "Qwen3 · 率性帅气男声", "en", false),
        new("Vincent", Qwen3, "Vincent", "Qwen3 · 沙哑烟嗓男声", "en", false),
        new("Andy_v3.1", QwenAudio, "Andy", "美式男声 · 年轻随和", "en", true),
        new("Brian_v3.1", QwenAudio, "Brian", "美式男声 · 低沉稳重", "en", true),
        new("David_v3.1", QwenAudio, "David", "美式男声 · 成熟", "en", true),
        new("Eric_v3.1", QwenAudio, "Eric", "英式男声", "en", true),
        new("Luca_v3.1", QwenAudio, "Luca", "英式男声 · 年轻", "en", true),
        new("Cally_v3.1", QwenAudio, "Cally", "美式女声 · 年轻", "en", true),
        new("Abby_v3.1", QwenAudio, "Abby", "美式女声", "en", true),
        new("Ava_v3.1", QwenAudio, "Ava", "美式女声 · 成熟", "en", true),
        new("Luna_v3.1", QwenAudio, "Luna", "英式女声", "en", true),
        new("loongandy_v3", CosyV3, "Andy（旧版）", "美式男声 · 播报腔，较正式", "en", false),
    };

    public static readonly IReadOnlyList<VoiceInfo> Chinese = new List<VoiceInfo>
    {
        new("longanyang_v3.1", QwenAudio, "龙安洋", "阳光大男孩", "zh", true),
        new("longhuohuo_v3.1", QwenAudio, "龙火火", "顽皮少年音", "zh", true),
        new("longanzhi_v3.1", QwenAudio, "龙安智", "睿智轻熟男", "zh", true),
        new("longhan_v3.1", QwenAudio, "龙寒", "温暖男声", "zh", true),
        new("longxing_v3.1", QwenAudio, "龙星", "温婉邻家女", "zh", true),
        new("longhua_v3.1", QwenAudio, "龙华", "元气甜美女", "zh", true),
        new("longanyang", CosyV3, "龙安洋（旧版）", "阳光大男孩 · CosyVoice", "zh", false),
    };

    /// <summary>账号里通过音色设计 / 声音复刻创建的音色（启动时从配置载入）。</summary>
    public static List<VoiceInfo> Custom { get; } = new();

    public static void LoadCustom(AppConfig cfg)
    {
        Custom.Clear();
        foreach (var c in cfg.CustomVoices.Where(c => !string.IsNullOrWhiteSpace(c.Id)))
            Custom.Add(new VoiceInfo(c.Id, c.Model, c.Name, c.Description, c.Language, c.Model.StartsWith("qwen-audio", StringComparison.OrdinalIgnoreCase)));
    }

    public static IEnumerable<VoiceInfo> EnglishAll => Custom.Where(v => v.Language == "en").Concat(English);

    public static VoiceInfo? Find(string id)
        => Custom.Concat(English).Concat(Chinese).FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string? InstructionFor(VoiceStyle style, string language) => (style, language) switch
    {
        (VoiceStyle.Casual, "en") => "Speak casually and naturally, like a relaxed gamer talking to friends on voice chat. Not like a news anchor.",
        (VoiceStyle.Callout, "en") => "Speak like a quick, clear game callout: short, punchy and urgent, but natural.",
        (VoiceStyle.Casual, _) => "用轻松自然的口语说，像和朋友开黑聊天，不要播音腔。",
        (VoiceStyle.Callout, _) => "像打游戏报点一样，简短干脆，清楚有力。",
        _ => null,
    };

    /// <summary>根据配置组装合成参数。自定义指令优先；不支持指令的音色不带指令。</summary>
    public static TtsVoice Resolve(string voiceId, double rate, VoiceStyle style, string customInstruction, string fallbackModel, string language)
    {
        var info = Find(voiceId);
        var model = info?.Model ?? fallbackModel;
        bool styleOk = info?.SupportsStyle ?? model.StartsWith("qwen-audio", StringComparison.OrdinalIgnoreCase);
        string? instruction = !styleOk ? null
            : !string.IsNullOrWhiteSpace(customInstruction) ? customInstruction.Trim()
            : InstructionFor(style, language);
        // qwen-audio 合成普通话或英语时不需要 language_hints；CosyVoice 需要；Qwen3-TTS 用它决定 language_type
        string? hint = model.StartsWith("cosyvoice", StringComparison.OrdinalIgnoreCase) || model.StartsWith("qwen3-tts", StringComparison.OrdinalIgnoreCase) ? language : null;
        return new TtsVoice(model, voiceId, rate, hint, instruction);
    }
}
