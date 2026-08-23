using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OneChuxin.TokenStats;

// 余额监测源（4.7.0 起；4.7.1 Key 加密存储+仅手动录入；4.8.1 统一额度语义；4.8.2 custom 简化）。Type 三类：
//  auto   = 按 URL 自动分流：DeepSeek/Moonshot/硅基流动/智谱走官方端点，其余 URL 一律按
//           One-API 系中转站组合端点（billing/subscription + billing/usage 差值）探测
//  custom = Base 地址（参考 DeepSeek：https://api.deepseek.com）+ Key，自动尝试常见余额接口
//           （/user/balance、/v1/users/me/balance、/v1/user/info、/api/paas/v4/users/me/balance、
//           One-API 组合）；接口特殊也可直接填完整余额接口 URL，「余额字段」路径可选兜底
//  preset = 无接口兜底（预设扣减）：必须填初始额度，当前额度 = 初始额度 − 该渠道累计计费
// 额度语义（4.8.1 起只有两个概念）：初始额度（Initial，可空、所有类型可用）+ 当前额度。
//  填了初始额度 → 跳过接口探测，当前额度 = 初始额度 − 该渠道累计计费（按价格规则估算，带明细）；
//  未填 → auto/custom 探测接口当前余额，preset 提示需填初始额度。旧「手动余额」(manual) 自动迁移为 initial。
// Key 安全（4.7.1，与千瞳 VisionRouter 同款）：落盘为 Windows 用户级 DPAPI 密文（dpapi:v1: 前缀 +
// 专属熵），内存中为明文；不扫描/不读取其他模块配置里的 Key，渠道只经配置页手动录入。
public sealed class BalanceSource
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "auto";      // auto / custom / preset
    public string Url { get; set; } = "";           // auto/custom: Base 地址（参考 DeepSeek：https://api.deepseek.com）；custom 也可填完整余额接口 URL；preset: 渠道匹配用（可空）
    public string ApiKey { get; set; } = "";        // 内存明文；BalanceStore 落盘前加密
    public string JsonPath { get; set; } = "";      // custom 可选取数点路径（自动尝试失败时再填，如 data.available_balance）
    /// <summary>初始额度（元，可空）：填入后该源按 初始额度 − 渠道累计计费 得出当前额度（跳过接口探测、不被轮询覆盖）；0 视为未设置。</summary>
    public decimal? Initial { get; set; }
    public string Currency { get; set; } = "CNY";   // custom/preset 展示币种（auto 由接口返回）
    public bool Enabled { get; set; } = true;

    public BalanceSource Clone() => (BalanceSource)MemberwiseClone();
}

// 一次探测结果（内存态 + 随 balance.json 的 state 段落盘，看板/重启后仍可见最近一次）
public sealed class BalanceState
{
    public decimal Balance;
    public string Currency = "CNY";
    public DateTime At;
    public bool Ok;
    public string Msg = "";
}

// 全局存储：{storage}/Tokenlog/balance.json = {"sources":[…],"state":{"<名称>":{…}}}
// 模式照 PricingStore（带缓存 + 手写 camelCase/PascalCase 双兼容解析）。
// Key 落盘为 DPAPI 密文（写入时加密）；读取时解密回内存明文供探测/编辑。
// 旧版明文 Key 首次读取时自动迁移为密文（重写文件），与千瞳迁移策略一致。
public static class BalanceStore
{
    static readonly object ioLock = new();
    static List<BalanceSource>? cache;
    static DateTime cacheStamp;

    const string ProtectedPrefix = "dpapi:v1:";
    static readonly byte[] SecretEntropy =
        Encoding.UTF8.GetBytes("OneChuxin.TokenStats.BalanceSource.ApiKey.v1");

    public static string BalanceFile =>
        Path.Combine(Path.GetDirectoryName(TokenStatsModule.LocateDataFile())!, "balance.json");

