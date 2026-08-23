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
using Alife.Function.FunctionCaller;
using ElectronNET.API;
using Microsoft.Extensions.Logging;

namespace OneChuxin.TokenStats;

[Module("Token用量看板",
    "在『展开思考』开关旁注入圆环 Token 用量挂件：显示用量（点击切换范围、悬停看含费用详情）；详情页支持历史明细与按渠道/角色/来源的维度分析，费用按可配置价格规则实时计算。用量记录分角色与汇总双写持久化，不修改客户端文件；激活时播放恩情满屏动画。",
    defaultCategory: "初心的小工具",
    EditorUI = typeof(TokenStatsUI))]
public class TokenStatsModule(ILogger<TokenStatsModule> logger, XmlFunctionCaller functionCaller, Interactor<TokenStatsModule> interactor) :
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
    bool entrancePending;              // 本次激活是否待播入场动画（OnStart 置位；页面回报播完/跳过或超时后清除）
    DateTime entrancePendingAt = DateTime.MinValue;
    string lastOverlayCat = "";        // 上次日志输出的状态类别（仅状态变迁才打日志，防多开刷屏）

    // 错误统计（4.7.0）：AI 输出中「出错：/出错:」标记的累计（正则见 ErrorTagRegex）
    int sessionErr;
    string lastErrText = "";           // 最近一次出错摘录（≤60 字）
    DateTime lastErrAt = DateTime.MinValue;

    // 余额监测（4.7.0）：低频轮询（BalanceIntervalMinutes，最小 5）+ /balance?refresh=1 手动；探测中不重入
    DateTime nextBalanceAt = DateTime.MinValue;
    bool balanceBusy;

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
        public int E; // 该日出错标记数（4.7.0；旧日志无 e 字段 = 0）
        public Dictionary<string, Agg[]>? ByModel; // 模型\u001F渠道 → [谷,峰]，范围费用计算用（懒分配）
    }

    sealed class Agg { public long In, Out, Cached; }

    protected override Task OnAwake()
    {
        ChatBot.TokenUsed += OnTokenUsed;
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatReceived += OnChatReceived;
        // XML 标签（隐式注入）：系统提示词只占一行触发标签，AI 调用 <TokenStat/> 时才展开完整使用说明
        try
        {
            functionCaller.RegisterHandler(new XmlHandler(this)
            {
                Description = "Token用量看板：查询各范围 Token 用量与费用、各渠道账户余额、出错次数。当被问到“用了多少 token / 花了多少钱 / 余额还剩多少 / 出错了吗”或需要主动汇报用量时调用。",
                Explanation = "调用 <TokenStatsModule/> 即可，无需参数（可选 range=session/today/d7/d30/total 只看单项）。返回紧凑文本：各范围用量/费用/出错数、每个余额监测源的余额与更新时间。余额源在模块配置页「余额监测」维护：auto=按 URL 自动探测（官方端点或 One-API 系中转站），custom=自定义接口，preset=初始额度减渠道累计费用的预估值；探测失败会附原因。",
            }, DocumentMode.Implicit, DestroyCancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token用量看板：XmlHandler 注册失败（角色未启用 FunctionCaller 时属预期）");
        }
        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        sessionStart = DateTime.Now;
        entrancePending = true;          // 入场动画仅在激活角色时播放（页面刷新/切换查看不播）
        entrancePendingAt = DateTime.Now;
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
        // 余额低频轮询：到期且有 Enabled 源才探测（后台执行不阻塞 Update；探测中不重入）
        if (!balanceBusy && DateTime.Now >= nextBalanceAt)
        {
            nextBalanceAt = DateTime.Now.AddMinutes(Math.Max(5, Configuration.BalanceIntervalMinutes));
            try
            {
                if (BalanceStore.Sources().Any(s => s.Enabled))
                    _ = Task.Run(async () => await ProbeBalancesAsync());
            }
            catch { }
        }
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
        string src, aiText;
        lock (sync) { aiText = roundAiText.ToString(); src = ClassifySource(pendingUserMsg, aiText, inheritSource); }
        // 错误统计（4.7.0）：本轮 AI 输出含「出错：/出错:」的次数（与 <Speak> 同位取全量，流式跨片安全）
        int errs = ErrorTagRegex.Matches(aiText).Count;
        (string channel, string model, string host) = ResolveChannelAndModel();
        string line = $"{{\"t\":\"{now:yyyy-MM-dd'T'HH:mm:ss.fff}\",\"v\":{usage.Total},\"i\":{usage.Input},\"o\":{usage.Output},\"c\":{usage.Cached},\"m\":\"{JsonEscape(model)}\",\"s\":\"{JsonEscape(src)}\",\"ch\":\"{JsonEscape(channel)}\",\"h\":\"{JsonEscape(host)}\",\"n\":\"{JsonEscape(Character?.Name ?? "")}\"" + (errs > 0 ? $",\"e\":{errs}" : "") + "}";
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
            if (errs > 0)
            {
                sessionErr += errs;
                ds.E += errs; hr.E += errs;
                lastErrText = ErrSnippet(aiText);
                lastErrAt = now;
            }
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

    // 错误标记（4.7.0）：客户端/上游异常常以「出错：」或「出错:」出现在 AI 输出里，按出现次数计
    static readonly Regex ErrorTagRegex = new("出错[：:]", RegexOptions.Compiled);

    // 出错摘录：首个「出错」前后共 ≤60 字（去换行），供 /stats 与 <TokenStat/> 展示
    static string ErrSnippet(string aiText)
    {
        int idx = aiText.IndexOf("出错", StringComparison.Ordinal);
        if (idx < 0) return "";
        int start = Math.Max(0, idx - 20);
        string s = aiText[start..Math.Min(aiText.Length, idx + 40)];
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 60 ? s[..60] : s;
    }

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

    static string RecLine(UsageRec r) => $"{{\"t\":\"{r.T:yyyy-MM-dd'T'HH:mm:ss.fff}\",\"v\":{r.V},\"i\":{r.I},\"o\":{r.O},\"c\":{r.C},\"m\":\"{JsonEscape(r.M ?? "")}\",\"s\":\"{JsonEscape(r.S ?? "")}\",\"ch\":\"{JsonEscape(r.Ch ?? "")}\",\"h\":\"{JsonEscape(r.H ?? "")}\",\"n\":\"{JsonEscape(r.N ?? "")}\"" + (r.E > 0 ? $",\"e\":{r.E}" : "") + "}";

    internal sealed class UsageRec
    {
        public DateTime T;
        public long V, I, O, C;
        public string? M;   // 该轮使用的模型名（旧记录可能为空）
        public string? S;   // 来源：qchat/ChatWindow/speak/报点/…（旧记录为空 → 未知）
        public string? Ch;  // 渠道：灵枢渠道组名 / endpoint 域名（旧记录为空 → 未知）
        public string? H;   // 渠道 endpoint 域名（价格规则可按 URL 匹配；旧记录为空）
        public string? N;   // 角色名（旧记录为空 → 未知）
        public int E;       // 该轮 AI 输出含「出错：」的次数（4.7.0；旧记录为 0）
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
                    E = el.TryGetProperty("e", out JsonElement ee) && ee.ValueKind == JsonValueKind.Number ? ee.GetInt32() : 0,
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
                    ds.Rounds++; ds.V += rec.V; ds.In += rec.I; ds.Out += rec.O; ds.Cached += rec.C; ds.E += rec.E;
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

    // 按天键区间聚合（含出错数 E），<TokenStat/> 与 /stats errors 复用
    (long V, long I, long O, long C, int R, int E) RangeAgg(string fromDate, string toDate)
    {
        long v = 0, i = 0, o = 0, c = 0;
        int r = 0, e = 0;
        lock (sync)
        {
            foreach (KeyValuePair<string, DayStat> kv in days)
            {
                if (string.CompareOrdinal(kv.Key, fromDate) < 0) continue;
                if (string.CompareOrdinal(kv.Key, toDate) > 0) break;
                v += kv.Value.V; i += kv.Value.In; o += kv.Value.Out; c += kv.Value.Cached;
                r += kv.Value.Rounds; e += kv.Value.E;
            }
        }
        return (v, i, o, c, r, e);
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
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);   // 4.8.3：格式合法但日历非法（如 2026-08-99）回退到全部天视图
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
                else if (path == "/fx")
                {
                    // 调试：向挂件页面派发 tstats-fx 事件重播入场动画，并回报页面内真实状态；
                    // /fx?probe=1 只读探测（不派发）：动画进度 e(ms)、fxring 是否在挂、页面可见性；
                    // /fx?gr=1 派发 tstats-gr 重播恩情层（独立时间轴，不受入场动画开关限制），
                    // 回报 gr 状态/本次主标语/已看标记（洗牌队列不重样的外部校验依据）
                    bool probe = QueryParam(query, "probe") == "1";
                    bool gr = QueryParam(query, "gr") == "1";
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildFxJson(await ReplayOverlayFxAsync(probe, gr))), cancellationToken);
                }
                else if (path == "/balance")
                {
                    // 余额监测（4.7.0）：返回全部源与最近探测结果；?refresh=1 先强制即时探测再返回
                    if (QueryParam(query, "refresh") == "1")
                        await ProbeBalancesAsync();
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildBalanceJson()), cancellationToken);
                }
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
        json.Append("},\"errors\":{");
        {
            int eS, eToday, eTot;
            string lastErr;
            lock (sync) { eS = sessionErr; lastErr = lastErrText; }
            eToday = RangeAgg(today, today).E;
            eTot = RangeAgg("0000-01-01", "9999-12-31").E;
            json.Append($"\"session\":{eS},\"today\":{eToday},\"total\":{eTot},\"last\":\"{JsonEscape(lastErr)}\"");
        }
        json.Append("},\"balance\":").Append(BalanceSummaryJson());
        json.Append("}");
        return json.ToString();
    }

    // 余额汇总（/stats 用）：监测源计数 + 与当前渠道匹配的源的余额（详情由看板另取 /balance）
    string BalanceSummaryJson()
    {
        List<BalanceSource> srcs = BalanceStore.Sources();
        int ok = srcs.Count(s => s.Enabled && BalanceStore.StateOf(s.Name)?.Ok == true);
        (string channel, _, string host) = ResolveChannelAndModel();
        string cur = "";
        foreach (BalanceSource s in srcs.Where(s => s.Enabled))
        {
            bool match =
                (host.Length > 0 && (s.Url.Contains(host, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(host, StringComparison.OrdinalIgnoreCase))) ||
                (channel.Length > 0 && (s.Url.Contains(channel, StringComparison.OrdinalIgnoreCase) || s.Name.Contains(channel, StringComparison.OrdinalIgnoreCase)));
            if (!match) continue;
            // 初始额度源优先展示（4.8.1：当前额度 = 初始额度 − 已计费用）
            if (s.Initial != null)
            {
                BalanceState ist = ResolveBalanceState(s);
                cur = $"{s.Name} {ist.Balance.ToString("0.##", CultureInfo.InvariantCulture)} {ist.Currency}（初始额度 − 已用）";
                break;
            }
            BalanceState? st = BalanceStore.StateOf(s.Name);
            if (st?.Ok == true)
            {
                cur = $"{s.Name} {st.Balance.ToString("0.##", CultureInfo.InvariantCulture)} {st.Currency}";
                break;
            }
        }
        return $"{{\"sources\":{srcs.Count},\"ok\":{ok},\"current\":\"{JsonEscape(cur)}\"}}";
    }

    // 余额 JSON（/balance）：源清单 + 各源当前余额 + 轮询间隔；
    // 初始额度源即时按 初始−已用 计算（无需等待轮询），其余取最近探测结果
    string BuildBalanceJson()
    {
        StringBuilder json = new(512);
        json.Append("{\"interval\":").Append(Math.Max(5, Configuration.BalanceIntervalMinutes));
        List<BalanceSource> srcs = BalanceStore.Sources();
        json.Append(",\"sources\":[");
        bool first = true;
        foreach (BalanceSource s in srcs)
        {
            if (!first) json.Append(',');
            first = false;
            BalanceState st = ResolveBalanceState(s);
            bool init = s.Initial != null;
            json.Append("{\"name\":\"").Append(JsonEscape(s.Name))
                .Append("\",\"type\":\"").Append(JsonEscape(string.IsNullOrWhiteSpace(s.Type) ? "auto" : s.Type))
                .Append("\",\"enabled\":").Append(s.Enabled ? "true" : "false")
                .Append(",\"initial\":").Append(init ? "true" : "false")
                .Append(",\"ok\":").Append(st.Ok ? "true" : "false")
                .Append(",\"balance\":\"").Append(st.Ok ? st.Balance.ToString("0.####", CultureInfo.InvariantCulture) : "")
                .Append("\",\"currency\":\"").Append(JsonEscape(st.Currency))
                .Append("\",\"at\":\"").Append(st.At == DateTime.MinValue ? "" : st.At.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("\",\"msg\":\"").Append(JsonEscape(st.Msg)).Append("\"}");
        }
        json.Append("]}");
        return json.ToString();
    }

    // 探测全部 Enabled 源（自动轮询 / 手动刷新 / 配置页“立即探测”共用）。
    // 填了初始额度（或 preset 类型）走本地扣减估算（当前额度=初始额度−渠道累计计费）；其余探测接口。
    public async Task ProbeBalancesAsync()
    {
        if (balanceBusy) return;
        balanceBusy = true;
        try
        {
            foreach (BalanceSource s in BalanceStore.Sources().Where(s => s.Enabled).ToList())
            {
                BalanceState st;
                if (s.Initial != null || (s.Type ?? "").Trim().Equals("preset", StringComparison.OrdinalIgnoreCase))
                    st = ResolveBalanceState(s);   // 初始额度扣减 / preset 估算（preset 未填初始额度时给出引导）
                else
                    st = await BalanceProbe.ProbeAsync(s);
                BalanceStore.SetState(s.Name, st);
            }
        }
        catch { }
        finally { balanceBusy = false; }
    }

    // 余额当前状态（看板/配置页/AI 查询共用，无需等待轮询）：
    //  填了初始额度 → 当前额度 = 初始额度 − 该渠道累计计费（按价格规则估算，带明细）；
    //  preset 未填初始额度 → Ok=false 引导；其余取最近探测结果。
    public BalanceState ResolveBalanceState(BalanceSource s)
    {
        if (s.Initial != null)
        {
            decimal cost = ChannelCost(s.Url, s.Name);
            decimal cur = s.Initial.Value - cost;
            return new BalanceState
            {
                Ok = true,
                Balance = cur,
                Currency = string.IsNullOrWhiteSpace(s.Currency) ? "CNY" : s.Currency.Trim(),
                At = DateTime.Now,
                Msg = $"初始额度 {s.Initial.Value.ToString("0.####", CultureInfo.InvariantCulture)} − 已计费用 {cost.ToString("0.####", CultureInfo.InvariantCulture)} = 当前 {cur.ToString("0.####", CultureInfo.InvariantCulture)}（按价格规则估算）",
            };
        }
        if ((s.Type ?? "").Trim().Equals("preset", StringComparison.OrdinalIgnoreCase))
            return new BalanceState { Ok = false, Msg = "preset 源需先在配置页填「初始额度」（当前额度=初始额度−已计费用）", At = DateTime.Now };
        return BalanceStore.StateOf(s.Name) ?? new BalanceState { Ok = false, Msg = "尚未探测", At = DateTime.Now };
    }

    // 某渠道（URL/渠道名包含匹配）在本角色全部历史里的计费总额
    decimal ChannelCost(string url, string name)
    {
        Dictionary<string, Agg[]> merged = new();
        lock (sync)
        {
            foreach (KeyValuePair<string, DayStat> kv in days)
            {
                if (kv.Value.ByModel == null) continue;
                foreach (KeyValuePair<string, Agg[]> m in kv.Value.ByModel)
                {
                    string[] parts = m.Key.Split('\u001F');
                    string channel = parts.Length > 1 ? parts[1] : "";
                    string host = parts.Length > 2 ? parts[2] : "";
                    bool hit =
                        (!string.IsNullOrWhiteSpace(url) && (host.Contains(url, StringComparison.OrdinalIgnoreCase) || channel.Contains(url, StringComparison.OrdinalIgnoreCase))) ||
                        (!string.IsNullOrWhiteSpace(name) && channel.Contains(name, StringComparison.OrdinalIgnoreCase));
                    if (!hit) continue;
                    if (!merged.TryGetValue(m.Key, out Agg[]? slots)) merged[m.Key] = slots = new Agg[2];
                    for (int i = 0; i < 2; i++)
                    {
                        Agg from = m.Value[i];
                        if (from == null) continue;
                        Agg to = slots[i] ??= new Agg();
                        to.In += from.In; to.Out += from.Out; to.Cached += from.Cached;
                    }
                }
            }
        }
        return merged.Count == 0 ? 0m : (AggsCost(merged) ?? 0m);
    }

    // ===== XML 标签（4.7.0 起，4.7.2 修复结果回注）：AI 查询用量/费用/余额/出错统计 =====
    // 注意：XmlFunctionCaller 的 Invoker 会丢弃方法返回值，结果必须经 interactor.Poke 推回对话，
    // 否则 AI 调用后拿不到数据（SmartWebSearch 等同款模式）。return 值仅作冗余兜底。
    [XmlFunction(FunctionMode.OneShot)]
    [Description("查询 Token 用量、费用、账户余额与出错统计；无需参数，可选 range=session/today/d7/d30/total 只看单项")]
    public string TokenStat([Description("可选范围：session/today/d7/d30/total，留空返回全部概览")] string? range = null)
    {
        string text;
        try { text = BuildStatsSummary(range); }
        catch (Exception ex) { text = "Token用量查询失败：" + ex.Message; }
        try { interactor.Poke(text); } catch { }
        return text;
    }

    string BuildStatsSummary(string? range)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        StringBuilder sb = new(640);
        sb.Append("【Token用量看板】");
        (string channel, string model, _) = ResolveChannelAndModel();
        sb.Append($"渠道 {channel} · 模型 {model}\n");
        bool Want(string key) => string.IsNullOrWhiteSpace(range) || string.Equals(range.Trim(), key, StringComparison.OrdinalIgnoreCase);

        if (Want("session"))
        {
            TokenUsage t;
            int r, eS;
            decimal? cost = SessionCost();
            lock (sync) { t = total; r = rounds; eS = sessionErr; }
            sb.Append($"本次会话：{t.Total:N0} tokens · 输入 {t.Input:N0} · 输出 {t.Output:N0} · 缓存 {t.Cached:N0} · {r} 轮");
            if (cost != null) sb.Append($" · 费用 ¥{cost.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
            if (eS > 0) sb.Append($" · 出错 {eS}");
            sb.Append('\n');
        }
        (string Key, string Label, string From, string To, decimal? Cost)[] rows =
        {
            ("today", "今天", today, today, RangeCost(today, today)),
            ("d7", "近7天", DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today, RangeCost(DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today)),
            ("d30", "近30天", DateTime.Now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today, RangeCost(DateTime.Now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), today)),
            ("total", "累计", "0000-01-01", "9999-12-31", RangeCost("0000-01-01", "9999-12-31")),
        };
        foreach ((string key, string label, string from, string to, decimal? cost) in rows)
        {
            if (!Want(key)) continue;
            (long v, long i, long o, long c, int r, int e) = RangeAgg(from, to);
            sb.Append($"{label}：{v:N0} tokens · 输入 {i:N0} · 输出 {o:N0} · 缓存 {c:N0} · {r} 轮");
            if (cost != null) sb.Append($" · 费用 ¥{cost.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
            if (e > 0) sb.Append($" · 出错 {e}");
            sb.Append('\n');
        }
        List<BalanceSource> srcs = BalanceStore.Sources();
        if (srcs.Count > 0)
        {
            sb.Append("账户余额：\n");
            foreach (BalanceSource s in srcs.Where(s => s.Enabled))
            {
                BalanceState st = ResolveBalanceState(s);
                sb.Append("- ").Append(s.Name);
                if (s.Initial != null)
                    sb.Append($"：{st.Balance.ToString("0.####", CultureInfo.InvariantCulture)} {st.Currency}（初始额度 {s.Initial.Value.ToString("0.####", CultureInfo.InvariantCulture)} − 已计费用 {ChannelCost(s.Url, s.Name).ToString("0.####", CultureInfo.InvariantCulture)}）");
                else if (st.Ok)
                    sb.Append($"：{st.Balance.ToString("0.####", CultureInfo.InvariantCulture)} {st.Currency}（{st.At:MM-dd HH:mm} 更新）");
                else
                    sb.Append($"：探测失败（{st.Msg}）");
                sb.Append('\n');
            }
        }
        string lastErr;
        lock (sync) lastErr = lastErrText;
        if (lastErr.Length > 0)
            sb.Append($"最近出错：{lastErr}\n");
        return sb.ToString().TrimEnd();
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
        if (range == "total")
        {
            from = "0001-01-01"; to = "9999-12-31";   // 总计：全量区间（4.8.3 修复：此前落入 else 被当成 today）
        }
        else
        {
            if (range == "d7") from = now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else if (range == "d30") from = now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            else if (range != "custom") { from = today; range = "today"; }
            if (to.Length == 0 || !IsIsoDate(to)) to = today;
            if (from.Length == 0 || !IsIsoDate(from)) from = today;
        }
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
            // 激活待播窗口：25s 内未播成（页面在后台/未查看该角色）则放弃，避免很久之后突然播
            if (entrancePending && (DateTime.Now - entrancePendingAt).TotalSeconds > 25)
                entrancePending = false;
            string js = OverlayJs
                .Replace("__PORT__", actualPort.ToString())
                .Replace("__NAME__", JsonEscape(Character?.Name ?? ""))
                .Replace("__RING__", Configuration.RingSize.ToString())
                .Replace("__GAP__", Configuration.GapBesideSwitch.ToString())
                .Replace("__CARDW__", Configuration.Width.ToString())
                .Replace("__CARDH__", Configuration.Height.ToString())
                .Replace("__FX__", entrancePending && Configuration.EntranceAnimation ? "1" : "0")
                .Replace("__GR__", Configuration.GratitudeMode ? "1" : "0"); // 恩情模式开关信号；首次强制观看由页面端 localStorage 判定
            Task<string> call = IpcAsync(() => main.WebContents.ExecuteJavaScriptAsync<string>(js));
            if (await Task.WhenAny(call, Task.Delay(2500)) != call)
            {
                SetOverlayState("timeout");
                return;
            }
            string result = call.Status == TaskStatus.RanToCompletion
                ? (call.Result ?? "").Trim().Trim('"')
                : "faulted";
            // 页面回报入场动画已播完/跳过/出错 → 本次激活的待播标记清除
            if (result.Contains("fx:done", StringComparison.Ordinal)
                || result.Contains("fx:skip", StringComparison.Ordinal)
                || result.Contains("fx:error", StringComparison.Ordinal))
                entrancePending = false;
            SetOverlayState(result);
        }
        catch (Exception ex)
        {
            SetOverlayState("error:" + ex.Message);
        }
    }

    // 调试端点 /fx 的实现：向主窗口页面派发 tstats-fx 重播事件，返回页面内真实 fx 状态；
    // gr=true 时改派 tstats-gr 重播恩情层（不受入场动画开关限制，恩情层为独立时间轴）
    async Task<string> ReplayOverlayFxAsync(bool probe = false, bool gr = false)
    {
        try
        {
            if (gr)
            {
                if (mainWindow == null || !Electron.WindowManager.BrowserWindows.Contains(mainWindow))
                    mainWindow = Electron.WindowManager.BrowserWindows.OrderBy(w => w.Id).FirstOrDefault();
                if (mainWindow == null) return "nowindow";
                BrowserWindow mainW = mainWindow;
                string grJs = "(function(){try{document.dispatchEvent(new CustomEvent('tstats-gr'));var M=window.__tstatsMgr;" +
                    "var r=document.getElementById('tstats-root');var sh=r&&r.shadowRoot?r.shadowRoot:null;" +
                    "return 'sent hidden='+document.hidden+' gr='+((M&&M.grState)||'')+' grLabel='+((M&&M.grLabel)||'')+" +
                    "' seen='+(localStorage.getItem('tstatsGrSeen')?'1':'0')+' txt='+(sh?sh.querySelectorAll('.grtxt').length:0)+" +
                    "' fw='+((M&&M.grFw)||0)+' pt='+((M&&M.grTxt)||0)+' gs='+((M&&M.grE)||0)}catch(e){return 'err:'+e.message}})()";
                Task<string> grCall = IpcAsync(() => mainW.WebContents.ExecuteJavaScriptAsync<string>(grJs));
                if (await Task.WhenAny(grCall, Task.Delay(2500)) != grCall) return "timeout";
                return grCall.Status == TaskStatus.RanToCompletion ? (grCall.Result ?? "").Trim().Trim('"') : "faulted";
            }
            if (!Configuration.EntranceAnimation) return "off"; // 配置关闭时任何途径都不播
            if (mainWindow == null || !Electron.WindowManager.BrowserWindows.Contains(mainWindow))
                mainWindow = Electron.WindowManager.BrowserWindows.OrderBy(w => w.Id).FirstOrDefault();
            if (mainWindow == null) return "nowindow";
            BrowserWindow main = mainWindow;
            string js = probe
                ? "(function(){try{var M=window.__tstatsMgr;var r=document.getElementById('tstats-root');" +
                  "var sh=r&&r.shadowRoot?r.shadowRoot:null;var fx=sh?sh.querySelector('.fxring'):null;var rg=sh?sh.querySelector('.ring'):null;" +
                  "var c=function(el){if(!el)return '-';var b=el.getBoundingClientRect();return Math.round(b.left+b.width/2)+','+Math.round(b.top+b.height/2)};" +
                  "return 'probe hidden='+document.hidden+' fx='+((M&&M.fxState)||'')+' e='+((M&&M.fxE)||0)+' fxring='+(fx?1:0)+' rc='+c(rg)+' fc='+c(fx)+' fs='+(fx?Math.round(fx.getBoundingClientRect().width):0)}catch(e){return 'err:'+e.message}})()"
                : "(function(){try{document.dispatchEvent(new CustomEvent('tstats-fx'));var M=window.__tstatsMgr;" +
                  "return 'sent hidden='+document.hidden+' fx='+((M&&M.fxState)||'')+' label='+((M&&M.fxLabel)||'')}catch(e){return 'err:'+e.message}})()";
            Task<string> call = IpcAsync(() => main.WebContents.ExecuteJavaScriptAsync<string>(js));
            if (await Task.WhenAny(call, Task.Delay(2500)) != call) return "timeout";
            return call.Status == TaskStatus.RanToCompletion ? (call.Result ?? "").Trim().Trim('"') : "faulted";
        }
        catch (Exception ex) { return "error:" + ex.Message; }
    }

    static string BuildFxJson(string result) => $"{{\"replay\":\"{JsonEscape(result)}\"}}";

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
    // 圆心最多4字符（1.2K/999K/9.9M）；点击圆环循环切换 本次/今天/7天/30天/累计（4.5.2 改回，
    // 此前的“点击弹出范围选择条”已移除），卡片网格内容随范围联动（时长/模型/最近一轮固定为会话数据）。
    // 入场动画（探查之眼·圆环风）：仅在激活角色时播放（C# 侧 entrancePending 随注册传 fx=1，播完/跳过或
    // 25s 超时后清除；页面刷新/切换查看不播）。首帧数据就绪后，屏幕中央（角色加载圆环原位）绽开
    // 圆环风探查之眼（非仿真，全部由圆构成）→ 目光巡视→沿弧线收拢飞至挂件圆环圆心（逐帧重瞄精准停靠）
    // →瞳孔张合落位、化作圆环（淡切+涟漪+弹跳+卡片预览）；文本在环下单行显示；rAF 单循环驱动（约3.3s），
    // 播完拆除全部装饰节点。
    // 恩情层（4.6.0 起，4.6.2 文本即礼花）：独立时间轴的满屏动画——金色闪光+双道闪电+
    // 礼花齐鸣（亮珠升空炸裂，**每发礼花携带 10-14 条恩情文本粒子从炸点向外飞散**，
    // 开场在闪电落点先炸一发 24 条超大半径，全场约 150 条；落点参差重叠、允许重复）
    // + 主标语（洗牌轮换）。恩情模式开（gr=1）每次激活都播；首次使用（页面 localStorage
    // 无 tstatsGrSeen）无视入场动画开关强制播一次，播完记已看；减少动态效果跳过并记已看；
    // 页面隐藏 defer 10s，超时不记已看下次再试。
    const string OverlayJs = """
        (function(){
          if(!document.body)return 'nopage';
          var M=window.__tstatsMgr;
          var MV=22; // 管理器脚本版本：插件升级后页面内驻留的旧管理器需重建（改此结构时递增）
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
              var RING=reg.cfg.ring,GAP=reg.cfg.gap,CW=reg.cfg.cw,CH=reg.cfg.ch,FX=reg.cfg.fx,GR=reg.cfg.gr;
              M.grState='';M.grLabel='';M.grFw=0;M.grTxt=0; // 每次驻留重建时清零恩情层状态（心跳只反映本驻留的播放）
              M.drop();
              var root=document.createElement('div');
              root.id='tstats-root';
              var rs=root.style;rs.position='fixed';rs.zIndex='99999';rs.pointerEvents='none';rs.left='0';rs.top='0';
              var sh=root.attachShadow({mode:'open'});
              sh.innerHTML='<style>'+
              '*{margin:0;padding:0;box-sizing:border-box}'+
              '.wrap{font-family:"Segoe UI",system-ui,"Microsoft YaHei",sans-serif}'+
              '.ring{width:'+RING+'px;height:'+RING+'px;position:relative;cursor:pointer;pointer-events:auto;filter:drop-shadow(0 2px 5px rgba(0,0,0,.18));transition:transform .2s,opacity .15s}'+
              '.ring:hover{transform:scale(1.08)}'+
              '.ring svg{display:block;width:'+RING+'px;height:'+RING+'px}'+
              '.value{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;color:#232a3a;font-weight:700;font-size:11px;line-height:1.1;font-variant-numeric:tabular-nums}'+
              '.value .lbl{font-size:8px;color:#9aa1b0;font-weight:400}'+
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
              '.title{font-size:12.5px;color:#3a4051;font-weight:650;letter-spacing:.3px}'+
              '.rgn{font-size:10px;color:#2f6fd8;background:#eef5ff;border:1px solid #d9e8ff;border-radius:999px;padding:1px 8px;font-weight:600}'+
              '.char{font-size:10.5px;color:#9aa1b0;margin-left:auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:64px}'+
              '.grid{display:grid;grid-template-columns:1fr 1fr;gap:4px 8px}'+
              '.item{display:flex;justify-content:space-between;align-items:baseline;background:#fbfcfe;border:1px solid #eef0f5;border-radius:8px;padding:3px 8px;font-size:11.5px}'+
              '.k{color:#9aa1b0;font-size:10px;letter-spacing:.4px}.v{font-variant-numeric:tabular-nums;font-weight:650;color:#232a3a}'+
              '.v.inp{color:#2f6fd8}.v.out{color:#db2777}.v.cache{color:#d97706}.v.rate{color:#7c3aed}.v.cost{color:#0e9f6e}'+
              '.last{color:#9aa1b0;font-size:10.5px;margin-top:7px}'+
              '.foot{margin-top:auto;display:flex;justify-content:space-between;align-items:center;color:#b6bac4;font-size:10px}'+
              '.lnk{color:#2f6fd8;text-decoration:none;font-size:10px;border:1px solid #d5e4ff;background:#f2f7ff;border-radius:999px;padding:2px 9px}'+
              '.lnk:hover{background:#e3eeff}'+
              // 入场动画 FX 层（屏幕中央绽开→收拢飞至挂件圆环；纯装饰、不拦截交互、播完即拆除）
              '.wrap.intro .ring{opacity:0}'+
              '.fxring{position:fixed;left:0;top:0;width:'+RING+'px;height:'+RING+'px;transform-origin:50% 50%;will-change:transform,opacity;filter:drop-shadow(0 6px 22px rgba(59,130,246,.35));pointer-events:none;z-index:2}'+
              '.fxring svg{display:block;width:'+RING+'px;height:'+RING+'px;overflow:visible}'+
              '.fxring .fxhalo{animation:fxspin 2.4s linear infinite;transform-box:fill-box;transform-origin:center}'+
              '.fxnum{position:absolute;top:100%;left:50%;transform:translateX(-50%);margin-top:9px;display:flex;align-items:baseline;gap:6px;white-space:nowrap;color:#232a3a;font-weight:700;font-size:11px;font-variant-numeric:tabular-nums}'+
              '.fxnum .fl{font-size:9px;color:#9aa1b0;font-weight:400}'+
              // 空态文案“词元账册 · 虚位以待”：单行流光渐变扫过（词元=Token 雅译，账册呼应看板），区别于稳态圆环的素色“暂无数据”
              '.fxnum .fl.em{background:linear-gradient(90deg,#9aa1b0,#3b82f6,#22d3ee,#9aa1b0);background-size:200% 100%;-webkit-background-clip:text;background-clip:text;color:transparent;animation:fxshimmer 1.6s linear infinite;font-size:9px;letter-spacing:1px}'+
              '@keyframes fxshimmer{to{background-position:-200% 0}}'+
              '.fxorb{position:absolute;inset:-4px;animation:fxorbspin 3.2s linear infinite}'+
              '.fxorb i{position:absolute;width:3px;height:3px;margin:-1.5px 0 0 -1.5px;border-radius:50%;background:#3b82f6;box-shadow:0 0 6px rgba(59,130,246,.85)}'+
              '@keyframes fxspin{to{transform:rotate(360deg)}}'+
              '@keyframes fxorbspin{to{transform:rotate(-360deg)}}'+
              '.fxdust{position:fixed;width:5px;height:5px;margin:-2.5px 0 0 -2.5px;border-radius:50%;background:radial-gradient(circle,#7dd3fc,#3b82f6 70%);box-shadow:0 0 8px rgba(125,211,252,.9);pointer-events:none;z-index:1;animation:fxdustfade .56s ease-out both}'+
              '@keyframes fxdustfade{from{opacity:.95;transform:scale(1)}to{opacity:0;transform:scale(.3)}}'+
              '.fxripple{position:fixed;border:2px solid rgba(59,130,246,.55);box-shadow:0 0 14px rgba(59,130,246,.35) inset;border-radius:50%;pointer-events:none;z-index:1}'+
              // 恩情层（4.6.0·雷霆满屏）：全屏金色闪光 + 蓝白闪电劈落 + 满屏金色恩情文案（主标语+短语）；
              // 独立时间轴（与探查之眼互不依赖）、纯装饰不拦截交互、播完整体拆除
              '.grlay{position:fixed;left:0;top:0;z-index:3;pointer-events:none}'+
              '.grflash{position:fixed;background:radial-gradient(circle at 50% 46%,rgba(253,230,138,.28),rgba(255,255,255,.10) 38%,transparent 68%);animation:grflash .42s ease-out both}'+
              '@keyframes grflash{from{opacity:1}to{opacity:0}}'+
              '.grbolt{position:fixed;left:0;top:0;width:100vw;height:100vh;animation:grflick .46s steps(1,end) both}'+
              '.grbolt svg{display:block;width:100%;height:100%;overflow:visible}'+
              '.grbolt .b1{fill:none;stroke:url(#tgrg);stroke-width:3.5;stroke-linecap:round;stroke-linejoin:round;filter:drop-shadow(0 0 9px rgba(147,197,253,.9))}'+
              '.grbolt .b1c{fill:none;stroke:#fff;stroke-width:1.4;stroke-linecap:round;stroke-linejoin:round;opacity:.9}'+
              '.grbolt .b2{fill:none;stroke:#93c5fd;stroke-width:1.6;stroke-linecap:round;opacity:.65}'+
              '@keyframes grflick{0%{opacity:1}25%{opacity:.35}50%{opacity:1}75%{opacity:.5}100%{opacity:0}}'+
              '.grhead{position:fixed;left:50%;top:38%;transform:translate(-50%,-50%);font-size:clamp(30px,4.6vw,54px);font-weight:800;letter-spacing:.3em;text-indent:.3em;white-space:nowrap;background:linear-gradient(180deg,#fde68a,#f59e0b 55%,#b45309);-webkit-background-clip:text;background-clip:text;color:transparent;filter:drop-shadow(0 2px 12px rgba(245,158,11,.45));animation:grheadin .5s cubic-bezier(.22,1.4,.36,1) both,grheadout .5s ease-in 2.75s both}'+
              '@keyframes grheadin{from{opacity:0;transform:translate(-50%,-50%) scale(.82)}to{opacity:1;transform:translate(-50%,-50%) scale(1)}}'+
              '@keyframes grheadout{to{opacity:0;transform:translate(-50%,-50%) scale(1.06)}}'+
              // 恩情文本粒子（4.6.2：文本即礼花——由炸点向外随机飞散定位，WAAPI 全程驱动；
              // 落点天然参差重叠、允许跨炸点重复，密度优先）
              '.grtxt{position:fixed;transform:translate(-50%,-50%);font-size:12px;font-weight:650;letter-spacing:.12em;white-space:nowrap;background:linear-gradient(180deg,#fcd34d,#d97706);-webkit-background-clip:text;background-clip:text;color:transparent;filter:drop-shadow(0 0 6px rgba(252,211,77,.4));opacity:0}'+
              // 四档字号错落（偶发 s0 大字点睛）
              '.grtxt.s0{font-size:17px;font-weight:750}'+
              '.grtxt.s2{font-size:11.5px;font-weight:600}'+
              '.grtxt.s3{font-size:10px;font-weight:500;letter-spacing:.08em}'+
              // 礼花（4.6.1）：亮珠(.grfwr)升空 → 凌空炸裂粒子(.grfwk)；WAAPI 逐粒子驱动，播完随层拆除
              '.grlayfw{position:fixed;left:0;top:0;pointer-events:none}'+
              '.grfwr{position:fixed;border-radius:50%;pointer-events:none;will-change:transform,opacity}'+
              '.grfwk{position:fixed;border-radius:50%;pointer-events:none;will-change:transform,opacity}'+
              '@keyframes grflash2{from{opacity:.4}to{opacity:0}}'+
              // 卡片入场预览（.ic）：展开过渡放宽为 luxuriant 缓动，网格项错峰上浮；移除 .ic 后按基础 .15s 收回
              '.wrap.ic .card{opacity:1;visibility:visible;transform:scale(1) translateY(0);transition:opacity .45s cubic-bezier(.22,1,.36,1),transform .45s cubic-bezier(.22,1,.36,1)}'+
              '.wrap.ic .card .item{animation:fxitem .45s cubic-bezier(.22,1,.36,1) both}'+
              '.wrap.ic .card .item:nth-child(2){animation-delay:.04s}'+
              '.wrap.ic .card .item:nth-child(3){animation-delay:.08s}'+
              '.wrap.ic .card .item:nth-child(4){animation-delay:.12s}'+
              '.wrap.ic .card .item:nth-child(5){animation-delay:.16s}'+
              '.wrap.ic .card .item:nth-child(6){animation-delay:.2s}'+
              '@keyframes fxitem{from{opacity:0;transform:translateY(5px)}to{opacity:1;transform:none}}'+
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
              '<div class="last" id="v9">最近一轮：—</div>'+
              '<div class="foot"><span>圆环弧=缓存命中率 · 点击圆环切换范围</span><a class="lnk" id="lnk" href="#" target="_blank" rel="noopener">详情页 ↗</a></div>'+
              '</div></div>';
              document.body.appendChild(root);
              var q=s=>sh.querySelector(s), wrap=q('.wrap'), ring=q('.ring'), card=q('.card'), arc=q('.arc');
              var fail=0, hideTimer=null, ro=null, lastR={}, curSel='session';
              var ORDER=['session','today','d7','d30','total'];
              var RN={session:'本次',today:'今天',d7:'7天',d30:'30天',total:'累计'};
              // 点击圆环循环切换统计范围（4.5.2 应用户要求改回）：本次→今天→7天→30天→累计→…；键盘 Enter/空格同
              function setRange(k){M.sel=k;try{localStorage.setItem('tstatsRange',k)}catch(e){}tick()}
              function nextRange(){setRange(ORDER[(ORDER.indexOf(curSel)+1)%ORDER.length]||ORDER[0])}
              ring.setAttribute('tabindex','0');ring.setAttribute('role','button');
              ring.setAttribute('aria-label','Token 用量圆环：点击循环切换统计范围');
              ring.title='点击切换统计范围：本次→今天→7天→30天→累计（圆环弧=缓存命中率）';
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
                  curSel=sel;
                  var R=d.ranges||{}, rg=R[sel]||{v:0,i:0,o:0,c:0,r:0};
                  lastR=R;
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
                  var hasData=(rg.v|0)>0||(rg.r|0)>0;
                  q('#t').textContent=hasData?fmt4(rg.v):'—';
                  q('#tl').textContent=hasData?(d.busy?'…':RN[sel]):'暂无数据';
                  var rate=(rg.c>0&&rg.i>0)?rg.c/rg.i:0;
                  arc.setAttribute('stroke-dasharray',(CIRC*rate).toFixed(1)+' '+CIRC.toFixed(1));
                  ring.classList.toggle('busy',!!d.busy);
                  ring.title='当前范围：'+RN[sel]+' · 圆环弧=缓存命中率'+((rg.c>0&&rg.i>0)?('（'+(rate*100).toFixed(0)+'%）'):'（暂无缓存数据）')+' · 点击切换范围';
                  // 首次数据就绪后播入场动画；fxArmed 是本次 build 的局部标志（同一驻留不重复）。
                  // FX=1 仅出现在激活后的注册里（C# entrancePending），页面刷新/切换查看拿到的是 0
                  if(FX&&!fxArmed){fxArmed=1;M.fxState='wait';setTimeout(function(){playFx(rg)},80)}
                  // 恩情层（4.6.0）：恩情模式开（GR=1）或首次使用未看过（强制，无视入场动画开关）→ 首帧数据就绪即播；
                  // 与眼动画同播时稍晚 180ms 起势——先见绽开、再闻惊雷
                  if((GR||!grSeen())&&!grArmed){grArmed=1;M.grState='wait';setTimeout(function(){playGr(false)},FX?180:80)}
                }).catch(function(){if(++fail>=3)root.style.display='none'});
              }
              ring.addEventListener('click',nextRange);
              ring.addEventListener('keydown',function(e){
                if(e.key==='Enter'||e.key===' '){e.preventDefault();nextRange()}
              });
              wrap.addEventListener('mouseenter',function(){
                if(hideTimer){clearTimeout(hideTimer);hideTimer=null}
                wrap.classList.add('on');
              });
              wrap.addEventListener('mouseleave',function(){
                hideTimer=setTimeout(function(){wrap.classList.remove('on')},150);
              });
              // ===== 入场动画（探查之眼·圆环风）：仅在角色激活时（C# 侧 entrancePending 随注册传 fx=1，
              // 播完或超时自动清除；页面刷新/切换查看不播），屏幕中央绽开圆环风探查之眼——非仿真，
              // 全部由圆构成（保留底环/渐变弧/光晕/轨道点 + 圆形虹膜/瞳孔/高光）——目光巡视片刻
              // → 沿弧线飞至挂件圆环“圆心”（飞行/落位/淡出逐帧重瞄，布局微移也精准停靠）
              // → 瞳孔张合落位、化作圆环（淡切+涟漪+弹跳+卡片预览）。
              // 文本在环下单行统一显示“词元账册 · 虚位以待”（流光），不随范围/数据区分；纯装饰不拦截交互；rAF 单循环驱动，播完拆除全部装饰节点；
              // 配置关闭（fx=0）时任何途径都不播；document 派发 'tstats-fx' 事件或 GET /fx 可手动重播 =====
              var fxRun=false,fxArmed=false;
              function easeOC(t){return 1-Math.pow(1-t,3)}
              function easeIO(t){return t<.5?4*t*t*t:1-Math.pow(-2*t+2,3)/2}
              // 只读探测：客户端加载指示（Ant Spin 转圈 / 大面积遮罩）还在时等它消失再起播（上限1.2s）
              function loaderBusy(){
                try{
                  var s=document.querySelector('.ant-spin-spinning');
                  if(s&&s.getClientRects().length>0)return true;
                  var ms=document.querySelectorAll('[class*="mask"],[class*="splash"]');
                  for(var i=0;i<ms.length;i++){var r=ms[i].getBoundingClientRect();
                    if(r.width>innerWidth*.7&&r.height>innerHeight*.7&&ms[i].style.display!=='none')return true}
                }catch(e){}
                return false;
              }
              function playFx(rg){
                if(fxRun)return;
                if(!FX){M.fxState='skip:off';return}
                var rr=ring.getBoundingClientRect();
                if(rr.width<1){M.fxState='skip:noring';return}
                try{if(matchMedia('(prefers-reduced-motion: reduce)').matches){M.fxState='skip:motion';return}}catch(e){}
                fxRun=true;wrap.classList.add('intro');
                var giveUp=function(msg){wrap.classList.remove('intro');fxRun=false;if(msg)M.fxState=msg};
                var go=function(){
                  M.fxState='play';
                  try{
                    var BIG=Math.max(6,Math.round(Math.min(innerWidth,innerHeight)*.3/RING)); // 4.5.1 略缩：直径占短边 30%（原 42%）
                    var C={x:innerWidth/2,y:innerHeight/2},L={x:rr.left+rr.width/2,y:rr.top+rr.height/2},MP={x:0,y:0};
                    function aim(){ // 落点=挂件圆环“圆心”（setFx 按圆心平移）；飞行/落位/淡出阶段逐帧重瞄，place() 布局微移也能精准停靠
                      var r2=ring.getBoundingClientRect();
                      if(r2.width>1){L.x=r2.left+r2.width/2;L.y=r2.top+r2.height/2}
                      var dx=L.x-C.x,dy=L.y-C.y,dist=Math.sqrt(dx*dx+dy*dy)||1;
                      var nx=-dy/dist,ny=dx/dist;if(ny>0){nx=-nx;ny=-ny} // 控制点偏向屏幕上方，飞行弧线更舒展
                      var bow=Math.max(36,Math.min(120,dist*.16));
                      MP.x=(C.x+L.x)/2+nx*bow;MP.y=(C.y+L.y)/2+ny*bow;
                    }
                    aim();
                    var N=Math.max(0,rg.v||0),has=N>0||(rg.r||0)>0,rate=(rg.c>0&&rg.i>0)?rg.c/rg.i:0;
                    // 放慢节奏让动画充分可读：蓄能巡视 1.5s → 飞行 1.15s → 瞳孔张合落位 0.35s → 化环淡切 0.3s
                    var CHARGE=1500,FLY=1150,SETTLE=350,FADE=300,TOT=CHARGE+FLY+SETTLE+FADE;
                    var fx=document.createElement('div');fx.className='fxring';
                    var dots='';for(var i=0;i<6;i++){var a=i*Math.PI/3;
                      dots+='<i style="left:'+(50+47*Math.cos(a)).toFixed(1)+'%;top:'+(50+47*Math.sin(a)).toFixed(1)+'%"></i>'}
                    fx.innerHTML='<svg viewBox="0 0 56 56">'+
                      '<defs><linearGradient id="tfxg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3b82f6"/><stop offset="1" stop-color="#22d3ee"/></linearGradient></defs>'+
                      '<circle cx="28" cy="28" r="24" fill="#fffdf9" stroke="#ecebe6" stroke-width="1"/>'+
                      '<circle class="fxarc" cx="28" cy="28" r="21" fill="none" stroke="url(#tfxg)" stroke-width="4.5" stroke-linecap="round" stroke-dasharray="0 131.9" transform="rotate(-90 28 28)"/>'+
                      '<g transform="translate(28,28)"><g class="fxig">'+
                      '<circle r="10.5" fill="url(#tfxg)"/><circle r="4.6" fill="#1e2b45"/><circle cx="3.1" cy="-3.1" r="1.9" fill="#fff" opacity=".95"/>'+
                      '</g></g>'+
                      '<circle class="fxhalo" cx="28" cy="28" r="26" fill="none" stroke="#93c5fd" stroke-width="2.5" stroke-linecap="round" stroke-dasharray="20 144" opacity=".55"/>'+
                      '</svg><div class="fxnum"><span class="fl"></span></div><div class="fxorb">'+dots+'</div>';
                    sh.appendChild(fx);
                    var fxa=fx.querySelector('.fxarc'),fxig=fx.querySelector('.fxig'),fl=fx.querySelector('.fl'),
                        fxnum=fx.querySelector('.fxnum');
                    // 环下文本不随范围/数据区分，统一为“词元账册 · 虚位以待”流光样式（词元=Token 雅译，账册呼应看板）
                    fl.textContent='词元账册 · 虚位以待';fl.classList.add('em');M.fxLabel='词元账册 · 虚位以待'
                    // 圆圈风“探查之眼”（非仿真）：全部由圆构成——渐变弧圆环 + 圆形虹膜/瞳孔/高光；“目光”=虹膜组整体平移巡视
                    function setGaze(gx,gy,sc,op){
                      fxig.setAttribute('transform','translate('+(gx||0).toFixed(2)+','+(gy||0).toFixed(2)+') scale('+Math.max(.05,sc==null?1:sc).toFixed(3)+')');
                      if(op!=null)fxig.setAttribute('opacity',Math.max(0,Math.min(1,op)).toFixed(3));
                    }
                    setGaze(0,0,.3,0);
                    var t0=null,tmrs=[],lastDust=0,rip=null,faded=false;
                    function bez(t){var u=1-t;return{x:u*u*C.x+2*u*t*MP.x+t*t*L.x,y:u*u*C.y+2*u*t*MP.y+t*t*L.y}}
                    function setFx(p,s,o){fx.style.transform='translate('+(p.x-RING/2)+'px,'+(p.y-RING/2)+'px) scale('+s+')';if(o!=null)fx.style.opacity=o}
                    function step(ts){
                      var more=true;
                      try{
                      if(t0===null)t0=ts;
                      var e=ts-t0;
                      M.fxE=e|0;
                      if(e<=CHARGE){ // 蓄能巡视：圆环绽放、渐变弧扫过、圆形虹膜浮现并左右探查
                        var k=easeOC(e/CHARGE);
                        setFx(C,BIG*(.55+.45*k),Math.min(1,e/300));
                        var w2=Math.min(1,e/400);
                        setGaze(4.5*w2*Math.sin(k*6.8),1.8*w2*Math.sin(k*4.2+1),.3+.7*k,Math.min(1,e/350));
                        fxa.setAttribute('stroke-dasharray',(CIRC*.82*k).toFixed(1)+' '+CIRC.toFixed(1));
                      }else if(e<=CHARGE+FLY){ // 飞行：逐帧重瞄落点沿弧线飞向挂件圆心，弧线收敛为命中率，目光回正，星尘尾迹，文本渐隐
                        aim();
                        var k2=easeIO((e-CHARGE)/FLY),p=bez(k2);
                        setFx(p,BIG+(1-BIG)*k2);
                        setGaze(4.5*(1-k2)*Math.sin(k2*9),1.8*(1-k2),1,1);
                        var a0=CIRC*.82,ap=has?CIRC*rate:0;
                        fxa.setAttribute('stroke-dasharray',(a0+(ap-a0)*k2).toFixed(1)+' '+CIRC.toFixed(1));
                        fxnum.style.opacity=k2<.55?1:Math.max(0,1-(k2-.55)/.45);
                        if(e-lastDust>70){lastDust=e;
                          var d=document.createElement('div');d.className='fxdust';
                          d.style.left=p.x+'px';d.style.top=p.y+'px';sh.appendChild(d);
                          (function(el){tmrs.push(setTimeout(function(){try{sh.removeChild(el)}catch(x){}},580))})(d)}
                      }else if(e<=CHARGE+FLY+SETTLE){ // 落位：瞳孔张合一次（圆圈式“眨眼”）+ 轻微回弹，持续跟踪圆心
                        aim();
                        var k3=(e-CHARGE-FLY)/SETTLE;
                        setFx(L,1+.1*Math.sin(Math.PI*k3)*(1-k3*.3));
                        setGaze(0,0,1+.14*Math.sin(Math.PI*k3),1);
                      }else if(e<=TOT){ // 化作圆环：虹膜收拢、淡切回真环 + 涟漪 + 弹跳 + 卡片预览
                        aim();
                        var k4=(e-CHARGE-FLY-SETTLE)/FADE;
                        if(!rip){
                          rip=document.createElement('div');rip.className='fxripple';
                          var rw=RING*.8;
                          rip.style.width=rip.style.height=rw+'px';
                          rip.style.left=(L.x-rw/2)+'px';rip.style.top=(L.y-rw/2)+'px';
                          sh.appendChild(rip);
                          try{rip.animate([{opacity:.7,transform:'scale(.6)'},{opacity:0,transform:'scale(2.6)'}],{duration:520,easing:'cubic-bezier(.22,1,.36,1)'}).addEventListener('finish',function(){try{sh.removeChild(rip)}catch(x){}})}catch(x){}
                          try{ring.animate([{transform:'scale(1)'},{transform:'scale(1.16)',offset:.35},{transform:'scale(1)'}],{duration:260,easing:'ease-out'})}catch(x){}
                        }
                        setGaze(0,0,1-.45*k4,1);
                        setFx(L,1,1-k4);
                        if(!faded&&k4>=.35){faded=true;wrap.classList.remove('intro');
                          wrap.classList.add('ic');
                          // 收回预览的定时器不进 tmrs：清理阶段（e>TOT）早于它到期，不能被清除，否则卡片卡在展开态
                          setTimeout(function(){wrap.classList.remove('ic')},2400);
                        }
                      }
                      more=e<=TOT;
                      if(more)requestAnimationFrame(step);
                      }catch(err){more=false;M.fxState='error:'+((err&&err.message)||err)}
                      if(!more){ // 清理：拆除装饰节点与计时器；循环中途异常也走这里，绝不留下隐藏的圆环
                        try{sh.removeChild(fx)}catch(x){}
                        for(var j=0;j<tmrs.length;j++)clearTimeout(tmrs[j]);
                        fxRun=false;
                        if(String(M.fxState).indexOf('error')!==0)M.fxState='done';
                      }
                    }
                    requestAnimationFrame(step);
                  }catch(err){M.fxState='error:'+((err&&err.message)||err);giveUp()}
                };
                // 后台页面 rAF 不触发：等回到前台再播（10s 内没回前台则放弃，圆环直接显示）
                if(document.hidden){
                  M.fxState='defer';
                  var done0=false,to0=null;
                  var once0=function(){if(done0)return;done0=true;
                    document.removeEventListener('visibilitychange',chk0);
                    if(to0)clearTimeout(to0);
                    if(!document.hidden)go();else giveUp('skip:hidden')};
                  var chk0=function(){if(!document.hidden)once0()};
                  to0=setTimeout(once0,10000);
                  document.addEventListener('visibilitychange',chk0);
                  return;
                }
                if(loaderBusy()){var w=0;var iv=setInterval(function(){
                  if(++w>12||!loaderBusy()){clearInterval(iv);if(w<=12)go();else{M.fxState='skip:loader';giveUp()}}
                },100)}
                else go();
              }
              // ===== 恩情层（4.6.0·雷霆满屏）：独立时间轴，与探查之眼互不依赖——眼动画关闭时也能单独满屏播放。
              // 触发：恩情模式开（GR=1）每次激活都播；首次使用（localStorage 无 tstatsGrSeen）强制播一次，
              // 无视入场动画开关；减少动态效果→跳过并记已看（无障碍豁免，永不强制）；页面隐藏→defer 至多
              // 10s，超时 skip:hidden 且不记已看（下次激活再试）。pointer-events:none 满屏但不拦截任何点击 =====
              var grRun=false,grArmed=false;
              var GRH=['恩重如山','感恩初心','恩深似海','大恩不言谢'];
              // 4.6.2 扩容至 90 条：经典成语 + 主题彩蛋（文本即礼花，允许跨炸点重复，池越大重复感越低）
              var GRP=['感激涕零','铭感五内','没齿难忘','感恩戴德','千恩万谢','谢天谢地','感激不尽','涌泉相报','结草衔环','恩同再造','知恩图报','镂骨铭肌','寸草春晖','一饭千金','投桃报李','感念在心','不胜感激','恩逾慈母','大恩大德','知遇之恩','恩若再生','感戴莫名','顶礼膜拜','永志不忘','铭刻于心','恩高义厚','义薄云天','春晖寸草','反哺之情','感恩怀德','恩泽绵长','没齿难报','恩重丘山','感激流涕','恩深爱重','来生再报','感恩不尽','恩同天地','感遇忘身','铭诸肺腑','恩波浩荡','千载不忘','惠泽千秋','感铭心切','五体投地','拜谢恩公','叩首致谢','恩重如岳','感激莫名','永怀感念','恩深谊厚','谢意如潮','词元有价 · 恩情无价','缓存命中 · 皆是恩情','每一枚 Token 都记得','账册满满 · 皆是厚恩','算力有尽 · 恩情无穷','上下文会忘 · 恩情不会','不忘初心 · 方得始终','temperature 再高 · 恩情不降温','知识截止 · 恩情不截止','loss 在降 · 恩情在涨','梯度下降 · 恩情上升','显存会爆 · 恩情不会','推理有延迟 · 感激零延迟','量化有损 · 恩情无损','params 千亿 · 不及一句谢谢','每 K 词元 · 皆是厚赐','缓存未命中 · 恩情必命中','上下文窗口装不下恩情','历史会截断 · 恩情不断','词元有尽 · 恩情不灭','第 4096 个词元也是恩情','一轮对话 · 十分恩情','busy 转圈时 · 也在感恩','恩情未量化 · 无法截断','attention 权重全给你','KV 缓存会淘汰 · 恩情不淘汰','softmax 分配不出这份感激','beam search 找不到更深的谢意','token by token · 恩情累积','嵌入向量装不下恩情','多轮对话 · 轮轮感恩','上下文越长 · 恩情越浓','生成完毕 · 感恩未完','词表六万 · 感激第一','温度采样不出真心','一轮生成 · 万分感激','prefill 很快 · 感激更快','decode 逐字 · 恩情逐字'];
              function grSeen(){try{return !!localStorage.getItem('tstatsGrSeen')}catch(e){return true}}
              function grMark(){try{localStorage.setItem('tstatsGrSeen','1')}catch(e){}}
              function grShuffle(a){for(var i=a.length-1;i>0;i--){var j=(Math.random()*(i+1))|0,t=a[i];a[i]=a[j];a[j]=t}return a}
              // 洗牌队列（抽干再补）：每次弹出 n 条并回写 localStorage，队列耗尽才用“未在本次已抽集中”
              // 的文案重洗补足——单场内不重复，连场之间只有换页边界少量重叠，主标语绝不连场重复
              function grDraw(key,pool,n){
                var q=null;try{q=JSON.parse(localStorage.getItem(key)||'null')}catch(e){}
                if(!q||!Array.isArray(q)||!q.length)q=grShuffle(pool.slice());
                var out=[];
                while(out.length<n){
                  if(!q.length){
                    var used={};for(var i=0;i<out.length;i++)used[out[i]]=1;
                    q=grShuffle(pool.filter(function(p){return !used[p]}));
                    if(!q.length)break;
                  }
                  out=out.concat(q.splice(0,n-out.length));
                }
                try{localStorage.setItem(key,JSON.stringify(q))}catch(e){}
                return out;
              }
              function playGr(replay){
                if(grRun)return;
                try{if(matchMedia('(prefers-reduced-motion: reduce)').matches){M.grState='skip:motion';grMark();return}}catch(e){}
                var go=function(){
                  if(grRun)return;
                  grRun=true;M.grState='play';
                  var lay=null,t0=null,tmrs=[];
                  var GRT=3900; // 惊雷0.5s → 满屏恩情+礼花2s → 收束淡出 → 礼花余韵，共约3.9s（恩情层可略长于眼动画）
                  function step(ts){
                    var more=true;
                    try{
                      if(t0===null)t0=ts;
                      var e=ts-t0;M.grE=e|0;
                      more=e<=GRT;
                      if(more)requestAnimationFrame(step);
                    }catch(err){more=false;M.grState='error:'+((err&&err.message)||err)}
                    if(!more){ // 拆除恩情层全部节点与计时器；循环中途异常也走这里，绝不残留
                      for(var j=0;j<tmrs.length;j++)clearTimeout(tmrs[j]);
                      try{if(lay&&lay.parentNode)lay.parentNode.removeChild(lay)}catch(x){}
                      grRun=false;
                      if(String(M.grState).indexOf('error')!==0)M.grState='done';
                      if(!replay)grMark();
                    }
                  }
                  try{
                    var W=innerWidth,H=innerHeight,C={x:W/2,y:H/2};
                    lay=document.createElement('div');lay.className='grlay';
                    sh.appendChild(lay);
                    // 惊雷：全屏金色闪光（第二道闪电时配弱闪光 grflash2）
                    function mkFlash(weak){
                      var f=document.createElement('div');f.className='grflash';
                      f.style.width=W+'px';f.style.height=H+'px';
                      f.style.animation=(weak?'grflash2':'grflash')+' .42s ease-out both';
                      lay.appendChild(f);
                      tmrs.push(setTimeout(function(){try{lay.removeChild(f)}catch(x){}},480));
                    }
                    // 闪电：屏幕顶部随机偏移劈至中心（锯齿折线+白色内芯+两道细分支），steps 闪烁衰减
                    function mkBolt(){
                      var x0=C.x+(Math.random()*160-80),seg=7,pts=[[x0,0]];
                      for(var i=1;i<seg;i++){var k=i/seg;
                        pts.push([x0+(C.x-x0)*k+(Math.random()*44-22),C.y*k+(Math.random()*24-12)])}
                      pts.push([C.x,C.y]);
                      var pd='M'+pts.map(function(p){return p[0].toFixed(1)+' '+p[1].toFixed(1)}).join(' L');
                      var br='';
                      for(var b=0;b<2;b++){var st=pts[2+((Math.random()*(seg-3))|0)];
                        br+='<path class="b2" d="M'+st[0].toFixed(1)+' '+st[1].toFixed(1)+' L'+(st[0]+Math.random()*70-35).toFixed(1)+' '+(st[1]+40+Math.random()*60).toFixed(1)+'"/>'}
                      var bo=document.createElement('div');bo.className='grbolt';
                      bo.innerHTML='<svg viewBox="0 0 '+W+' '+H+'">'+
                        '<defs><linearGradient id="tgrg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#e0f2fe"/><stop offset="1" stop-color="#3b82f6"/></linearGradient></defs>'+
                        '<path class="b1" d="'+pd+'"/><path class="b1c" d="'+pd+'"/>'+br+'</svg>';
                      lay.appendChild(bo);
                      tmrs.push(setTimeout(function(){try{lay.removeChild(bo)}catch(x){}},600));
                    }
                    mkFlash(false);mkBolt();
                    // 礼花辉光容器（位于文本之下：炸裂辉光衬在文字后）
                    var fwL=document.createElement('div');fwL.className='grlayfw';
                    lay.appendChild(fwL);
                    // 恩情文本粒子（4.6.2：文本即礼花）：从炸点(bx,by)向外随机飞散——
                    // 随机方向/半径/±12°微旋转/四档字号，减速外扩+末段下坠+淡出；
                    // 落点天然参差重叠、跨炸点允许重复（每炸点内部不重复），密度优先
                    function spawnTexts(bx,by,cnt,r0,r1,absT){
                      var bag=grShuffle(GRP.slice());
                      var dur=Math.max(900,3600-absT); // 收尾不越过总时长
                      for(var p=0;p<cnt&&p<bag.length;p++){
                        var el=document.createElement('div');
                        var tier=Math.random()<.12?'s0':['s1','s2','s3'][(Math.random()*3)|0];
                        el.className='grtxt '+tier;
                        el.textContent=bag[p];
                        el.style.left=bx.toFixed(1)+'px';el.style.top=by.toFixed(1)+'px';
                        lay.appendChild(el);
                        var a=Math.random()*Math.PI*2,rad=r0+Math.random()*(r1-r0);
                        var dx=Math.cos(a)*rad,dy=Math.sin(a)*rad*.8;
                        var r2=(Math.random()*24-12).toFixed(1);
                        try{el.animate([
                          {transform:'translate(-50%,-50%) scale(.35)',opacity:0},
                          {transform:'translate(-50%,-50%) translate('+(dx*.85).toFixed(1)+'px,'+(dy*.85-12).toFixed(1)+'px) rotate('+r2+'deg)',opacity:1,offset:.3},
                          {transform:'translate(-50%,-50%) translate('+dx.toFixed(1)+'px,'+(dy+26).toFixed(1)+'px) rotate('+r2+'deg)',opacity:.95,offset:.8},
                          {transform:'translate(-50%,-50%) translate('+dx.toFixed(1)+'px,'+(dy+46).toFixed(1)+'px) scale(.92) rotate('+r2+'deg)',opacity:0}
                        ],{duration:dur,easing:'cubic-bezier(.17,.67,.35,1)'})}catch(x){}
                        (function(el2,ms){tmrs.push(setTimeout(function(){try{lay.removeChild(el2)}catch(x){}},ms))})(el,dur+150);
                      }
                    }
                    // 礼花辉光粒子：每发 26-35 粒，金/冰蓝/暖红轮换，三段关键帧模拟减速+坠落
                    var pals=[['#fff7d6','#f59e0b','#b45309'],['#e0f2fe','#60a5fa','#1d4ed8'],['#ffe4e6','#fb7185','#be123c']];
                    var fwP=0;
                    function boom(bx,by,pal){
                      var cnt=26+((Math.random()*10)|0);
                      for(var p=0;p<cnt;p++){
                        var el=document.createElement('div');el.className='grfwk';
                        var sz2=(2.5+Math.random()*3).toFixed(1);
                        el.style.cssText='left:'+bx.toFixed(1)+'px;top:'+by.toFixed(1)+'px;width:'+sz2+'px;height:'+sz2+'px;background:radial-gradient(circle,'+pal[0]+','+pal[1]+' 55%,transparent 72%);box-shadow:0 0 8px '+pal[1];
                        fwL.appendChild(el);
                        var a=Math.random()*Math.PI*2,rad=60+Math.random()*95;
                        var dx=Math.cos(a)*rad,dy=Math.sin(a)*rad*.85;
                        try{el.animate([
                          {transform:'translate(-50%,-50%)',opacity:1},
                          {transform:'translate('+(dx*.8).toFixed(1)+'px,'+(dy*.8-14).toFixed(1)+'px)',opacity:.95,offset:.55},
                          {transform:'translate('+dx.toFixed(1)+'px,'+(dy+46).toFixed(1)+'px)',opacity:0}
                        ],{duration:700+Math.random()*250,easing:'cubic-bezier(.17,.67,.35,1)'})}catch(x){}
                        (function(el2){tmrs.push(setTimeout(function(){try{fwL.removeChild(el2)}catch(x){}},1150))})(el);
                      }
                    }
                    // 一发完整礼花 = 辉光炸裂 + 文本粒子同点飞散
                    function burst(bx,by,pal,absT,cnt,r0,r1){
                      boom(bx,by,pal);
                      spawnTexts(bx,by,cnt,r0,r1,absT);
                    }
                    function rocket(tx,ty,absT,cnt){
                      var pal=pals[fwP++%pals.length];
                      var r0=document.createElement('div');r0.className='grfwr';
                      r0.style.cssText='left:'+tx.toFixed(1)+'px;top:'+(H+6)+'px;width:3px;height:3px;background:radial-gradient(circle,#fff,'+pal[1]+' 60%,transparent);box-shadow:0 0 10px '+pal[1];
                      fwL.appendChild(r0);
                      try{
                        var an=r0.animate([{transform:'translate(-50%,-50%)',opacity:.9},{transform:'translate(-50%,-50%) translateY('+(ty-H-6)+'px)',opacity:1}],{duration:520+Math.random()*220,easing:'cubic-bezier(.3,.7,.5,1)'});
                        an.addEventListener('finish',function(){try{fwL.removeChild(r0)}catch(x){}burst(tx,ty,pal,absT,cnt,45,185)});
                      }catch(x){try{fwL.removeChild(r0)}catch(x2){}burst(tx,ty,pal,absT,cnt,45,185)}
                    }
                    // 开场中心大炸：闪电劈落点先来一发超大半径文本礼花（24 条，铺满中屏）
                    tmrs.push(setTimeout(function(){burst(C.x,C.y,pals[fwP++%pals.length],240,24,110,Math.min(W,H)*.34)},240));
                    // 后续礼花：380ms 起每 150-260ms 一发亮珠（升空约 0.6s 后炸），截止 2.35s
                    var tFw=[],tc=380+Math.random()*120,planned=24;
                    while(tc<2350){var cn=10+((Math.random()*5)|0);tFw.push({t:tc,c:cn});planned+=cn;tc+=150+Math.random()*110}
                    for(var f=0;f<tFw.length;f++)(function(o){
                      tmrs.push(setTimeout(function(){
                        var bx=40+Math.random()*(W-80),by=H*.12+Math.random()*H*.5;
                        if(Math.abs(bx-C.x)<W*.14&&Math.abs(by-C.y)<H*.16)bx=(bx+W*.22)%(W-60)+30; // 避开主标语
                        rocket(bx,by,o.t+630,o.c);
                      },o.t))})(tFw[f]);
                    M.grFw=tFw.length+1; // 本场礼花发数（含开场大炸）
                    M.grTxt=planned;     // 本场计划文本粒子数（/fx?gr=1 外部可观测）
                    // 主标语（洗牌轮换，绝不连场重复）
                    var head=document.createElement('div');head.className='grhead';
                    head.textContent=grDraw('tstatsGrH',GRH,1)[0];M.grLabel=head.textContent;
                    lay.appendChild(head);
                    // 雷霆震颤 ×2：开场一道 + 1.45s 后随第二道闪电再震一道（只抖自己的节点，不碰客户端内容）
                    function quake(){
                      try{lay.animate([{transform:'translate(0,0)'},{transform:'translate(3px,-2px)'},{transform:'translate(-3px,2px)'},{transform:'translate(2px,1px)'},{transform:'translate(0,0)'}],{duration:320,easing:'ease-out'})}catch(x){}
                    }
                    quake();
                    tmrs.push(setTimeout(function(){mkFlash(true);mkBolt();quake()},1450+Math.random()*250));
                    requestAnimationFrame(step);
                  }catch(err){M.grState='error:'+((err&&err.message)||err);try{if(lay&&lay.parentNode)lay.parentNode.removeChild(lay)}catch(x){}for(var j2=0;j2<tmrs.length;j2++)clearTimeout(tmrs[j2]);grRun=false}
                };
                // 后台页面 rAF 不触发：等回到前台再播（10s 内没回前台则放弃，不记已看，下次激活再试）
                if(document.hidden){
                  M.grState='defer';
                  var done0=false,to0=null;
                  var once0=function(){if(done0)return;done0=true;
                    document.removeEventListener('visibilitychange',chk0);
                    if(to0)clearTimeout(to0);
                    if(!document.hidden)go();else M.grState='skip:hidden'};
                  var chk0=function(){if(!document.hidden)once0()};
                  to0=setTimeout(once0,10000);
                  document.addEventListener('visibilitychange',chk0);
                  return;
                }
                go();
              }
              q('#lnk').href='http://127.0.0.1:'+M.port+'/';
              place();tick();
              try{ro=new ResizeObserver(place)}catch(e){}
              M.ui={root:root,ro:ro};
              M.place=place;M.tick=tick;
              M.fxImpl=function(){var s=M.sel||'session';playFx((lastR||{})[s]||{})};
              M.grImpl=function(){playGr(true)}; // 重播恩情层（不写“已看”标记，调试/演示用）
            };
            M.sync=function(){
              var vis=M.visible(),want=0;
              for(var p in M.reg){if(M.reg.hasOwnProperty(p)&&M.reg[p].name===vis){want=+p;break}}
              if(want!==0&&want===M.port&&M.ui)return;
              if(want===0){if(M.ui||M.port!==0){M.drop();M.port=0}return}
              M.port=want;M.build();M.built=want;M.tick();
            };
            // 返回调用方自己的状态：显示中→ok/injected+矩形（injected 表示本次刚完成构建，
            // 供 C# 侧只打一次“已就绪”日志），未显示（页面在看别的角色或非聊天页）→hidden；
            // 末尾附入场动画状态（wait/play/done/skip:*），随 1.2s 心跳回传 /stats 供实证
            M.register=function(port,name,cfg){
              M.reg[port]={name:name,cfg:cfg};
              M.sync();
              if(M.port!==+port)return 'hidden';
              var r=(M.built===+port)?'injected':'ok';
              M.built=0;
              return r+' '+M.rect()+(M.fxState?(' fx:'+M.fxState):'')+(M.grState?(' gr:'+M.grState):'');
            };
            M.unregister=function(port){delete M.reg[port];
              if(M.port===+port){M.port=0;M.drop();M.sync()}};
            setInterval(function(){M.sync();if(M.place)M.place()},1000);
            setInterval(function(){if(M.tick)M.tick()},1000);
            addEventListener('resize',function(){if(M.place)M.place()});
            // 重播钩子：控制台 document.dispatchEvent(new CustomEvent('tstats-fx')) 可随时重看入场动画
            document.addEventListener('tstats-fx',function(){try{M.fxImpl&&M.fxImpl()}catch(e){}});
            // 恩情层重播钩子：GET /fx?gr=1 或控制台 document.dispatchEvent(new CustomEvent('tstats-gr'))
            document.addEventListener('tstats-gr',function(){try{M.grImpl&&M.grImpl()}catch(e){}});
          }
          return M.register(__PORT__,'__NAME__',{ring:__RING__,gap:__GAP__,cw:__CARDW__,ch:__CARDH__,fx:__FX__,gr:__GR__});
        })()
        """;

    // 插件详情页：深色实时控制台 + 浅色范围统计/明细面板；
    // 范围：今天/近7天/近30天/总计/自定义——单天自动按小时显示，多天按天显示；含命中率列与最近轮次面板。
    // 视图状态（范围/口径/维度）写入 URL hash；明细与最近轮次表头点击排序；表格窄屏横向滚动。
    const string DashboardHtml = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <title>Token用量看板</title>
        <style>
        *{margin:0;padding:0;box-sizing:border-box}
        body{font-family:"Segoe UI",system-ui,"Microsoft YaHei",sans-serif;background:#f2f3f7;color:#23272f;padding:26px 14px 40px}
        .wrap{max-width:min(1560px,94vw);margin:0 auto}
        /* 4.7.0 自适应加宽：指标格/明细格 auto-fit；宽屏下「维度分析+用量明细」双列并排 */
        .cols{display:contents}
        @media(min-width:1200px){.cols{display:grid;grid-template-columns:1fr 1fr;gap:14px;align-items:start}}
        .console{background:linear-gradient(160deg,#171c2e 0,#131726 60%,#10141f 100%);border:1px solid #262d45;border-radius:16px;padding:18px 20px 16px;color:#dbe2f2;box-shadow:0 18px 40px rgba(19,23,38,.28)}
        .kicker{font:700 10px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:3px;color:#7f9bd9}
        .console h1{font-size:19px;font-weight:700;letter-spacing:1px;margin-top:6px;color:#f2f5ff}
        .c-head{display:flex;align-items:flex-start;gap:12px}
        .c-sub{font-size:11.5px;color:#8b94ad;margin-top:5px}
        .meter{margin-left:auto;display:flex;align-items:center;gap:7px;padding:6px 12px;border:1px solid rgba(127,155,217,.28);border-radius:999px;background:rgba(127,155,217,.08);font-size:11px;color:#aeb9d6;flex:0 0 auto}
        .led{width:7px;height:7px;border-radius:50%;background:#5eead4;box-shadow:0 0 8px #5eead4;animation:led 1.8s ease-in-out infinite}
        .meter.busy .led{background:#8ff0ff;box-shadow:0 0 8px #8ff0ff}
        @keyframes led{50%{opacity:.55}}
        .c-metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(118px,1fr));gap:8px;margin-top:14px}
        .cm{padding:8px 10px;border:1px solid rgba(127,155,217,.14);border-radius:11px;background:rgba(10,13,24,.45)}
        .cm span{display:block;font:700 9px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:1.2px;color:#67718f}
        .cm strong{display:block;margin-top:4px;font:700 14px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;color:#dbe7ff;letter-spacing:.3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .c-last{margin-top:10px;font-size:11.5px;color:#8b94ad}
        .c-last b{color:#c6d2ee;font-variant-numeric:tabular-nums;font-weight:600}
        .panel{margin-top:14px;background:#fff;border:1px solid #e5e7ee;border-radius:14px;padding:14px 16px 16px;box-shadow:0 6px 18px rgba(23,27,40,.05)}
        .p-head{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
        .p-dot{width:8px;height:8px;border-radius:50%;background:#3b82f6;box-shadow:0 0 6px rgba(59,130,246,.45)}
        .p-title{font-size:14px;font-weight:650;color:#3a4051}
        .p-sub{font-size:11.5px;color:#9aa1b0}
        .tabs{margin-left:auto;display:flex;gap:6px;flex-wrap:wrap;align-items:center}
        .tab{padding:5px 14px;border:1px solid #dfe2ea;border-radius:999px;font-size:12.5px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
        .tab:hover{border-color:#a9c4f8;color:#2f6fd8;transform:translateY(-1px)}
        .tab.on{background:#3b82f6;border-color:#3b82f6;color:#fff;box-shadow:0 4px 12px rgba(59,130,246,.3)}
        .tab2{padding:4px 11px;border:1px solid #dfe2ea;border-radius:999px;font-size:12px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
        .tab2:hover{border-color:#a9c4f8;color:#2f6fd8}
        .tab2.on{background:#3b82f6;border-color:#3b82f6;color:#fff}
        .scp{padding:4px 11px;border:1px solid #dfe2ea;border-radius:999px;font-size:12px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
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
        .hero .hk{font:700 10px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:2px;color:#9aa1b0;margin-bottom:6px}
        .hero .num{font:700 34px/1 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;letter-spacing:-.5px;font-variant-numeric:tabular-nums}
        .cells{display:grid;grid-template-columns:repeat(auto-fit,minmax(118px,1fr));gap:8px}
        /* 余额面板卡片（4.7.0） */
        .bcards{display:grid;grid-template-columns:repeat(auto-fill,minmax(232px,1fr));gap:10px;margin-top:12px}
        .bcard{border:1px solid #e8eaf1;background:#fbfcfe;border-radius:12px;padding:11px 13px}
        .bcard.err{border-color:#f3c8c8;background:#fdf7f7}
        .bcard .bn{font-size:12.5px;font-weight:650;color:#3a4051;display:flex;gap:6px;align-items:center;min-width:0}
        .bcard .bn .nm{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .btag{flex:0 0 auto;font:650 9.5px/1.7 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:.5px;border:1px solid #dbe4ff;color:#2f6fd8;background:#f0f6ff;border-radius:6px;padding:0 6px}
        .btag.preset{border-color:#d8f0e2;color:#0e9f6e;background:#f0fbf5}
        .btag.custom{border-color:#eadcf7;color:#7c3aed;background:#f9f4fe}
        .bcard .bv{margin-top:6px;font:700 21px/1.15 ui-monospace,SFMono-Regular,Consolas,monospace;color:#0e9f6e;font-variant-numeric:tabular-nums;word-break:break-all}
        .bcard.err .bv{color:#c94f4f;font-size:12.5px;font-weight:600}
        .bcard .bc{font-size:10.5px;color:#9aa1b0;margin-top:3px}
        .bcard .bm{font-size:10.5px;color:#b6bac4;margin-top:2px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
        .bref{padding:5px 14px;border:1px solid #dfe2ea;border-radius:999px;font-size:12px;color:#5c6270;cursor:pointer;background:#fff;user-select:none;transition:all .12s}
        .bref:hover{border-color:#6ee7b7;color:#059669}
        .cell{border:1px solid #e8eaf1;background:#fbfcfe;border-radius:11px;padding:9px 11px}
        .cell .k{font-size:11px;color:#9aa1b0}
        .cell .s{font-size:10px;color:#b6bac4;margin-top:2px}
        .cell .v{margin-top:3px;font:650 14px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;font-variant-numeric:tabular-nums;white-space:nowrap}
        .v.inp{color:#2f6fd8}.v.out{color:#db2777}.v.cache{color:#d97706}.v.rate{color:#7c3aed}.v.avg{color:#0e9f6e}
        table{width:100%;border-collapse:collapse;margin-top:6px;font-size:12.5px}
        th,td{padding:7px 10px;text-align:right;border-bottom:1px solid #f1f2f6;white-space:nowrap}
        th{color:#9aa1b0;font-weight:600;background:#fafbfd;font-size:11.5px}
        th.srt{cursor:pointer;user-select:none}
        th.srt:hover{color:#2f6fd8}
        th.srt.sa::after{content:" ↑";color:#2f6fd8}
        th.srt.sd::after{content:" ↓";color:#2f6fd8}
        th:first-child,td:first-child{text-align:left}
        tbody tr:hover td{background:#f8faff}
        .bdg{display:inline-block;min-width:96px;text-align:center;font:650 11px/1.7 ui-monospace,SFMono-Regular,Consolas,monospace;border:1px solid #e2e5ee;border-radius:7px;background:#fff;color:#5c6270;padding:0 6px}
        tr.is-today .bdg{border-color:#a9c4f8;color:#2f6fd8;background:#f0f6ff}
        td b{font-variant-numeric:tabular-nums}
        .barw{width:100%;min-width:56px;background:#f1f2f6;border-radius:99px;height:6px}
        .bar{height:6px;border-radius:99px;background:linear-gradient(90deg,#3b82f6,#7cb0ff);min-width:2px}
        .mdl{display:inline-block;max-width:150px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font:600 11px/1.6 ui-monospace,SFMono-Regular,Consolas,monospace;color:#5c6270;background:#f5f6fa;border:1px solid #e8eaf1;border-radius:6px;padding:1px 7px}
        .empty td{text-align:center;color:#b6bac4;padding:20px}
        .tscroll{overflow-x:auto;-webkit-overflow-scrolling:touch}
        .sh{display:flex;align-items:center;gap:6px;justify-content:flex-end}
        .sh>span{font-size:10.5px;color:#9aa1b0;min-width:36px;text-align:right;font-variant-numeric:tabular-nums}
        .cst:not(.on) input,.cst:not(.on)>span{display:none}
        .foot{margin-top:12px;font-size:11.5px;color:#b6bac4;line-height:1.7;word-break:break-all}
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
            <div class="cm"><span>今日出错</span><strong id="serr">0</strong></div>
            <div class="cm"><span>余额(当前渠道)</span><strong id="sbal">—</strong></div>
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
            <div class="cell"><div class="k">轮次</div><div class="v" id="grd">0</div><div class="s" id="gds">覆盖 0 天</div></div>
            <div class="cell"><div class="k" id="gavgk">日均</div><div class="v avg" id="gavg">0</div></div>
          </div>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">账户余额</span><span class="p-sub" id="bsub">余额监测源</span>
            <div class="tabs"><span class="bref" id="bref" tabindex="0" role="button">立即刷新</span></div>
          </div>
          <div class="foot" style="margin-top:2px">按 URL 自动探测（DeepSeek/Moonshot/硅基/智谱官方端点 · 其余按 One-API 系中转站额度−已用）· 在配置页填「初始额度」的源按 初始−已用 扣减估算 · 自定义接口与预设扣减源在模块配置页「余额监测」维护 · 低频轮询不随用量秒刷</div>
          <div class="bcards" id="bcards"></div>
        </section>
        <div class="cols">
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
          <div class="tscroll"><table>
            <thead><tr><th>名称</th><th>轮次</th><th>输入</th><th>输出</th><th>缓存</th><th>合计</th><th title="按价格规则计价（元/百万tokens）">费用(元)</th><th style="width:20%">占比</th></tr></thead>
            <tbody id="ab"></tbody>
          </table></div>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">用量明细</span><span class="p-sub" id="dsub">按天</span></div>
          <div class="tscroll"><table>
            <thead><tr><th class="srt" data-k="sk" tabindex="0">时间</th><th class="srt" data-k="rounds" tabindex="0">轮次</th><th class="srt" data-k="i" tabindex="0">输入</th><th class="srt" data-k="o" tabindex="0">输出</th><th class="srt" data-k="c" tabindex="0">缓存</th><th class="srt" data-k="rate" tabindex="0">命中率</th><th class="srt" data-k="v" tabindex="0">合计</th><th style="width:22%">用量条</th></tr></thead>
            <tbody id="tb"></tbody>
          </table></div>
        </section>
        </div><!-- .cols（宽屏下维度分析+用量明细双列并排） -->
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">最近对话轮次</span><span class="p-sub" id="rsub">本角色 · 最近15条 · 含模型/来源/渠道/费用</span></div>
          <div class="tscroll"><table>
            <thead><tr><th class="srt" data-k="t" tabindex="0">时间</th><th>模型</th><th>来源</th><th>渠道</th><th class="srt" data-k="i" tabindex="0">输入</th><th class="srt" data-k="o" tabindex="0">输出</th><th class="srt" data-k="c" tabindex="0">缓存</th><th class="srt" data-k="v" tabindex="0">合计</th><th class="srt" data-k="con" tabindex="0" title="按价格规则计价（元/百万tokens）">费用(元)</th><th title="相对最近15轮中最大单轮的用量占比" style="width:16%">占比</th></tr></thead>
            <tbody id="rb"></tbody>
          </table></div>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">价格规则</span><span class="p-sub">只读 · 编辑入口在模块配置页（渠道价格设置）</span></div>
          <div class="foot" style="margin-top:2px" id="pksub">峰=工作日 9:00–12:00、14:00–18:00（机器本地时间），其余谷 · 费用 = 命中×命中价 + (输入−命中)×未命中价 + 输出×输出价（元/百万tokens）</div>
          <div class="tscroll"><table>
            <thead><tr><th>渠道/规则</th><th>URL匹配</th><th>模型匹配</th><th>峰谷</th><th>命中</th><th>未命中</th><th>输出</th></tr></thead>
            <tbody id="pb"></tbody>
          </table></div>
        </section>
        <div class="foot" id="foot">加载中…</div>
        </div>
        <script>
        var days=[], hourData=null, hourDay=null, mode='today', todayStr='';
        var adim='byChannel', ana=null;
        var scope='self', CUR='';
        var dSort={k:'sk',d:-1}, rSort={k:'t',d:-1}, lastRecs=[];
        try{var sp0=localStorage.getItem('tstatsScope');if(sp0==='all')scope=sp0}catch(e){}
        // 视图状态（范围/口径/维度）写入 URL hash，刷新/收藏/分享可还原；自定义范围含日期不持久化
        try{
          var h0=location.hash||'';
          var m1=h0.match(/\/(today|d7|d30|total)\//),m2=h0.match(/\/(self|all)\//),m3=h0.match(/\/(byChannel|byName|bySource|byModel)\/?$/);
          if(m1)mode=m1[1];if(m2)scope=m2[1];if(m3)adim=m3[1];
        }catch(e){}
        var $=function(id){return document.getElementById(id)};
        var fmt=function(n){return Number(n||0).toLocaleString('zh-CN')};
        var esc=function(s){return String(s==null?'':s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')};
        var fmtC=function(c){return c==null?'—':'¥'+c};
        function p2(n){return String(n).padStart(2,'0')}
        function isoD(d){return d.getFullYear()+'-'+p2(d.getMonth()+1)+'-'+p2(d.getDate())}
        function sortRows(arr,st){return arr.slice().sort(function(a,b){var x=a[st.k],y=b[st.k];return (x<y?-1:x>y?1:0)*st.d})}
        function syncHash(){try{history.replaceState(null,'','#/'+mode+'/'+(scope==='all'?'all':'self')+'/'+adim)}catch(e){}}
        function syncAria(){document.querySelectorAll('.tab,.tab2,.scp').forEach(function(el){el.setAttribute('aria-pressed',el.classList.contains('on')?'true':'false')})}
        function needDay(){
          if(mode==='today')return todayStr||isoD(new Date());
          if(mode==='custom'&&$('df').value&&$('df').value===$('dt').value)return $('df').value;
          return null;
        }
        function dailyList(){
          var today=isoD(new Date());
          if(mode==='custom'){var f=$('df').value||today,t=$('dt').value||today;
            if(f>t){var sw=f;f=t;t=sw;  // 起止填反时自动交换并在标题中说明，避免“看似无数据”
              return{label:'自定义 '+f+' ~ '+t+'（起止已自动交换）',list:days.filter(function(x){return x.d>=f&&x.d<=t})}}
            return{label:'自定义 '+f+' ~ '+t,list:days.filter(function(x){return x.d>=f&&x.d<=t})}}
          if(mode==='d7'){var f7=isoD(new Date(Date.now()-6*864e5));
            return{label:'近7天（'+f7+' ~ '+today+'）',list:days.filter(function(x){return x.d>=f7})}}
          if(mode==='d30'){var f30=isoD(new Date(Date.now()-29*864e5));
            return{label:'近30天（'+f30+' ~ '+today+'）',list:days.filter(function(x){return x.d>=f30})}}
          return{label:'总计（全部历史）',list:days.slice()};
        }
        function render(){
          todayStr=isoD(new Date());
          var hd=needDay(),list,label,unit,rows,loading=false,peak=null;
          if(hd!=null){
            label=hd+' · 按小时';unit='按小时';
            list=days.filter(function(x){return x.d===hd});
            if(hourDay===hd&&hourData){
              rows=hourData.slice().map(function(x){
                return{label:p2(x.h)+':00–'+p2(x.h<23?x.h+1:24)+':00',rounds:x.rounds,v:x.v,i:x.i,o:x.o,c:x.c,rate:(x.c>0&&x.i>0)?x.c/x.i:0,sk:'H'+p2(x.h),isToday:hd===todayStr}});
              hourData.forEach(function(x){if(!peak||x.v>peak.v)peak=x});
            }else{rows=null;loading=true}
          }else{
            var rg=dailyList();
            label=rg.label;unit='按天';
            list=rg.list;
            rows=list.slice().map(function(x){
              return{label:x.d,rounds:x.rounds,v:x.v,i:x.i,o:x.o,c:x.c,rate:(x.c>0&&x.i>0)?x.c/x.i:0,sk:x.d,isToday:x.d===todayStr}});
          }
          var v=0,i=0,o=0,c=0,r=0;
          list.forEach(function(x){v+=x.v;i+=x.i;o+=x.o;c+=x.c;r+=x.rounds});
          $('btl').textContent=label.toUpperCase()+' · 总 TOKEN';
          $('bt').textContent=fmt(v);
          $('gin').textContent=fmt(i);$('gout').textContent=fmt(o);$('gc').textContent=fmt(c);
          if(c>0&&i>0){$('gr').textContent=(c/i*100).toFixed(1)+'%';$('gr').title=''}
          else{$('gr').textContent='—';$('gr').title='供应商未回报缓存数据时无法计算命中率'}
          $('grd').textContent=r;
          $('gds').textContent='覆盖 '+list.length+' 天';
          if(peak!=null){$('gavgk').textContent='峰值时段';$('gavg').textContent=p2(peak.h)+':00–'+p2(peak.h<23?peak.h+1:24)+':00'}
          else{$('gavgk').textContent='日均';$('gavg').textContent=list.length>0?fmt(Math.round(v/list.length)):'0'}
          var sorted=rows?sortRows(rows,dSort):null;
          var shown=sorted?sorted.slice(0,90):null;
          $('dsub').textContent=unit+(unit==='按天'?(sorted&&sorted.length>90?(' · 共 '+sorted.length+' 天，仅显示最近 90 天'):''):'');
          var tb=$('tb');tb.innerHTML='';
          if(loading){tb.innerHTML='<tr class="empty"><td colspan="8">加载中…</td></tr>';return}
          if(!shown||shown.length===0){tb.innerHTML='<tr class="empty"><td colspan="8">该范围内暂无用量记录 —— 与角色对话一轮后，用量会自动出现在这里</td></tr>';return}
          var mx=1;shown.forEach(function(x){if(x.v>mx)mx=x.v});
          shown.forEach(function(x){
            var tr=document.createElement('tr');
            if(x.isToday)tr.className='is-today';
            tr.innerHTML='<td><span class="bdg">'+x.label+'</span></td><td>'+x.rounds+'</td><td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td>'+
              '<td>'+((x.c>0&&x.i>0)?(x.c/x.i*100).toFixed(1)+'%':'—')+'</td><td><b>'+fmt(x.v)+'</b></td>'+
              '<td><div class="barw"><div class="bar" style="width:'+Math.max(1.5,x.v/mx*100).toFixed(1)+'%"></div></div></td>';
            tb.appendChild(tr);
          });
        }
        function renderRecords(recs){
          lastRecs=recs||[];
          var rb=$('rb');
          if(lastRecs.length===0){rb.innerHTML='<tr class="empty"><td colspan="10">暂无记录 —— 发起对话后，每轮用量会实时记录于此</td></tr>';return}
          var rows=sortRows(lastRecs.map(function(x){
            return{t:x.t,m:x.m,s:x.s,h:x.h,n:x.n,ch:x.ch,i:x.i,o:x.o,c:x.c,v:x.v,co:x.co,con:(parseFloat(x.co)||0)};
          }),rSort);
          var mx=Math.max.apply(null,rows.map(function(x){return x.v}));
          rb.innerHTML=rows.map(function(x){
            var mdl=x.m&&x.m.length>0?x.m:'—';
            return '<tr><td><span class="bdg">'+x.t+'</span></td><td><span class="mdl" title="'+(mdl==='—'?'旧版本记录未写入模型信息':esc(mdl))+'">'+esc(mdl)+'</span></td>'+
              '<td><span class="mdl">'+esc(x.s&&x.s.length?x.s:'未知')+'</span></td><td><span class="mdl" title="'+esc((x.h&&x.h.length?x.h+' · ':'')+(x.n&&x.n.length?x.n:''))+'">'+esc(x.ch&&x.ch.length?x.ch:'未知')+'</span></td>'+
              '<td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td><td><b>'+fmt(x.v)+'</b></td><td class="cost">'+fmtC(x.co)+'</td>'+
              '<td><div class="sh"><span>'+Math.max(1,Math.round(x.v/mx*100))+'%</span><div class="barw"><div class="bar" style="width:'+Math.max(1.5,x.v/mx*100).toFixed(1)+'%"></div></div></div></td></tr>';
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
          if(rows.length===0){ab.innerHTML='<tr class="empty"><td colspan="8">该范围内暂无记录 —— 与角色对话后自动汇总</td></tr>';return}
          var tv=Math.max(1,ana.total.v||0);
          ab.innerHTML=rows.map(function(x){
            var pc=x.v/tv*100;
            return '<tr><td><span class="bdg">'+esc(x.k)+'</span></td><td>'+x.r+'</td><td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td><td><b>'+fmt(x.v)+'</b></td><td class="cost">'+fmtC(x.cost)+'</td>'+
              '<td><div class="sh"><span>'+pc.toFixed(1)+'%</span><div class="barw"><div class="bar" style="width:'+Math.max(1.5,pc).toFixed(1)+'%"></div></div></div></td></tr>';
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
          syncAria();
          render();
        }
        function tickStats(){
          fetch('/stats',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            if(d.character)CUR=d.character;
            var m=String(d.model||'').replace(/LanguageModel|Model$/,'');
            $('meta').textContent=d.character+' · '+m;
            $('sc').textContent=d.character;$('sm').textContent=m;$('smc').textContent=d.channel||'—';
            $('sm').title=String(d.model||'');$('smc').title=String(d.channel||'');
            var b=$('busy');b.textContent=d.busy?'生成中…':'空闲';
            $('meter').classList.toggle('busy',!!d.busy);
            $('sr').textContent=d.rounds;$('st').textContent=fmt(d.total);
            $('si').textContent=fmt(d.input);$('so').textContent=fmt(d.output);$('scd').textContent=fmt(d.cached);
            $('scost').textContent=fmtC(d.costs?d.costs.session:null);
            $('scost').title='会话费用（价格规则见页面底部）';
            var er=d.errors||{};
            $('serr').textContent=fmt(er.today||0);
            $('serr').title='今日出错标记「出错：」累计 '+(er.total||0)+' 次'+(er.last?' · 最近：'+er.last:'');
            var bal=d.balance||{};
            $('sbal').textContent=bal.current&&bal.current.length?bal.current:'—';
            $('sbal').title='余额监测源 '+(bal.sources||0)+' 个 / 探测成功 '+(bal.ok||0)+'（详情见下方余额面板）';
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
        document.querySelectorAll('.tab').forEach(function(t){t.addEventListener('click',function(){mode=t.dataset.r;syncTabs();syncHash();tickHist();tickAnalytics()})});
        document.querySelectorAll('.tab2').forEach(function(t){
          if(t.dataset.d===adim)t.classList.add('on');
          t.addEventListener('click',function(){
            adim=t.dataset.d;
            document.querySelectorAll('.tab2').forEach(function(x){x.classList.toggle('on',x.dataset.d===adim)});
            syncAria();syncHash();renderAnalytics();
          });
        });
        function setScope(s){
          scope=s;try{localStorage.setItem('tstatsScope',s)}catch(e){}
          document.querySelectorAll('.scp').forEach(function(x){x.classList.toggle('on',x.dataset.s===s)});
          $('rsub').textContent=(s==='all'?'全部角色（汇总日志）':'本角色')+' · 最近15条 · 含模型/来源/渠道/费用';
          syncAria();syncHash();
          tickRecords();tickAnalytics();
        }
        document.querySelectorAll('.scp').forEach(function(x){x.addEventListener('click',function(){setScope(x.dataset.s)})});
        setScope(scope);
        $('go').addEventListener('click',function(){
          if(!$('df').value)$('df').value=isoD(new Date(Date.now()-29*864e5));
          if(!$('dt').value)$('dt').value=isoD(new Date());
          mode='custom';syncTabs();syncHash();tickHist();tickAnalytics();
        });
        $('df').value=isoD(new Date(Date.now()-29*864e5));
        $('dt').value=isoD(new Date());
        // 键盘可达：胶囊与排序表头可 Tab 聚焦，Enter/空格触发
        document.querySelectorAll('.tab,.tab2,.scp').forEach(function(el){
          el.setAttribute('tabindex','0');el.setAttribute('role','button');
          el.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();el.click()}});
        });
        // 表头点击排序（明细表 tb / 最近轮次表 rb），同列再点反向
        function toggleSort(th){
          var tbl=th.closest('table'),isD=tbl.querySelector('tbody').id==='tb',st=isD?dSort:rSort,k=th.dataset.k;
          if(st.k===k)st.d=-st.d;else{st.k=k;st.d=-1}
          tbl.querySelectorAll('th.srt').forEach(function(x){x.classList.remove('sa','sd');
            if(x.dataset.k===st.k)x.classList.add(st.d>0?'sa':'sd')});
          if(isD)render();else renderRecords(lastRecs);
        }
        document.querySelectorAll('th.srt').forEach(function(th){
          th.setAttribute('tabindex','0');th.setAttribute('role','button');
          th.addEventListener('click',function(){toggleSort(th)});
          th.addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();toggleSort(th)}});
        });
        function tickBalance(){
          fetch('/balance',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            var bc=$('bcards'),ss=d.sources||[];
            $('bsub').textContent='监测源 '+ss.length+' 个 · 每 '+(d.interval||30)+' 分钟自动探测';
            if(ss.length===0){bc.innerHTML='<div class="bcard"><div class="bn"><span class="nm">暂无监测源</span></div><div class="bc">在模块配置页「余额监测」添加（检测到的渠道可一键加入）</div></div>';return}
            bc.innerHTML=ss.map(function(s){
              var tag=s.initial?'初始额度':(s.type==='preset'?'预设扣减':(s.type==='custom'?'自定义':'自动探测'));
              var tc=s.initial?' preset':(s.type==='preset'?' preset':(s.type==='custom'?' custom':''));
              var val=s.ok?(''+s.balance+' '+(s.currency||'')):('失败：'+(s.msg||'未知'));
              return '<div class="bcard'+(s.ok?'':' err')+'"><div class="bn"><span class="nm">'+esc(s.name)+'</span><span class="btag'+tc+'">'+tag+'</span></div>'+
                '<div class="bv">'+esc(val)+'</div>'+
                '<div class="bc">'+(s.enabled?'':'已停用 · ')+(s.at&&s.at.length?'更新于 '+esc(s.at):'尚未探测')+'</div>'+
                (s.ok&&s.msg&&s.msg.length?'<div class="bm" title="'+esc(s.msg)+'">'+esc(s.msg)+'</div>':'')+'</div>';
            }).join('');
          }).catch(function(){});
        }
        $('bref').addEventListener('click',function(){
          $('bref').textContent='探测中…';
          fetch('/balance?refresh=1',{cache:'no-store'}).then(function(r){return r.json()}).then(function(){tickBalance();$('bref').textContent='立即刷新'}).catch(function(){$('bref').textContent='立即刷新'});
        });
        $('bref').addEventListener('keydown',function(e){if(e.key==='Enter'||e.key===' '){e.preventDefault();$('bref').click()}});
        tickStats();tickHist();tickRecords();tickAnalytics();tickPricing();tickBalance();syncTabs();syncAria();syncHash();
        setInterval(tickStats,1000);setInterval(tickHist,3000);setInterval(tickRecords,5000);
        setInterval(tickAnalytics,15000);setInterval(tickPricing,60000);setInterval(tickBalance,60000);
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
    [Description("圆心数值的统计范围：session=本次会话，today=今天，d7=近7天，d30=近30天，total=累计（点击挂件圆环会弹出范围选择条，临时切换并记忆）")]
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

    [DisplayName("入场动画")]
    [Description("仅在激活角色时播放：屏幕中央绽开圆环风探查之眼（非仿真，全由圆构成）并飞至挂件位置（页面刷新/切换查看不播；系统开启“减少动态效果”时自动跳过，关闭后任何途径都不播）")]
    public bool EntranceAnimation { get; set; } = true;

    [DisplayName("恩情模式")]
    [Description("开启后每次激活角色都播放恩情动画：满屏恩情文本如礼花般从炸点飞散（高密度、允许重复）+惊雷闪电+礼花辉光（约3.9秒，不拦截任何点击）；首次使用插件时无视『入场动画』开关强制观看一次（系统开启“减少动态效果”时自动跳过并视为已观看）")]
    public bool GratitudeMode { get; set; } = true;

    [DisplayName("余额轮询间隔(分钟)")]
    [Description("余额监测源的自动探测间隔（分钟，最小 5）；配置页「余额监测」与看板余额面板均可手动立即探测")]
    public int BalanceIntervalMinutes { get; set; } = 30;
}
