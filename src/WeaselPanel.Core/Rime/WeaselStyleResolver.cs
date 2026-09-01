//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  样式解析：复刻上游 _UpdateUIStyle（RimeWithWeasel.cpp:1164-1361）的布局派生逻辑。
//  GPL-3.0。
//
//  ── 为什么必须复刻而不是「读到什么显示什么」────────────────────────────
//  小狼毫渲染用的不是配置里的值，而是**派生后的值**。三处派生缺一不可：
//
//   A. 布局类型 5 步覆盖链 —— 后写的键覆盖先写的，且 fullscreen 的分支
//      依赖上一步的结果（horizontal 决定 fullscreen 往哪个方向扩）。
//   B. fullscreen 三处副作用 —— max_width=0、inline_preedit=false、
//      shadow_radius=0。不做的话，全屏预览会带着最大宽度和阴影。
//   C. padding / margin 的 max 修正 —— margin_x 会被
//      max(hilite_padding_x, |margin_x|) 顶上去，**符号保持**。
//      用户设 margin_x=1 而 hilite_padding=2 时，实际生效的是 2。
//
//  任何一处缺失，面板预览就与真实候选窗对不上，且用户在面板里怎么调都调不出来。
//
//  ── 两层样式（本类对外提供两个入口，对应上游两个 initialize 取值）──────
//   initialize=true  ResolveGlobal      全局层：config_open("weasel")，
//                                       键缺失时写入 falseValue（RimeWithWeasel.cpp:123）。
//   initialize=false ResolveSchemaOverlay 方案层：先重置为全局基础样式，
//                                       再**只覆盖显式存在的键**（第 558-568 行）。
//
//  这意味着：输入方案（schema）可以在自己的 style 段覆盖全局外观。
//  面板若不区分这两层，用户在外观页改了半天没反应，会直接判定为面板有 bug。
//  用 Differences() 即可检测出「哪些项被当前方案覆盖了」。

using System;
using System.Collections.Generic;

namespace WeaselPanel.Core.Rime;

public static class WeaselStyleResolver
{
    /// <summary>
    /// 全局层：从共享 weasel.yaml 与用户 patch 合并后的视图解析出厂/用户样式。
    /// </summary>
    public static WeaselStyle ResolveGlobal(RimeConfigView config) =>
        Resolve(config, WeaselStyle.CreateInitial(), initialize: true);

    /// <summary>
    /// 方案层：以全局样式为基底，用某个输入方案的 style 段增量覆盖。
    /// 只有**显式存在**的键才会覆盖；缺失的键保留全局值。
    /// </summary>
    public static WeaselStyle ResolveSchemaOverlay(WeaselStyle globalStyle, RimeConfigView schemaConfig) =>
        Resolve(schemaConfig, globalStyle.Clone(), initialize: false);

