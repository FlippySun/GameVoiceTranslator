using System.Diagnostics;
using System.Globalization;
using System.Text;
using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;
using GVT.Core.Pipeline;
using NAudio.Wave;

// 离线回放测试工具：不需要声卡和游戏，直接用文本/录音文件跑真实的云端链路，测质量和延迟。
//
//   GVT.Replay devices                                   列出音频设备
//   GVT.Replay mt    <corpus.tsv> [--dir zh2en|en2zh] [--translator mt|llm]
//                    只测翻译。每行：原文<TAB>必须出现的关键词（用 | 分隔，可省略）
//   GVT.Replay synth <corpus.tsv> --out <dir> [--voice longanyang]
//                    用合成语音把语料读成 16 kHz WAV，作为识别测试音频
//   GVT.Replay run   <wav目录> [--dir zh2en|en2zh] [--fast]
//                    完整链路：按真实速度推流 → 识别 → 翻译 → 合成，记录每一步耗时，输出译音 WAV

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0) { PrintUsage(); return 1; }

string Opt(string name, string def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}
bool Flag(string name) => args.Contains(name);

var cfg = AppConfig.LoadOrCreate();
var modelOpt = Opt("--model", "");
if (modelOpt != "") cfg.DashScope.LlmModel = modelOpt;
var translatorOpt = Opt("--translator", "");
if (translatorOpt == "llm") cfg.DashScope.Translator = TranslatorKind.Llm;
if (translatorOpt == "mt") cfg.DashScope.Translator = TranslatorKind.Mt;
var direction = Opt("--dir", "zh2en") == "en2zh" ? Direction.EnToZh : Direction.ZhToEn;

try
{
    switch (args[0])
    {
        case "devices":
            Console.WriteLine("播放设备：");
            foreach (var d in DeviceCatalog.ListRender()) Console.WriteLine("  " + d);
            Console.WriteLine("录音设备：");
            foreach (var d in DeviceCatalog.ListCapture()) Console.WriteLine("  " + d);
            Console.WriteLine(DefaultDevices.Describe());
            return 0;
        case "save-key":
        {
            // 从标准输入读取 Key 并用 DPAPI 加密保存，不回显
            var k = Console.In.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(k)) { Console.Error.WriteLine("没有读到 Key"); return 1; }
            SecretStore.SaveApiKey(k);
            Console.WriteLine($"已加密保存（{k[..6]}…，长度 {k.Length}）");
            return 0;
        }
        case "asr-dump" when args.Length > 1:
        {
            // 打印识别服务返回的原始 JSON（看字级时间戳）
            var key = SecretStore.GetApiKey()!;
            var samples = Load16kMono(args[1]);
            await using var s = FunAsrSession.Start(cfg.DashScope, key, Opt("--lang", "en"), null);
            s.RawMessage += m => { if (m.Contains("\"sentence_end\":true")) Console.WriteLine(m); };
            for (int off = 0; off < samples.Length; off += 1600) { s.Push(samples.AsSpan(off, Math.Min(1600, samples.Length - off))); await Task.Delay(100); }
            Console.WriteLine("最终：" + await s.FinishAsync(CancellationToken.None));
            return 0;
        }
        case "engine-smoke":
        {
            // 用真实设备启动整套引擎几秒钟（不说话就不会产生任何声音或费用），确认设备都能打开
            var (g, svc) = Services();
            var root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "GameVoiceTranslator.sln"))) root = Path.GetDirectoryName(root);
            using var engine = new Engine(cfg, svc, g, root ?? AppContext.BaseDirectory);
            Log.Line += l => Console.WriteLine("  " + l);
            engine.Start();
            for (int i = 0; i < 4; i++)
            {
                await Task.Delay(1000);
                var st = engine.GetStatus();
                Console.WriteLine($"  发送 {st.SendStage} 麦克风 {st.MicDb:F0} dB | 接收 {st.ReceiveStage} 来源 {st.SourceDb:F0} dB | {st.Warning}");
            }
            engine.Stop();
            return 0;
        }
        case "probe":
            return await Probe();
        case "sessions" when args.Length > 1:
        {
            var dev = DeviceCatalog.FindRender(args[1]);
            var sessions = dev.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                string proc;
                try { proc = System.Diagnostics.Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch { proc = "?"; }
                Console.WriteLine($"  {proc} (pid {s.GetProcessID}) 状态 {s.State} 峰值 {s.AudioMeterInformation.MasterPeakValue:F3}");
            }
            return 0;
        }
        case "listen" when args.Length > 1:
        {
            // 只录不放：每 250 ms 打印一次峰值，用来确认虚拟线空闲时是否真的安静
            using var cap = new CaptureSource(DeviceCatalog.FindCapture(args[1]));
            float peak = -120f;
            cap.Data += s => { var p = PcmConvert.PeakDb(s); if (p > peak) peak = p; };
            cap.Start();
            for (int i = 0; i < 12; i++) { await Task.Delay(250); Console.Write($"{peak:F0} "); peak = -120f; }
            Console.WriteLine();
            return 0;
        }
        case "cable-test" when args.Length > 2:
            return await CableTest(args[1], args[2]);
        case "set-default" when args.Length > 2:
            var flow = args[1] == "capture" ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.Render;
            bool? comms = Flag("--comms") ? true : Flag("--media") ? false : null;
            Console.WriteLine("已设为默认：" + DefaultDevices.Set(flow, args[2], comms));
            return 0;
        case "mt" when args.Length > 1:
            return await RunMt(args[1]);
        case "synth" when args.Length > 1:
            return await RunSynth(args[1], Opt("--out", "samples"), Opt("--voice", "longanyang"));
        case "run" when args.Length > 1:
            return await RunPipeline(args[1], Flag("--fast"));
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("失败：" + ex.Message);
    return 1;
}

