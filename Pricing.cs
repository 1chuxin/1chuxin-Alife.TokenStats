using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OneChuxin.TokenStats;

// 单条价格规则（单位：元 / 百万 tokens）。命中价作用于输入中的缓存命中部分，
// 未命中价作用于输入其余部分；PeakEnabled=false 时恒按谷价（无峰谷差异的渠道）。
// 匹配为「包含」比较（忽略大小写），URL / 渠道名 / 模型ID 均可留空；
// 加权计分取最高：URL命中=4，模型=2，渠道名=1（可叠加，如 URL+模型=6）。
// 推荐按 URL（endpoint 域名）配置——比灵枢组名稳定，重排/改名不受影响；
// 同一 URL 下不同模型（如 flash/pro）用「URL+模型」组合精确区分。
public sealed class PriceRule
{
    public string Name { get; set; } = "";
    public string? UrlMatch { get; set; }
    public string? ChannelMatch { get; set; }
    public string? ModelMatch { get; set; }
    public bool PeakEnabled { get; set; } = true;
    public decimal HitPeak { get; set; }
    public decimal HitOff { get; set; }
    public decimal MissPeak { get; set; }
    public decimal MissOff { get; set; }
    public decimal OutPeak { get; set; }
    public decimal OutOff { get; set; }

    public PriceRule Clone() => (PriceRule)MemberwiseClone();

    // DeepSeek 官方价（2026-08-23 抓取 api-docs.deepseek.com；官方调价后可在配置页改）
    public static List<PriceRule> Defaults() => new()
    {
        new PriceRule { Name = "DeepSeek V4-Flash（官方价）", ModelMatch = "flash",
            HitPeak = 0.10m, HitOff = 0.05m, MissPeak = 3.0m, MissOff = 1.5m, OutPeak = 9.0m, OutOff = 4.5m },
        new PriceRule { Name = "DeepSeek V4-Pro（官方价）", ModelMatch = "pro",
            HitPeak = 0.30m, HitOff = 0.15m, MissPeak = 9.0m, MissOff = 4.5m, OutPeak = 27.0m, OutOff = 13.5m },
    };

    // 按模型名猜一份规则（flash/pro 预填官方价，其余空白待填）
    public static PriceRule Guess(string? model)
    {
        string m = (model ?? "").ToLowerInvariant();
        if (m.Contains("flash")) return Defaults()[0].Clone();
        if (m.Contains("pro")) return Defaults()[1].Clone();
        return new PriceRule();
    }
}

public sealed class ChannelInfo
{
    public string Owner = "";   // 所属角色名（全局配置为 "全局"）
    public string Name = "";    // 灵枢渠道组名（无命名的组显示 第N组）
    public string Model = "";
    public string Host = "";    // endpoint 域名
}

// 计价引擎：规则全局存于 {storage}/Tokenlog/pricing.json（与用量日志同目录，卸载插件不丢）。
// 费用一律在展示时计算（日志只存 tokens/模型/渠道/时间戳），改价后全历史即时重定价。
// 峰谷：高峰=工作日 9:00–12:00、14:00–18:00（机器本地时间，默认视为北京时间），其余为谷。
public static class PricingStore
{
    static readonly object ioLock = new();
    static List<PriceRule>? cache;
    static DateTime cacheStamp;

    public static string PricingFile =>
        Path.Combine(Path.GetDirectoryName(TokenStatsModule.LocateDataFile())!, "pricing.json");

