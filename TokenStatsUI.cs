using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace OneChuxin.TokenStats;

// 模块配置页面板（ModuleDetailView 经 DynamicComponent 挂载）。
// 布局：用量快照（直接读 usage-log.jsonl，未激活可看）+ 偏好（圆环统计范围快捷选择）
// + 数据管理（清空历史，二次确认）+ 使用指引；端口/尺寸/间距等调优参数收进"高级设置"折叠块，
// 配置表单经 DefaultUI 保留其中，保存仍用页面底部"应用到全局/角色"按钮。
public class TokenStatsUI : ModuleUIBase<TokenStatsModule, TokenStatsConfig>, IDisposable
{
    static readonly (string Key, string Name)[] Ranges =
    {
        ("session", "本次"),
        ("today", "今天"),
        ("d7", "近7天"),
        ("d30", "近30天"),
        ("total", "累计"),
    };

    [Inject]
    IJSRuntime JS { get; set; } = null!;

    Timer? refreshTimer;
    string html = "";
    string clearFrom = "";
    string clearTo = "";
    string? armedAction;   // "range"/"all"：已进入二次确认状态
    DateTime armedAt;
    string statusMsg = "";
    DateTime msgAt;

    protected override void OnInitialized()
    {
        Rebuild();
        refreshTimer = new Timer(_ => _ = InvokeAsync(() =>
        {
            if (armedAction != null && (DateTime.Now - armedAt).TotalSeconds > 4)
                armedAction = null;
            if (statusMsg.Length > 0 && (DateTime.Now - msgAt).TotalSeconds > 8)
                statusMsg = "";
            Rebuild();
            StateHasChanged();
        }), null, 3000, 3000);
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        int seq = 0;
        b.AddContent(seq++, (MarkupString)html);

        // 偏好：圆环统计范围快捷选择
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-sec");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sdot");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-stitle");
        b.AddContent(seq++, "偏好");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-ssub");
        b.AddContent(seq++, "圆环显示的统计范围（挂件上点击圆环也可临时切换）");
        b.CloseElement();
        b.CloseElement(); // sec

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-pills");
        foreach ((string Key, string Name) rg in Ranges)
        {
            bool on = Configuration?.RingRange == rg.Key;
            b.OpenElement(seq++, "button");
            b.AddAttribute(seq++, "class", "tsui-pill" + (on ? " on" : ""));
            b.AddAttribute(seq++, "type", "button");
            b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => SetRingRange(rg.Key)));
            b.AddContent(seq++, rg.Name);
            b.CloseElement();
        }
        b.CloseElement();

        // 数据管理：按时间段清除 / 全部清空（均二次确认）
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-sec");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sdot");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-stitle");
        b.AddContent(seq++, "数据");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-ssub");
        b.AddContent(seq++, "清除指定时间段（精确到秒）或全部用量日志，不影响当前会话统计");
        b.CloseElement();
        b.CloseElement(); // sec

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-clear");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "datetime-local");
        b.AddAttribute(seq++, "step", "1");
        b.AddAttribute(seq++, "class", "tsui-date");
        b.AddAttribute(seq++, "value", clearFrom);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => clearFrom = v, ""));
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-arrow");
        b.AddContent(seq++, "→");
        b.CloseElement();
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "datetime-local");
        b.AddAttribute(seq++, "step", "1");
        b.AddAttribute(seq++, "class", "tsui-date");
        b.AddAttribute(seq++, "value", clearTo);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => clearTo = v, ""));
        b.CloseElement();
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn" + (armedAction == "range" ? " danger" : ""));
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => ArmOrExecute("range")));
        b.AddContent(seq++, armedAction == "range" ? "确认清除该时间段？" : "清除所选时间段");
        b.CloseElement();
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn" + (armedAction == "all" ? " danger" : ""));
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => ArmOrExecute("all")));
        b.AddContent(seq++, armedAction == "all" ? "确认清空全部历史？" : "清空全部历史");
        b.CloseElement();
        if (statusMsg.Length > 0)
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-status");
            b.AddContent(seq++, statusMsg);
            b.CloseElement();
        }
        b.CloseElement(); // clear row

        // 高级设置：调优参数折叠（默认收起），表单由 DefaultUI 提供
        b.OpenElement(seq++, "details");
        b.AddAttribute(seq++, "class", "tsui-adv");
        b.OpenElement(seq++, "summary");
        b.AddContent(seq++, "高级设置（HTTP 端口 / 尺寸 / 间距等调优参数）");
        b.CloseElement();
        if (DefaultUI != null)
            b.AddContent(seq++, DefaultUI);
        b.CloseElement();
    }

    void SetRingRange(string key)
    {
        if (Configuration == null)
            return;
        Configuration.RingRange = key;
        if (Module is IConfigurable configurable)
            configurable.Configuration = Configuration;
        _ = TryClearOverlayMemoryAsync();
        Rebuild();
        StateHasChanged();
    }

    async Task TryClearOverlayMemoryAsync()
    {
        // 挂件优先用 localStorage 记忆的范围；清除后以这里的配置为准
        try { await JS.InvokeVoidAsync("localStorage.removeItem", "tstatsRange"); } catch { }
    }

    void ArmOrExecute(string action)
    {
        if (armedAction != action)
        {
            armedAction = action;
            armedAt = DateTime.Now;
            StateHasChanged();
            return;
        }
        armedAction = null;
        try
        {
            if (action == "all")
            {
                if (Module != null) Module.ResetHistory();
                else TokenStatsModule.DeleteDataFile();
                statusMsg = "已清空全部历史";
            }
            else if (clearFrom.Length == 0 || clearTo.Length == 0)
            {
                statusMsg = "请先选择起止时间";
            }
            else if (!DateTime.TryParse(clearFrom, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime f) ||
                     !DateTime.TryParse(clearTo, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
            {
                statusMsg = "时间格式无法识别";
            }
            else
            {
                if (f > t) (f, t) = (t, f);
                int removed = Module != null ? Module.ResetHistory(f, t) : TokenStatsModule.ClearRecords(f, t);
                statusMsg = $"已清除 {removed} 条记录（{f:yyyy-MM-dd HH:mm:ss} ~ {t:yyyy-MM-dd HH:mm:ss}）";
            }
        }
        catch (Exception ex)
        {
            statusMsg = "清除失败：" + ex.Message;
        }
        msgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void Rebuild()
    {
        try { html = BuildHtml(Module); }
        catch { }
    }

    static string Fmt(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string BuildHtml(TokenStatsModule? module)
    {
        string dataFile = TokenStatsModule.LocateDataFile();
        List<TokenStatsModule.UsageRec> recs = TokenStatsModule.ReadUsageRecords(dataFile);

        Dictionary<string, (long V, int R)> daySums = new();
        foreach (TokenStatsModule.UsageRec rec in recs)
        {
            string day = rec.T.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            (long V, int R) cur = daySums.TryGetValue(day, out var v) ? v : (0, 0);
            daySums[day] = (cur.V + rec.V, cur.R + 1);
        }

        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        long SumSince(string from) => daySums.Where(kv => kv.Key.CompareTo(from) >= 0).Sum(kv => kv.Value.V);
        int RoundsSince(string from) => daySums.Where(kv => kv.Key.CompareTo(from) >= 0).Sum(kv => kv.Value.R);
        long todayV = SumSince(today);
        long d7 = SumSince(DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        long d30 = SumSince(DateTime.Now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        long allV = daySums.Values.Sum(x => x.V);

        StringBuilder s = new(4096);
        s.Append("<style>");
        s.Append(".tsui-wrap{margin:2px 0 18px;font-family:\"Segoe UI\",system-ui,\"Microsoft YaHei\",sans-serif;color:#23272f}");
        s.Append(".tsui-bar{display:flex;align-items:center;gap:9px;flex-wrap:wrap;background:linear-gradient(160deg,#171c2e,#10141f);color:#dbe2f2;border-radius:12px;padding:12px 16px;box-shadow:0 8px 22px rgba(19,23,38,.18)}");
        s.Append(".tsui-dot{width:7px;height:7px;border-radius:50%;background:#5eead4;box-shadow:0 0 8px #5eead4;flex:0 0 auto}");
        s.Append(".tsui-dot.off{background:#67718f;box-shadow:none}");
        s.Append(".tsui-kicker{font:700 8px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:2.5px;color:#7f9bd9}");
        s.Append(".tsui-bar .st{font-size:12px;color:#c6d2ee}");
        s.Append(".tsui-bar .st b{color:#8ff0ff;font-weight:600;font-variant-numeric:tabular-nums}");
        s.Append(".tsui-link{margin-left:auto;font-size:11px;color:#8ff0ff;text-decoration:none;border:1px solid rgba(127,155,217,.35);border-radius:999px;padding:3px 12px;flex:0 0 auto}");
        s.Append(".tsui-link:hover{background:rgba(127,155,217,.15)}");
        s.Append(".tsui-hero{display:flex;align-items:baseline;gap:10px;margin:14px 2px 8px}");
        s.Append(".tsui-hero .hk{font:700 9px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:2px;color:#9aa1b0}");
        s.Append(".tsui-hero .num{font:700 28px/1 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;letter-spacing:-.5px;font-variant-numeric:tabular-nums}");
        s.Append(".tsui-cells{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}");
        s.Append(".tsui-cell{border:1px solid #e8eaf1;background:#fbfcfe;border-radius:11px;padding:8px 11px}");
        s.Append(".tsui-cell .k{font-size:10.5px;color:#9aa1b0}");
        s.Append(".tsui-cell .v{margin-top:3px;font:650 15px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;font-variant-numeric:tabular-nums}");
        s.Append(".tsui-cell .s{font-size:9.5px;color:#b6bac4;margin-top:2px}");
        s.Append(".tsui-days{margin-top:12px;border:1px solid #e8eaf1;border-radius:11px;overflow:hidden}");
        s.Append(".tsui-row{display:flex;align-items:center;gap:10px;padding:6px 12px;border-bottom:1px solid #f1f2f6;font-size:11.5px}");
        s.Append(".tsui-row:last-child{border-bottom:0}");
        s.Append(".tsui-row .d{width:86px;font:650 10.5px/1 ui-monospace,SFMono-Regular,Consolas,monospace;color:#5c6270}");
        s.Append(".tsui-row.is-today .d{color:#2f6fd8}");
        s.Append(".tsui-row .n{width:46px;color:#9aa1b0;text-align:right}");
        s.Append(".tsui-row .bw{flex:1;height:6px;background:#f1f2f6;border-radius:99px}");
        s.Append(".tsui-row .bar{height:6px;border-radius:99px;background:linear-gradient(90deg,#3b82f6,#7cb0ff);min-width:2px}");
        s.Append(".tsui-row .t{width:86px;text-align:right;font:650 11px/1 ui-monospace,SFMono-Regular,Consolas,monospace;font-variant-numeric:tabular-nums;color:#232a3a}");
        s.Append(".tsui-hint{margin-top:10px;font-size:10.5px;color:#9aa1b0;line-height:1.7}");
        s.Append(".tsui-note{margin-top:8px;font-size:10.5px;color:#b6bac4;word-break:break-all}");
        s.Append(".tsui-sec{display:flex;align-items:center;gap:8px;margin:16px 0 8px;flex-wrap:wrap}");
        s.Append(".tsui-sdot{width:8px;height:8px;border-radius:50%;background:#3b82f6;box-shadow:0 0 6px rgba(59,130,246,.45)}");
        s.Append(".tsui-stitle{font-size:13px;font-weight:650;color:#3a4051}");
        s.Append(".tsui-ssub{font-size:10.5px;color:#9aa1b0}");
        s.Append(".tsui-pills{display:flex;gap:6px;flex-wrap:wrap}");
        s.Append(".tsui-pill{padding:5px 14px;border:1px solid #dfe2ea;border-radius:999px;font-size:12px;color:#5c6270;background:#fff;cursor:pointer;font-family:inherit;transition:all .12s}");
        s.Append(".tsui-pill:hover{border-color:#a9c4f8;color:#2f6fd8;transform:translateY(-1px)}");
        s.Append(".tsui-pill.on{background:#3b82f6;border-color:#3b82f6;color:#fff;box-shadow:0 4px 12px rgba(59,130,246,.3)}");
        s.Append(".tsui-btn{padding:6px 16px;border:1px solid #dfe2ea;border-radius:999px;font-size:12px;color:#5c6270;background:#fff;cursor:pointer;font-family:inherit;transition:all .12s}");
        s.Append(".tsui-btn:hover{border-color:#f3b8b8;color:#c53030}");
        s.Append(".tsui-btn.danger{background:#fff1f0;border-color:#ffa39e;color:#cf1322}");
        s.Append(".tsui-clear{display:flex;gap:8px;align-items:center;flex-wrap:wrap}");
        s.Append(".tsui-date{border:1px solid #dfe2ea;border-radius:8px;padding:5px 10px;font-size:11.5px;color:#5c6270;font-family:inherit;outline:0;background:#fff;width:185px}");
        s.Append(".tsui-date:focus{border-color:#a9c4f8}");
        s.Append(".tsui-arrow{color:#b6bac4;font-size:11px}");
        s.Append(".tsui-status{font-size:11.5px;color:#2f6fd8}");
        s.Append(".tsui-adv{margin-top:16px;border:1px dashed #e2e5ee;border-radius:11px;padding:10px 14px}");
        s.Append(".tsui-adv summary{font-size:11.5px;color:#9aa1b0;cursor:pointer;user-select:none}");
        s.Append(".tsui-adv summary:hover{color:#5c6270}");
        s.Append(".tsui-adv[open] summary{margin-bottom:8px}");
        s.Append("</style>");
        s.Append("<div class=\"tsui-wrap\">");

        // 顶部状态条
        s.Append("<div class=\"tsui-bar\">");
        (int Rounds, long Total, bool Busy, int Port) snap = module != null ? module.LiveSnapshot() : (0, 0, false, 0);
        if (module != null)
        {
            s.Append("<span class=\"tsui-dot\"></span><span class=\"tsui-kicker\">USAGE SNAPSHOT</span>");
            s.Append($"<span class=\"st\">角色已激活 · 会话 <b>{snap.Rounds}</b> 轮 · <b>{Fmt(snap.Total)}</b> Token · {(snap.Busy ? "生成中…" : "空闲")}</span>");
            if (snap.Port > 0)
                s.Append($"<a class=\"tsui-link\" href=\"http://127.0.0.1:{snap.Port}/\" target=\"_blank\" rel=\"noopener\">详情页 ↗</a>");
        }
        else
        {
            s.Append("<span class=\"tsui-dot off\"></span><span class=\"tsui-kicker\">USAGE SNAPSHOT</span>");
            s.Append("<span class=\"st\">角色未激活 · 以下为历史用量（激活后自动注入挂件）</span>");
        }
        s.Append("</div>");

        // 汇总：大数字 + 四格
        s.Append($"<div class=\"tsui-hero\"><span class=\"hk\">TODAY · 总 TOKEN</span><span class=\"num\">{Fmt(todayV)}</span></div>");
        s.Append("<div class=\"tsui-cells\">");
        Cell(s, "今天", todayV, RoundsSince(today));
        Cell(s, "近7天", d7, RoundsSince(DateTime.Now.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Cell(s, "近30天", d30, RoundsSince(DateTime.Now.AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Cell(s, "累计", allV, recs.Count);
        s.Append("</div>");

        // 最近14天明细（仅有记录的天，倒序）
        List<KeyValuePair<string, (long V, int R)>> recent = daySums.OrderByDescending(kv => kv.Key).Take(14).ToList();
        if (recent.Count > 0)
        {
            long mx = Math.Max(1, recent.Max(kv => kv.Value.V));
            s.Append("<div class=\"tsui-days\">");
            foreach (KeyValuePair<string, (long V, int R)> kv in recent)
            {
                s.Append($"<div class=\"tsui-row{(kv.Key == today ? " is-today" : "")}\">");
                s.Append($"<span class=\"d\">{kv.Key}{(kv.Key == today ? "（今天）" : "")}</span>");
                s.Append($"<span class=\"n\">{kv.Value.R} 轮</span>");
                s.Append($"<span class=\"bw\"><span class=\"bar\" style=\"display:block;width:{Math.Max(1.5, (double)kv.Value.V / mx * 100).ToString("0.0", CultureInfo.InvariantCulture)}%\"></span></span>");
                s.Append($"<span class=\"t\">{Fmt(kv.Value.V)}</span>");
                s.Append("</div>");
            }
            s.Append("</div>");
        }

        // 使用指引
        s.Append("<div class=\"tsui-hint\">用法：对话页圆环悬停展开详情卡片 · 点击圆环切换统计范围 · 详情页支持单天按小时 / 多天按天的明细；修改偏好后点击页面底部『应用』按钮保存。</div>");
        s.Append($"<div class=\"tsui-note\">数据文件：{Esc(dataFile)}</div>");
        s.Append("</div>");
        return s.ToString();

        static void Cell(StringBuilder sb, string k, long v, int rounds)
        {
            sb.Append($"<div class=\"tsui-cell\"><div class=\"k\">{k}</div><div class=\"v\">{Fmt(v)}</div><div class=\"s\">{rounds} 轮</div></div>");
        }
    }

    public void Dispose()
    {
        refreshTimer?.Dispose();
        refreshTimer = null;
    }
}