(Glossary, DashScopeServices) Services()
{
    var key = SecretStore.GetApiKey() ?? throw new InvalidOperationException("没有 API Key：先在主程序里保存，或设置环境变量 DASHSCOPE_API_KEY。");
    var glossary = Glossary.Load(cfg.ResolveGlossaryPath());
    return (glossary, new DashScopeServices(cfg.DashScope, key, glossary));
}

static List<(string Text, string[] MustHave)> LoadCorpus(string path)
    => File.ReadAllLines(path)
        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
        .Select(l =>
        {
            var parts = l.Split('\t');
            var must = parts.Length > 1 ? parts[1].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
            return (parts[0].Trim(), must);
        }).ToList();

async Task<int> RunMt(string corpusPath)
{
    var (_, svc) = Services();
    var corpus = LoadCorpus(corpusPath);
    var outPath = Path.Combine(Log.LogDirectory, $"mt-{cfg.DashScope.Translator}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
    var sb = new StringBuilder("source,translation,ms,missing\n");
    var latencies = new List<double>();
    int fails = 0;
    foreach (var (text, must) in corpus)
    {
        var sw = Stopwatch.StartNew();
        string tr;
        try { tr = (await svc.Translator.TranslateAsync(text, direction, Array.Empty<string>(), CancellationToken.None)).Text; }
        catch (Exception ex) { tr = "<错误> " + ex.Message; }
        sw.Stop();
        latencies.Add(sw.Elapsed.TotalMilliseconds);
        // 关键词组内用 / 表示“任选其一”，例如 don't/do not
        // 以 ! 开头表示“译文里不能出现”，用来检查改口后旧说法有没有残留
        var missing = must.Where(m => m.StartsWith('!')
                ? (m[1..].Any(c => c > 127)
                    ? tr.Contains(m[1..])
                    : System.Text.RegularExpressions.Regex.IsMatch(tr, @"\b" + System.Text.RegularExpressions.Regex.Escape(m[1..]) + @"\b",
                        m.Length == 2 ? System.Text.RegularExpressions.RegexOptions.None : System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                : !m.Split('/').Any(alt => tr.Contains(alt, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missing.Length > 0) fails++;
        Console.WriteLine($"{(missing.Length > 0 ? "✗" : "✓")} {sw.ElapsedMilliseconds,5}ms  {text}  →  {tr}{(missing.Length > 0 ? "   缺少: " + string.Join(", ", missing) : "")}");
        sb.AppendLine($"\"{text.Replace("\"", "\"\"")}\",\"{tr.Replace("\"", "\"\"")}\",{sw.ElapsedMilliseconds},\"{string.Join(" ", missing)}\"");
    }
    File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
    PrintPercentiles("翻译耗时", latencies);
    Console.WriteLine($"关键词检查未通过：{fails}/{corpus.Count}。结果：{outPath}");
    return 0;
}

async Task<int> RunSynth(string corpusPath, string outDir, string voice)
{
    var (_, svc) = Services();
    Directory.CreateDirectory(outDir);
    var corpus = LoadCorpus(corpusPath);
    int i = 0;
    foreach (var (text, _) in corpus)
    {
        i++;
        var samples = new List<float>();
        await using var tts = svc.PrepareTts(voice, 1.0, voice.Contains("loong") ? "en" : "zh");
        await tts.SpeakAsync(text, samples.AddRange, CancellationToken.None);
        // 前后各留 0.3 秒静音，转成 16 kHz 单声道
        var dm = new MonoDownmixer(tts.SampleRate, 1);
        var pad = new float[tts.SampleRate * 3 / 10];
        var mono = dm.Process(pad.Concat(samples).Concat(pad).ToArray(), out _);
        var path = Path.Combine(outDir, $"{i:D3}.wav");
        using (var w = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
            w.Write(PcmConvert.FloatToPcm16(mono));
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), text);
        Console.WriteLine($"{path}  {text}");
    }
    return 0;
}

