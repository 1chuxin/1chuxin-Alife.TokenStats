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
// 4.8.0 起改为分页结构（参考千瞳）：概览 / 偏好 / 渠道价格 / 余额监测 / 数据 / 高级设置，
// 顶部 tablist 导航 + 仅渲染当前页；同时整体放大字号与间距、降低信息密度。
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

    // 分页（0 起）：label 为页名，meta 为导航上的小字提示，与 switch 分支一一对应
    static readonly (string Label, string Meta)[] Pages =
    {
        ("概览", "用量速览"),
        ("偏好", "范围 · 动画"),
        ("渠道价格", "单价 · 峰谷"),
        ("余额监测", "接口 · 手动"),
        ("数据", "清除历史"),
        ("高级设置", "端口 · 尺寸"),
    };

    int _activeSection;

    [Inject]
    IJSRuntime JS { get; set; } = null!;

    Timer? refreshTimer;
    string overviewHtml = "";
    string clearFrom = "";
    string clearTo = "";
    string? armedAction;   // "range"/"all"：已进入二次确认状态
    DateTime armedAt;
    string statusMsg = "";
    DateTime msgAt;
    string prefMsg = "";   // 偏好已改但尚未点『应用』保存的提醒
    DateTime prefMsgAt;
    string confirmWipe = "";  // 清空全部历史的强确认输入（须输入“清空”）

    // 渠道价格设置（全局 pricing.json；编辑副本，点保存才落盘）
    List<PriceRule> rules = new();
    List<ChannelInfo> channels = new();
    string priceMsg = "";
    DateTime priceMsgAt;

    // 余额监测设置（全局 balance.json；编辑副本，点保存才落盘；Key 落盘为 DPAPI 密文）
    List<BalanceSource> balSrcs = new();
    string balMsg = "";
    DateTime balMsgAt;

    protected override void OnInitialized()
    {
        rules = PricingStore.Rules().Select(r => r.Clone()).ToList();
        balSrcs = BalanceStore.Sources().Select(s => s.Clone()).ToList();
        LoadChannelList();
        Rebuild();
        refreshTimer = new Timer(_ => _ = InvokeAsync(() =>
        {
            if (armedAction != null && (DateTime.Now - armedAt).TotalSeconds > (armedAction == "all" ? 8 : 4))
                armedAction = null;
            if (statusMsg.Length > 0 && (DateTime.Now - msgAt).TotalSeconds > 8)
                statusMsg = "";
            if (priceMsg.Length > 0 && (DateTime.Now - priceMsgAt).TotalSeconds > 8)
                priceMsg = "";
            if (balMsg.Length > 0 && (DateTime.Now - balMsgAt).TotalSeconds > 8)
                balMsg = "";
            if (prefMsg.Length > 0 && (DateTime.Now - prefMsgAt).TotalSeconds > 8)
                prefMsg = "";
            Rebuild();
            StateHasChanged();
        }), null, 3000, 3000);
    }

    void LoadChannelList()
    {
        try { channels = Module?.GetLiveChannels() ?? new List<ChannelInfo>(); }
        catch { channels = new List<ChannelInfo>(); }
        foreach (ChannelInfo scanned in PricingStore.ScanChannels())
            if (!channels.Exists(c => c.Owner == scanned.Owner && c.Name == scanned.Name && c.Model == scanned.Model))
                channels.Add(scanned);
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        int seq = 0;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-wrap");
        b.AddContent(seq++, (MarkupString)BuildCss());

        BuildSnapshotBar(b, ref seq);
        BuildPageNav(b, ref seq);

        b.OpenElement(seq++, "div");
        b.SetKey(_activeSection);
        b.AddAttribute(seq++, "class", "tsui-page");
        b.AddAttribute(seq++, "role", "tabpanel");
        b.AddAttribute(seq++, "aria-label", Pages[Math.Clamp(_activeSection, 0, Pages.Length - 1)].Label);
        switch (_activeSection)
        {
            case 1: BuildPrefs(b); break;
            case 2: BuildPricing(b); break;
            case 3: BuildBalance(b); break;
            case 4: BuildData(b); break;
            case 5: BuildAdvanced(b); break;
            default: b.AddContent(seq++, (MarkupString)overviewHtml); break;
        }
        b.CloseElement();

        b.CloseElement(); // wrap
    }

    // ===== 顶部状态条（常驻，各页可见）=====
    void BuildSnapshotBar(RenderTreeBuilder b, ref int seq)
    {
        (int Rounds, long Total, bool Busy, int Port) snap = Module != null ? Module.LiveSnapshot() : (0, 0, false, 0);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-bar");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", Module != null ? "tsui-dot" : "tsui-dot off");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-kicker");
        b.AddContent(seq++, "USAGE SNAPSHOT");
        b.CloseElement();
        if (Module != null)
        {
            b.AddContent(seq++, (MarkupString)$"<span class=\"st\">角色已激活 · 会话 <b>{snap.Rounds}</b> 轮 · <b>{Fmt(snap.Total)}</b> Token · {(snap.Busy ? "生成中…" : "空闲")}</span>");
            if (snap.Port > 0)
            {
                b.OpenElement(seq++, "a");
                b.AddAttribute(seq++, "class", "tsui-link");
                b.AddAttribute(seq++, "href", $"http://127.0.0.1:{snap.Port}/");
                b.AddAttribute(seq++, "target", "_blank");
                b.AddAttribute(seq++, "rel", "noopener");
                b.AddContent(seq++, "详情页 ↗");
                b.CloseElement();
            }
        }
        else
        {
            b.AddContent(seq++, (MarkupString)"<span class=\"st\">角色未激活 · 以下为历史用量（激活后自动注入挂件）</span>");
        }
        b.CloseElement();
    }

    // ===== 分页导航（参考千瞳 tablist）=====
    void BuildPageNav(RenderTreeBuilder b, ref int seq)
    {
        b.OpenElement(seq++, "nav");
        b.AddAttribute(seq++, "class", "tsui-nav");
        b.AddAttribute(seq++, "role", "tablist");
        b.AddAttribute(seq++, "aria-label", "配置分区");
        for (int p = 0; p < Pages.Length; p++)
        {
            int page = p;
            bool active = _activeSection == page;
            b.OpenElement(seq++, "button");
            b.AddAttribute(seq++, "type", "button");
            b.AddAttribute(seq++, "class", active ? "tsui-nav-btn active" : "tsui-nav-btn");
            b.AddAttribute(seq++, "role", "tab");
            b.AddAttribute(seq++, "aria-selected", active);
            b.AddAttribute(seq++, "title", Pages[page].Label + " · " + Pages[page].Meta);
            b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () =>
            {
                _activeSection = page;
                StateHasChanged();
            }));
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-nav-label");
            b.AddContent(seq++, Pages[page].Label);
            b.CloseElement();
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-nav-meta");
            b.AddContent(seq++, Pages[page].Meta);
            b.CloseElement();
            b.CloseElement();
        }
        b.CloseElement();
    }

    // ===== 页 1：偏好 =====
    void BuildPrefs(RenderTreeBuilder b)
    {
        int seq = 0;
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
        b.AddContent(seq++, "圆环显示的统计范围（点击挂件圆环会弹出范围选择条，可临时切换）");
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
        if (prefMsg.Length > 0)
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-status");
            b.AddContent(seq++, prefMsg);
            b.CloseElement();
        }
        b.CloseElement();

        // 入场动画 / 恩情模式：大号开关卡片（4.8.2 起，原裸复选框太小）
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-fxgrid");
        b.OpenElement(seq++, "label");
        b.AddAttribute(seq++, "class", "tsui-fxcard");
        b.AddAttribute(seq++, "title", "仅在激活角色时播放：屏幕中央绽开圆环风探查之眼（非仿真，全由圆构成）并飞至挂件位置；页面刷新/切换查看不播，系统开启“减少动态效果”或关闭本开关时不播，改后需重新激活角色生效");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-fxhead");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sw");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "checkbox");
        b.AddAttribute(seq++, "checked", Configuration?.EntranceAnimation ?? true);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => SetEntranceFx(v), Configuration?.EntranceAnimation ?? true));
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tr");
        b.CloseElement();
        b.CloseElement(); // sw
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-fxt");
        b.AddContent(seq++, "入场动画");
        b.CloseElement();
        b.CloseElement(); // fxhead
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-fxd");
        b.AddContent(seq++, "激活角色时，屏幕中央绽开「探查之眼」并飞至挂件位置（仅激活时播放，刷新/切页不播）");
        b.CloseElement();
        b.CloseElement(); // fxcard
        b.OpenElement(seq++, "label");
        b.AddAttribute(seq++, "class", "tsui-fxcard");
        b.AddAttribute(seq++, "title", "每次激活角色都播放恩情动画：满屏恩情文本如礼花般从炸点飞散（高密度、允许重复）+惊雷闪电+礼花辉光（约3.9秒，不拦截任何点击）；首次使用插件时无视『入场动画』开关强制观看一次（“减少动态效果”自动跳过并视为已看），改后需重新激活角色生效");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-fxhead");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sw");
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "checkbox");
        b.AddAttribute(seq++, "checked", Configuration?.GratitudeMode ?? true);
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => SetGratitudeMode(v), Configuration?.GratitudeMode ?? true));
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tr");
        b.CloseElement();
        b.CloseElement(); // sw
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-fxt");
        b.AddContent(seq++, "恩情模式");
        b.CloseElement();
        b.CloseElement(); // fxhead
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-fxd");
        b.AddContent(seq++, "每次激活都播放满屏「恩情」礼花特效（约 3.9 秒）；首次使用会强制观看一次");
        b.CloseElement();
        b.CloseElement(); // fxcard
        b.CloseElement(); // fxgrid
    }

    // ===== 页 2：渠道价格 =====
    void BuildPricing(RenderTreeBuilder b)
    {
        int seq = 0;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-sec");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sdot");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-stitle");
        b.AddContent(seq++, "渠道价格");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-ssub");
        b.AddContent(seq++, "元/百万tokens · 匹配：URL>模型>渠道名（任填其一即可）· 峰=工作日 9:00–12:00 / 14:00–18:00，谷=其余 · 全局生效，改价后历史费用即时重定价");
        b.CloseElement();
        b.CloseElement(); // sec

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-chrow");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-clb");
        b.AddContent(seq++, "检测到的渠道（点击为其生成价格规则）：");
        b.CloseElement();
        if (channels.Count == 0)
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-cnone");
            b.AddContent(seq++, "未检测到灵枢渠道（角色未激活或未配置），可手动新增规则");
            b.CloseElement();
        }
        else
        {
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "tsui-pills");
            for (int i = 0; i < channels.Count; i++)
            {
                int idx = i;
                ChannelInfo c = channels[i];
                b.OpenElement(seq++, "button");
                b.AddAttribute(seq++, "class", "tsui-pill");
                b.AddAttribute(seq++, "type", "button");
                b.AddAttribute(seq++, "title", $"{c.Owner} · {c.Model} @ {c.Host}（点击生成价格规则）");
                b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => AddChannelRule(idx)));
                b.AddContent(seq++, $"{c.Name} · {c.Model}");
                b.CloseElement();
            }
            b.CloseElement();
        }
        b.CloseElement(); // chrow

        for (int i = 0; i < rules.Count; i++)
        {
            int idx = i;
            PriceRule r = rules[i];
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "tsui-prow");

            // 字段顺序：渠道名 → URL → 模型名 → 峰谷开关 → 价格；
            // 渠道名同时作为显示名与匹配名（合并旧"规则名/渠道名匹配"两字段）
            void Field(string cls, string label, string val, Action<string> set)
            {
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-nw");
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-lb");
                b.AddContent(seq++, label);
                b.CloseElement();
                b.OpenElement(seq++, "input");
                b.AddAttribute(seq++, "type", "text");
                b.AddAttribute(seq++, "class", cls);
                b.AddAttribute(seq++, "value", val);
                b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => set(v), val));
                b.CloseElement();
                b.CloseElement();
            }
            void Num(string label, decimal val, Action<decimal> set)
            {
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-nw");
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-lb");
                b.AddContent(seq++, label);
                b.CloseElement();
                b.OpenElement(seq++, "input");
                b.AddAttribute(seq++, "type", "number");
                b.AddAttribute(seq++, "step", "0.01");
                b.AddAttribute(seq++, "class", "tsui-num");
                b.AddAttribute(seq++, "value", BindConverter.FormatValue(val));
                b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => set(v), val));
                b.CloseElement();
                b.CloseElement();
            }

            Field("tsui-pk", "渠道名", r.ChannelMatch ?? r.Name, v => { rules[idx].Name = v; rules[idx].ChannelMatch = v; });
            Field("tsui-pm", "URL（推荐）", r.UrlMatch ?? "", v => rules[idx].UrlMatch = v);
            Field("tsui-pm", "模型名", r.ModelMatch ?? "", v => rules[idx].ModelMatch = v);

            b.OpenElement(seq++, "label");
            b.AddAttribute(seq++, "class", "tsui-chk");
            b.AddAttribute(seq++, "title", "开启=分高峰/谷段两档价；关闭=单一价格（峰段也按谷价档计算）");
            b.OpenElement(seq++, "input");
            b.AddAttribute(seq++, "type", "checkbox");
            b.AddAttribute(seq++, "checked", r.PeakEnabled);
            b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => rules[idx].PeakEnabled = v, r.PeakEnabled));
            b.CloseElement();
            b.AddContent(seq++, "峰谷");
            b.CloseElement();

            // 峰谷开关联动：开=六格（峰/谷各三档），关=三格单一价（编辑谷价档，峰价保留待再开启）
            if (r.PeakEnabled)
            {
                Num("命中·峰", r.HitPeak, v => rules[idx].HitPeak = v);
                Num("命中·谷", r.HitOff, v => rules[idx].HitOff = v);
                Num("未中·峰", r.MissPeak, v => rules[idx].MissPeak = v);
                Num("未中·谷", r.MissOff, v => rules[idx].MissOff = v);
                Num("输出·峰", r.OutPeak, v => rules[idx].OutPeak = v);
                Num("输出·谷", r.OutOff, v => rules[idx].OutOff = v);
            }
            else
            {
                Num("命中价", r.HitOff, v => rules[idx].HitOff = v);
                Num("未命中价", r.MissOff, v => rules[idx].MissOff = v);
                Num("输出价", r.OutOff, v => rules[idx].OutOff = v);
            }

            b.OpenElement(seq++, "button");
            b.AddAttribute(seq++, "class", "tsui-del");
            b.AddAttribute(seq++, "type", "button");
            b.AddAttribute(seq++, "title", "删除该规则");
            b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => DelRule(idx)));
            b.AddContent(seq++, "删");
            b.CloseElement();

            b.CloseElement(); // prow
        }

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-pbar");
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, AddRule));
        b.AddContent(seq++, "＋ 手动添加渠道/规则");
        b.CloseElement();
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "title", "恢复为 DeepSeek V4-Flash / V4-Pro 官方峰谷价");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, RestoreDefaultPrices));
        b.AddContent(seq++, "恢复官方默认价");
        b.CloseElement();
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn pri");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, SavePricingFromUi));
        b.AddContent(seq++, "保存价格规则");
        b.CloseElement();
        if (priceMsg.Length > 0)
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-status");
            b.AddContent(seq++, priceMsg);
            b.CloseElement();
        }
        b.CloseElement(); // pbar
    }

    // ===== 页 3：余额监测 =====
    void BuildBalance(RenderTreeBuilder b)
    {
        int seq = 0;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-sec");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sdot");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-stitle");
        b.AddContent(seq++, "余额监测");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-ssub");
        b.AddContent(seq++, "按「接口地址」自动探测官方/中转站余额 · 也可自定义接口或预设扣减 · 填「初始额度」的源按 初始−已用 扣减估算（当前额度=初始额度−按用量计费，跳过探测）· Key 仅手动录入，DPAPI 密文落盘");
        b.CloseElement();
        b.CloseElement(); // sec

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-chrow");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-clb");
        b.AddContent(seq++, "轮询间隔(分钟)：");
        b.CloseElement();
        int interval = Math.Max(5, Configuration?.BalanceIntervalMinutes ?? 30);
        b.OpenElement(seq++, "input");
        b.AddAttribute(seq++, "type", "number");
        b.AddAttribute(seq++, "class", "tsui-num");
        b.AddAttribute(seq++, "min", "5");
        b.AddAttribute(seq++, "value", BindConverter.FormatValue(interval));
        b.AddAttribute(seq++, "title", "自动探测间隔（分钟，最小 5）；保存并重新激活后生效，也可随时点「立即探测」");
        b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => SetBalanceInterval(v), interval));
        b.CloseElement();
        b.CloseElement(); // chrow（轮询间隔）

        for (int i = 0; i < balSrcs.Count; i++)
        {
            int idx = i;
            BalanceSource s = balSrcs[i];
            string type = string.IsNullOrWhiteSpace(s.Type) ? "auto" : s.Type.Trim().ToLowerInvariant();
            b.OpenElement(seq++, "div");
            b.AddAttribute(seq++, "class", "tsui-prow");
            void BField(string cls, string label, string val, Action<string> set, string? title = null, string? placeholder = null)
            {
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-nw");
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-lb");
                b.AddContent(seq++, label);
                b.CloseElement();
                b.OpenElement(seq++, "input");
                b.AddAttribute(seq++, "type", "text");
                b.AddAttribute(seq++, "class", cls);
                b.AddAttribute(seq++, "value", val);
                if (title != null) b.AddAttribute(seq++, "title", title);
                if (placeholder != null) b.AddAttribute(seq++, "placeholder", placeholder);
                b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => set(v), val));
                b.CloseElement();
                b.CloseElement();
            }
            BField("tsui-pk", "名称", s.Name, v => balSrcs[idx].Name = v);
            // 类型三选一（auto/custom/preset）：每个选项自带一句人话说明，替代原自由文本输入
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-pills");
            (string Key, string Label, string Tip)[] types =
            {
                ("auto", "自动探测", "填 Base 地址（如 https://api.deepseek.com），按地址自动识别官方端点或 One-API 系中转站（额度−已用）"),
                ("custom", "自定义接口", "填 Base 地址（参考 DeepSeek）即可，自动尝试常见余额接口；接口特殊可填完整地址，可选「余额字段」取数"),
                ("preset", "预设扣减", "无接口兜底：预设初始额度 − 该渠道累计费用（按价格规则估算，展示为“预估”）"),
            };
            foreach ((string key, string label, string tip) in types)
            {
                b.OpenElement(seq++, "button");
                b.AddAttribute(seq++, "type", "button");
                b.AddAttribute(seq++, "class", "tsui-pill" + (type == key ? " on" : ""));
                b.AddAttribute(seq++, "title", tip);
                b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => SetBalType(idx, key)));
                b.AddContent(seq++, label);
                b.CloseElement();
            }
            b.CloseElement(); // 类型胶囊
            BField("tsui-pm", "接口地址", s.Url, v => balSrcs[idx].Url = v,
                type == "auto" ? "Base 地址（参考 DeepSeek：https://api.deepseek.com），按地址自动识别官方端点或 One-API 系中转站（末尾 /v1 可省略）"
                : type == "custom" ? "Base 地址（参考 DeepSeek：https://api.deepseek.com），自动尝试常见余额接口；接口特殊也可直接填完整余额接口地址"
                : "渠道匹配关键字（可为空）：按 URL/名称匹配该渠道历史费用",
                "https://api.deepseek.com");
            BField("tsui-pm", "API Key", s.ApiKey, v => balSrcs[idx].ApiKey = v,
                "仅探测请求使用；落盘为 Windows 用户级 DPAPI 密文，不读取其他模块的 Key",
                "sk-…");
            if (type == "custom")
            {
                BField("tsui-pm", "余额字段(可选)", s.JsonPath, v => balSrcs[idx].JsonPath = v,
                    "自动尝试失败时再填：余额在返回 JSON 里的点路径，如 data.available_balance、balance_infos.0.total_balance（留空=按各接口默认路径取数）",
                    "data.available_balance");
                // 自定义接口使用指引（4.8.2）：参考 DeepSeek 只需 Base 地址 + Key
                b.OpenElement(seq++, "span");
                b.AddAttribute(seq++, "class", "tsui-chint");
                b.AddContent(seq++, "参考 DeepSeek 填 Base 地址 + Key 即可：自动尝试 /user/balance、/v1/users/me/balance 等常见余额接口；接口特殊时填完整地址，仍不行再补「余额字段」");
                b.CloseElement();
            }
            // 初始额度（4.8.1 统一语义，所有类型可用）：文本输入，填入=按用量扣减（当前额度=初始额度−已计费用，跳过探测）；清空=恢复类型探测
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-nw");
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-lb");
            b.AddContent(seq++, "初始额度(元)");
            b.CloseElement();
            b.OpenElement(seq++, "input");
            b.AddAttribute(seq++, "type", "text");
            b.AddAttribute(seq++, "inputmode", "decimal");
            b.AddAttribute(seq++, "class", "tsui-num");
            b.AddAttribute(seq++, "value", s.Initial?.ToString("0.####", CultureInfo.InvariantCulture) ?? "");
            b.AddAttribute(seq++, "title", type == "preset"
                ? "预设扣减必需：当前额度 = 初始额度 − 该渠道累计计费（按价格规则估算）"
                : "填入后该源按用量扣减：当前额度 = 初始额度 − 已计费用（跳过接口探测，不被轮询覆盖）；清空则恢复接口探测");
            b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => SetBalInitial(idx, v), s.Initial?.ToString("0.####", CultureInfo.InvariantCulture) ?? ""));
            b.CloseElement();
            b.CloseElement();
            b.OpenElement(seq++, "label");
            b.AddAttribute(seq++, "class", "tsui-chk");
            b.AddAttribute(seq++, "title", "停用后不参与自动轮询与立即探测（看板仍显示最近一次结果）");
            b.OpenElement(seq++, "input");
            b.AddAttribute(seq++, "type", "checkbox");
            b.AddAttribute(seq++, "checked", s.Enabled);
            b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => balSrcs[idx].Enabled = v, s.Enabled));
            b.CloseElement();
            b.AddContent(seq++, "启用");
            b.CloseElement();
            // 初始额度源即时按 初始−已用 展示（不依赖轮询）；其余取最近探测结果
            BalanceState? st = s.Initial != null && Module != null ? Module.ResolveBalanceState(s) : BalanceStore.StateOf(s.Name);
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-status");
            b.AddAttribute(seq++, "title", st?.Msg ?? "");
            b.AddContent(seq++, st == null ? "尚未探测" : st.Ok ? $"✓ {st.Balance.ToString("0.####")} {st.Currency} · {st.At:MM-dd HH:mm}" : $"✗ {st.Msg}");
            b.CloseElement();
            b.OpenElement(seq++, "button");
            b.AddAttribute(seq++, "class", "tsui-del");
            b.AddAttribute(seq++, "type", "button");
            b.AddAttribute(seq++, "title", "删除该监测源");
            b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, () => DelBalSource(idx)));
            b.AddContent(seq++, "删");
            b.CloseElement();
            b.CloseElement(); // prow
        }

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-pbar");
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, AddBalSource));
        b.AddContent(seq++, "＋ 添加源");
        b.CloseElement();
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "title", "立即探测全部启用的监测源（无需等轮询；需先保存新改动）");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, ProbeBalNow));
        b.AddContent(seq++, "立即探测");
        b.CloseElement();
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "class", "tsui-btn pri");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, SaveBalances));
        b.AddContent(seq++, "保存");
        b.CloseElement();
        if (balMsg.Length > 0)
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-status");
            b.AddContent(seq++, balMsg);
            b.CloseElement();
        }
        b.CloseElement(); // pbar
    }

    // ===== 页 4：数据管理 =====
    void BuildData(RenderTreeBuilder b)
    {
        int seq = 0;
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
        b.AddContent(seq++, "清除指定时间段（精确到秒）或全部用量日志（汇总 + 各角色分日志），不影响当前会话统计");
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
        if (armedAction == "all")
        {
            b.OpenElement(seq++, "input");
            b.AddAttribute(seq++, "type", "text");
            b.AddAttribute(seq++, "class", "tsui-wipe");
            b.AddAttribute(seq++, "placeholder", "输入“清空”确认");
            b.AddAttribute(seq++, "title", "为防止误删，须在此输入“清空”二字后点击确认");
            b.AddAttribute(seq++, "value", confirmWipe);
            b.AddAttribute(seq++, "onchange", EventCallback.Factory.CreateBinder(this, v => confirmWipe = v ?? "", ""));
            b.CloseElement();
        }
        if (statusMsg.Length > 0)
        {
            b.OpenElement(seq++, "span");
            b.AddAttribute(seq++, "class", "tsui-status");
            b.AddContent(seq++, statusMsg);
            b.CloseElement();
        }
        b.CloseElement(); // clear row
    }

    // ===== 页 5：高级设置 =====
    void BuildAdvanced(RenderTreeBuilder b)
    {
        int seq = 0;
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "tsui-sec");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-sdot");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-stitle");
        b.AddContent(seq++, "高级设置");
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "tsui-ssub");
        b.AddContent(seq++, "HTTP 端口 / 尺寸 / 间距等调优参数（改后需重新激活角色生效）");
        b.CloseElement();
        b.CloseElement(); // sec

        b.AddContent(seq++, DefaultUI);
    }

    void SetRingRange(string key)
    {
        if (Configuration == null)
            return;
        Configuration.RingRange = key;
        if (Module is IConfigurable configurable)
            configurable.Configuration = Configuration;
        _ = TryClearOverlayMemoryAsync();
        string disp = Ranges.FirstOrDefault(r => r.Key == key).Name ?? key;
        prefMsg = $"圆环范围已切至「{disp}」，记得点击页面底部『应用』按钮保存配置";
        prefMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void SetEntranceFx(bool on)
    {
        if (Configuration == null)
            return;
        Configuration.EntranceAnimation = on;
        if (Module is IConfigurable configurable)
            configurable.Configuration = Configuration;
        prefMsg = (on ? "入场动画已开启" : "入场动画已关闭") + "，记得点击页面底部『应用』按钮保存配置（重新激活角色后生效）";
        prefMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void SetBalanceInterval(decimal v)
    {
        if (Configuration == null)
            return;
        Configuration.BalanceIntervalMinutes = Math.Max(5, (int)Math.Round(v));
        if (Module is IConfigurable configurable)
            configurable.Configuration = Configuration;
        prefMsg = "余额轮询间隔已设为 " + Configuration.BalanceIntervalMinutes + " 分钟，记得点击页面底部『应用』按钮保存配置";
        prefMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void SetBalType(int idx, string type)
    {
        if (idx < 0 || idx >= balSrcs.Count) return;
        balSrcs[idx].Type = type;
        Rebuild();
        StateHasChanged();
    }

    void SetBalInitial(int idx, string raw)
    {
        if (idx < 0 || idx >= balSrcs.Count) return;
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            balSrcs[idx].Initial = null;   // 清空=恢复类型探测（preset 则提示需填初始额度）
        else if (decimal.TryParse(raw, out decimal m) && m > 0)
            balSrcs[idx].Initial = m;
        else
        {
            balMsg = "初始额度需为正数（可带小数），清空则恢复探测";
            balMsgAt = DateTime.Now;
        }
        Rebuild();
        StateHasChanged();
    }

    void AddBalSource()
    {
        balSrcs.Add(new BalanceSource { Name = "新监测源", Type = "auto", Enabled = true });
        balMsg = "已添加：选类型、填名称/接口地址/Key 后点「保存」";
        balMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void DelBalSource(int idx)
    {
        if (idx >= 0 && idx < balSrcs.Count) balSrcs.RemoveAt(idx);
        balMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void SaveBalances()
    {
        // 去掉名称为空的行（名称是 state 的键，必须唯一非空）
        balSrcs = balSrcs.Where(s => s.Name.Trim().Length > 0).Select(s => { s.Name = s.Name.Trim(); return s; }).ToList();
        BalanceStore.Save(balSrcs);
        balMsg = "已保存 " + balSrcs.Count + " 个余额监测源（Key 已加密存于 storage/Tokenlog/balance.json）";
        balMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
    }

    void ProbeBalNow()
    {
        if (Module == null)
        {
            balMsg = "模块未激活，无法探测（激活角色后再试）";
            balMsgAt = DateTime.Now;
            Rebuild();
            StateHasChanged();
            return;
        }
        balMsg = "探测中…（每源超时 10 秒）";
        balMsgAt = DateTime.Now;
        Rebuild();
        StateHasChanged();
        TokenStatsModule mod = Module;
        _ = Task.Run(async () =>
        {
            try
            {
                await mod.ProbeBalancesAsync();
                balMsg = "探测完成，各源状态已更新（未保存的新增源不会参与探测）";
            }
            catch (Exception ex)
            {
                balMsg = "探测失败：" + ex.Message;
            }
            balMsgAt = DateTime.Now;
            await InvokeAsync(() => { Rebuild(); StateHasChanged(); });
        });
    }

    void SetGratitudeMode(bool on)
    {
        if (Configuration == null)
            return;
        Configuration.GratitudeMode = on;
        if (Module is IConfigurable configurable)
            configurable.Configuration = Configuration;
        prefMsg = (on ? "恩情模式已开启（每次激活都播满屏恩情文本）" : "恩情模式已关闭（首次使用的强制观看义务不受影响）") + "，记得点击页面底部『应用』按钮保存配置（重新激活角色后生效）";
        prefMsgAt = DateTime.Now;
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
            confirmWipe = "";
            StateHasChanged();
            return;
        }
        armedAction = null;
        try
        {
            if (action == "all")
            {
                if (confirmWipe != "清空")
                {
                    // 强确认：全量删除不可逆，仅点击两次不够，须输入“清空”二字
                    statusMsg = "请先在旁边输入框输入『清空』二字，再点击确认";
                    armedAction = "all";
                }
                else
                {
                    if (Module != null) Module.ResetHistory();
                    else TokenStatsModule.DeleteDataFile();
                    statusMsg = "已清空全部历史";
                    confirmWipe = "";
                }
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

    void AddRule()
    {
        rules.Add(new PriceRule { Name = "新规则" });
        StateHasChanged();
    }

    void DelRule(int idx)
    {
        if (idx >= 0 && idx < rules.Count)
            rules.RemoveAt(idx);
        StateHasChanged();
    }

    void AddChannelRule(int idx)
    {
        if (idx < 0 || idx >= channels.Count) return;
        ChannelInfo c = channels[idx];
        PriceRule r = PriceRule.Guess(c.Model);
        r.Name = c.Name;
        r.ChannelMatch = c.Name;
        r.UrlMatch = c.Host.Length > 0 ? c.Host : null; // 域名比组名稳定，双匹配最稳
        r.ModelMatch = null; // 渠道级规则不锁模型
        rules.Add(r);
        SavePricing($"已为渠道「{c.Name}」（{c.Owner}）创建价格规则，可继续调整单价后保存");
    }

    void RestoreDefaultPrices()
    {
        rules = PriceRule.Defaults();
        SavePricing("已恢复 DeepSeek V4-Flash / V4-Pro 官方峰谷默认价");
    }

    void SavePricingFromUi() => SavePricing("价格规则已保存（全局生效，历史费用即时重定价）");

    void SavePricing(string msg)
    {
        PricingStore.Save(rules);
        priceMsg = msg;
        priceMsgAt = DateTime.Now;
        StateHasChanged();
    }

    void Rebuild()
    {
        try { overviewHtml = BuildOverview(); }
        catch { }
    }

    static string Fmt(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ===== 全局样式：4.8.0 起整体放大字号、放宽间距、降低信息密度 =====
    static string BuildCss()
    {
        StringBuilder s = new(4096);
        s.Append("<style>");
        s.Append(".tsui-wrap{max-width:1100px;margin:4px 0 22px;font-family:\"Segoe UI\",system-ui,\"Microsoft YaHei\",sans-serif;color:#23272f;font-size:14px;line-height:1.55}");
        s.Append(".tsui-bar{display:flex;align-items:center;gap:12px;flex-wrap:wrap;background:linear-gradient(160deg,#171c2e,#10141f);color:#dbe2f2;border-radius:14px;padding:14px 18px;box-shadow:0 8px 22px rgba(19,23,38,.18)}");
        s.Append(".tsui-dot{width:8px;height:8px;border-radius:50%;background:#5eead4;box-shadow:0 0 8px #5eead4;flex:0 0 auto}");
        s.Append(".tsui-dot.off{background:#67718f;box-shadow:none}");
        s.Append(".tsui-kicker{font:700 11px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:2.5px;color:#7f9bd9}");
        s.Append(".tsui-bar .st{font-size:14px;color:#c6d2ee}");
        s.Append(".tsui-bar .st b{color:#8ff0ff;font-weight:600;font-variant-numeric:tabular-nums}");
        s.Append(".tsui-link{margin-left:auto;font-size:13px;color:#8ff0ff;text-decoration:none;border:1px solid rgba(127,155,217,.35);border-radius:999px;padding:5px 14px;flex:0 0 auto}");
        s.Append(".tsui-link:hover{background:rgba(127,155,217,.15)}");
        s.Append(".tsui-nav{display:flex;gap:10px;flex-wrap:wrap;margin:16px 0 6px;padding:10px 12px;background:#f5f7fb;border:1px solid #e8eaf1;border-radius:12px}");
        s.Append(".tsui-nav-btn{display:flex;flex-direction:column;align-items:flex-start;gap:2px;padding:9px 16px;border:1px solid #dfe2ea;border-radius:10px;background:#fff;cursor:pointer;font-family:inherit;text-align:left;transition:all .12s}");
        s.Append(".tsui-nav-btn:hover{border-color:#a9c4f8;transform:translateY(-1px)}");
        s.Append(".tsui-nav-btn.active{background:#3b82f6;border-color:#3b82f6;box-shadow:0 4px 12px rgba(59,130,246,.3)}");
        s.Append(".tsui-nav-label{font-size:14.5px;font-weight:600;color:#3a4051}");
        s.Append(".tsui-nav-btn.active .tsui-nav-label{color:#fff}");
        s.Append(".tsui-nav-meta{font-size:11.5px;color:#9aa1b0}");
        s.Append(".tsui-nav-btn.active .tsui-nav-meta{color:#dbe8ff}");
        s.Append(".tsui-page{margin-top:6px}");
        s.Append(".tsui-hero{display:flex;align-items:baseline;gap:12px;margin:18px 2px 10px;flex-wrap:wrap}");
        s.Append(".tsui-hero .hk{font:700 12px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;letter-spacing:2px;color:#9aa1b0}");
        s.Append(".tsui-hero .num{font:700 34px/1 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;letter-spacing:-.5px;font-variant-numeric:tabular-nums}");
        s.Append(".tsui-cells{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}");
        s.Append(".tsui-cell{border:1px solid #e8eaf1;background:#fbfcfe;border-radius:12px;padding:12px 14px}");
        s.Append(".tsui-cell .k{font-size:13px;color:#9aa1b0}");
        s.Append(".tsui-cell .v{margin-top:4px;font:650 19px/1.2 ui-monospace,SFMono-Regular,Consolas,monospace;color:#232a3a;font-variant-numeric:tabular-nums}");
        s.Append(".tsui-cell .s{font-size:12px;color:#b6bac4;margin-top:3px}");
        s.Append(".tsui-days{margin-top:14px;border:1px solid #e8eaf1;border-radius:12px;overflow:hidden}");
        s.Append(".tsui-row{display:flex;align-items:center;gap:12px;padding:9px 14px;border-bottom:1px solid #f1f2f6;font-size:13.5px}");
        s.Append(".tsui-row:last-child{border-bottom:0}");
        s.Append(".tsui-row .d{width:100px;font:650 13px/1 ui-monospace,SFMono-Regular,Consolas,monospace;color:#5c6270}");
        s.Append(".tsui-row.is-today .d{color:#2f6fd8}");
        s.Append(".tsui-row .n{width:52px;color:#9aa1b0;text-align:right}");
        s.Append(".tsui-row .bw{flex:1;height:8px;background:#f1f2f6;border-radius:99px}");
        s.Append(".tsui-row .bar{height:8px;border-radius:99px;background:linear-gradient(90deg,#3b82f6,#7cb0ff);min-width:2px}");
        s.Append(".tsui-row .t{width:100px;text-align:right;font:650 13px/1 ui-monospace,SFMono-Regular,Consolas,monospace;font-variant-numeric:tabular-nums;color:#232a3a}");
        s.Append(".tsui-hint{margin-top:12px;font-size:13px;color:#9aa1b0;line-height:1.75}");
        s.Append(".tsui-note{margin-top:10px;font-size:12.5px;color:#b6bac4;word-break:break-all}");
        s.Append(".tsui-sec{display:flex;align-items:center;gap:10px;margin:20px 0 10px;flex-wrap:wrap}");
        s.Append(".tsui-sdot{width:9px;height:9px;border-radius:50%;background:#3b82f6;box-shadow:0 0 6px rgba(59,130,246,.45)}");
        s.Append(".tsui-stitle{font-size:16px;font-weight:650;color:#3a4051}");
        s.Append(".tsui-ssub{font-size:13px;color:#9aa1b0}");
        s.Append(".tsui-wipe{border:1px solid #ffa39e;border-radius:9px;padding:7px 12px;font-size:13px;color:#cf1322;font-family:inherit;outline:0;background:#fff1f0;width:150px}");
        s.Append(".tsui-pills{display:flex;gap:8px;flex-wrap:wrap}");
        s.Append(".tsui-pill{padding:8px 18px;border:1px solid #dfe2ea;border-radius:999px;font-size:14px;color:#5c6270;background:#fff;cursor:pointer;font-family:inherit;transition:all .12s}");
        s.Append(".tsui-pill:hover{border-color:#a9c4f8;color:#2f6fd8;transform:translateY(-1px)}");
        s.Append(".tsui-pill.on{background:#3b82f6;border-color:#3b82f6;color:#fff;box-shadow:0 4px 12px rgba(59,130,246,.3)}");
        s.Append(".tsui-btn{padding:9px 18px;border:1px solid #dfe2ea;border-radius:999px;font-size:14px;color:#5c6270;background:#fff;cursor:pointer;font-family:inherit;transition:all .12s}");
        s.Append(".tsui-btn:hover{border-color:#f3b8b8;color:#c53030}");
        s.Append(".tsui-btn.danger{background:#fff1f0;border-color:#ffa39e;color:#cf1322}");
        s.Append(".tsui-clear{display:flex;gap:10px;align-items:center;flex-wrap:wrap}");
        s.Append(".tsui-date{border:1px solid #dfe2ea;border-radius:9px;padding:8px 12px;font-size:13.5px;color:#5c6270;font-family:inherit;outline:0;background:#fff;width:210px}");
        s.Append(".tsui-date:focus{border-color:#a9c4f8}");
        s.Append(".tsui-arrow{color:#b6bac4;font-size:13px}");
        s.Append(".tsui-status{font-size:13.5px;color:#2f6fd8}");
        s.Append(".tsui-chrow{margin:4px 0 8px;display:flex;align-items:center;gap:10px;flex-wrap:wrap}");
        s.Append(".tsui-clb{font-size:13.5px;color:#5c6270}");
        s.Append(".tsui-cnone{font-size:13px;color:#b6bac4}");
        s.Append(".tsui-prow{display:flex;gap:10px;align-items:flex-start;flex-wrap:wrap;margin:8px 0;padding:12px 14px;border:1px solid #e8eaf1;border-radius:12px;background:#fbfcfe}");
        s.Append(".tsui-pk,.tsui-pm,.tsui-num{border:1px solid #dfe2ea;border-radius:8px;padding:7px 9px;font-size:13.5px;color:#5c6270;font-family:inherit;outline:0;background:#fff}");
        s.Append(".tsui-pk{width:150px}.tsui-pm{width:130px}.tsui-num{width:90px;text-align:right}");
        s.Append(".tsui-pk:focus,.tsui-pm:focus,.tsui-num:focus{border-color:#a9c4f8}");
        s.Append(".tsui-nw{display:flex;flex-direction:column;align-items:flex-start;gap:3px}");
        s.Append(".tsui-lb{font-size:12px;color:#9aa1b0;white-space:nowrap}");
        s.Append(".tsui-chk{display:flex;align-items:center;gap:6px;font-size:13px;color:#5c6270;margin:0 6px;cursor:pointer;user-select:none}");
        s.Append(".tsui-pbar{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-top:12px}");
        s.Append(".tsui-btn.pri{border-color:#a9c4f8;color:#2f6fd8;font-weight:600}");
        s.Append(".tsui-btn.pri:hover{border-color:#2f6fd8;background:#f2f7ff;color:#2f6fd8}");
        s.Append(".tsui-del{padding:6px 12px;border:1px solid #dfe2ea;border-radius:8px;background:#fff;color:#b6bac4;cursor:pointer;font-size:12.5px;font-family:inherit;transition:all .12s}");
        s.Append(".tsui-del:hover{border-color:#f3b8b8;color:#c53030}");
        // 4.8.2：行内控件（峰谷/启用开关、删除按钮、状态文本）下压到与输入框同一水平线
        s.Append(".tsui-prow > .tsui-chk,.tsui-prow > .tsui-del,.tsui-prow > .tsui-status{margin-top:22px}");
        // 4.8.2：偏好页入场动画/恩情模式大号开关卡片
        s.Append(".tsui-fxgrid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:12px;margin-top:10px}");
        s.Append(".tsui-fxcard{display:flex;flex-direction:column;gap:9px;padding:16px 18px;border:1px solid #e8eaf1;border-radius:12px;background:#fbfcfe;cursor:pointer;user-select:none}");
        s.Append(".tsui-fxcard:hover{border-color:#a9c4f8}");
        s.Append(".tsui-fxhead{display:flex;align-items:center;gap:12px}");
        s.Append(".tsui-sw{position:relative;width:46px;height:26px;flex:0 0 auto}");
        s.Append(".tsui-sw input{position:absolute;opacity:0;width:0;height:0}");
        s.Append(".tsui-sw .tr{position:absolute;inset:0;background:#dfe2ea;border-radius:999px;transition:background .15s}");
        s.Append(".tsui-sw .tr::after{content:\"\";position:absolute;top:3px;left:3px;width:20px;height:20px;background:#fff;border-radius:50%;box-shadow:0 1px 3px rgba(0,0,0,.25);transition:transform .15s}");
        s.Append(".tsui-sw input:checked + .tr{background:#3b82f6}");
        s.Append(".tsui-sw input:checked + .tr::after{transform:translateX(20px)}");
        s.Append(".tsui-fxt{font-size:15px;font-weight:650;color:#3a4051}");
        s.Append(".tsui-fxd{font-size:13px;color:#9aa1b0;line-height:1.6}");
        s.Append(".tsui-chint{flex:1 1 100%;font-size:12.5px;color:#9aa1b0;line-height:1.6}");
        s.Append("</style>");
        return s.ToString();
    }

    // ===== 概览页内容：大数字 + 四格 + 最近14天明细 + 指引 =====
    static string BuildOverview()
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

        StringBuilder s = new(2048);

        // 汇总：大数字 + 四格（配置页快照读汇总日志=全部角色口径；各角色看板/挂件只读本角色分日志）
        s.Append($"<div class=\"tsui-hero\"><span class=\"hk\">TODAY · 总 TOKEN（全部角色汇总）</span><span class=\"num\">{Fmt(todayV)}</span></div>");
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
        s.Append("<div class=\"tsui-hint\">用法：对话页圆环悬停展开详情卡片 · 点击圆环弹出范围选择条 · 详情页支持单天按小时 / 多天按天的明细、表头点击排序、视图状态保存在网址中 · 入场动画/恩情模式在『偏好』页开关（聊天页控制台派发 tstats-fx 事件可重播）；修改偏好后点击页面底部『应用』按钮保存。</div>");
        s.Append($"<div class=\"tsui-note\">数据文件：{Esc(dataFile)}</div>");
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
