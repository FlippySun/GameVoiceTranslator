using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GVT.Core.Aliyun;
using GVT.Core.Config;
using NAudio.Wave;

/// <summary>
/// 用“保存识别音频”录下的真实对局片段，对比几种识别方案。
/// 没有人工标注，所以输出并排的识别结果给人看，同时统计几个客观指标：空结果、术语串、平均每秒 token。
/// </summary>
static class AsrAb
{
    public sealed record Variant(string Name, string Model, bool Hotwords, bool FunVocabulary, bool Disfluency);

    public static readonly Variant[] All =
    {
        new("fun", "fun-asr-realtime", false, false, false),
        new("funv", "fun-asr-realtime", false, true, false),
        new("msg", "qwen-audio-3.1-asr-flash-message", false, false, false),
        new("msgh", "qwen-audio-3.1-asr-flash-message", true, false, false),
        new("msghd", "qwen-audio-3.1-asr-flash-message", true, false, true),
        new("strh", "qwen-audio-3.1-asr-flash-streaming", true, false, false),
    };

    private sealed record Clip(string Name, string Wav, bool Send, string Original, float[] Samples);

    public static async Task<int> RunAsync(AppConfig cfg, Glossary glossary, string key, string dir, string[] variantNames, int limit, int realtime, string outPath)
    {
        var variants = All.Where(v => variantNames.Length == 0 || variantNames.Contains(v.Name)).ToArray();
        var clips = Directory.GetFiles(dir, "*.wav").OrderBy(f => f).Take(limit).Select(Load).Where(c => c.Samples.Length > 1600).ToList();
        Console.WriteLine($"{clips.Count} 段录音（发送 {clips.Count(c => c.Send)}，接收 {clips.Count(c => !c.Send)}），方案：{string.Join(", ", variants.Select(v => v.Name))}");

        var hot = new Dictionary<bool, Dictionary<string, int>>
        {
            [true] = glossary.HotwordsFor(Direction.ZhToEn),
            [false] = glossary.HotwordsFor(Direction.EnToZh),
        };
        Console.WriteLine($"热词 中文 {hot[true].Count} 个：{string.Join("、", hot[true].Keys)}");
        Console.WriteLine($"热词 英文 {hot[false].Count} 个：{string.Join(", ", hot[false].Keys)}");

        var vocabIds = new Dictionary<bool, string>();
        if (variants.Any(v => v.FunVocabulary))
        {
            foreach (var send in new[] { true, false })
                vocabIds[send] = await AsrVocabulary.CreateAsync(cfg.DashScope, key, "fun-asr-realtime", hot[send], send ? "zh" : "en");
            Console.WriteLine("fun-asr 热词表已就绪");
        }

        // 准确性：整段一次推完，结果并排
        var results = new Dictionary<(string Clip, string Variant), (string Text, int InTok, int OutTok, double Ms)>();
        var sem = new SemaphoreSlim(8);
        var jobs = new List<Task>();
        foreach (var c in clips)
            foreach (var v in variants)
                jobs.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var r = await RecognizeAsync(cfg.DashScope, key, c, v, hot[c.Send], vocabIds.GetValueOrDefault(c.Send), paced: false);
                        lock (results) results[(c.Name, v.Name)] = r;
                    }
                    catch (Exception ex)
                    {
                        lock (results) results[(c.Name, v.Name)] = ("<错误 " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0] + ">", 0, 0, 0);
                    }
                    finally { sem.Release(); }
                }));
        int total = jobs.Count;
        while (jobs.Any(j => !j.IsCompleted))
        {
            await Task.WhenAny(Task.WhenAll(jobs), Task.Delay(5000));
            Console.Write($"\r  {jobs.Count(j => j.IsCompleted)}/{total}");
        }
        Console.WriteLine();

        using (var w = new StreamWriter(outPath, false, new UTF8Encoding(true)))
        {
            w.WriteLine("clip\tdir\taudio_s\toriginal\t" + string.Join("\t", variants.Select(v => v.Name)));
            foreach (var c in clips)
                w.WriteLine($"{c.Name}\t{(c.Send ? "send" : "recv")}\t{c.Samples.Length / 16000.0:F2}\t{Clean(c.Original)}\t" +
                            string.Join("\t", variants.Select(v => Clean(results[(c.Name, v.Name)].Text))));
        }
        Console.WriteLine("并排结果：" + outPath);

        foreach (var send in new[] { true, false })
        {
            var cs = clips.Where(c => c.Send == send).ToList();
            double secs = cs.Sum(c => c.Samples.Length / 16000.0);
            Console.WriteLine($"\n== {(send ? "发送（我说中文）" : "接收（队友说英文）")} {cs.Count} 段，{secs:F0} 秒 ==");
            Console.WriteLine("方案      空结果  术语串  撤离/exfil  阔剑/claymore  错误  每秒token  与原记录相同");
            foreach (var v in variants)
            {
                var rs = cs.Select(c => (c, r: results[(c.Name, v.Name)])).ToList();
                int empty = rs.Count(x => string.IsNullOrWhiteSpace(Strip(x.r.Text)));
                int err = rs.Count(x => x.r.Text.StartsWith("<错误"));
                int exfil = rs.Count(x => x.r.Text.Contains("撤离") || x.r.Text.Contains("exfil", StringComparison.OrdinalIgnoreCase));
                int clay = rs.Count(x => x.r.Text.Contains("阔剑") || x.r.Text.Contains("claymore", StringComparison.OrdinalIgnoreCase));
                int lists = rs.Count(x => LooksLikeTermList(x.r.Text, hot[send]));
                double tps = rs.Sum(x => x.r.InTok + x.r.OutTok) / Math.Max(1, secs);
                int same = rs.Count(x => Norm(x.r.Text) == Norm(x.c.Original));
                Console.WriteLine($"{v.Name,-8}  {empty,6}  {lists,6}  {exfil,10}  {clay,13}  {err,4}  {tps,9:F1}  {same,6}");
            }
        }

        if (realtime > 0)
        {
            // 延迟：按真实语速推流，测“音频推完 → 拿到最终结果”的时间
            var pick = clips.Where((c, i) => i % Math.Max(1, clips.Count / realtime) == 0).Take(realtime).ToList();
            Console.WriteLine($"\n== 按真实语速推流测延迟（{pick.Count} 段）==");
            foreach (var v in variants)
            {
                var ms = new List<double>();
                foreach (var batch in pick.Chunk(6))
                {
                    var rs = await Task.WhenAll(batch.Select(async c =>
                    {
                        try { return (await RecognizeAsync(cfg.DashScope, key, c, v, hot[c.Send], vocabIds.GetValueOrDefault(c.Send), paced: true)).Ms; }
                        catch { return -1; }
                    }));
                    ms.AddRange(rs.Where(x => x >= 0));
                }
                ms.Sort();
                Console.WriteLine($"{v.Name,-8} P50 {Pct(ms, 0.5):F0} ms  P90 {Pct(ms, 0.9):F0} ms  失败 {pick.Count - ms.Count}");
            }
        }

        foreach (var id in vocabIds.Values) await AsrVocabulary.DeleteAsync(cfg.DashScope, key, id);
        return 0;
    }

    private static double Pct(List<double> s, double p) => s.Count == 0 ? double.NaN : s[Math.Min(s.Count - 1, (int)(s.Count * p))];

    private static async Task<(string Text, int InTok, int OutTok, double Ms)> RecognizeAsync(
        DashScopeConfig dc, string key, Clip c, Variant v, Dictionary<string, int> hot, string? vocabId, bool paced)
    {
        var req = new AsrRequest(c.Send ? "zh" : "en")
        {
            Model = v.Model,
            Hotwords = v.Hotwords ? hot : null,
            VocabularyId = v.FunVocabulary ? vocabId : null,
            DisfluencyRemoval = v.Disfluency,
        };
        await using var s = FunAsrSession.Start(dc, key, req);
        const int chunk = 1600;
        for (int off = 0; off < c.Samples.Length; off += chunk)
        {
            s.Push(c.Samples.AsSpan(off, Math.Min(chunk, c.Samples.Length - off)));
            if (paced) await Task.Delay(100);
        }
        var sw = Stopwatch.StartNew();
        var text = await s.FinishAsync(CancellationToken.None);
        var ms = sw.Elapsed.TotalMilliseconds;
        return (text, s.Tokens.Input, s.Tokens.Output, ms);
    }

    private static Clip Load(string wav)
    {
        using var r = new WaveFileReader(wav);
        var bytes = new byte[r.Length];
        int n = r.Read(bytes, 0, bytes.Length);
        var samples = new float[n / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        var txt = Path.ChangeExtension(wav, ".txt");
        var name = Path.GetFileNameWithoutExtension(wav);
        return new Clip(name, wav, name.Contains("-send-"), File.Exists(txt) ? File.ReadAllText(txt) : "", samples);
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    private static string Strip(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    private static string Norm(string s) => Strip(s).ToLowerInvariant();

    /// <summary>结果里出现 3 个以上热词：多半是把词表吐出来了。</summary>
    private static bool LooksLikeTermList(string text, Dictionary<string, int> hot)
        => hot.Keys.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)) >= 3;


}