async Task<int> RunPipeline(string wavDir, bool fast)
{
    var (glossary, svc) = Services();
    var isSend = direction == Direction.ZhToEn;
    var rate = svc.TtsSampleRate;
    var usage = new UsageMeter(cfg.Budget);
    var player = new SpeechQueuePlayer(rate, 1, rate) { MaxSlotAgeSeconds = 60 };
    var ch = new TranslationChannel(new ChannelOptions
    {
        Name = isSend ? "发送" : "接收",
        Direction = direction,
        AsrLanguageHint = isSend ? cfg.Send.AsrLanguageHint : cfg.Receive.AsrLanguageHint,
        AsrContext = glossary.AsrContextFor(direction),
        Voice = isSend ? cfg.Send.Voice : cfg.Receive.Voice,
        TtsRate = isSend ? cfg.Send.TtsRate : cfg.Receive.TtsRate,
        TtsLanguageHint = isSend ? cfg.Send.TtsLanguageHint : cfg.Receive.TtsLanguageHint,
        SkipIfAlreadyTarget = !isSend,
    }, svc, player, usage);

    var outDir = Path.Combine(wavDir, "out");
    Directory.CreateDirectory(outDir);
    var csv = new LatencyCsv(Path.Combine(outDir, $"latency-{DateTime.Now:yyyyMMdd-HHmmss}.csv"));
    var records = new List<LatencyRecord>();
    TaskCompletionSource<LatencyRecord>? waiting = null;
    ch.Completed += r => { csv.Append(r); records.Add(r); waiting?.TrySetResult(r); };
    ch.Subtitle += e => { if (e.Kind is SubtitleKind.Error or SubtitleKind.Dropped) Console.WriteLine($"   ! {e.Text}"); };
    string? currentName = null;
    player.SlotEnded += s =>
    {
        if (s.History.Count == 0 || currentName == null) return;
        using var w = new WaveFileWriter(Path.Combine(outDir, currentName), new WaveFormat(rate, 16, 1));
        foreach (var c in s.History) w.Write(PcmConvert.FloatToPcm16(c));
    };

    using var stop = new CancellationTokenSource();
    var pump = Task.Run(async () =>
    {
        var buf = new float[rate / 50];
        while (!stop.IsCancellationRequested) { player.Read(buf, 0, buf.Length); await Task.Delay(20); }
    });

    foreach (var file in Directory.GetFiles(wavDir, "*.wav").OrderBy(f => f))
    {
        currentName = Path.GetFileName(file);
        var samples = Load16kMono(file);
        waiting = new TaskCompletionSource<LatencyRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        ch.BeginUtterance(ReadOnlySpan<float>.Empty);
        for (int off = 0; off < samples.Length; off += 1600)
        {
            ch.PushAudio(samples.AsSpan(off, Math.Min(1600, samples.Length - off)));
            if (!fast) await Task.Delay(100);
        }
        ch.EndUtterance();
        var done = await Task.WhenAny(waiting.Task, Task.Delay(30000));
        if (done != waiting.Task) { Console.WriteLine($"{currentName}: 超时"); continue; }
        var r = waiting.Task.Result;
        var expected = Path.ChangeExtension(file, ".txt");
        Console.WriteLine($"{currentName}: {r}");
        if (File.Exists(expected)) Console.WriteLine($"   参考：{File.ReadAllText(expected).Trim()}");
        Console.WriteLine($"   识别：{r.Source}");
        Console.WriteLine($"   译文：{r.Translation}");
    }
    stop.Cancel();
    await pump;

    var ok = records.Where(r => r.Outcome == "done").ToList();
    PrintPercentiles("句末→识别完成", ok.Select(r => r.EndToAsrMs ?? 0).ToList());
    PrintPercentiles("句末→翻译完成", ok.Select(r => r.EndToMtMs ?? 0).ToList());
    PrintPercentiles("句末→合成首包", ok.Select(r => r.EndToTtsFirstMs ?? 0).ToList());
    PrintPercentiles("句末→开始播放", ok.Select(r => r.EndToPlayMs ?? 0).ToList());
    Console.WriteLine(usage);
    Console.WriteLine($"译音与延迟明细：{outDir}");
    return 0;
}