    private static WeaselStyle Resolve(RimeConfigView cfg, WeaselStyle s, bool initialize)
    {
        // ── 1. 行内编码（hilite_spacing 的修正依赖它，必须早于修正段）──────
        // RimeWithWeasel.cpp:1192
        GetBool(cfg, "style/inline_preedit", initialize, v => s.InlinePreedit = v);

        // RimeWithWeasel.cpp:1194
        GetBool(cfg, "style/vertical_auto_reverse", initialize, v => s.VerticalAutoReverse = v);

        // ── 2. 对齐方式 ──────────────────────────────────────────────────
        // RimeWithWeasel.cpp:1222。fallback 是当前值 → 非法值等于没写。
        ParseStringOpt(cfg, "style/layout/align_type", AlignTypes, s.AlignType,
            v => s.AlignType = v);

        // ── 3. 布局类型 5 步覆盖链（顺序不可调整）──────────────────────────

        // ① style/horizontal（RimeWithWeasel.cpp:1229）
        //    initialize=true 且键缺失 → LAYOUT_VERTICAL（falseValue）
        GetBool(cfg, "style/horizontal", initialize,
            v => s.LayoutType = v, WeaselLayoutType.Horizontal, WeaselLayoutType.Vertical);

        // ② style/fullscreen（第 1235 行）
        //    trueValue 依赖上一步结果；falseValue 是当前值 → 写 false 等于没写
        GetBool(cfg, "style/fullscreen", false,
            v => s.LayoutType = v,
            s.LayoutType == WeaselLayoutType.Horizontal
                ? WeaselLayoutType.HorizontalFullscreen
                : WeaselLayoutType.VerticalFullscreen,
            s.LayoutType);

        // ③ style/vertical_text（第 1240 行）
        //    ⚠️ 只有 true 才生效；写 vertical_text: false **无法**取消②的 fullscreen
        GetBool(cfg, "style/vertical_text", false,
            v => s.LayoutType = v, WeaselLayoutType.VerticalText, s.LayoutType);

        // ④ 竖排方向开关（第 1242、1244 行）
        GetBool(cfg, "style/vertical_text_left_to_right", false, v => s.VerticalTextLeftToRight = v);
        GetBool(cfg, "style/vertical_text_with_wrap", false, v => s.VerticalTextWithWrap = v);

        // ⑤ style/text_orientation（第 1246-1253 行）
        //    "vertical" → 强制竖排文字；"horizontal" → 不改（保持前四步结果）
        var textOrientation = false;
        ParseStringOpt(cfg, "style/text_orientation", TextOrientations, textOrientation,
            v => textOrientation = v);
        if (textOrientation)
            s.LayoutType = WeaselLayoutType.VerticalText;

        // ── 4. style/layout/* 尺寸键 ─────────────────────────────────────
        // RimeWithWeasel.cpp:1260-1268，全部走 _abs
        GetInt(cfg, "style/layout/baseline", () => s.Baseline, v => s.Baseline = v, abs: true);
        GetInt(cfg, "style/layout/linespacing", () => s.Linespacing, v => s.Linespacing = v, abs: true);
        GetInt(cfg, "style/layout/min_width", () => s.MinWidth, v => s.MinWidth = v, abs: true);
        GetInt(cfg, "style/layout/max_width", () => s.MaxWidth, v => s.MaxWidth = v, abs: true);
        GetInt(cfg, "style/layout/min_height", () => s.MinHeight, v => s.MinHeight = v, abs: true);
        GetInt(cfg, "style/layout/max_height", () => s.MaxHeight, v => s.MaxHeight = v, abs: true);

        // ⑥ style/layout/type（第 1268-1274 行）—— 链中优先级最高
        ParseStringOpt(cfg, "style/layout/type", LayoutTypes, s.LayoutType, v => s.LayoutType = v);

        // ── 5. fullscreen 三处副作用（第 1276-1280 行）───────────────────
        //    ⚠️ 必须在 max_width 读取之后、shadow_radius 修正之前
        if (s.LayoutType is WeaselLayoutType.HorizontalFullscreen or WeaselLayoutType.VerticalFullscreen)
        {
            s.MaxWidth = 0;
            s.InlinePreedit = false;
        }

        // ── 6. 边框、边距、间距（第 1286-1310 行）────────────────────────
        //    ⚠️ 别键优先：上游是「取主键，取不到再取别键」，
        //       weasel.yaml 出厂写的是 border_width / hilite_padding / round_corner。
        GetInt(cfg, "style/layout/border", () => s.Border, v => s.Border = v,
            aliasKey: "style/layout/border_width", abs: true);

        GetInt(cfg, "style/layout/margin_x", () => s.MarginX, v => s.MarginX = v);
        GetInt(cfg, "style/layout/margin_y", () => s.MarginY, v => s.MarginY = v);

        GetInt(cfg, "style/layout/spacing", () => s.Spacing, v => s.Spacing = v, abs: true);
        GetInt(cfg, "style/layout/candidate_spacing", () => s.CandidateSpacing,
            v => s.CandidateSpacing = v, abs: true);
        GetInt(cfg, "style/layout/hilite_spacing", () => s.HiliteSpacing,
            v => s.HiliteSpacing = v, abs: true);

        GetInt(cfg, "style/layout/hilite_padding_x", () => s.HilitePaddingX,
            v => s.HilitePaddingX = v, aliasKey: "style/layout/hilite_padding", abs: true);
        GetInt(cfg, "style/layout/hilite_padding_y", () => s.HilitePaddingY,
            v => s.HilitePaddingY = v, aliasKey: "style/layout/hilite_padding", abs: true);

        // 阴影半径：先取绝对值，再被 fullscreen 归零（第 1304-1301 行）
        GetInt(cfg, "style/layout/shadow_radius", () => s.ShadowRadius,
            v => s.ShadowRadius = v, abs: true);
        s.ShadowRadius *= s.LayoutType is WeaselLayoutType.HorizontalFullscreen
            or WeaselLayoutType.VerticalFullscreen ? 0 : 1;

        GetInt(cfg, "style/layout/shadow_offset_x", () => s.ShadowOffsetX, v => s.ShadowOffsetX = v);
        GetInt(cfg, "style/layout/shadow_offset_y", () => s.ShadowOffsetY, v => s.ShadowOffsetY = v);

        // 圆角：hilited_corner_radius 与 corner_radius 的别键都是 round_corner
        GetInt(cfg, "style/layout/hilited_corner_radius", () => s.RoundCorner,
            v => s.RoundCorner = v, aliasKey: "style/layout/round_corner", abs: true);
        GetInt(cfg, "style/layout/corner_radius", () => s.RoundCornerEx,
            v => s.RoundCornerEx = v, aliasKey: "style/layout/round_corner", abs: true);

        // ── 7. padding / spacing 的 max 修正（第 1311-1350 行）────────────
        ApplyPaddingFixes(s);

        // ── 8. margin 的 max 修正（第 1352-1354 行）──────────────────────
        //    符号保持：负边距（贴边）在修正后依然是负的
        var scaleX = s.MarginX < 0 ? -1 : 1;
        s.MarginX = scaleX * Math.Max(s.HilitePaddingX, Math.Abs(s.MarginX));
        var scaleY = s.MarginY < 0 ? -1 : 1;
        s.MarginY = scaleY * Math.Max(s.HilitePaddingY, Math.Abs(s.MarginY));

        return s;
    }

