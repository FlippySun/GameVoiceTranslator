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
        var missing = must.Where(m => !m.Split('/').Any(alt => tr.Contains(alt, StringComparison.OrdinalIgnoreCase))).ToArray();
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