// 依次尝试候选接口地址，找出这把 Key 能用的识别、翻译、合成入口
async Task<int> Probe()
{
    var key = SecretStore.GetApiKey() ?? throw new InvalidOperationException("没有 API Key");
    var glossary = Glossary.Load(cfg.ResolveGlossaryPath());
    string[] httpBases = { "https://dashscope.aliyuncs.com/compatible-mode/v1", "https://maas.qianwenaiapi.com/compatible-mode/v1", "https://cn-beijing.maas.qianwenaiapi.com/compatible-mode/v1" };
    string[] wsUrls = { "wss://dashscope.aliyuncs.com/api-ws/v1/inference", "wss://cn-beijing.maas.qianwenaiapi.com/api-ws/v1/inference", "wss://maas.qianwenaiapi.com/api-ws/v1/inference" };

    string? goodHttp = null, goodWs = null;
    foreach (var b in httpBases)
    {
        var dc = Clone(cfg.DashScope); dc.CompatibleBaseUrlOverride = b;
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await new QwenTranslator(dc, key, glossary).TranslateAsync("左边二楼有两个人，别开枪", Direction.ZhToEn, Array.Empty<string>(), CancellationToken.None);
            Console.WriteLine($"✓ 翻译 {b}  {sw.ElapsedMilliseconds}ms  → {r.Text}");
            goodHttp ??= b;
        }
        catch (Exception ex) { Console.WriteLine($"✗ 翻译 {b}  {Short(ex.Message)}"); }
    }
    foreach (var u in wsUrls)
    {
        var dc = Clone(cfg.DashScope); dc.WebSocketUrlOverride = u;
        var sw = Stopwatch.StartNew();
        try
        {
            int bytes = 0;
            await using var tts = CosyVoiceSession.Prepare(dc, key, cfg.Send.Voice, 1.0, "en");
            await tts.SpeakAsync("Two on the left, second floor. Don't shoot.", s => bytes += s.Length, CancellationToken.None);
            Console.WriteLine($"✓ 合成 {u}  {sw.ElapsedMilliseconds}ms  {bytes / (double)dc.TtsSampleRate:F1}s 音频");
            sw.Restart();
            await using var asr = FunAsrSession.Start(dc, key, "zh", null);
            asr.Push(new float[16000]);
            var text = await asr.FinishAsync(CancellationToken.None);
            Console.WriteLine($"✓ 识别 {u}  {sw.ElapsedMilliseconds}ms（静音测试，结果“{text}”）");
            goodWs ??= u;
        }
        catch (Exception ex) { Console.WriteLine($"✗ 语音 {u}  {Short(ex.Message)}"); }
    }
    if (goodHttp != null && goodWs != null)
    {
        cfg.DashScope.CompatibleBaseUrlOverride = goodHttp == httpBases[0] ? "" : goodHttp;
        cfg.DashScope.WebSocketUrlOverride = goodWs == wsUrls[0] ? "" : goodWs;
        cfg.Save();
        Console.WriteLine($"已写入配置：翻译 {goodHttp}，语音 {goodWs}");
        return 0;
    }
    return 4;

    static DashScopeConfig Clone(DashScopeConfig c) => System.Text.Json.JsonSerializer.Deserialize<DashScopeConfig>(
        System.Text.Json.JsonSerializer.Serialize(c, AppConfig.JsonOptions), AppConfig.JsonOptions)!;
    static string Short(string s) => s.Length > 220 ? s[..220] + "…" : s;
}