    /// <summary>
    /// RimeWithWeasel.cpp:1311-1350。高亮内边距会把 spacing / candidate_spacing /
    /// hilite_spacing 顶上去 —— 否则高亮块会超出候选条目区域被裁掉。
    /// </summary>
    private static void ApplyPaddingFixes(WeaselStyle s)
    {
        if (s.LayoutType != WeaselLayoutType.VerticalText)
        {
            // 横排 / 竖排（非竖排文字）
            s.Spacing = Math.Max(s.Spacing, s.HilitePaddingY * 2);

            s.CandidateSpacing =
                s.LayoutType is WeaselLayoutType.VerticalFullscreen or WeaselLayoutType.Vertical
                    ? Math.Max(s.CandidateSpacing, s.HilitePaddingY * 2)
                    : Math.Max(s.CandidateSpacing, s.HilitePaddingX * 2);

            if (!s.InlinePreedit)
                s.HiliteSpacing = Math.Max(s.HiliteSpacing, s.HilitePaddingX);
        }
        else
        {
            // 竖排文字：x / y 的主次关系与横排相反
            s.Spacing = Math.Max(s.Spacing, s.HilitePaddingX * 2);
            s.CandidateSpacing = Math.Max(s.CandidateSpacing, s.HilitePaddingX * 2);

            if (s.VerticalTextWithWrap)
                s.CandidateSpacing = Math.Max(s.CandidateSpacing, s.HilitePaddingY * 2);

            if (!s.InlinePreedit)
                s.HiliteSpacing = Math.Max(s.HiliteSpacing, s.HilitePaddingY);
        }
    }