    public static List<BalanceSource> Sources()
    {
        lock (ioLock)
        {
            try
            {
                if (cache != null && cacheStamp == File.GetLastWriteTimeUtc(BalanceFile))
                    return cache;
                List<BalanceSource> loaded = new();
                States.Clear();
                bool migrated = false;
                if (File.Exists(BalanceFile))
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(BalanceFile));
                    if (doc.RootElement.TryGetProperty("sources", out JsonElement arr))
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            BalanceSource s = new();
                            s.Name = Str(e, "name", "Name");
                            s.Type = Str(e, "type", "Type");
                            s.Url = Str(e, "url", "Url");
                            string rawKey = Str(e, "apiKey", "ApiKey");
                            if (rawKey.Length > 0 && !rawKey.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                            {
                                s.ApiKey = rawKey;      // 旧版明文 → 解析为明文并标记迁移
                                migrated = true;
                            }
                            else
                                s.ApiKey = UnprotectSecret(rawKey);
                            s.JsonPath = Str(e, "jsonPath", "JsonPath");
                            // 初始额度（4.8.1 统一语义，可空、所有类型可用）：0 视为未设置；
                            // 旧「手动余额」(manual) 自动迁移为 initial（manualSet=false 显式关闭的不迁移）
                            JsonElement? iv = Find(e, "initial", "Initial");
                            decimal? initVal = null;
                            if (iv?.ValueKind == JsonValueKind.Number && iv.Value.TryGetDecimal(out decimal ivD)) initVal = ivD;
                            else if (iv?.ValueKind == JsonValueKind.String && decimal.TryParse(iv.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal ivD2)) initVal = ivD2;
                            if (initVal == null || initVal == 0m)
                            {
                                bool manualDisabled = Find(e, "manualSet", "ManualSet")?.ValueKind == JsonValueKind.False;
                                JsonElement? mv = Find(e, "manual", "Manual");
                                decimal? manVal = null;
                                if (mv?.ValueKind == JsonValueKind.Number && mv.Value.TryGetDecimal(out decimal m)) manVal = m;
                                else if (mv?.ValueKind == JsonValueKind.String && decimal.TryParse(mv.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal m2)) manVal = m2;
                                if (!manualDisabled && manVal != null && manVal != 0m)
                                {
                                    initVal = manVal;    // 旧“手动余额”即初始额度
                                    migrated = true;
                                }
                            }
                            if (initVal == 0m) initVal = null;   // 0 视为未设置（旧 preset 文件默认值），避免 0−费用=负数
                            s.Initial = initVal;
                            s.Currency = Str(e, "currency", "Currency");
                            JsonElement? en = Find(e, "enabled", "Enabled");
                            if (en?.ValueKind == JsonValueKind.False) s.Enabled = false;
                            if (en?.ValueKind == JsonValueKind.True) s.Enabled = true;
                            if (s.Name.Length > 0) loaded.Add(s);
                        }
                    if (doc.RootElement.TryGetProperty("state", out JsonElement st))
                        foreach (JsonProperty p in st.EnumerateObject())
                        {
                            BalanceState v = new()
                            {
                                Balance = Dec(p.Value, "balance", "Balance"),
                                Currency = Str(p.Value, "currency", "Currency"),
                                Ok = Find(p.Value, "ok", "Ok")?.ValueKind == JsonValueKind.True,
                                Msg = Str(p.Value, "msg", "Msg"),
                                At = DateTime.TryParse(Str(p.Value, "at", "At"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at) ? at : DateTime.MinValue,
                            };
                            States[p.Name] = v;
                        }
                }
                cache = loaded;
                if (migrated)
                    WriteFile(cache);   // 旧明文 Key 立即迁移为密文落盘
                cacheStamp = File.GetLastWriteTimeUtc(BalanceFile);
                return cache;
            }
            catch
            {
                cache ??= new List<BalanceSource>();
                return cache;
            }
        }
    }

    internal static readonly Dictionary<string, BalanceState> States = new();

    public static BalanceState? StateOf(string name)
    {
        lock (ioLock) return States.TryGetValue(name, out BalanceState? s) ? s : null;
    }

    public static void SetState(string name, BalanceState st)
    {
        lock (ioLock)
        {
            States[name] = st;
            WriteFile(cache ?? new List<BalanceSource>());
            try { cacheStamp = File.GetLastWriteTimeUtc(BalanceFile); } catch { }
        }
    }

    public static void Save(List<BalanceSource> list)
    {
        lock (ioLock)
        {
            cache = list.Select(s => s.Clone()).ToList();
            WriteFile(cache);
            try { cacheStamp = File.GetLastWriteTimeUtc(BalanceFile); } catch { }
        }
    }

    static void WriteFile(List<BalanceSource> list)
    {
        try
        {
            Dictionary<string, object> state = new();
            foreach (KeyValuePair<string, BalanceState> kv in States)
                state[kv.Key] = new { balance = kv.Value.Balance, currency = kv.Value.Currency, at = kv.Value.At.ToString("yyyy-MM-dd HH:mm:ss"), ok = kv.Value.Ok, msg = kv.Value.Msg };
            // ApiKey 落盘前加密（内存副本保持明文，探测与 UI 编辑不受影响）
            List<BalanceSource> onDisk = list.Select(s =>
            {
                BalanceSource c = s.Clone();
                c.ApiKey = ProtectSecret(s.ApiKey) ?? "";
                return c;
            }).ToList();
            JsonSerializerOptions opt = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            File.WriteAllText(BalanceFile, JsonSerializer.Serialize(new { sources = onDisk, state }, opt));
        }
        catch { }
    }

    /// <summary>明文 → dpapi:v1:Base64（Windows 用户级 DPAPI + 专属熵）；空值/已加密原样返回（幂等）。</summary>
    public static string? ProtectSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return value;
        try
        {
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), SecretEntropy, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(encrypted);
        }
        catch (PlatformNotSupportedException)
        {
            return value;   // 非 Windows 环境退化为明文（Alife 客户端实际仅 Windows）
        }
    }

    /// <summary>解密 dpapi:v1: 密文；无前缀=旧明文透传（迁移兼容）；解密失败按空处理（换用户/损坏，不误发）。</summary>
    public static string UnprotectSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return value;
        try
        {
            byte[] encrypted = Convert.FromBase64String(value[ProtectedPrefix.Length..]);
            byte[] plain = ProtectedData.Unprotect(
                encrypted, SecretEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return "";
        }
        catch (FormatException)
        {
            return "";
        }
    }

    static JsonElement? Find(JsonElement e, params string[] names)
    {
        foreach (string n in names)
            if (e.TryGetProperty(n, out JsonElement v))
                return v;
        return null;
    }

    static string Str(JsonElement e, params string[] names)
    {
        JsonElement? v = Find(e, names);
        return v?.ValueKind == JsonValueKind.String ? v.Value.GetString() ?? "" : "";
    }

    static decimal Dec(JsonElement e, params string[] names)
    {
        JsonElement? v = Find(e, names);
        if (v?.ValueKind == JsonValueKind.Number && v.Value.TryGetDecimal(out decimal d)) return d;
        if (v?.ValueKind == JsonValueKind.String && decimal.TryParse(v.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d2)) return d2;
        return 0m;
    }
}