// 往虚拟线的输入端放 1 kHz 测试音，同时从输出端录音，确认声音能穿过去
static async Task<int> CableTest(string renderName, string captureName)
{
    using var mixer = new OutputMixer(DeviceCatalog.FindRender(renderName));
    using var cap = new CaptureSource(DeviceCatalog.FindCapture(captureName));
    var tone = new NAudio.Wave.SampleProviders.SignalGenerator(mixer.SampleRate, mixer.Channels)
    {
        Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin, Frequency = 1000, Gain = 0.25,
    };
    float peak = -120f;
    cap.Data += s => { var p = PcmConvert.PeakDb(s); if (p > peak) peak = p; };
    cap.Start();
    await Task.Delay(300);
    float silence = peak;
    peak = -120f;
    mixer.AddInput(tone);
    mixer.Start();
    await Task.Delay(1200);
    Console.WriteLine($"{mixer.DeviceName}（{mixer.SampleRate} Hz/{mixer.Channels}ch） → {cap.DeviceName}（{cap.SampleRate} Hz/{cap.Channels}ch）");
    Console.WriteLine($"  放音前峰值 {silence:F1} dBFS，放音时峰值 {peak:F1} dBFS → {(peak > -20 ? "通过" : "未检测到测试音")}");
    return peak > -20 ? 0 : 3;
}

static float[] Load16kMono(string path)
{
    using var reader = new AudioFileReader(path);
    var all = new List<float>();
    var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
    int n;
    while ((n = reader.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
    var dm = new MonoDownmixer(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
    return dm.Process(all.ToArray(), out _);
}

static void PrintPercentiles(string label, List<double> values)
{
    if (values.Count == 0) { Console.WriteLine($"{label}：无数据"); return; }
    var s = values.OrderBy(v => v).ToList();
    double P(double q) => s[(int)Math.Min(s.Count - 1, Math.Floor(q * s.Count))];
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label}：P50 {P(0.5):F0} ms，P95 {P(0.95):F0} ms，最大 {s[^1]:F0} ms（{s.Count} 条）"));
}

static void PrintUsage() => Console.WriteLine("""
用法：
  GVT.Replay devices
  GVT.Replay mt    <corpus.tsv> [--dir zh2en|en2zh] [--translator mt|llm]
  GVT.Replay synth <corpus.tsv> --out <dir> [--voice longanyang]
  GVT.Replay run   <wav目录> [--dir zh2en|en2zh] [--fast]
""");