    // ── 上游三个辅助模板的等价实现 ──────────────────────────────────────

    /// <summary>
    /// 对应 _RimeGetBool（RimeWithWeasel.cpp:1050）。
    /// <para>
    /// ⚠️ 语义极易误读：第 5 参 trueValue 是「键存在且为真时映射到的值」，
    /// **不是默认值**。键缺失时走 falseValue 分支，且只在 cond 为真时才写。
    /// </para>
    /// </summary>
    private static void GetBool<T>(RimeConfigView cfg, string key, bool cond,
        Action<T> set, T trueValue, T falseValue)
    {
        // 「键是否存在」与「键的值」是两个独立信息，不可合并
        var exists = cfg.TryGetBool(key, out var temp);
        if (exists || cond)
            set(temp ? trueValue : falseValue);
    }

    private static void GetBool(RimeConfigView cfg, string key, bool cond, Action<bool> set) =>
        GetBool(cfg, key, cond, set, true, false);

    /// <summary>
    /// 对应 _RimeGetIntStr 的 int 分支（RimeWithWeasel.cpp:1080）。
    /// 主键取不到再取别键；**两者都取不到时保持原值**，但 abs 修正照样执行
    /// （上游的 func 在 if 之外，无条件作用于 value）。
    /// </summary>
    private static void GetInt(RimeConfigView cfg, string key,
        Func<int> get, Action<int> set, string? aliasKey = null, bool abs = false)
    {
        var value = get();

        if (cfg.TryGetInt(key, out var v)) value = v;
        else if (aliasKey is not null && cfg.TryGetInt(aliasKey, out var fb)) value = fb;

        if (abs) value = Math.Abs(value);
        set(value);
    }

    /// <summary>
    /// 对应 _RimeParseStringOptWithFallback（RimeWithWeasel.cpp:1062）。
    /// 键存在但值不在枚举表中 → 落到 fallback（调用方传当前值即为「等于没写」）。
    /// </summary>
    private static void ParseStringOpt<T>(RimeConfigView cfg, string key,
        IReadOnlyDictionary<string, T> map, T fallback, Action<T> set)
    {
        if (cfg.TryGetString(key, out var text) && map.TryGetValue(text, out var matched))
        {
            set(matched);
            return;
        }
        set(fallback);
    }

    // ── 字符串选项表（与上游 Array<...> 的顺序一一对应）─────────────────

    private static readonly Dictionary<string, WeaselLayoutAlignType> AlignTypes =
        new(StringComparer.Ordinal)
        {
            ["bottom"] = WeaselLayoutAlignType.Bottom,
            ["center"] = WeaselLayoutAlignType.Center,
            ["top"] = WeaselLayoutAlignType.Top,
        };

    /// <summary>
    /// style/layout/type 的 5 个取值（RimeWithWeasel.cpp:1264-1271）。
    /// 这是布局链的最后一环，优先级最高。
    /// </summary>
    private static readonly Dictionary<string, WeaselLayoutType> LayoutTypes =
        new(StringComparer.Ordinal)
        {
            ["vertical"] = WeaselLayoutType.Vertical,
            ["horizontal"] = WeaselLayoutType.Horizontal,
            ["vertical_text"] = WeaselLayoutType.VerticalText,
            ["vertical+fullscreen"] = WeaselLayoutType.VerticalFullscreen,
            ["horizontal+fullscreen"] = WeaselLayoutType.HorizontalFullscreen,
        };

    /// <summary>
    /// style/text_orientation（RimeWithWeasel.cpp:1246）。
    /// 注意映射的是 bool：只有 "vertical" 会产生副作用，"horizontal" 是 no-op。
    /// </summary>
    private static readonly Dictionary<string, bool> TextOrientations =
        new(StringComparer.Ordinal)
        {
            ["horizontal"] = false,
            ["vertical"] = true,
        };
}