    public static List<PriceRule> Rules()
    {
        lock (ioLock)
        {
            try
            {
                if (cache != null && cacheStamp == File.GetLastWriteTimeUtc(PricingFile))
                    return cache;
                if (File.Exists(PricingFile))
                {
                    List<PriceRule> loaded = new();
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(PricingFile));
                    if (doc.RootElement.TryGetProperty("rules", out JsonElement arr))
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            PriceRule r = new();
                            r.Name = FindProp(e, "name", "Name")?.GetString() ?? "";
                            r.UrlMatch = FindProp(e, "urlMatch", "UrlMatch")?.GetString();
                            r.ChannelMatch = FindProp(e, "channelMatch", "ChannelMatch")?.GetString();
                            r.ModelMatch = FindProp(e, "modelMatch", "ModelMatch")?.GetString();
                            JsonElement? pk = FindProp(e, "peakEnabled", "PeakEnabled");
                            if (pk?.ValueKind == JsonValueKind.True) r.PeakEnabled = true;
                            if (pk?.ValueKind == JsonValueKind.False) r.PeakEnabled = false;
                            r.HitPeak = GetDec(FindProp(e, "hitPeak", "HitPeak"));
                            r.HitOff = GetDec(FindProp(e, "hitOff", "HitOff"));
                            r.MissPeak = GetDec(FindProp(e, "missPeak", "MissPeak"));
                            r.MissOff = GetDec(FindProp(e, "missOff", "MissOff"));
                            r.OutPeak = GetDec(FindProp(e, "outPeak", "OutPeak"));
                            r.OutOff = GetDec(FindProp(e, "outOff", "OutOff"));
                            loaded.Add(r);
                        }
                    cache = loaded;
                }
                else
                {
                    cache = PriceRule.Defaults();
                    WriteFile(cache);
                }
                cacheStamp = File.GetLastWriteTimeUtc(PricingFile);
                return cache;
            }
            catch
            {
                cache ??= PriceRule.Defaults();
                return cache;
            }
        }
    }

    static decimal GetDec(JsonElement? e) => e?.ValueKind == JsonValueKind.Number && e.Value.TryGetDecimal(out decimal d) ? d : 0m;

    // 找不到键时返回 null（default(JsonElement) 上调 GetString() 会抛异常）
    static JsonElement? FindProp(JsonElement e, params string[] names)
    {
        foreach (string n in names)
            if (e.TryGetProperty(n, out JsonElement v))
                return v;
        return null;
    }

    public static void Save(List<PriceRule> list)
    {
        lock (ioLock)
        {
            cache = list.Select(r => r.Clone()).ToList();
            WriteFile(cache);
            try { cacheStamp = File.GetLastWriteTimeUtc(PricingFile); } catch { }
        }
    }

    static void WriteFile(List<PriceRule> list)
    {
        try
        {
            // camelCase 属性名持久化（与手写解析键一致；读取端兼容旧 PascalCase）
            JsonSerializerOptions opt = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(PricingFile, JsonSerializer.Serialize(new { rules = list }, opt));
        }
        catch { }
    }

    public static PriceRule? Match(string? channel, string? model, string? url = null)
    {
        PriceRule? best = null;
        int bestScore = 0;
        foreach (PriceRule r in Rules())
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(r.UrlMatch) && url != null
                && url.Contains(r.UrlMatch, StringComparison.OrdinalIgnoreCase)) score += 4;
            if (!string.IsNullOrWhiteSpace(r.ModelMatch) && model != null
                && model.Contains(r.ModelMatch, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (!string.IsNullOrWhiteSpace(r.ChannelMatch) && channel != null
                && channel.Contains(r.ChannelMatch, StringComparison.OrdinalIgnoreCase)) score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }
        return best;
    }

    public static bool IsPeak(DateTime t)
    {
        if (t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        int h = t.Hour;
        return (h >= 9 && h < 12) || (h >= 14 && h < 18);
    }

    // 费用（元）；未匹配到任何规则时返回 null（界面显示 "—"）
    public static decimal? Cost(long input, long output, long cached, DateTime t, string? channel, string? model, string? url = null)
    {
        PriceRule? r = Match(channel, model, url);
        if (r == null) return null;
        bool peak = r.PeakEnabled && IsPeak(t);
        return (cached * (peak ? r.HitPeak : r.HitOff)
            + Math.Max(0, input - cached) * (peak ? r.MissPeak : r.MissOff)
            + output * (peak ? r.OutPeak : r.OutOff)) / 1_000_000m;
    }

    // 扫描本机灵枢(LanguageModelRouter)配置文件里的渠道组（角色未激活时也能列出）：
    // storage/Character/*/Configuration/ 与 storage/Configuration/ 下 *LanguageModelRouter*.json
    public static List<ChannelInfo> ScanChannels()
    {
        List<ChannelInfo> list = new();
        try
        {
            string storage = Path.GetDirectoryName(Path.GetDirectoryName(TokenStatsModule.LocateDataFile()))!;
            foreach (string file in Directory.EnumerateFiles(storage, "*LanguageModelRouter*.json", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}Plugins{Path.DirectorySeparatorChar}")) continue;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("Groups", out JsonElement groups)) continue;
                    string owner = "全局";
                    int ci = file.LastIndexOf($"{Path.DirectorySeparatorChar}Character{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
                    if (ci >= 0)
                    {
                        int start = ci + 11;
                        int end = file.IndexOf(Path.DirectorySeparatorChar, start);
                        if (end > start) owner = file[start..end];
                    }
                    int slot = 0;
                    foreach (JsonElement g in groups.EnumerateArray())
                    {
                        string ep = GetStr(g, "Endpoint"), key = GetStr(g, "ApiKey");
                        if (ep.Length == 0 || key.Length == 0) { slot++; continue; }
                        string name = GetStr(g, "GroupName");
                        string modelId = GetStr(g, "ModelId");
                        string host = Uri.TryCreate(ep, UriKind.Absolute, out Uri? u) ? u.Host : ep;
                        list.Add(new ChannelInfo
                        {
                            Owner = owner,
                            Name = name.Length > 0 ? name : $"第{slot + 1}组",
                            Model = modelId,
                            Host = host,
                        });
                        slot++;
                    }
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    static string GetStr(JsonElement g, string prop) =>
        g.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
