using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using ElectronNET.API;
using Microsoft.Extensions.Logging;

namespace OneChuxin.TokenStats;

[Module("Token用量看板",
    "在『展开思考』开关旁注入圆环 Token 用量挂件：圆心紧凑显示用量（如 9.9K），点击切换 本次/今天/近7天/近30天/累计，悬停展开详情卡片（含范围费用）。详情页支持按小时/按天的历史明细、逐轮模型/来源/渠道/费用，以及按渠道/角色/来源的维度分析。按来源（qchat/主窗口/桌宠speak/报点）与渠道（灵枢多组API）归类，费用按可配置价格规则（默认 DeepSeek 官方峰谷价）展示时计算，改价后全历史即时重定价。用量记录双写持久化于 storage/Tokenlog/：各角色分日志 usage-log.&lt;角色名&gt;.jsonl（范围统计按角色独立，多开互不串数）+ 汇总 usage-log.jsonl（详情页可切换『全部角色』口径），均可在配置页按时间段精确到秒清理，不修改客户端文件；随角色激活开启，停止角色自动移除挂件（会话清零、历史保留）。",
    defaultCategory: "初心的小工具",
    EditorUI = typeof(TokenStatsUI))]
public class TokenStatsModule(ILogger<TokenStatsModule> logger) :
    ChatBehaviour,
    IConfigurable<TokenStatsConfig>
{
    public TokenStatsConfig Configuration { get; set; } = null!;

    readonly object sync = new();
    TokenUsage total;
    TokenUsage lastRound;
    int rounds;
    DateTime sessionStart = DateTime.Now;

    // 会话级 模型×渠道 聚合（费用计算用）：键 model\u001Fchannel → [谷,峰]
    readonly Dictionary<string, Agg[]> sessionAggs = new();
    string curSource = "系统";   // 最近一轮来源（qchat/ChatWindow/speak/报点/…，/stats 与挂件展示）
    string lastChannel = "";     // 最近一轮渠道（灵枢渠道组名）

    // 本轮归因素材：ChatSent 捕获用户消息全文（含 [消息来源(x)] 标签），ChatReceived 累计
    // AI 输出（识别 <Speak> 桌宠说话）；无标签轮（工具续轮等）继承上一轮来源
    string pendingUserMsg = "";
    readonly StringBuilder roundAiText = new();
    string inheritSource = "系统";

    // 历史用量（按天聚合，键 yyyy-MM-dd 升序）。logFile=本角色分日志（usage-log.<角色名>.jsonl，
    // 看板/挂件范围统计与按天/按小时明细均按角色独立），masterFile=汇总日志（usage-log.jsonl，
    // 双写保留全机数据，详情页维度分析/最近记录可切换『全部角色』口径读取）；
    // hours 为单天 24 小时桶（懒分配，单天详情页按小时显示用）
    readonly SortedDictionary<string, DayStat> days = new();
    readonly Dictionary<string, DayStat[]> hours = new();
    string logFile = "";
    string masterFile = "";
    bool ioWarned;

    CancellationTokenSource? serverCts;
    int actualPort;
    BrowserWindow? mainWindow;
    int lastInjectFrame = -1000;
    string overlayState = "pending";   // ok/injected/hidden/nopage/error: 最近一次注入探测结果
    string lastOverlayCat = "";        // 上次日志输出的状态类别（仅状态变迁才打日志，防多开刷屏）

    // ElectronNET 的 Once 应答事件名不带窗口Id，同类调用交错会串线应答，全局串行
    static readonly SemaphoreSlim ipcLock = new(1, 1);

    // 用量日志文件的跨实例 IO 锁（多角色同时激活时防止两实例并发写坏行）
    static readonly object fileIoLock = new();

    static async Task<T> IpcAsync<T>(Func<Task<T>> call)
    {
        await ipcLock.WaitAsync();
        try { return await call(); }
        finally { ipcLock.Release(); }
    }

    sealed class DayStat
    {
        public int Rounds;
        public long V, In, Out, Cached;
        public Dictionary<string, Agg[]>? ByModel; // 模型\u001F渠道 → [谷,峰]，范围费用计算用（懒分配）
    }

    sealed class Agg { public long In, Out, Cached; }

    protected override Task OnAwake()
    {
        ChatBot.TokenUsed += OnTokenUsed;
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatReceived += OnChatReceived;
        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        sessionStart = DateTime.Now;
        lock (sync) { sessionAggs.Clear(); curSource = "系统"; lastChannel = ""; }
        LoadHistory();
        if (TryStartServer())
            await EnsureOverlayAsync();
    }

    protected override async Task OnUpdate()
    {
        // 每约1.2s校验一次注入状态（页面导航/重建自愈），单次仅为一次轻量JS探测
        if (UpdateContext.FrameCount - lastInjectFrame < 4)
            return;
        lastInjectFrame = UpdateContext.FrameCount;
        await EnsureOverlayAsync();
    }

    protected override Task OnDestroy()
    {
        ChatBot.TokenUsed -= OnTokenUsed;
        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatReceived -= OnChatReceived;
        serverCts?.Cancel();
        serverCts = null;
        // 从页面移除挂件（IPC可能在销毁中途失效，失败无害：残留挂件会在下次注入时自清理）
        BrowserWindow? main = mainWindow;
        if (main != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await IpcAsync(() => main.WebContents.ExecuteJavaScriptAsync<string>(
                        "(function(){try{if(window.__tstatsMgr)window.__tstatsMgr.unregister(" + actualPort + ");if(window.__tstatsTeardown)window.__tstatsTeardown()}catch(e){}return 'removed'})()"));
                }
                catch { }
            });
        }
        return Task.CompletedTask;
    }

    void OnChatSent(string message)
    {
        lock (sync)
        {
            pendingUserMsg = message ?? "";
            roundAiText.Clear();
        }
    }

    void OnChatReceived(string text)
    {
        if (!string.IsNullOrEmpty(text))
            lock (sync) roundAiText.Append(text);
    }

    void OnTokenUsed(TokenUsage usage)
    {
        // 流式失败等异常轮的空用量不入账
        if (usage.Total == 0 && usage.Input == 0 && usage.Output == 0) return;
        DateTime now = DateTime.Now;
        string src;
        lock (sync) src = ClassifySource(pendingUserMsg, roundAiText.ToString(), inheritSource);
        (string channel, string model, string host) = ResolveChannelAndModel();
        string line = $"{{\"t\":\"{now:yyyy-MM-dd'T'HH:mm:ss.fff}\",\"v\":{usage.Total},\"i\":{usage.Input},\"o\":{usage.Output},\"c\":{usage.Cached},\"m\":\"{JsonEscape(model)}\",\"s\":\"{JsonEscape(src)}\",\"ch\":\"{JsonEscape(channel)}\",\"h\":\"{JsonEscape(host)}\",\"n\":\"{JsonEscape(Character?.Name ?? "")}\"}}";
        bool peak = PricingStore.IsPeak(now);
        lock (sync)
        {
            total += usage;
            lastRound = usage;
            rounds++;
            inheritSource = src;
            curSource = src;
            lastChannel = channel;
            string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!days.TryGetValue(day, out DayStat ds)) days[day] = ds = new DayStat();
            ds.Rounds++; ds.V += usage.Total; ds.In += usage.Input; ds.Out += usage.Output; ds.Cached += usage.Cached;
            AddModelAgg(ds.ByModel ??= new Dictionary<string, Agg[]>(), model, channel, host, usage.Input, usage.Output, usage.Cached, peak);
            AddModelAgg(sessionAggs, model, channel, host, usage.Input, usage.Output, usage.Cached, peak);
            if (!hours.TryGetValue(day, out DayStat[] hs)) hours[day] = hs = new DayStat[24];
            DayStat hr = hs[now.Hour] ??= new DayStat();
            hr.Rounds++; hr.V += usage.Total; hr.In += usage.Input; hr.Out += usage.Output; hr.Cached += usage.Cached;
            if (logFile.Length > 0)
            {
                try
                {
                    lock (fileIoLock)
                    {
                        File.AppendAllText(logFile, line + "\n");   // 角色分日志（本角色看板数据源）
                        if (masterFile.Length > 0 && !string.Equals(masterFile, logFile, StringComparison.OrdinalIgnoreCase))
                            File.AppendAllText(masterFile, line + "\n");   // 汇总日志（全机数据）
                    }
                    ioWarned = false;
                }
                catch (Exception ex) { WarnIoOnce(ex); }
            }
        }
    }

    static void AddModelAgg(Dictionary<string, Agg[]> dict, string model, string channel, string host, long i, long o, long c, bool peak)
    {
        string key = model + '\u001F' + channel + '\u001F' + host;
        Agg[] slots = dict.TryGetValue(key, out Agg[]? a) ? a : (dict[key] = new Agg[2]);
        Agg agg = slots[peak ? 1 : 0] ??= new Agg();
        agg.In += i; agg.Out += o; agg.Cached += c;
    }

    static readonly Regex SourceTagRegex = new(@"\[消息来源\(([^)\]]+)\)\]", RegexOptions.Compiled);

    // 来源归类：优先看 AI 输出是否含 <Speak>（桌宠说话，DeskPet 匹配本身忽略大小写，
    // 模型实际常输出小写 <speak>），再按用户消息的 [消息来源(模块名)] /
    // 消息来源:[ChatWindow] 标签映射；无标签（工具续轮等）继承上一轮。
    // 其他第三方模块的标签按模块原名显示。
    static string ClassifySource(string userMsg, string aiText, string inherit)
    {
        if (Regex.IsMatch(aiText, "<Speak[\\s>]", RegexOptions.IgnoreCase)) return "speak";
        Match m = SourceTagRegex.Match(userMsg);
        if (m.Success)
        {
            switch (m.Groups[1].Value)
            {
                case "QChatService": return "qchat";
                case "DeskPetService": return "speak";
                case "SystemEventService": return "报点";
                case "XmlFunctionCaller": return inherit.Length > 0 ? inherit : "系统";
                default: return m.Groups[1].Value;
            }
        }
        if (userMsg.Contains("消息来源:[ChatWindow]")) return "ChatWindow";
        return inherit.Length > 0 ? inherit : "系统";
    }

    void WarnIoOnce(Exception ex)
    {
        if (ioWarned) return;
        ioWarned = true;
        logger.LogWarning(ex, $"Token用量看板：用量日志读写失败（{logFile}），历史统计可能不完整");
    }

    // 定位用量日志：{应用根}/storage/Tokenlog/usage-log.jsonl（用户 storage 下独立目录，
    // 与插件代码分离，卸载/更新插件不丢数据）。静态：配置页 UI 在模块未实例化时也要能读历史。
    // 一次性迁移：旧版本日志位于插件目录，目标不存在时自动搬移。
    internal static string LocateDataFile()
    {
        IEnumerable<string> Roots()
        {
            string? cwd = null;
            try { cwd = Directory.GetCurrentDirectory(); } catch { }
            if (cwd != null) yield return cwd;
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                yield return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
            }
        }
        foreach (string root in Roots())
        {
            try
            {
                string storageDir = Path.Combine(root, "storage");
                if (!Directory.Exists(storageDir)) continue;
                string logDir = Path.Combine(storageDir, "Tokenlog");
                Directory.CreateDirectory(logDir);
                string file = Path.Combine(logDir, "usage-log.jsonl");
                MigrateLegacyLog(root, file);
                return file;
            }
            catch { }
        }
        string fallbackDir = Path.Combine(Directory.GetCurrentDirectory(), "storage", "Tokenlog");
        try { Directory.CreateDirectory(fallbackDir); } catch { }
        return Path.Combine(fallbackDir, "usage-log.jsonl");
    }

    static void MigrateLegacyLog(string root, string targetFile)
    {
        try
        {
            lock (fileIoLock)
            {
                if (File.Exists(targetFile)) return;
                string legacy = Path.Combine(root, "storage", "Plugins", "1chuxin.TokenStats", "usage-log.jsonl");
                if (File.Exists(legacy))
                    File.Move(legacy, targetFile);
            }
        }
        catch { }
    }

    // 角色分日志路径：storage/Tokenlog/usage-log.<角色名>.jsonl。看板范围统计（今天/近7天/累计）
    // 与按天/按小时明细均按角色独立；汇总 usage-log.jsonl 双写保留全机数据。非法文件名字符替换为
    // 下划线、超长截断（80字符），角色名为空时落 usage-log._.jsonl（绝不与汇总文件重名）
    internal static string CharLogPath(string masterPath, string charName)
    {
        string invalid = new string(Path.GetInvalidFileNameChars());
        StringBuilder sb = new(charName.Length);
        foreach (char c in charName)
            sb.Append(c == '\u001F' || invalid.IndexOf(c) >= 0 ? '_' : c);
        if (sb.Length > 80) sb.Length = 80;
        return Path.Combine(Path.GetDirectoryName(masterPath) ?? "", "usage-log." + (sb.Length > 0 ? sb.ToString() : "_") + ".jsonl");
    }

    // 首次为角色生成分日志：从汇总日志抽取该角色历史记录（先写 .tmp 再原子替换，中断不留半截
    // 文件；已存在即返回，幂等）。汇总日志不被修改；旧版无角色名(n)的记录无法归属，仅保留于汇总
    static void EnsureCharLog(string charFile, string charName)
    {
        try
        {
            lock (fileIoLock)
            {
                if (File.Exists(charFile)) return;
                List<UsageRec> mine = new();
                if (charName.Length > 0)
                    foreach (UsageRec r in ReadUsageRecords(LocateDataFile()))
                        if (string.Equals(r.N, charName, StringComparison.Ordinal))
                            mine.Add(r);
                string tmp = charFile + ".tmp";
                using (StreamWriter w = new(tmp, false, new UTF8Encoding(false)))
                    foreach (UsageRec r in mine)
                        w.WriteLine(RecLine(r));
                File.Move(tmp, charFile, true);
            }
        }
        catch { }
    }

    // 全部用量日志（汇总 + 各角色分日志）：清空/按时间段清理时逐个处理
    static List<string> AllUsageLogs()
    {
        List<string> files = new();
        string master = LocateDataFile();
        files.Add(master);
        try
        {
            string? dir = Path.GetDirectoryName(master);
            if (dir != null)
                foreach (string f in Directory.EnumerateFiles(dir, "usage-log*.jsonl"))
                    if (!string.Equals(f, master, StringComparison.OrdinalIgnoreCase))
                        files.Add(f);
        }
        catch { }
        return files;
    }

    static string RecLine(UsageRec r) => $"{{\"t\":\"{r.T:yyyy-MM-dd'T'HH:mm:ss.fff}\",\"v\":{r.V},\"i\":{r.I},\"o\":{r.O},\"c\":{r.C},\"m\":\"{JsonEscape(r.M ?? "")}\",\"s\":\"{JsonEscape(r.S ?? "")}\",\"ch\":\"{JsonEscape(r.Ch ?? "")}\",\"h\":\"{JsonEscape(r.H ?? "")}\",\"n\":\"{JsonEscape(r.N ?? "")}\"}}";

    internal sealed class UsageRec
    {
        public DateTime T;
        public long V, I, O, C;
        public string? M;   // 该轮使用的模型名（旧记录可能为空）
        public string? S;   // 来源：qchat/ChatWindow/speak/报点/…（旧记录为空 → 未知）
        public string? Ch;  // 渠道：灵枢渠道组名 / endpoint 域名（旧记录为空 → 未知）
        public string? H;   // 渠道 endpoint 域名（价格规则可按 URL 匹配；旧记录为空）
        public string? N;   // 角色名（旧记录为空 → 未知）
    }

    // 解析 usage-log.jsonl（每轮一行），坏行跳过——模块加载与配置页 UI 共用。
    // 时间戳固定 InvariantCulture 解析（避免非公历区域设置下日志日期被误读）
    internal static List<UsageRec> ReadUsageRecords(string path)
    {
        List<UsageRec> list = new();
        if (!File.Exists(path))
            return list;
        string[] lines;
        lock (fileIoLock)
        {
            try { lines = File.ReadAllLines(path); }
            catch { return list; }
        }
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement el = doc.RootElement;
                if (!DateTime.TryParse(el.GetProperty("t").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)) continue;
                list.Add(new UsageRec
                {
                    T = t,
                    V = el.GetProperty("v").GetInt64(),
                    I = el.GetProperty("i").GetInt64(),
                    O = el.GetProperty("o").GetInt64(),
                    C = el.GetProperty("c").GetInt64(),
                    M = el.TryGetProperty("m", out JsonElement m) ? m.GetString() : null,
                    S = el.TryGetProperty("s", out JsonElement s) ? s.GetString() : null,
                    Ch = el.TryGetProperty("ch", out JsonElement ch) ? ch.GetString() : null,
                    H = el.TryGetProperty("h", out JsonElement hh) ? hh.GetString() : null,
                    N = el.TryGetProperty("n", out JsonElement n) ? n.GetString() : null,
                });
            }
            catch { }
        }
        return list;
    }

    // 供配置页 UI 读取实时会话状态（未激活时 UI 不会拿到模块实例）
    public (int Rounds, long Total, bool Busy, int Port) LiveSnapshot()
    {
        int r;
        long v;
        lock (sync)
        {
            r = rounds;
            v = total.Total;
        }
        bool busy;
        try { busy = ChatBot.IsChatOccupied; } catch { busy = false; }
        return (r, v, busy, actualPort);
    }

    internal static void DeleteDataFile()
    {
        // 清空=删除汇总与所有角色分日志
        try
        {
            foreach (string f in AllUsageLogs().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                try { lock (fileIoLock) File.Delete(f); } catch { }
        }
        catch { }
    }

    // 重写全部用量日志（汇总+各角色分日志）：剔除时间区间（含端点，精确到秒）内的记录。
    // 同一轮在分日志与汇总中各存一份，按（时间戳+总量）去重后返回删除轮数
    internal static int ClearRecords(DateTime from, DateTime to)
    {
        try
        {
            HashSet<string> removedKeys = new();
            foreach (string path in AllUsageLogs().Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (!File.Exists(path)) continue;
                List<UsageRec> recs = ReadUsageRecords(path);
                if (recs.Count == 0) continue;
                List<string> kept = new(recs.Count);
                bool changed = false;
                foreach (UsageRec r in recs)
                {
                    if (r.T >= from && r.T <= to)
                    {
                        removedKeys.Add($"{r.T:yyyy-MM-dd'T'HH:mm:ss.fff}|{r.V}");
                        changed = true;
                    }
                    else
                        kept.Add(RecLine(r));
                }
                if (changed)
                    lock (fileIoLock) File.WriteAllLines(path, kept);
            }
            return removedKeys.Count;
        }
        catch { return 0; }
    }

    // 清空历史（配置页）：无区间=全部删除；有区间=仅删该时间段（精确到秒）。会话统计不动。
    public int ResetHistory(DateTime? from = null, DateTime? to = null)
    {
        if (from == null || to == null)
        {
            DeleteDataFile();
            lock (sync) { days.Clear(); hours.Clear(); }
            return 0;
        }
        int removed = ClearRecords(from.Value, to.Value);
        LoadHistory();
        return removed;
    }

    void LoadHistory()
    {
        masterFile = LocateDataFile();
        logFile = CharLogPath(masterFile, Character?.Name ?? "");
        EnsureCharLog(logFile, Character?.Name ?? "");
        lock (sync) { days.Clear(); hours.Clear(); }
        try
        {
            foreach (UsageRec rec in ReadUsageRecords(logFile))
            {
                string day = rec.T.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                bool peak = PricingStore.IsPeak(rec.T);
                lock (sync)
                {
                    if (!days.TryGetValue(day, out DayStat ds)) days[day] = ds = new DayStat();
                    ds.Rounds++; ds.V += rec.V; ds.In += rec.I; ds.Out += rec.O; ds.Cached += rec.C;
                    AddModelAgg(ds.ByModel ??= new Dictionary<string, Agg[]>(), rec.M ?? "", rec.Ch ?? "", rec.H ?? "", rec.I, rec.O, rec.C, peak);
                    if (!hours.TryGetValue(day, out DayStat[] hs)) hours[day] = hs = new DayStat[24];
                    DayStat hr = hs[rec.T.Hour] ??= new DayStat();
                    hr.Rounds++; hr.V += rec.V; hr.In += rec.I; hr.Out += rec.O; hr.Cached += rec.C;
                }
            }
            long totV = 0; int totR = 0;
            lock (sync) foreach (DayStat d in days.Values) { totV += d.V; totR += d.Rounds; }
            logger.LogInformation($"Token用量看板：已加载本角色（{Character?.Name ?? "?"}）历史用量 {days.Count} 天 / {totR} 轮 / {totV} Token（角色分日志 {logFile}，汇总 {masterFile}）");
        }
        catch (Exception ex) { WarnIoOnce(ex); }
    }

    // 按天键区间聚合为 {v,i,o,c,r}（ISO日期字符串可按序直接比较）
    string RangeJson(string fromDate, string toDate)
    {
        long v = 0, i = 0, o = 0, c = 0;
        int r = 0;
        lock (sync)
        {
            foreach (KeyValuePair<string, DayStat> kv in days)
            {
                if (string.CompareOrdinal(kv.Key, fromDate) < 0) continue;
                if (string.CompareOrdinal(kv.Key, toDate) > 0) break;
                v += kv.Value.V; i += kv.Value.In; o += kv.Value.Out; c += kv.Value.Cached; r += kv.Value.Rounds;
            }
        }
        return $"{{\"v\":{v},\"i\":{i},\"o\":{o},\"c\":{c},\"r\":{r}}}";
    }

    static string ValidRange(string? s) => s is "today" or "d7" or "d30" or "total" ? s : "session";

    // 灵枢(LanguageModelRouter)渠道组快照（反射通用读取，不依赖具体插件类型）
    internal sealed record RouterGroup(int Slot, string Name, string Model, string Host, bool Configured);

    List<RouterGroup> ReadRouterGroups()
    {
        List<RouterGroup> list = new();
        try
        {
            object? lm = ChatBot.LanguageModel;
            object? config = lm?.GetType().GetProperty("Configuration")?.GetValue(lm)
                ?? lm?.GetType().GetField("Configuration")?.GetValue(lm);
            object? groups = config?.GetType().GetProperty("Groups")?.GetValue(config);
            if (groups is System.Collections.IEnumerable enumerable)
            {
                int slot = 0;
                foreach (object g in enumerable)
                {
                    string name = ReadStringMember(g, "GroupName") ?? "";
                    string model = ReadStringMember(g, "ModelId") ?? "";
                    string endpoint = ReadStringMember(g, "Endpoint") ?? "";
                    bool configured = g.GetType().GetProperty("IsConfigured")?.GetValue(g) is true;
                    string host = Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? u) ? u.Host : endpoint;
                    list.Add(new RouterGroup(slot, name.Length > 0 ? name : $"第{slot + 1}组", model, host, configured));
                    slot++;
                }
            }
        }
        catch { }
        return list;
    }

    int ReadForcedGroupIndex()
    {
        try
        {
            object? lm = ChatBot.LanguageModel;
            object? config = lm?.GetType().GetProperty("Configuration")?.GetValue(lm)
                ?? lm?.GetType().GetField("Configuration")?.GetValue(lm);
            return config?.GetType().GetProperty("ForcedGroupIndex")?.GetValue(config) is int i ? i : -1;
        }
        catch { return -1; }
    }

    // 当前有效渠道 + 模型 + endpoint域名：灵枢 = 强制锁定组（默认容灾模式下，容灾成功会先于
    // 响应返回把 ForcedGroupIndex 持久化，故 TokenUsed 时读到的即本轮实际渠道；「优先主渠道」
    // 模式下的瞬时容灾不落配置，该轮会归因到起始组——已知小概率误差，见实现方案文档）；
    // 其余语言模型视为单渠道（渠道名取 endpoint 域名，取不到则"默认渠道"）。
    // 域名随日志落盘（h 字段），价格规则可按 URL 匹配（比组名稳定，推荐）。
    (string Channel, string Model, string Host) ResolveChannelAndModel()
    {
        try
        {
            List<RouterGroup> groups = ReadRouterGroups();
            if (groups.Count > 0)
            {
                int forced = ReadForcedGroupIndex();
                RouterGroup? eff = groups.FirstOrDefault(g => g.Configured && g.Slot == forced)
                    ?? groups.FirstOrDefault(g => g.Configured)
                    ?? groups[0];
                return (eff.Name, eff.Model.Length > 0 ? eff.Model : "未知模型", eff.Host);
            }
            object? lm = ChatBot.LanguageModel;
            if (lm == null) return ("未配置", "未配置", "");
            string model = ReadStringMember(lm, "ModelId") ?? ReadStringMember(lm, "ModelName") ?? ReadStringMember(lm, "modelId") ?? "";
            object? config = lm.GetType().GetProperty("Configuration")?.GetValue(lm)
                ?? lm.GetType().GetField("Configuration")?.GetValue(lm);
            if (model.Length == 0 && config != null)
                model = ReadStringMember(config, "modelId") ?? ReadStringMember(config, "ModelId") ?? ReadStringMember(config, "ModelName") ?? "";
            string channel = "默认渠道";
            // 官方 OpenAI 插件配置为小写字段 endpoint/modelId；灵枢为属性 Endpoint/ModelId
            string? endpoint = (config == null ? null : ReadStringMember(config, "Endpoint", "endpoint") ?? ReadStringMember(config, "BaseUrl", "baseUrl") ?? ReadStringMember(config, "Url", "url"))
                ?? ReadStringMember(lm, "Endpoint", "endpoint") ?? ReadStringMember(lm, "BaseUrl", "baseUrl");
            string host = "";
            if (endpoint != null && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? u) && u.Host.Length > 0)
            {
                host = u.Host;
                channel = host;
            }
            return (channel, model.Length > 0 ? model : lm.GetType().Name, host);
        }
        catch { return ("未知", "未知", ""); }
    }

    // 供配置页"渠道价格设置"列出当前角色的渠道（未挂灵枢则返回单渠道）
    public List<ChannelInfo> GetLiveChannels()
    {
        List<ChannelInfo> list = new();
        try
        {
            string owner = Character?.Name ?? "";
            List<RouterGroup> groups = ReadRouterGroups();
            if (groups.Count > 0)
            {
                foreach (RouterGroup g in groups)
                    list.Add(new ChannelInfo { Owner = owner, Name = g.Name, Model = g.Model, Host = g.Host });
            }
            else
            {
                (string ch, string model, string host) = ResolveChannelAndModel();
                list.Add(new ChannelInfo { Owner = owner, Name = ch, Model = model, Host = host });
            }
        }
        catch { }
        return list;
    }

    static string? ReadStringMember(object obj, params string[] names)
    {
        Type t = obj.GetType();
        foreach (string name in names)
        {
            try
            {
                object? v = t.GetProperty(name)?.GetValue(obj) ?? t.GetField(name)?.GetValue(obj);
                if (v is string s && s.Trim().Length > 0)
                    return s.Trim();
            }
            catch { }
        }
        return null;
    }

    static bool IsIsoDate(string s)
    {
        if (s.Length != 10 || s[4] != '-' || s[7] != '-') return false;
        foreach (char ch in s)
            if (ch != '-' && (ch < '0' || ch > '9')) return false;
        return true;
    }

    bool TryStartServer()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int port = Math.Clamp(Configuration.Port + attempt, 1, 65535);
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(4);
                serverCts = new CancellationTokenSource();
                actualPort = port;
                _ = Task.Run(() => AcceptLoopAsync(listener, serverCts.Token));
                logger.LogInformation($"Token用量看板服务已启动: http://127.0.0.1:{port}/stats");
                return true;
            }
            catch (Exception ex)
            {
                try { listener?.Stop(); } catch { }
                if (attempt == 19)
                    logger.LogError(ex, $"Token用量看板：端口 {Configuration.Port}~{port} 均不可用，统计挂件未开启");
            }
        }
        return false;
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token用量看板服务循环退出");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                NetworkStream stream = client.GetStream();
                byte[] buffer = new byte[4096];
                int read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    return;
                string request = Encoding.UTF8.GetString(buffer, 0, read);
                string firstLine = request.Split('\r', '\n')[0];
                string[] parts = firstLine.Split(' ');
                string path = parts.Length > 1 ? parts[1] : "/";
                int queryIndex = path.IndexOf('?');
                string query = queryIndex >= 0 ? path[(queryIndex + 1)..] : "";
                if (queryIndex >= 0)
                    path = path[..queryIndex];

                if (path == "/stats")
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildStatsJson()), cancellationToken);
                else if (path == "/history")
                {
                    // /history?day=YYYY-MM-DD → 单天按小时明细；否则全部按天
                    string? day = null;
                    int di = query.IndexOf("day=", StringComparison.Ordinal);
                    if (di >= 0)
                    {
                        int end = query.IndexOf('&', di);
                        string cand = query[(di + 4)..(end < 0 ? query.Length : end)];
                        if (IsIsoDate(cand)) day = cand;
                    }
                    byte[] body = Encoding.UTF8.GetBytes(day != null ? BuildHoursJson(day) : BuildHistoryJson());
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", body, cancellationToken);
                }
                else if (path == "/records")
                {
                    int n = 15;
                    int ni = query.IndexOf("n=", StringComparison.Ordinal);
                    if (ni >= 0)
                    {
                        int end = query.IndexOf('&', ni);
                        string ns = query[(ni + 2)..(end < 0 ? query.Length : end)];
                        if (int.TryParse(ns, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                            n = Math.Clamp(parsed, 1, 100);
                    }
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildRecordsJson(n, QueryParam(query, "name"))), cancellationToken);
                }
                else if (path == "/analytics")
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildAnalyticsJson(query)), cancellationToken);
                else if (path == "/pricing")
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildPricingJson()), cancellationToken);
                else if (path == "/" || path == "/index.html")
                    await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(DashboardHtml), cancellationToken);
                else
                    await RespondAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "404"u8.ToArray(), cancellationToken);
            }
        }
        catch { }
    }

    static async Task RespondAsync(NetworkStream stream, string status, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        string head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    string BuildStatsJson()
    {
        TokenUsage t, l;
        int r;
        string src;
        lock (sync)
        {
            t = total;
            l = lastRound;
            r = rounds;
            src = curSource;
        }
        int elapsed = Math.Max(0, (int)(DateTime.Now - sessionStart).TotalSeconds);
        (string channel, string model, string host) = ResolveChannelAndModel();
        string character = Character?.Name ?? "?";
        bool busy;
        try { busy = ChatBot.IsChatOccupied; } catch { busy = false; }

        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        StringBuilder json = new(768);
        json.Append('{');
        json.Append($"\"character\":\"{JsonEscape(character)}\"");
        json.Append($",\"model\":\"{JsonEscape(model)}\"");
        json.Append($",\"channel\":\"{JsonEscape(channel)}\"");
        json.Append($",\"src\":\"{JsonEscape(src)}\"");
        json.Append($",\"elapsed\":{elapsed}");
        json.Append($",\"rounds\":{r}");
        json.Append($",\"total\":{t.Total}");
        json.Append($",\"input\":{t.Input}");
        json.Append($",\"output\":{t.Output}");
        json.Append($",\"cached\":{t.Cached}");
        json.Append($",\"lastInput\":{l.Input}");
        json.Append($",\"lastOutput\":{l.Output}");
        json.Append($",\"lastCached\":{l.Cached}");
        json.Append($",\"busy\":{(busy ? "true" : "false")}");
        json.Append($",\"ring\":{Configuration.RingSize}");
        json.Append($",\"overlay\":\"{JsonEscape(overlayState)}\"");
        json.Append($",\"ringDef\":\"{ValidRange(Configuration.RingRange)}\"");
        json.Append($",\"logFile\":\"{JsonEscape(logFile)}\"");
        json.Append(",\"ranges\":{");
        json.Append("\"session\":{").Append($"\"v\":{t.Total},\"i\":{t.Input},\"o\":{t.Output},\"c\":{t.Cached},\"r\":{r}").Append('}');
        json.Append(",\"today\":").Append(RangeJson(today, today));
        json.Append(",\"d7\":").Append(RangeJson(DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today));
        json.Append(",\"d30\":").Append(RangeJson(DateTime.Now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today));
        json.Append(",\"total\":").Append(RangeJson("0000-01-01", "9999-12-31"));
        json.Append("},\"costs\":{");
        json.Append("\"session\":").Append(CostJson(SessionCost()));
        json.Append(",\"today\":").Append(CostJson(RangeCost(today, today)));
        json.Append(",\"d7\":").Append(CostJson(RangeCost(DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today)));
        json.Append(",\"d30\":").Append(CostJson(RangeCost(DateTime.Now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today)));
        json.Append(",\"total\":").Append(CostJson(RangeCost("0000-01-01", "9999-12-31")));
        json.Append("}}");
        return json.ToString();
    }

    decimal? SessionCost()
    {
        lock (sync) return sessionAggs.Count == 0 ? null : AggsCost(sessionAggs);
    }

    // 按天键区间合并 模型×渠道×峰谷 聚合后计价（/stats 每秒轮询调用，必须走预聚合）
    decimal? RangeCost(string fromDate, string toDate)
    {
        Dictionary<string, Agg[]> merged = new();
        lock (sync)
        {
            foreach (KeyValuePair<string, DayStat> kv in days)
            {
                if (string.CompareOrdinal(kv.Key, fromDate) < 0) continue;
                if (string.CompareOrdinal(kv.Key, toDate) > 0) break;
                if (kv.Value.ByModel == null) continue;
                foreach (KeyValuePair<string, Agg[]> m in kv.Value.ByModel)
                {
                    if (!merged.TryGetValue(m.Key, out Agg[]? slots))
                        merged[m.Key] = slots = new Agg[2];
                    for (int s = 0; s < 2; s++)
                    {
                        Agg from = m.Value[s];
                        if (from == null) continue;
                        Agg to = slots[s] ??= new Agg();
                        to.In += from.In; to.Out += from.Out; to.Cached += from.Cached;
                    }
                }
            }
        }
        return merged.Count == 0 ? null : AggsCost(merged);
    }

    // 聚合计价：键 model\u001Fchannel\u001Fhost 拆开匹配规则；[谷,峰] 槽分别用对应档价
    // （规则关闭峰谷时两槽都按谷价）。完全无匹配规则时返回 null（界面显示 "—"）。
    static decimal? AggsCost(Dictionary<string, Agg[]> aggs)
    {
        decimal total = 0;
        bool matched = false;
        foreach (KeyValuePair<string, Agg[]> kv in aggs)
        {
            string[] parts = kv.Key.Split('\u001F');
            string model = parts.Length > 0 ? parts[0] : "";
            string channel = parts.Length > 1 ? parts[1] : "";
            string host = parts.Length > 2 ? parts[2] : "";
            PriceRule? rule = PricingStore.Match(channel, model, host);
            if (rule == null) continue;
            matched = true;
            Agg off = kv.Value[0];
            Agg pk = kv.Value[1];
            if (off != null)
                total += TokensCost(off, rule.HitOff, rule.MissOff, rule.OutOff);
            if (pk != null)
                total += TokensCost(pk,
                    rule.PeakEnabled ? rule.HitPeak : rule.HitOff,
                    rule.PeakEnabled ? rule.MissPeak : rule.MissOff,
                    rule.PeakEnabled ? rule.OutPeak : rule.OutOff);
        }
        return matched ? total : null;
    }

    static decimal TokensCost(Agg a, decimal hit, decimal miss, decimal output) =>
        (a.Cached * hit + Math.Max(0, a.In - a.Cached) * miss + a.Out * output) / 1_000_000m;

    // 费用 JSON 值：null 或带引号的字符串（保留小数精度，客户端直接拼 ¥）
    static string CostJson(decimal? cost) => cost == null
        ? "null"
        : $"\"{cost.Value.ToString("0.####", CultureInfo.InvariantCulture)}\"";

    string BuildHistoryJson()
    {
        StringBuilder json = new(2048);
        json.Append("{\"days\":[");
        lock (sync)
        {
            bool first = true;
            foreach (KeyValuePair<string, DayStat> kv in days)
            {
                if (!first) json.Append(',');
                first = false;
                json.Append($"{{\"d\":\"{kv.Key}\",\"rounds\":{kv.Value.Rounds},\"v\":{kv.Value.V},\"i\":{kv.Value.In},\"o\":{kv.Value.Out},\"c\":{kv.Value.Cached}}}");
            }
        }
        json.Append("]}");
        return json.ToString();
    }

    string BuildHoursJson(string day)
    {
        StringBuilder json = new(1024);
        json.Append($"{{\"day\":\"{day}\",\"hours\":[");
        lock (sync)
        {
            if (hours.TryGetValue(day, out DayStat[]? hs))
            {
                bool first = true;
                for (int h = 0; h < 24; h++)
                {
                    DayStat d = hs[h];
                    if (d == null) continue;
                    if (!first) json.Append(',');
                    first = false;
                    json.Append($"{{\"h\":{h},\"rounds\":{d.Rounds},\"v\":{d.V},\"i\":{d.In},\"o\":{d.Out},\"c\":{d.Cached}}}");
                }
            }
        }
        json.Append("]}");
        return json.ToString();
    }

    // 最近 n 轮原始记录（倒序，含逐轮模型/来源/渠道/角色与按当前价格规则算出的费用），
    // 供详情页"最近对话"面板
    // 提取 query 参数值（要求键在串首或前接 &，避免误匹配后缀相同的键；返回值未做URL解码）
    static string QueryParam(string query, string key)
    {
        int at = 0;
        while (at <= query.Length)
        {
            int i = query.IndexOf(key + "=", at, StringComparison.Ordinal);
            if (i < 0) return "";
            if (i == 0 || query[i - 1] == '&')
            {
                int v = i + key.Length + 1;
                int end = query.IndexOf('&', v);
                return query[v..(end < 0 ? query.Length : end)];
            }
            at = i + 1;
        }
        return "";
    }

    string BuildRecordsJson(int n, string nameFilter)
    {
        // nameFilter：空=本角色（实例所属角色），all=汇总全部角色，其余=指定角色名（URL编码）
        string want = nameFilter.Length == 0 ? (Character?.Name ?? "") : (nameFilter == "all" ? "" : Uri.UnescapeDataString(nameFilter));
        StringBuilder json = new(4096);
        json.Append("{\"recs\":[");
        try
        {
            List<UsageRec> recs = ReadUsageRecords(LocateDataFile());
            int written = 0;
            for (int i = recs.Count - 1; i >= 0 && written < n; i--)
            {
                UsageRec r = recs[i];
                if (want.Length > 0 && !string.Equals(r.N ?? "", want, StringComparison.Ordinal)) continue;
                if (written > 0) json.Append(',');
                written++;
                json.Append($"{{\"t\":\"{r.T:yyyy-MM-dd HH:mm:ss}\",\"v\":{r.V},\"i\":{r.I},\"o\":{r.O},\"c\":{r.C},\"m\":\"{JsonEscape(r.M ?? "")}\",\"s\":\"{JsonEscape(r.S ?? "")}\",\"ch\":\"{JsonEscape(r.Ch ?? "")}\",\"h\":\"{JsonEscape(r.H ?? "")}\",\"n\":\"{JsonEscape(r.N ?? "")}\",\"co\":{CostJson(PricingStore.Cost(r.I, r.O, r.C, r.T, r.Ch, r.M, r.H))}}}");
            }
        }
        catch { }
        json.Append("]}");
        return json.ToString();
    }

    sealed class DimAgg
    {
        public int R;
        public long I, O, C, V;
        public decimal Cost;
        public bool Matched;
    }

    // 维度分析：按 来源/渠道/角色/模型 聚合 tokens 与费用（逐条记录计价，峰谷按记录时间戳精确判定）。
    // query: range=today|d7|d30|total|custom&from=YYYY-MM-DD&to=YYYY-MM-DD&name=<空=本角色|all=全部|角色名>
    string BuildAnalyticsJson(string query)
    {
        DateTime now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string range = QueryParam(query, "range");
        string from = QueryParam(query, "from"), to = QueryParam(query, "to");
        if (range == "d7") from = now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (range == "d30") from = now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (range != "custom") { from = today; range = "today"; }
        if (to.Length == 0 || !IsIsoDate(to)) to = today;
        if (from.Length == 0 || !IsIsoDate(from)) from = today;
        string nameQ = QueryParam(query, "name");
        string wantName = nameQ.Length == 0 ? (Character?.Name ?? "") : (nameQ == "all" ? "" : Uri.UnescapeDataString(nameQ));

        Dictionary<string, DimAgg> bySource = new(), byChannel = new(), byName = new(), byModel = new();
        DimAgg totalDim = new();
        foreach (UsageRec rec in ReadUsageRecords(LocateDataFile()))
        {
            if (wantName.Length > 0 && !string.Equals(rec.N ?? "", wantName, StringComparison.Ordinal)) continue;
            string day = rec.T.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(day, from) < 0 || string.CompareOrdinal(day, to) > 0) continue;
            decimal? cost = PricingStore.Cost(rec.I, rec.O, rec.C, rec.T, rec.Ch, rec.M, rec.H);
            void Add(Dictionary<string, DimAgg> dict, string key)
            {
                DimAgg a = dict.TryGetValue(key, out DimAgg? v) ? v : dict[key] = new DimAgg();
                a.R++; a.I += rec.I; a.O += rec.O; a.C += rec.C; a.V += rec.V;
                if (cost != null) { a.Cost += cost.Value; a.Matched = true; }
            }
            Add(bySource, rec.S?.Length > 0 ? rec.S : "未知");
            Add(byChannel, rec.Ch?.Length > 0 ? rec.Ch : "未知");
            Add(byName, rec.N?.Length > 0 ? rec.N : "未知");
            Add(byModel, rec.M?.Length > 0 ? rec.M : "未知");
            totalDim.R++; totalDim.I += rec.I; totalDim.O += rec.O; totalDim.C += rec.C; totalDim.V += rec.V;
            if (cost != null) { totalDim.Cost += cost.Value; totalDim.Matched = true; }
        }

        StringBuilder json = new(4096);
        json.Append($"{{\"from\":\"{from}\",\"to\":\"{to}\"");
        json.Append($",\"total\":{DimRow(totalDim, "")}");
        json.Append(",\"bySource\":").Append(DimList(bySource));
        json.Append(",\"byChannel\":").Append(DimList(byChannel));
        json.Append(",\"byName\":").Append(DimList(byName));
        json.Append(",\"byModel\":").Append(DimList(byModel));
        json.Append("}");
        return json.ToString();

        static string DimRow(DimAgg a, string key) => $"{{\"k\":\"{JsonEscape(key)}\",\"r\":{a.R},\"i\":{a.I},\"o\":{a.O},\"c\":{a.C},\"v\":{a.V},\"cost\":{(a.Matched ? $"\"{a.Cost.ToString("0.####", CultureInfo.InvariantCulture)}\"" : "null")}}}";
        static string DimList(Dictionary<string, DimAgg> dict)
        {
            StringBuilder sb = new(1024);
            sb.Append('[');
            bool first = true;
            foreach (KeyValuePair<string, DimAgg> kv in dict.OrderByDescending(x => x.Value.V).Take(20))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(DimRow(kv.Value, kv.Key));
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    // 价格规则（只读）+ 检测到的渠道（详情页展示；编辑入口在模块配置页）
    string BuildPricingJson()
    {
        StringBuilder json = new(2048);
        json.Append("{\"rules\":[");
        bool first = true;
        foreach (PriceRule r in PricingStore.Rules())
        {
            if (!first) json.Append(',');
            first = false;
            json.Append($"{{\"name\":\"{JsonEscape(r.Name)}\",\"url\":\"{JsonEscape(r.UrlMatch ?? "")}\",\"channel\":\"{JsonEscape(r.ChannelMatch ?? "")}\",\"model\":\"{JsonEscape(r.ModelMatch ?? "")}\",\"peak\":{(r.PeakEnabled ? "true" : "false")}");
            json.Append($",\"hit\":[{Dec(r.HitPeak)},{Dec(r.HitOff)}],\"miss\":[{Dec(r.MissPeak)},{Dec(r.MissOff)}],\"out\":[{Dec(r.OutPeak)},{Dec(r.OutOff)}]}}");
        }
        json.Append("],\"channels\":[");
        first = true;
        List<ChannelInfo> channels = GetLiveChannels();
        foreach (ChannelInfo scanned in PricingStore.ScanChannels())
            if (!channels.Exists(c => c.Owner == scanned.Owner && c.Name == scanned.Name && c.Model == scanned.Model))
                channels.Add(scanned);
        foreach (ChannelInfo c in channels)
        {
            if (!first) json.Append(',');
            first = false;
            json.Append($"{{\"owner\":\"{JsonEscape(c.Owner)}\",\"name\":\"{JsonEscape(c.Name)}\",\"model\":\"{JsonEscape(c.Model)}\",\"host\":\"{JsonEscape(c.Host)}\"}}");
        }
        json.Append("]}");
        return json.ToString();

        static string Dec(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    }

    static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    // 向主窗口页面注册本实例（端口+角色名+外观）。页面端为共享管理器（__tstatsMgr）：
    // 多角色多开时页面上只有一个挂件，数据源跟随主窗口当前查看的角色（路由 /agent/{name}），
    // 实例之间不再互相拆建。注入幂等：重复注册仅刷新注册表，稳态返回 ok+矩形。
    async Task EnsureOverlayAsync()
    {
        try
        {
            if (mainWindow == null || !Electron.WindowManager.BrowserWindows.Contains(mainWindow))
            {
                mainWindow = null;
                try
                {
                    mainWindow = Electron.WindowManager.BrowserWindows.OrderBy(w => w.Id).FirstOrDefault();
                }
                catch { }
                if (mainWindow == null)
                    return;
            }
            BrowserWindow main = mainWindow;
            string js = OverlayJs
                .Replace("__PORT__", actualPort.ToString())
                .Replace("__NAME__", JsonEscape(Character?.Name ?? ""))
                .Replace("__RING__", Configuration.RingSize.ToString())
                .Replace("__GAP__", Configuration.GapBesideSwitch.ToString())
                .Replace("__CARDW__", Configuration.Width.ToString())
                .Replace("__CARDH__", Configuration.Height.ToString());
            Task<string> call = IpcAsync(() => main.WebContents.ExecuteJavaScriptAsync<string>(js));
            if (await Task.WhenAny(call, Task.Delay(2500)) != call)
            {
                SetOverlayState("timeout");
                return;
            }
            SetOverlayState(call.Status == TaskStatus.RanToCompletion
                ? (call.Result ?? "").Trim().Trim('"')
                : "faulted");
        }
        catch (Exception ex)
        {
            SetOverlayState("error:" + ex.Message);
        }
    }

    // 记录挂件状态；仅当状态类别变化时输出日志（稳态 ok/hidden 静默，避免多开刷屏）
    void SetOverlayState(string state)
    {
        overlayState = state;
        string cat = state.Length == 0 ? "empty" : state.Split(' ')[0].Split(':')[0];
        if (cat == lastOverlayCat)
            return;
        bool wasSilent = lastOverlayCat.Length == 0 || lastOverlayCat is "pending" or "hidden" or "nopage" or "ok";
        lastOverlayCat = cat;
        if (cat == "injected")
            logger.LogInformation($"Token用量看板挂件已就绪（{Character?.Name ?? "?"}，停靠『展开思考』开关旁，跟随当前查看的角色），详情页 http://127.0.0.1:{actualPort}/");
        else if (cat is "timeout" or "faulted" or "error" or "empty")
            logger.LogWarning($"Token用量看板：挂件状态异常（{state}）");
        else if (!wasSilent && cat is "hidden" or "nopage")
            logger.LogInformation($"Token用量看板：挂件已隐藏（{state}）");
    }

    // 挂件注入脚本。定位：文本含“展开思考”的叶子元素 + 其父级内的 .ant-switch；
    // 悬停展开/收起为纯本地DOM（无窗口resize、无IPC）；数据每秒 fetch 本地 /stats。
    // 圆心最多4字符（1.2K/999K/9.9M）；点击圆环或卡片范围胶囊切换 本次/今天/7天/30天/累计，
    // 卡片网格内容随范围联动（时长/模型/最近一轮固定为会话数据）。
    const string OverlayJs = """
        (function(){
          if(!document.body)return 'nopage';
          var M=window.__tstatsMgr;
          var MV=3; // 管理器脚本版本：插件升级后页面内驻留的旧管理器需重建（改此结构时递增）
          if(M&&M.v!==MV){
            // 旧版管理器：拆除其UI并解除能力（清注册表/置空回调，旧定时器此后空转无害）
            try{if(M.drop)M.drop()}catch(e){}
            try{M.reg={};M.place=null;M.tick=null;M.ui=null}catch(e){}
            M=null;
          }
          if(!M){
            // 更早期单实例挂件残留清理（升级/重载场景）
            var legacy=document.getElementById('tstats-root');
            if(legacy){try{if(window.__tstatsTeardown)window.__tstatsTeardown()}catch(e){}
              if(legacy.parentNode)legacy.parentNode.removeChild(legacy)}
            // 共享管理器：所有已激活角色的统计实例都向它注册（端口+角色名+外观配置），
            // 页面上始终只有一个挂件，数据源跟随主窗口当前查看的角色（路由 /agent/{name}）。
            M=window.__tstatsMgr={v:MV,reg:{},port:0,ui:null,place:null,tick:null,built:0,sel:null};
            try{var s0=localStorage.getItem('tstatsRange');if(['session','today','d7','d30','total'].indexOf(s0)>=0)M.sel=s0}catch(e){}
            M.visible=function(){try{var m=location.pathname.match(/\/agent\/([^\/]+)/);return m?decodeURIComponent(m[1]):''}catch(e){return ''}};
            M.rect=function(){if(!M.ui)return '0,0,0,0';var r=M.ui.root.getBoundingClientRect();
              return Math.round(r.left)+','+Math.round(r.top)+','+Math.round(r.width)+','+Math.round(r.height)};
            M.drop=function(){if(!M.ui)return;try{M.ui.ro&&M.ui.ro.disconnect()}catch(e){}
              try{M.ui.root.parentNode&&M.ui.root.parentNode.removeChild(M.ui.root)}catch(e){}
              M.ui=null;M.place=null;M.tick=null};
            M.build=function(){
              var reg=M.reg[M.port];if(!reg)return;
              var RING=reg.cfg.ring,GAP=reg.cfg.gap,CW=reg.cfg.cw,CH=reg.cfg.ch;
              M.drop();
              var root=document.createElement('div');
              root.id='tstats-root';
              var rs=root.style;rs.position='fixed';rs.zIndex='99999';rs.pointerEvents='none';rs.left='0';rs.top='0';
              var sh=root.attachShadow({mode:'open'});
              sh.innerHTML='<style>'+
              '*{margin:0;padding:0;box-sizing:border-box}'+
              '.wrap{font-family:"Segoe UI",system-ui,"Microsoft YaHei",sans-serif}'+
              '.ring{width:'+RING+'px;height:'+RING+'px;position:relative;cursor:pointer;pointer-events:auto;filter:drop-shadow(0 2px 5px rgba(0,0,0,.18));transition:transform .2s}'+
              '.ring:hover{transform:scale(1.08)}'+
              '.ring svg{display:block;width:'+RING+'px;height:'+RING+'px}'+
              '.value{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;color:#232a3a;font-weight:700;font-size:10px;line-height:1.1;font-variant-numeric:tabular-nums}'+
              '.value .lbl{font-size:7px;color:#9aa1b0;font-weight:400}'+
              '.arc{transition:stroke-dasharray .3s}'+
              '.busyArc{display:none}'+
              '.ring.busy .arc{opacity:.25}'+
              '.ring.busy .busyArc{display:block;animation:tspin .9s linear infinite;transform-box:fill-box;transform-origin:center}'+
              '.ring.busy .value{animation:tpulse 1s ease-in-out infinite}'+
              '@keyframes tspin{to{transform:rotate(360deg)}}'+
              '@keyframes tpulse{50%{transform:scale(1.08)}}'+
              '.card{position:absolute;width:'+CW+'px;height:'+CH+'px;background:#fffdf9;border:1px solid #ecebe6;border-radius:12px;box-shadow:0 10px 28px rgba(23,27,40,.16),0 2px 6px rgba(23,27,40,.08);padding:10px 12px;display:flex;flex-direction:column;pointer-events:auto;opacity:0;transform:scale(.96) translateY(-6px);transform-origin:top left;transition:opacity .15s,transform .15s;visibility:hidden}'+
              '.wrap.on .card{opacity:1;transform:scale(1) translateY(0);visibility:visible}'+
              '.head{display:flex;align-items:center;gap:6px;margin:0 0 7px}'+
              '.dot{width:7px;height:7px;border-radius:50%;background:#34d399;box-shadow:0 0 6px rgba(52,211,153,.65);flex:0 0 auto;transition:background .3s}'+
              '.title{font-size:12px;color:#3a4051;font-weight:650;letter-spacing:.3px}'+
              '.rgn{font-size:9.5px;color:#2f6fd8;background:#eef5ff;border:1px solid #d9e8ff;border-radius:999px;padding:1px 8px;font-weight:600}'+
              '.char{font-size:10px;color:#9aa1b0;margin-left:auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:64px}'+
              '.grid{display:grid;grid-template-columns:1fr 1fr;gap:4px 8px}'+
              '.item{display:flex;justify-content:space-between;align-items:baseline;background:#fbfcfe;border:1px solid #eef0f5;border-radius:8px;padding:3px 8px;font-size:11px}'+
              '.k{color:#9aa1b0;font-size:9.5px;letter-spacing:.4px}.v{font-variant-numeric:tabular-nums;font-weight:650;color:#232a3a}'+
              '.v.inp{color:#2f6fd8}.v.out{color:#db2777}.v.cache{color:#d97706}.v.rate{color:#7c3aed}.v.cost{color:#0e9f6e}'+
              '.hrow{display:flex;gap:4px;margin-top:7px}'+
              '.hb{flex:1;text-align:center;font-size:9px;color:#9aa1b0;background:#fbfcfe;border:1px solid #eef0f5;border-radius:999px;padding:2px 0;line-height:1.35;cursor:pointer;transition:border-color .12s,background .12s}'+
              '.hb:hover{border-color:#a9c4f8}'+
              '.hb b{display:block;font-size:11px;color:#3a4051;font-weight:650;font-variant-numeric:tabular-nums}'+
              '.hb.on{border-color:#3b82f6;background:#eef5ff}'+
              '.hb.on b{color:#2563eb}'+
              '.last{color:#9aa1b0;font-size:10px;margin-top:7px}'+
              '.foot{margin-top:auto;display:flex;justify-content:space-between;align-items:center;color:#b6bac4;font-size:9.5px}'+
              '.lnk{color:#2f6fd8;text-decoration:none;font-size:9.5px;border:1px solid #d5e4ff;background:#f2f7ff;border-radius:999px;padding:2px 9px}'+
              '.lnk:hover{background:#e3eeff}'+
              '</style>'+
              '<div class="wrap"><div class="ring">'+
              '<svg viewBox="0 0 56 56">'+
              '<circle cx="28" cy="28" r="24" fill="#fffdf9" stroke="#ecebe6" stroke-width="1"/>'+
              '<circle cx="28" cy="28" r="21" fill="none" stroke="#eef0f4" stroke-width="4.5"/>'+
              '<circle class="arc" cx="28" cy="28" r="21" fill="none" stroke="#3b82f6" stroke-width="4.5" stroke-linecap="round" stroke-dasharray="0 131.9" transform="rotate(-90 28 28)"/>'+
              '<circle class="busyArc" cx="28" cy="28" r="26" fill="none" stroke="#3b82f6" stroke-width="2.5" stroke-linecap="round" stroke-dasharray="22 142" />'+
              '</svg>'+
              '<div class="value"><span id="t">0</span><span class="lbl" id="tl">Token</span></div>'+
              '</div><div class="card">'+
              '<div class="head"><span class="dot" id="dot"></span><span class="title">Token 用量</span><span class="rgn" id="rgn">本次</span><span class="char" id="c">…</span></div>'+
              '<div class="grid">'+
              '<div class="item"><span class="k">总量</span><span class="v" id="v1">0</span></div>'+
              '<div class="item"><span class="k">费用</span><span class="v cost" id="v2">—</span></div>'+
              '<div class="item"><span class="k">输入</span><span class="v inp" id="v3">0</span></div>'+
              '<div class="item"><span class="k">输出</span><span class="v out" id="v4">0</span></div>'+
              '<div class="item"><span class="k">缓存命中</span><span class="v cache" id="v5">0</span></div>'+
              '<div class="item"><span class="k">命中率</span><span class="v rate" id="v6">—</span></div>'+
              '</div>'+
              '<div class="hrow" id="hr"></div>'+
              '<div class="last" id="v9">最近一轮：—</div>'+
              '<div class="foot"><span>圆环=命中率 · 点击换范围</span><a class="lnk" id="lnk" href="#" target="_blank" rel="noopener">详情页 ↗</a></div>'+
              '</div></div>';
              document.body.appendChild(root);
              var q=s=>sh.querySelector(s), wrap=q('.wrap'), ring=q('.ring'), card=q('.card'), arc=q('.arc');
              var fail=0, hideTimer=null, ro=null;
              var ORDER=['session','today','d7','d30','total'];
              var RN={session:'本次',today:'今天',d7:'7天',d30:'30天',total:'累计'};
              function setRange(k){M.sel=k;try{localStorage.setItem('tstatsRange',k)}catch(e){}tick()}
              ring.title='点击切换统计范围';
              function findSwitch(){
                var els=document.querySelectorAll('span,div,label,p');
                for(var i=0;i<els.length;i++){var el=els[i];
                  if(el.children.length>0)continue;
                  if((el.textContent||'').trim().indexOf('展开思考')<0)continue;
                  var p=el.parentElement,s=p?p.querySelector('.ant-switch'):null;
                  var t=(s&&s.getBoundingClientRect().width>0)?s:el;
                  if(t.getClientRects().length<1)continue;
                  var r=t.getBoundingClientRect();
                  if(r.width<5||r.height<5||r.top<0)continue;
                  return{r:r.right,t:r.top,h:r.height,el:t};
                }
                return null;
              }
              function place(){
                var sw=findSwitch();
                if(!sw){root.style.display='none';return}
                root.style.display='';
                var x=Math.max(4,Math.min(sw.r+GAP,innerWidth-RING-4));
                var y=Math.max(4,Math.min(sw.t+sw.h/2-RING/2,innerHeight-RING-4));
                rs.left=x+'px';rs.top=y+'px';
                var cx=Math.max(4-x,Math.min(0,innerWidth-CW-8-x));
                card.style.left=cx+'px';card.style.top=(RING+6)+'px';
                if(ro){try{ro.disconnect()}catch(e){} try{ro.observe(sw.el)}catch(e){}}
              }
              var fmt=n=>Number(n||0).toLocaleString('zh-CN');
              function d1(x){return x.toFixed(1).replace('.0','')}
              function fmt4(v){v=Math.max(0,Math.round(v||0));
                if(v<1000)return ''+v;
                if(v<9950)return d1(v/1000)+'K';
                if(v<995000)return Math.round(v/1000)+'K';
                if(v<9950000)return d1(v/1000000)+'M';
                if(v<995000000)return Math.round(v/1000000)+'M';
                if(v<9950000000)return d1(v/1000000000)+'B';
                return Math.round(v/1000000000)+'B';
              }
              var CIRC=2*Math.PI*21;
              function tick(){
                fetch('http://127.0.0.1:'+M.port+'/stats',{cache:'no-store'}).then(r=>r.json()).then(d=>{
                  fail=0;place();
                  var sel=M.sel||((ORDER.indexOf(d.ringDef)>=0)?d.ringDef:'session');
                  var R=d.ranges||{}, rg=R[sel]||{v:0,i:0,o:0,c:0,r:0};
                  q('#c').textContent=d.character;
                  q('#rgn').textContent=RN[sel];
                  q('#v1').textContent=fmt(rg.v);
                  q('#v2').textContent=rg.r;
                  q('#v3').textContent=fmt(rg.i);
                  q('#v4').textContent=fmt(rg.o);
                  q('#v5').textContent=fmt(rg.c);
              var co=(d.costs&&d.costs[sel]!=null)?d.costs[sel]:null;
              q('#v2').textContent=co!=null?('¥'+co):'—';
              q('#v2').title=co!=null?'当前范围费用（价格规则在模块配置页调整）':'未匹配到价格规则（模块配置页可设置）';
              q('#v9').title='来源 '+(d.src||'—')+' · 渠道 '+(d.channel||'—')+' · 模型 '+(d.model||'—');
                  if(rg.c>0&&rg.i>0){q('#v6').textContent=(rg.c/rg.i*100).toFixed(1)+'%';q('#v6').title=''}
                  else{q('#v6').textContent='—';q('#v6').title=rg.i>0?'供应商未回报缓存数据':'暂无数据'}
                  if(d.rounds>0)q('#v9').textContent='最近一轮：输入 '+fmt(d.lastInput)+' · 输出 '+fmt(d.lastOutput)+(d.lastCached>0?' · 缓存 '+fmt(d.lastCached):'');
                  var dot=q('#dot');
                  dot.style.background=d.busy?'#60a5fa':'#34d399';
                  dot.style.boxShadow=d.busy?'0 0 6px rgba(96,165,250,.7)':'0 0 6px rgba(52,211,153,.65)';
                  var hs='';
                  for(var k=0;k<ORDER.length;k++){var rk=ORDER[k],rv=R[rk]||{v:0};
                    hs+='<span class="hb'+(rk==sel?' on':'')+'" data-k="'+rk+'" title="切换到'+RN[rk]+'">'+RN[rk]+'<b>'+fmt4(rv.v)+'</b></span>'}
                  q('#hr').innerHTML=hs;
                  q('#t').textContent=fmt4(rg.v);
                  q('#tl').textContent=d.busy?'…':RN[sel];
                  var rate=(rg.c>0&&rg.i>0)?rg.c/rg.i:0;
                  arc.setAttribute('stroke-dasharray',(CIRC*rate).toFixed(1)+' '+CIRC.toFixed(1));
                  ring.classList.toggle('busy',!!d.busy);
                  ring.title='当前范围：'+RN[sel]+'（点击切换）';
                }).catch(function(){if(++fail>=3)root.style.display='none'});
              }
              ring.addEventListener('click',function(){
                if(!M.sel)M.sel='session';
                setRange(ORDER[(ORDER.indexOf(M.sel)+1)%ORDER.length]);
              });
              q('#hr').addEventListener('click',function(e){
                var t=e.target;
                while(t&&t.parentElement!==this)t=t.parentElement;
                if(t&&t.dataset&&t.dataset.k)setRange(t.dataset.k);
              });
              wrap.addEventListener('mouseenter',function(){
                if(hideTimer){clearTimeout(hideTimer);hideTimer=null}
                wrap.classList.add('on');
              });
              wrap.addEventListener('mouseleave',function(){
                hideTimer=setTimeout(function(){wrap.classList.remove('on')},150);
              });
              q('#lnk').href='http://127.0.0.1:'+M.port+'/';
              place();tick();
              try{ro=new ResizeObserver(place)}catch(e){}
              M.ui={root:root,ro:ro};
              M.place=place;M.tick=tick;
            };
            M.sync=function(){
              var vis=M.visible(),want=0;
              for(var p in M.reg){if(M.reg.hasOwnProperty(p)&&M.reg[p].name===vis){want=+p;break}}
              if(want!==0&&want===M.port&&M.ui)return;
              if(want===0){if(M.ui||M.port!==0){M.drop();M.port=0}return}
              M.port=want;M.build();M.built=want;M.tick();
            };
            // 返回调用方自己的状态：显示中→ok/injected+矩形（injected 表示本次刚完成构建，
            // 供 C# 侧只打一次“已就绪”日志），未显示（页面在看别的角色或非聊天页）→hidden
            M.register=function(port,name,cfg){
              M.reg[port]={name:name,cfg:cfg};
              M.sync();
              if(M.port!==+port)return 'hidden';
              var r=(M.built===+port)?'injected':'ok';
              M.built=0;
              return r+' '+M.rect();
            };
            M.unregister=function(port){delete M.reg[port];
              if(M.port===+port){M.port=0;M.drop();M.sync()}};
            setInterval(function(){M.sync();if(M.place)M.place()},1000);
            setInterval(function(){if(M.tick)M.tick()},1000);
            addEventListener('resize',function(){if(M.place)M.place()});
          }
          return M.register(__PORT__,'__NAME__',{ring:__RING__,gap:__GAP__,cw:__CARDW__,ch:__CARDH__});
        })()
        """;

    // 插件详情页：深色实时控制台 + 浅色范围统计/明细面板；
    // 范围：今天/近7天/近30天/总计/自定义——单天自动按小时显示，多天按天显示；含命中率列与最近轮次面板。
    const string DashboardHtml = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <title>Token用量看板</title>
        <style>
        *{margin:0;padding:0;box-sizing:border-box}
        body{font-family:"Segoe UI",system-ui,"Microsoft YaHei",sans-serif;background:#f2f3f7;color:#23272f;padding:26px 14px 40px}
        .wrap{max-width:880px;margin:0 auto}
        .console{background:linear-gradient(160deg,#171c2e 0,#131726 60%,#10141f 100%);border:1px solid #262d45;border-radius:16px;padding:18px 20px 16px;color:#dbe2f2;box-shadow:0 18px 40px rgba(19,23,38,.28)}
        .kicker{font:700 9px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:3px;color:#7f9bd9}
        .console h1{font-size:19px;font-weight:700;letter-spacing:1px;margin-top:6px;color:#f2f5ff}
        .c-head{display:flex;align-items:flex-start;gap:12px}
        .c-sub{font-size:11px;color:#8b94ad;margin-top:5px}
        .meter{margin-left:auto;display:flex;align-items:center;gap:7px;padding:6px 12px;border:1px solid rgba(127,155,217,.28);border-radius:999px;background:rgba(127,155,217,.08);font-size:11px;color:#aeb9d6;flex:0 0 auto}
        .led{width:7px;height:7px;border-radius:50%;background:#5eead4;box-shadow:0 0 8px #5eead4;animation:led 1.8s ease-in-out infinite}
        .meter.busy .led{background:#8ff0ff;box-shadow:0 0 8px #8ff0ff}
        @keyframes led{50%{opacity:.55}}
        .c-metrics{display:grid;grid-template-columns:repeat(5,1fr);gap:8px;margin-top:14px}
        .cm{padding:8px 10px;border:1px solid rgba(127,155,217,.14);border-radius:11px;background:rgba(10,13,24,.45)}
        .cm span{display:block;font:700 8px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:1.2px;color:#67718f}
        .cm strong{display:block;margin-top:4px;font:700 13px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;color:#dbe7ff;letter-spacing:.3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .c-last{margin-top:10px;font-size:11.5px;color:#8b94ad}
        .c-last b{color:#c6d2ee;font-variant-numeric:tabular-nums;font-weight:600}
        .panel{margin-top:14px;background:#fff;border:1px solid #e5e7ee;border-radius:14px;padding:14px 16px 16px;box-shadow:0 6px 18px rgba(23,27,40,.05)}
        .p-head{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
        .p-dot{width:8px;height:8px;border-radius:50%;background:#3b82f6;box-shadow:0 0 6px rgba(59,130,246,.45)}
        .p-title{font-size:14px;font-weight:650;color:#3a4051}
        .p-sub{font-size:11px;color:#9aa1b0}
        .tabs{margin-left:auto;display:flex;gap:6px;flex-wrap:wrap;align-items:center}
        .tab{padding:5px 14px;border:1px solid #dfe2ea;border-radius:999px;font-size:12px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
        .tab:hover{border-color:#a9c4f8;color:#2f6fd8;transform:translateY(-1px)}
        .tab.on{background:#3b82f6;border-color:#3b82f6;color:#fff;box-shadow:0 4px 12px rgba(59,130,246,.3)}
        .tab2{padding:4px 11px;border:1px solid #dfe2ea;border-radius:999px;font-size:11.5px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
        .tab2:hover{border-color:#a9c4f8;color:#2f6fd8}
        .tab2.on{background:#3b82f6;border-color:#3b82f6;color:#fff}
        .scp{padding:4px 11px;border:1px solid #dfe2ea;border-radius:999px;font-size:11.5px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
        .scp:hover{border-color:#6ee7b7;color:#059669}
        .scp.on{background:#0e9f6e;border-color:#0e9f6e;color:#fff}
        .sgrp{display:flex;gap:6px;padding-right:8px;margin-right:2px;border-right:1px solid #e5e7ee}
        .cost{font-variant-numeric:tabular-nums;color:#0e9f6e;font-weight:650}
        .cst{display:flex;gap:4px;align-items:center;border:1px solid #dfe2ea;border-radius:999px;background:#fff;padding:3px 6px 3px 10px}
        .cst.on{border-color:#3b82f6;box-shadow:0 4px 12px rgba(59,130,246,.22)}
        .cst span{color:#b6bac4;font-size:11px}
        .cst input{border:0;font-size:11px;color:#5c6270;outline:0;font-family:inherit}
        .cst button{border:0;background:none;color:#2f6fd8;cursor:pointer;font-size:12px;padding:2px 6px;font-family:inherit}
        .hero{margin:14px 2px 10px}
        .hero .hk{font:700 9px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:2px;color:#9aa1b0;margin-bottom:6px}
        .hero .num{font:700 34px/1 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;letter-spacing:-.5px;font-variant-numeric:tabular-nums}
        .cells{display:grid;grid-template-columns:repeat(6,1fr);gap:8px}
        .cell{border:1px solid #e8eaf1;background:#fbfcfe;border-radius:11px;padding:9px 11px}
        .cell .k{font-size:10.5px;color:#9aa1b0}
        .cell .v{margin-top:3px;font:650 14px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;font-variant-numeric:tabular-nums;white-space:nowrap}
        .v.inp{color:#2f6fd8}.v.out{color:#db2777}.v.cache{color:#d97706}.v.rate{color:#7c3aed}.v.avg{color:#0e9f6e}
        table{width:100%;border-collapse:collapse;margin-top:6px;font-size:12px}
        th,td{padding:7px 10px;text-align:right;border-bottom:1px solid #f1f2f6;white-space:nowrap}
        th{color:#9aa1b0;font-weight:600;background:#fafbfd;font-size:11px}
        th:first-child,td:first-child{text-align:left}
        tbody tr:hover td{background:#f8faff}
        .bdg{display:inline-block;min-width:96px;text-align:center;font:650 10.5px/1.7 ui-monospace,SFMono-Regular,Consolas,monospace;border:1px solid #e2e5ee;border-radius:7px;background:#fff;color:#5c6270;padding:0 6px}
        tr.is-today .bdg{border-color:#a9c4f8;color:#2f6fd8;background:#f0f6ff}
        td b{font-variant-numeric:tabular-nums}
        .barw{width:100%;min-width:56px;background:#f1f2f6;border-radius:99px;height:6px}
        .bar{height:6px;border-radius:99px;background:linear-gradient(90deg,#3b82f6,#7cb0ff);min-width:2px}
        .mdl{display:inline-block;max-width:150px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font:600 10.5px/1.6 ui-monospace,SFMono-Regular,Consolas,monospace;color:#5c6270;background:#f5f6fa;border:1px solid #e8eaf1;border-radius:6px;padding:1px 7px}
        .empty td{text-align:center;color:#b6bac4;padding:20px}
        .foot{margin-top:12px;font-size:11px;color:#b6bac4;line-height:1.7;word-break:break-all}
        @media(max-width:720px){.c-metrics,.cells{grid-template-columns:repeat(2,1fr)}.cm:nth-child(5),.cells .cell:nth-child(5),.cells .cell:nth-child(6){grid-column:auto}}
        </style>
        </head>
        <body>
        <div class="wrap">
        <header class="console">
          <div class="c-head">
            <div>
              <div class="kicker">TOKEN USAGE CONSOLE</div>
              <h1>Token 用量看板</h1>
              <div class="c-sub" id="meta">…</div>
            </div>
            <div class="meter" id="meter"><span class="led"></span><span id="busy">空闲</span></div>
          </div>
          <div class="c-metrics">
            <div class="cm"><span>角色</span><strong id="sc">—</strong></div>
            <div class="cm"><span>模型</span><strong id="sm">—</strong></div>
            <div class="cm"><span>渠道</span><strong id="smc">—</strong></div>
            <div class="cm"><span>会话时长</span><strong id="se">00:00:00</strong></div>
            <div class="cm"><span>会话轮次</span><strong id="sr">0</strong></div>
            <div class="cm"><span>会话累计</span><strong id="st">0</strong></div>
            <div class="cm"><span>会话输入</span><strong id="si">0</strong></div>
            <div class="cm"><span>会话输出</span><strong id="so">0</strong></div>
            <div class="cm"><span>会话缓存</span><strong id="scd">0</strong></div>
            <div class="cm"><span>会话费用</span><strong id="scost">—</strong></div>
          </div>
          <div class="c-last" id="slast">最近一轮：—</div>
        </header>
        <section class="panel">
          <div class="p-head">
            <span class="p-dot"></span><span class="p-title">范围统计</span>
            <div class="tabs">
              <span class="tab" data-r="today">今天</span>
              <span class="tab" data-r="d7">近7天</span>
              <span class="tab" data-r="d30">近30天</span>
              <span class="tab" data-r="total">总计</span>
              <span class="cst" id="cst"><input type="date" id="df"><span>→</span><input type="date" id="dt"><button id="go">自定义</button></span>
            </div>
          </div>
          <div class="hero"><div class="hk" id="btl">总 TOKEN</div><div class="num" id="bt">0</div></div>
          <div class="cells">
            <div class="cell"><div class="k">输入</div><div class="v inp" id="gin">0</div></div>
            <div class="cell"><div class="k">输出</div><div class="v out" id="gout">0</div></div>
            <div class="cell"><div class="k">缓存命中</div><div class="v cache" id="gc">0</div></div>
            <div class="cell"><div class="k">命中率</div><div class="v rate" id="gr">—</div></div>
            <div class="cell"><div class="k">轮次 / 天数</div><div class="v" id="grd">0</div></div>
            <div class="cell"><div class="k" id="gavgk">日均</div><div class="v avg" id="gavg">0</div></div>
          </div>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">维度分析</span><span class="p-sub" id="asub">本角色 · 按渠道 · 角色 · 来源 · 汇总用量与费用</span>
            <div class="tabs">
              <span class="sgrp"><span class="scp" data-s="self">本角色</span><span class="scp" data-s="all">全部角色</span></span>
              <span class="tab2" data-d="byChannel">渠道</span>
              <span class="tab2" data-d="byName">角色</span>
              <span class="tab2" data-d="bySource">来源</span>
              <span class="tab2" data-d="byModel">模型</span>
            </div>
          </div>
          <table>
            <thead><tr><th>名称</th><th>轮次</th><th>输入</th><th>输出</th><th>缓存</th><th>合计</th><th>费用</th><th style="width:16%">占比</th></tr></thead>
            <tbody id="ab"></tbody>
          </table>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">用量明细</span><span class="p-sub" id="dsub">按天</span></div>
          <table>
            <thead><tr><th>时间</th><th>轮次</th><th>输入</th><th>输出</th><th>缓存</th><th>命中率</th><th>合计</th><th style="width:22%">用量条</th></tr></thead>
            <tbody id="tb"></tbody>
          </table>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">最近对话轮次</span><span class="p-sub" id="rsub">本角色 · 最近15条 · 含模型/来源/渠道/费用</span></div>
          <table>
            <thead><tr><th>时间</th><th>模型</th><th>来源</th><th>渠道</th><th>输入</th><th>输出</th><th>缓存</th><th>合计</th><th>费用</th><th style="width:12%">占比</th></tr></thead>
            <tbody id="rb"></tbody>
          </table>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">价格规则</span><span class="p-sub">只读 · 编辑入口在模块配置页（渠道价格设置）</span></div>
          <div class="foot" style="margin-top:2px" id="pksub">峰=工作日 9:00–12:00、14:00–18:00（机器本地时间），其余谷 · 费用 = 命中×命中价 + (输入−命中)×未命中价 + 输出×输出价（元/百万tokens）</div>
          <table>
            <thead><tr><th>渠道/规则</th><th>URL匹配</th><th>模型匹配</th><th>峰谷</th><th>命中</th><th>未命中</th><th>输出</th></tr></thead>
            <tbody id="pb"></tbody>
          </table>
        </section>
        <div class="foot" id="foot">加载中…</div>
        </div>
        <script>
        var days=[], hourData=null, hourDay=null, mode='today', todayStr='';
        var adim='byChannel', ana=null;
        var scope='self', CUR='';
        try{var sp0=localStorage.getItem('tstatsScope');if(sp0==='all')scope=sp0}catch(e){}
        var $=function(id){return document.getElementById(id)};
        var fmt=function(n){return Number(n||0).toLocaleString('zh-CN')};
        var esc=function(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')};
        var fmtC=function(c){return c==null?'—':'¥'+c};
        function p2(n){return String(n).padStart(2,'0')}
        function isoD(d){return d.getFullYear()+'-'+p2(d.getMonth()+1)+'-'+p2(d.getDate())}
        function needDay(){
          if(mode==='today')return todayStr||isoD(new Date());
          if(mode==='custom'&&$('df').value&&$('df').value===$('dt').value)return $('df').value;
          return null;
        }
        function dailyList(){
          var today=isoD(new Date());
          if(mode==='custom'){var f=$('df').value||today,t=$('dt').value||today;
            return{label:'自定义 '+f+' ~ '+t,list:days.filter(function(x){return x.d>=f&&x.d<=t})}}
          if(mode==='d7'){var f7=isoD(new Date(Date.now()-6*864e5));
            return{label:'近7天（'+f7+' ~ '+today+'）',list:days.filter(function(x){return x.d>=f7})}}
          if(mode==='d30'){var f30=isoD(new Date(Date.now()-29*864e5));
            return{label:'近30天（'+f30+' ~ '+today+'）',list:days.filter(function(x){return x.d>=f30})}}
          return{label:'总计（全部历史）',list:days.slice()};
        }
        function render(){
          todayStr=isoD(new Date());
          var hd=needDay(),list,label,rows,unit,loading=false,peak=null;
          if(hd!=null){
            label=hd+' · 按小时';unit='按小时';
            list=days.filter(function(x){return x.d===hd});
            if(hourDay===hd&&hourData){
              rows=hourData.slice().sort(function(a,b){return b.h-a.h}).map(function(x){
                return{label:p2(x.h)+':00–'+p2(x.h<23?x.h+1:24)+':00',rounds:x.rounds,v:x.v,i:x.i,o:x.o,c:x.c,isToday:hd===todayStr}});
              hourData.forEach(function(x){if(!peak||x.v>peak.v)peak=x});
            }else{rows=null;loading=true}
          }else{
            var rg=dailyList();
            label=rg.label;unit='按天';
            list=rg.list;
            rows=list.slice().sort(function(a,b){return a.d<b.d?1:a.d>b.d?-1:0}).slice(0,90)
              .map(function(x){return{label:x.d,rounds:x.rounds,v:x.v,i:x.i,o:x.o,c:x.c,isToday:x.d===todayStr}});
          }
          var v=0,i=0,o=0,c=0,r=0;
          list.forEach(function(x){v+=x.v;i+=x.i;o+=x.o;c+=x.c;r+=x.rounds});
          $('btl').textContent=label.toUpperCase()+' · 总 TOKEN';
          $('bt').textContent=fmt(v);
          $('gin').textContent=fmt(i);$('gout').textContent=fmt(o);$('gc').textContent=fmt(c);
          if(c>0&&i>0){$('gr').textContent=(c/i*100).toFixed(1)+'%';$('gr').title=''}
          else{$('gr').textContent='—';$('gr').title='供应商未回报缓存数据时无法计算命中率'}
          $('grd').textContent=r+' / '+list.length;
          if(peak!=null){$('gavgk').textContent='峰值时段';$('gavg').textContent=p2(peak.h)+':00–'+p2(peak.h<23?peak.h+1:24)+':00'}
          else{$('gavgk').textContent='日均';$('gavg').textContent=list.length>0?fmt(Math.round(v/list.length)):'0'}
          $('dsub').textContent=unit+(unit==='按天'?' · 最多显示最近90天':'');
          var tb=$('tb');tb.innerHTML='';
          if(loading){tb.innerHTML='<tr class="empty"><td colspan="8">加载中…</td></tr>';return}
          if(!rows||rows.length===0){tb.innerHTML='<tr class="empty"><td colspan="8">该范围内暂无用量记录</td></tr>';return}
          var mx=1;rows.forEach(function(x){if(x.v>mx)mx=x.v});
          rows.forEach(function(x){
            var tr=document.createElement('tr');
            if(x.isToday)tr.className='is-today';
            tr.innerHTML='<td><span class="bdg">'+x.label+'</span></td><td>'+x.rounds+'</td><td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td>'+
              '<td>'+((x.c>0&&x.i>0)?(x.c/x.i*100).toFixed(1)+'%':'—')+'</td><td><b>'+fmt(x.v)+'</b></td>'+
              '<td><div class="barw"><div class="bar" style="width:'+Math.max(1.5,x.v/mx*100).toFixed(1)+'%"></div></div></td>';
            tb.appendChild(tr);
          });
        }
        function renderRecords(recs){
          var rb=$('rb');
          if(!recs||recs.length===0){rb.innerHTML='<tr class="empty"><td colspan="10">暂无记录</td></tr>';return}
          var mx=Math.max.apply(null,recs.map(function(x){return x.v}));
          rb.innerHTML=recs.map(function(x){
            var mdl=x.m&&x.m.length>0?x.m:'—';
            return '<tr><td><span class="bdg">'+x.t+'</span></td><td><span class="mdl" title="'+(mdl==='—'?'旧版本记录未写入模型信息':esc(mdl))+'">'+esc(mdl)+'</span></td>'+
              '<td><span class="mdl">'+esc(x.s&&x.s.length?x.s:'未知')+'</span></td><td><span class="mdl" title="'+esc((x.h&&x.h.length?x.h+' · ':'')+(x.n&&x.n.length?x.n:''))+'">'+esc(x.ch&&x.ch.length?x.ch:'未知')+'</span></td>'+
              '<td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td><td><b>'+fmt(x.v)+'</b></td><td class="cost">'+fmtC(x.co)+'</td>'+
              '<td><div class="barw"><div class="bar" style="width:'+Math.max(1.5,x.v/mx*100).toFixed(1)+'%"></div></div></td></tr>';
          }).join('');
        }
        function anaUrl(){
          var u='/analytics?range='+(mode==='custom'?'custom':mode)+'&name='+(scope==='all'?'all':encodeURIComponent(CUR));
          if(mode==='custom')u+='&from='+($('df').value||isoD(new Date()))+'&to='+($('dt').value||isoD(new Date()));
          return u;
        }
        function tickAnalytics(){
          fetch(anaUrl(),{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){ana=d;renderAnalytics()}).catch(function(){});
        }
        function renderAnalytics(){
          var ab=$('ab');
          if(!ana){ab.innerHTML='<tr class="empty"><td colspan="8">加载中…</td></tr>';return}
          $('asub').textContent=(scope==='all'?'全部角色（汇总日志）':'本角色')+' · 合计 '+fmt(ana.total.v)+' Token · 费用 '+fmtC(ana.total.cost);
          var rows=ana[adim]||[];
          if(rows.length===0){ab.innerHTML='<tr class="empty"><td colspan="8">该范围内暂无记录</td></tr>';return}
          var tv=Math.max(1,ana.total.v||0);
          ab.innerHTML=rows.map(function(x){
            return '<tr><td><span class="bdg">'+esc(x.k)+'</span></td><td>'+x.r+'</td><td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td><td><b>'+fmt(x.v)+'</b></td><td class="cost">'+fmtC(x.cost)+'</td>'+
              '<td><div class="barw"><div class="bar" style="width:'+Math.max(1.5,x.v/tv*100).toFixed(1)+'%"></div></div></td></tr>';
          }).join('');
        }
        function tickPricing(){
          fetch('/pricing',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            var pb=$('pb'),rs=d.rules||[];
            var pv=function(a,peak){return peak?(a[0]+'峰 / '+a[1]+'谷'):('单价 '+a[1])};
            if(rs.length===0){pb.innerHTML='<tr class="empty"><td colspan="7">暂无规则</td></tr>';return}
            pb.innerHTML=rs.map(function(r){
              return '<tr><td>'+esc(r.name)+'</td><td>'+esc(r.url&&r.url.length?r.url:'—')+'</td><td>'+esc(r.model&&r.model.length?r.model:'—')+'</td>'+
                '<td>'+(r.peak?'开':'关')+'</td><td>'+pv(r.hit,r.peak)+'</td><td>'+pv(r.miss,r.peak)+'</td><td>'+pv(r.out,r.peak)+'</td></tr>';
            }).join('');
            var chs=d.channels||[];
            if(chs.length)
              $('pksub').textContent+=' · 检测到渠道：'+chs.map(function(c){return c.name+'('+c.model+(c.owner?'@'+c.owner:'')+')'}).join('、');
          }).catch(function(){});
        }
        function syncTabs(){
          document.querySelectorAll('.tab').forEach(function(t){t.classList.toggle('on',t.dataset.r===mode)});
          $('cst').classList.toggle('on',mode==='custom');
          render();
        }
        function tickStats(){
          fetch('/stats',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            if(d.character)CUR=d.character;
            var m=String(d.model||'').replace(/LanguageModel|Model$/,'');
            $('meta').textContent=d.character+' · '+m;
            $('sc').textContent=d.character;$('sm').textContent=m;$('smc').textContent=d.channel||'—';
            var b=$('busy');b.textContent=d.busy?'生成中…':'空闲';
            $('meter').classList.toggle('busy',!!d.busy);
            $('sr').textContent=d.rounds;$('st').textContent=fmt(d.total);
            $('si').textContent=fmt(d.input);$('so').textContent=fmt(d.output);$('scd').textContent=fmt(d.cached);
            $('scost').textContent=fmtC(d.costs?d.costs.session:null);
            $('scost').title='会话费用（价格规则见页面底部）';
            var s=Math.max(0,d.elapsed|0);
            $('se').textContent=p2((s/3600)|0)+':'+p2(((s%3600)/60)|0)+':'+p2(s%60);
            if(d.rounds>0)$('slast').innerHTML='最近一轮：输入 <b>'+fmt(d.lastInput)+'</b> · 输出 <b>'+fmt(d.lastOutput)+'</b>'+(d.lastCached>0?' · 缓存 <b>'+fmt(d.lastCached)+'</b>':'')+' · 来源 <b>'+esc(d.src||'—')+'</b> · 渠道 <b>'+esc(d.channel||'—')+'</b>';
            if(d.logFile)$('foot').textContent='本角色数据：'+d.logFile+'（另有汇总 usage-log.jsonl 双写保留全机数据） · 可在插件配置页按时间段清理 · 单天范围自动按小时显示 · 维度分析/最近记录可切『全部角色』口径';
          }).catch(function(){});
        }
        function tickHist(){
          fetch('/history',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            days=d.days||[];
            var hd=needDay();
            if(hd!=null){
              fetch('/history?day='+hd,{cache:'no-store'}).then(function(r2){return r2.json()}).then(function(h){
                hourDay=hd;hourData=h.hours||[];render();
              }).catch(function(){render()});
            }else render();
          }).catch(function(){});
        }
        function tickRecords(){
          fetch('/records?n=15&name='+(scope==='all'?'all':encodeURIComponent(CUR)),{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            renderRecords(d.recs);
          }).catch(function(){});
        }
        document.querySelectorAll('.tab').forEach(function(t){t.addEventListener('click',function(){mode=t.dataset.r;syncTabs();tickHist();tickAnalytics()})});
        document.querySelectorAll('.tab2').forEach(function(t){
          if(t.dataset.d===adim)t.classList.add('on');
          t.addEventListener('click',function(){
            adim=t.dataset.d;
            document.querySelectorAll('.tab2').forEach(function(x){x.classList.toggle('on',x.dataset.d===adim)});
            renderAnalytics();
          });
        });
        function setScope(s){
          scope=s;try{localStorage.setItem('tstatsScope',s)}catch(e){}
          document.querySelectorAll('.scp').forEach(function(x){x.classList.toggle('on',x.dataset.s===s)});
          $('rsub').textContent=(s==='all'?'全部角色（汇总日志）':'本角色')+' · 最近15条 · 含模型/来源/渠道/费用';
          tickRecords();tickAnalytics();
        }
        document.querySelectorAll('.scp').forEach(function(x){x.addEventListener('click',function(){setScope(x.dataset.s)})});
        setScope(scope);
        $('go').addEventListener('click',function(){
          if(!$('df').value)$('df').value=isoD(new Date(Date.now()-29*864e5));
          if(!$('dt').value)$('dt').value=isoD(new Date());
          mode='custom';syncTabs();tickHist();tickAnalytics();
        });
        $('df').value=isoD(new Date(Date.now()-29*864e5));
        $('dt').value=isoD(new Date());
        tickStats();tickHist();tickRecords();tickAnalytics();tickPricing();syncTabs();
        setInterval(tickStats,1000);setInterval(tickHist,3000);setInterval(tickRecords,5000);
        setInterval(tickAnalytics,15000);setInterval(tickPricing,60000);
        </script>
        </body>
        </html>
        """;
}

public class TokenStatsConfig
{
    [DisplayName("HTTP起始端口")]
    [Description("统计数据本地服务端口，被占用时自动向后扫描（共20个）；详情页 http://127.0.0.1:端口/")]
    public int Port { get; set; } = 18790;

    [DisplayName("圆环尺寸")]
    [Description("圆环挂件的边长（像素，默认40）")]
    public int RingSize { get; set; } = 40;

    [DisplayName("圆环统计范围")]
    [Description("圆心数值的统计范围：session=本次会话，today=今天，d7=近7天，d30=近30天，total=累计（在挂件上点击圆环或卡片胶囊也可切换并记忆）")]
    public string RingRange { get; set; } = "session";

    [DisplayName("展开宽度")]
    [Description("悬停展开的详情卡片宽度")]
    public int Width { get; set; } = 300;

    [DisplayName("展开高度")]
    [Description("悬停展开的详情卡片高度")]
    public int Height { get; set; } = 228;

    [DisplayName("与开关间距")]
    [Description("圆环左缘与展开思考开关右缘之间的水平像素距离")]
    public int GapBesideSwitch { get; set; } = 12;
}
