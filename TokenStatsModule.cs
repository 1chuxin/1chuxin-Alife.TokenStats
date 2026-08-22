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
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using ElectronNET.API;
using Microsoft.Extensions.Logging;

namespace OneChuxin.TokenStats;

[Module("Token用量看板",
    "在『展开思考』开关旁注入圆环 Token 用量挂件：圆心紧凑显示用量（如 9.9K），点击切换 本次/今天/近7天/近30天/累计，悬停展开详情卡片；详情页支持按小时/按天的历史明细与逐轮模型标记。用量记录持久化于 storage/Tokenlog/usage-log.jsonl（可在配置页按时间段精确到秒清理），不修改客户端文件；随角色激活开启，停止角色自动移除挂件（会话清零、历史保留）。",
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

    // 历史用量（按天聚合，键 yyyy-MM-dd 升序），来源/落盘于插件目录 usage-log.jsonl；
    // hours 为单天 24 小时桶（懒分配，单天详情页按小时显示用）
    readonly SortedDictionary<string, DayStat> days = new();
    readonly Dictionary<string, DayStat[]> hours = new();
    string logFile = "";
    bool ioWarned;

    CancellationTokenSource? serverCts;
    int actualPort;
    BrowserWindow? mainWindow;
    int lastInjectFrame = -1000;
    string overlayState = "pending";   // ok/injected/nopage/error: 最近一次注入探测结果

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
    }

    protected override Task OnAwake()
    {
        ChatBot.TokenUsed += OnTokenUsed;
        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        sessionStart = DateTime.Now;
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
                        "(function(){try{window.__tstatsTeardown&&window.__tstatsTeardown()}catch(e){}return 'removed'})()"));
                }
                catch { }
            });
        }
        return Task.CompletedTask;
    }

    void OnTokenUsed(TokenUsage usage)
    {
        DateTime now = DateTime.Now;
        string model = JsonEscape(ResolveModelName());
        string line = $"{{\"t\":\"{now:yyyy-MM-dd'T'HH:mm:ss.fff}\",\"v\":{usage.Total},\"i\":{usage.Input},\"o\":{usage.Output},\"c\":{usage.Cached},\"m\":\"{model}\"}}";
        lock (sync)
        {
            total += usage;
            lastRound = usage;
            rounds++;
            string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!days.TryGetValue(day, out DayStat ds)) days[day] = ds = new DayStat();
            ds.Rounds++; ds.V += usage.Total; ds.In += usage.Input; ds.Out += usage.Output; ds.Cached += usage.Cached;
            if (!hours.TryGetValue(day, out DayStat[] hs)) hours[day] = hs = new DayStat[24];
            DayStat hr = hs[now.Hour] ??= new DayStat();
            hr.Rounds++; hr.V += usage.Total; hr.In += usage.Input; hr.Out += usage.Output; hr.Cached += usage.Cached;
            if (logFile.Length > 0)
            {
                try { lock (fileIoLock) File.AppendAllText(logFile, line + "\n"); ioWarned = false; }
                catch (Exception ex) { WarnIoOnce(ex); }
            }
        }
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

    internal sealed class UsageRec
    {
        public DateTime T;
        public long V, I, O, C;
        public string? M;   // 该轮使用的模型名（旧记录可能为空）
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
        try { lock (fileIoLock) File.Delete(LocateDataFile()); } catch { }
    }

    // 重写日志：剔除时间区间（含端点，精确到秒）内的记录，返回删除条数
    internal static int ClearRecords(DateTime from, DateTime to)
    {
        try
        {
            string path = LocateDataFile();
            List<UsageRec> recs = ReadUsageRecords(path);
            if (recs.Count == 0) return 0;
            List<string> kept = new(recs.Count);
            int removed = 0;
            foreach (UsageRec r in recs)
            {
                if (r.T >= from && r.T <= to)
                    removed++;
                else
                    kept.Add($"{{\"t\":\"{r.T:yyyy-MM-dd'T'HH:mm:ss.fff}\",\"v\":{r.V},\"i\":{r.I},\"o\":{r.O},\"c\":{r.C},\"m\":\"{JsonEscape(r.M ?? "")}\"}}");
            }
            if (removed > 0)
                lock (fileIoLock) File.WriteAllLines(path, kept);
            return removed;
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
        logFile = LocateDataFile();
        lock (sync) { days.Clear(); hours.Clear(); }
        try
        {
            foreach (UsageRec rec in ReadUsageRecords(logFile))
            {
                string day = rec.T.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                lock (sync)
                {
                    if (!days.TryGetValue(day, out DayStat ds)) days[day] = ds = new DayStat();
                    ds.Rounds++; ds.V += rec.V; ds.In += rec.I; ds.Out += rec.O; ds.Cached += rec.C;
                    if (!hours.TryGetValue(day, out DayStat[] hs)) hours[day] = hs = new DayStat[24];
                    DayStat hr = hs[rec.T.Hour] ??= new DayStat();
                    hr.Rounds++; hr.V += rec.V; hr.In += rec.I; hr.Out += rec.O; hr.Cached += rec.C;
                }
            }
            long totV = 0; int totR = 0;
            lock (sync) foreach (DayStat d in days.Values) { totV += d.V; totR += d.Rounds; }
            logger.LogInformation($"Token用量看板：已加载历史用量 {days.Count} 天 / {totR} 轮 / {totV} Token（{logFile}）");
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

    // 取真实模型名：官方 OpenAI 插件为 Configuration.modelId 字段；灵枢(LanguageModelRouter)为
    // Configuration.Groups[0].ModelId（主组，容灾切换时可能与实际使用的组不同）。均为反射通用读取，
    // 不依赖具体插件类型；都取不到时回退类型名。
    string ResolveModelName()
    {
        try
        {
            object? lm = ChatBot.LanguageModel;
            if (lm == null) return "未配置";
            string? direct = ReadStringMember(lm, "ModelId") ?? ReadStringMember(lm, "ModelName") ?? ReadStringMember(lm, "modelId");
            if (direct != null) return direct;
            object? config = lm.GetType().GetProperty("Configuration")?.GetValue(lm)
                ?? lm.GetType().GetField("Configuration")?.GetValue(lm);
            if (config != null)
            {
                string? inConfig = ReadStringMember(config, "modelId") ?? ReadStringMember(config, "ModelId") ?? ReadStringMember(config, "ModelName");
                if (inConfig != null) return inConfig;
                object? groups = config.GetType().GetProperty("Groups")?.GetValue(config);
                if (groups is System.Collections.IEnumerable list)
                {
                    foreach (object g in list)
                    {
                        string? id = ReadStringMember(g, "ModelId") ?? ReadStringMember(g, "modelId");
                        if (id != null) return id;
                    }
                }
            }
            return lm.GetType().Name;
        }
        catch { return "未知"; }
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
                    await RespondAsync(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(BuildRecordsJson(n)), cancellationToken);
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
        lock (sync)
        {
            t = total;
            l = lastRound;
            r = rounds;
        }
        int elapsed = Math.Max(0, (int)(DateTime.Now - sessionStart).TotalSeconds);
        string model = ResolveModelName();
        string character = Character?.Name ?? "?";
        bool busy;
        try { busy = ChatBot.IsChatOccupied; } catch { busy = false; }

        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        StringBuilder json = new(640);
        json.Append('{');
        json.Append($"\"character\":\"{JsonEscape(character)}\"");
        json.Append($",\"model\":\"{JsonEscape(model)}\"");
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
        json.Append("}}");
        return json.ToString();
    }

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

    // 最近 n 轮原始记录（倒序，含逐轮模型名），供详情页"最近对话"面板
    string BuildRecordsJson(int n)
    {
        StringBuilder json = new(2048);
        json.Append("{\"recs\":[");
        try
        {
            List<UsageRec> recs = ReadUsageRecords(LocateDataFile());
            int written = 0;
            for (int i = recs.Count - 1; i >= 0 && written < n; i--)
            {
                UsageRec r = recs[i];
                if (written > 0) json.Append(',');
                written++;
                json.Append($"{{\"t\":\"{r.T:yyyy-MM-dd HH:mm:ss}\",\"v\":{r.V},\"i\":{r.I},\"o\":{r.O},\"c\":{r.C},\"m\":\"{JsonEscape(r.M ?? "")}\"}}");
            }
        }
        catch { }
        json.Append("]}");
        return json.ToString();
    }

    static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    // 在主窗口页面注入/校验挂件：已存在且健康则返回 ok+矩形，否则（重新）构建。
    // 幂等：__tstatsTeardown 先清理旧实例（跨激活/换端口安全），Shadow DOM 隔离样式。
    async Task EnsureOverlayAsync()
    {
        try
        {
            if (mainWindow == null)
            {
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
                .Replace("__RING__", Configuration.RingSize.ToString())
                .Replace("__GAP__", Configuration.GapBesideSwitch.ToString())
                .Replace("__CARDW__", Configuration.Width.ToString())
                .Replace("__CARDH__", Configuration.Height.ToString());
            Task<string> call = IpcAsync(() => main.WebContents.ExecuteJavaScriptAsync<string>(js));
            if (await Task.WhenAny(call, Task.Delay(2500)) != call)
            {
                overlayState = "timeout";
                return;
            }
            overlayState = call.Status == TaskStatus.RanToCompletion
                ? (call.Result ?? "").Trim().Trim('"')
                : "faulted";
            if (overlayState == "nopage")
                return;
            if (overlayState.StartsWith("injected", StringComparison.Ordinal))
                logger.LogInformation($"Token用量看板挂件已注入主窗口页面（停靠『展开思考』开关旁），详情页 http://127.0.0.1:{actualPort}/");
        }
        catch (Exception ex)
        {
            overlayState = "error:" + ex.Message;
            logger.LogWarning(ex, "Token用量看板：注入失败");
        }
    }

    // 挂件注入脚本。定位：文本含“展开思考”的叶子元素 + 其父级内的 .ant-switch；
    // 悬停展开/收起为纯本地DOM（无窗口resize、无IPC）；数据每秒 fetch 本地 /stats。
    // 圆心最多4字符（1.2K/999K/9.9M）；点击圆环或卡片范围胶囊切换 本次/今天/7天/30天/累计，
    // 卡片网格内容随范围联动（时长/模型/最近一轮固定为会话数据）。
    const string OverlayJs = """
        (function(){
          var PORT=__PORT__,RING=__RING__,GAP=__GAP__,CW=__CARDW__,CH=__CARDH__;
          if(!document.body)return 'nopage';
          var ex=document.getElementById('tstats-root');
          if(ex&&window.__tstatsAlive&&ex.dataset.tsp==String(PORT)){
            var er=ex.getBoundingClientRect();
            return 'ok '+Math.round(er.left)+','+Math.round(er.top)+','+Math.round(er.width)+','+Math.round(er.height);
          }
          if(window.__tstatsTeardown){try{window.__tstatsTeardown()}catch(e){}}
          var root=document.createElement('div');
          root.id='tstats-root';
          root.dataset.tsp=String(PORT);
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
          '.v.inp{color:#2f6fd8}.v.out{color:#db2777}.v.cache{color:#d97706}.v.rate{color:#7c3aed}'+
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
          '<div class="item"><span class="k">轮数</span><span class="v" id="v2">0</span></div>'+
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
          var fail=0, hideTimer=null, placeTimer=null, pollTimer=null, ro=null;
          var ORDER=['session','today','d7','d30','total'];
          var RN={session:'本次',today:'今天',d7:'7天',d30:'30天',total:'累计'};
          var sel=null;
          try{var s0=localStorage.getItem('tstatsRange');if(ORDER.indexOf(s0)>=0)sel=s0}catch(e){}
          function setRange(k){sel=k;try{localStorage.setItem('tstatsRange',sel)}catch(e){}tick()}
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
            fetch('http://127.0.0.1:'+PORT+'/stats',{cache:'no-store'}).then(r=>r.json()).then(d=>{
              fail=0;place();
              if(!sel)sel=(ORDER.indexOf(d.ringDef)>=0)?d.ringDef:'session';
              var R=d.ranges||{}, rg=R[sel]||{v:0,i:0,o:0,c:0,r:0};
              q('#c').textContent=d.character;
              q('#rgn').textContent=RN[sel];
              q('#v1').textContent=fmt(rg.v);
              q('#v2').textContent=rg.r;
              q('#v3').textContent=fmt(rg.i);
              q('#v4').textContent=fmt(rg.o);
              q('#v5').textContent=fmt(rg.c);
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
            if(!sel)sel='session';
            setRange(ORDER[(ORDER.indexOf(sel)+1)%ORDER.length]);
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
          q('#lnk').href='http://127.0.0.1:'+PORT+'/';
          place();tick();
          placeTimer=setInterval(place,1000);
          pollTimer=setInterval(tick,1000);
          addEventListener('resize',place);
          try{ro=new ResizeObserver(place)}catch(e){}
          window.__tstatsAlive=true;
          window.__tstatsTeardown=function(){
            window.__tstatsAlive=false;
            clearInterval(placeTimer);clearInterval(pollTimer);clearTimeout(hideTimer);
            removeEventListener('resize',place);
            try{ro&&ro.disconnect()}catch(e){}
            root.remove();delete window.__tstatsTeardown;
          };
          var r0=root.getBoundingClientRect();
          return 'injected '+Math.round(r0.left)+','+Math.round(r0.top)+','+Math.round(r0.width)+','+Math.round(r0.height);
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
        .c-metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:8px;margin-top:14px}
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
            <div class="cm"><span>会话时长</span><strong id="se">00:00:00</strong></div>
            <div class="cm"><span>会话轮次</span><strong id="sr">0</strong></div>
            <div class="cm"><span>会话累计</span><strong id="st">0</strong></div>
            <div class="cm"><span>会话输入</span><strong id="si">0</strong></div>
            <div class="cm"><span>会话输出</span><strong id="so">0</strong></div>
            <div class="cm"><span>会话缓存</span><strong id="scd">0</strong></div>
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
          <div class="p-head"><span class="p-dot"></span><span class="p-title">用量明细</span><span class="p-sub" id="dsub">按天</span></div>
          <table>
            <thead><tr><th>时间</th><th>轮次</th><th>输入</th><th>输出</th><th>缓存</th><th>命中率</th><th>合计</th><th style="width:22%">用量条</th></tr></thead>
            <tbody id="tb"></tbody>
          </table>
        </section>
        <section class="panel">
          <div class="p-head"><span class="p-dot"></span><span class="p-title">最近对话轮次</span><span class="p-sub">每轮原始记录 · 最近15条 · 含模型标记</span></div>
          <table>
            <thead><tr><th>时间</th><th>模型</th><th>输入</th><th>输出</th><th>缓存</th><th>合计</th><th style="width:24%">占比</th></tr></thead>
            <tbody id="rb"></tbody>
          </table>
        </section>
        <div class="foot" id="foot">加载中…</div>
        </div>
        <script>
        var days=[], hourData=null, hourDay=null, mode='today', todayStr='';
        var $=function(id){return document.getElementById(id)};
        var fmt=function(n){return Number(n||0).toLocaleString('zh-CN')};
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
          if(!recs||recs.length===0){rb.innerHTML='<tr class="empty"><td colspan="7">暂无记录</td></tr>';return}
          var mx=Math.max.apply(null,recs.map(function(x){return x.v}));
          rb.innerHTML=recs.map(function(x){
            var mdl=x.m&&x.m.length>0?x.m:'—';
            return '<tr><td><span class="bdg">'+x.t+'</span></td><td><span class="mdl" title="'+(mdl==='—'?'旧版本记录未写入模型信息':mdl)+'">'+mdl+'</span></td><td>'+fmt(x.i)+'</td><td>'+fmt(x.o)+'</td><td>'+fmt(x.c)+'</td><td><b>'+fmt(x.v)+'</b></td>'+
              '<td><div class="barw"><div class="bar" style="width:'+Math.max(1.5,x.v/mx*100).toFixed(1)+'%"></div></div></td></tr>';
          }).join('');
        }
        function syncTabs(){
          document.querySelectorAll('.tab').forEach(function(t){t.classList.toggle('on',t.dataset.r===mode)});
          $('cst').classList.toggle('on',mode==='custom');
          render();
        }
        function tickStats(){
          fetch('/stats',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            var m=String(d.model||'').replace(/LanguageModel|Model$/,'');
            $('meta').textContent=d.character+' · '+m;
            $('sc').textContent=d.character;$('sm').textContent=m;
            var b=$('busy');b.textContent=d.busy?'生成中…':'空闲';
            $('meter').classList.toggle('busy',!!d.busy);
            $('sr').textContent=d.rounds;$('st').textContent=fmt(d.total);
            $('si').textContent=fmt(d.input);$('so').textContent=fmt(d.output);$('scd').textContent=fmt(d.cached);
            var s=Math.max(0,d.elapsed|0);
            $('se').textContent=p2((s/3600)|0)+':'+p2(((s%3600)/60)|0)+':'+p2(s%60);
            if(d.rounds>0)$('slast').innerHTML='最近一轮：输入 <b>'+fmt(d.lastInput)+'</b> · 输出 <b>'+fmt(d.lastOutput)+'</b>'+(d.lastCached>0?' · 缓存 <b>'+fmt(d.lastCached)+'</b>':'');
            if(d.logFile)$('foot').textContent='数据文件：'+d.logFile+'（可在插件配置页按时间段清理） · 单天范围自动按小时显示，多天范围按天显示 · 圆环挂件：点击切换统计范围';
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
          fetch('/records?n=15',{cache:'no-store'}).then(function(r){return r.json()}).then(function(d){
            renderRecords(d.recs);
          }).catch(function(){});
        }
        document.querySelectorAll('.tab').forEach(function(t){t.addEventListener('click',function(){mode=t.dataset.r;syncTabs();tickHist()})});
        $('go').addEventListener('click',function(){
          if(!$('df').value)$('df').value=isoD(new Date(Date.now()-29*864e5));
          if(!$('dt').value)$('dt').value=isoD(new Date());
          mode='custom';syncTabs();tickHist();
        });
        $('df').value=isoD(new Date(Date.now()-29*864e5));
        $('dt').value=isoD(new Date());
        tickStats();tickHist();tickRecords();syncTabs();
        setInterval(tickStats,1000);setInterval(tickHist,3000);setInterval(tickRecords,5000);
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