// 余额探测器：静态 HttpClient（超时 10s），逐源独立 try/catch，失败返回 Ok=false + 原因（降级不抛出）。
// 填了初始额度 / preset 类型不在此处理（按渠道历史计费，由模块侧 ResolveBalanceState 扣减估算）。
public static class BalanceProbe
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<BalanceState> ProbeAsync(BalanceSource s)
    {
        BalanceState st = new() { At = DateTime.Now };
        try
        {
            string type = (s.Type ?? "auto").Trim().ToLowerInvariant();
            if (type == "custom")
            {
                string cu = s.Url.Trim();
                if (cu.Length == 0)
                {
                    st.Ok = false; st.Msg = "custom 源缺少接口地址";
                    return st;
                }
                if (!cu.StartsWith("http", StringComparison.OrdinalIgnoreCase)) cu = "https://" + cu;
                string croot = cu.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? cu[..^3].TrimEnd('/') : cu.TrimEnd('/');
                string path = s.JsonPath?.Trim() ?? "";

                // 候选端点按序尝试，取到数字即返回：完整地址（老格式）→ 常见官方风格余额接口 → One-API 系组合
                (string Ep, string Def, bool Style)[] cands =
                {
                    (cu, "", false),
                    (croot + "/user/balance", "balance_infos.0.total_balance", true),
                    (croot + "/v1/users/me/balance", "data.available_balance", false),
                    (croot + "/v1/user/info", "data.totalBalance", false),
                    (croot + "/api/paas/v4/users/me/balance", "balance_infos.0.total_balance", true),
                };
                List<string> tried = new();
                foreach ((string ep, string def, bool style) in cands)
                {
                    try
                    {
                        string body = await GetAsync(ep, s.ApiKey);
                        decimal? v = path.Length > 0
                            ? ReadNum(body, path)
                            : def.Length > 0 ? ReadNum(body, def) : ReadNum(body, "");
                        if (v == null && def == "data.totalBalance") v = ReadNum(body, "data.balance");
                        if (v == null)
                        {
                            tried.Add(ep);
                            continue;
                        }
                        st.Balance = v.Value;
                        st.Currency = string.IsNullOrWhiteSpace(s.Currency) ? "CNY" : s.Currency.Trim();
                        if (style && path.Length == 0)
                            try { FirstBalanceInfo(body, out string cur); if (cur.Length > 0) st.Currency = cur; } catch { }
                        st.Ok = true;
                        st.Msg = "自定义接口（" + ep + "）" + (path.Length > 0 ? "，按「余额字段」取数" : "");
                        return st;
                    }
                    catch (Exception ex)
                    {
                        string m = ex.Message is { Length: > 80 } ? ex.Message[..80] : ex.Message ?? "错误";
                        tried.Add(ep + "（" + m + "）");
                    }
                }
                // One-API 系中转站组合（额度 − 已用 美分）最后兜底
                try
                {
                    string sub = await GetAsync(croot + "/v1/dashboard/billing/subscription", s.ApiKey);
                    decimal? limit = ReadNum(sub, "hard_limit_usd");
                    if (limit != null)
                    {
                        string end = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        string usageBody = await GetAsync(croot + "/v1/dashboard/billing/usage?start_date=2024-01-01&end_date=" + end, s.ApiKey);
                        decimal? used = (ReadNum(usageBody, "total_usage") ?? 0m) / 100m;
                        st.Balance = limit.Value - used.Value;
                        st.Currency = "USD";
                        st.Ok = true;
                        st.Msg = "One-API 系中转站：额度 " + limit.Value.ToString("0.##", CultureInfo.InvariantCulture) + " − 已用 " + used.Value.ToString("0.##", CultureInfo.InvariantCulture);
                        return st;
                    }
                    tried.Add(croot + "/v1/dashboard/billing/subscription（无 hard_limit_usd）");
                }
                catch (Exception ex)
                {
                    string m = ex.Message is { Length: > 80 } ? ex.Message[..80] : ex.Message ?? "错误";
                    tried.Add(croot + "/v1/dashboard/billing/subscription（" + m + "）");
                }
                if (tried.Count > 3) tried.RemoveRange(0, tried.Count - 3);
                string list = string.Join("；", tried);
                if (list.Length > 240) list = list[..240];
                st.Ok = false;
                st.Msg = "常见余额接口均未取到数字（" + list + "）。接口特殊请填完整余额地址，或补「余额字段」路径";
                return st;
            }

            // auto：按 URL 分流
            string url = s.Url.Trim();
            if (url.Length == 0)
            {
                st.Ok = false; st.Msg = "缺少 URL";
                return st;
            }
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            string root = url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3].TrimEnd('/') : url.TrimEnd('/');
            string host = Uri.TryCreate(root, UriKind.Absolute, out Uri? u) ? u.Host.ToLowerInvariant() : root.ToLowerInvariant();

            if (host.Contains("deepseek"))
            {
                string body = await GetAsync(root + "/user/balance", s.ApiKey);
                st.Balance = FirstBalanceInfo(body, out string cur);
                st.Currency = cur.Length > 0 ? cur : "CNY";
                st.Ok = true; st.Msg = "DeepSeek 官方端点";
            }
            else if (host.Contains("moonshot") || host.Contains("kimi"))
            {
                string body = await GetAsync(root + "/v1/users/me/balance", s.ApiKey);
                decimal? v = ReadNum(body, "data.available_balance");
                if (v == null) throw new Exception("返回缺少 data.available_balance");
                st.Balance = v.Value; st.Currency = "CNY"; st.Ok = true; st.Msg = "Moonshot 官方端点";
            }
            else if (host.Contains("siliconflow"))
            {
                string body = await GetAsync(root + "/v1/user/info", s.ApiKey);
                decimal? v = ReadNum(body, "data.totalBalance") ?? ReadNum(body, "data.balance");
                if (v == null) throw new Exception("返回缺少 data.totalBalance/balance");
                st.Balance = v.Value; st.Currency = "CNY"; st.Ok = true; st.Msg = "硅基流动官方端点";
            }
            else if (host.Contains("bigmodel") || host.Contains("zhipu"))
            {
                string body = await GetAsync(root + "/api/paas/v4/users/me/balance", s.ApiKey);
                st.Balance = FirstBalanceInfo(body, out string cur);
                st.Currency = cur.Length > 0 ? cur : "CNY";
                st.Ok = true; st.Msg = "智谱端点（官方未文档化，失败可改 custom/preset）";
            }
            else
            {
                // 其余一律按 One-API / New-API 系中转站：subscription(hard_limit_usd) − usage(total_usage/100 美分)
                string sub = await GetAsync(root + "/v1/dashboard/billing/subscription", s.ApiKey);
                decimal? limit = ReadNum(sub, "hard_limit_usd");
                if (limit == null) throw new Exception("中转站未实现 billing/subscription（hard_limit_usd 缺失）");
                string end = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string usageBody = await GetAsync(root + "/v1/dashboard/billing/usage?start_date=2024-01-01&end_date=" + end, s.ApiKey);
                decimal? used = (ReadNum(usageBody, "total_usage") ?? 0m) / 100m;
                st.Balance = limit.Value - used.Value;
                st.Currency = "USD";
                st.Ok = true;
                st.Msg = $"One-API 系中转站：额度 {limit.Value.ToString("0.##", CultureInfo.InvariantCulture)} − 已用 {used.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
            }
        }
        catch (Exception ex)
        {
            st.Ok = false;
            string m = ex.Message is { Length: > 160 } ? ex.Message[..160] : ex.Message ?? "未知错误";
            st.Msg = m + "（可在配置页改 custom/preset 或核对 URL）";
        }
        return st;
    }

    static async Task<string> GetAsync(string url, string key)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(key))
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        using HttpResponseMessage resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string frag = body.Length > 120 ? body[..120] : body;
            throw new Exception($"HTTP {(int)resp.StatusCode} {frag.Trim()}");
        }
        return body;
    }

    // 点路径取数（data.available_balance）；支持数组下标（balance_infos.0.total_balance 或 [0]）；
    // 数字或数字字符串均可；路径为空取根值
    static decimal? ReadNum(string json, string path)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement el = doc.RootElement;
            if (!string.IsNullOrWhiteSpace(path))
                foreach (string rawSeg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    string seg = rawSeg.Trim();
                    if (seg.Length >= 3 && seg[0] == '[' && seg[^1] == ']')
                        seg = seg[1..^1].Trim();
                    if (int.TryParse(seg, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && el.ValueKind == JsonValueKind.Array)
                    {
                        if (idx < 0 || idx >= el.GetArrayLength()) return null;
                        el = el[idx];
                    }
                    else if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(seg, out JsonElement next))
                        el = next;
                    else return null;
                }
            return el.ValueKind switch
            {
                JsonValueKind.Number when el.TryGetDecimal(out decimal d) => d,
                JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) => d,
                _ => null,
            };
        }
        catch { return null; }
    }

    // DeepSeek/智谱风格：balance_infos[0].total_balance（+currency）
    static decimal FirstBalanceInfo(string json, out string currency)
    {
        currency = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("balance_infos", out JsonElement arr) || arr.GetArrayLength() == 0)
                throw new Exception("返回缺少 balance_infos");
            JsonElement first = arr[0];
            currency = first.TryGetProperty("currency", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            if (first.TryGetProperty("total_balance", out JsonElement v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal d)) return d;
                if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d2)) return d2;
            }
            throw new Exception("balance_infos[0] 缺少 total_balance");
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message);
        }
    }
}
