//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  样式数据：字段严格对齐上游 UIStyle（include/WeaselIPCData.h:195）的布局部分。
//  GPL-3.0。
//
//  ── 本类只覆盖「布局派生状态」，颜色不在此处 ──────────────────────────
//  颜色由 ColorSchemeResolver 负责（22 条回退链 + blend_colors）。
//  刻意不把颜色塞进来，避免出现「一半字段已解析、一半还是零值」的半成品状态 ——
//  那种状态在 UI 上表现为预览错色，且极难定位。
//
//  ── 为什么字段是可变的 ────────────────────────────────────────────────
//  上游 _UpdateUIStyle 是「拿到一个 UIStyle& 逐步改写」，且存在顺序依赖：
//  后面的键会覆盖前面的结果，还要根据中间结果做修正（见 WeaselStyleResolver）。
//  用不可变 + with 表达式会让这段代码变得难读且易错，故沿用可变形态。
//
//  ⚠️ CreateInitial() 的初值来自 UIStyle 构造函数，**不是** weasel.yaml。
//  二者含义不同：构造初值是「键全部缺失时的兜底」，
//  而出厂默认值是「共享 weasel.yaml + 用户 patch 合并后的值」（见 WeaselDefaults）。

using System;
using System.Collections.Generic;

namespace WeaselPanel.Core.Rime;

/// <summary>布局类型。枚举值顺序与上游一致，**不可调整**（存在按数值索引的查表）。</summary>
public enum WeaselLayoutType
{
    Vertical = 0,
    Horizontal,
    VerticalText,
    VerticalFullscreen,
    HorizontalFullscreen,
}

/// <summary>候选窗对齐方式。</summary>
public enum WeaselLayoutAlignType
{
    Bottom = 0,
    Center,
    Top,
}

public sealed class WeaselStyle
{
    // ── 布局类型与方向 ──────────────────────────────────────────────────
    public WeaselLayoutType LayoutType { get; set; }
    public WeaselLayoutAlignType AlignType { get; set; }
    public bool VerticalTextLeftToRight { get; set; }
    public bool VerticalTextWithWrap { get; set; }
    public bool VerticalAutoReverse { get; set; }

    /// <summary>
    /// 行内编码。本不属于「布局」，但 hilite_spacing 的修正依赖它
    /// （RimeWithWeasel.cpp:1336 `if (!style.inline_preedit)`），故一并解析。
    /// </summary>
    public bool InlinePreedit { get; set; }

    // ── style/layout/* 尺寸 ────────────────────────────────────────────
    public int MinWidth { get; set; }
    public int MaxWidth { get; set; }
    public int MinHeight { get; set; }
    public int MaxHeight { get; set; }
    public int Border { get; set; }
    public int MarginX { get; set; }
    public int MarginY { get; set; }
    public int Spacing { get; set; }
    public int CandidateSpacing { get; set; }
    public int HiliteSpacing { get; set; }
    public int HilitePaddingX { get; set; }
    public int HilitePaddingY { get; set; }
    public int RoundCorner { get; set; }

    /// <summary>对应 style/layout/corner_radius（候选窗整体圆角），别键 round_corner。</summary>
    public int RoundCornerEx { get; set; }

    public int ShadowRadius { get; set; }
    public int ShadowOffsetX { get; set; }
    public int ShadowOffsetY { get; set; }
    public int Baseline { get; set; }
    public int Linespacing { get; set; }

    /// <summary>
    /// 上游 UIStyle 构造函数的初值（WeaselIPCData.h:295-366）。
    /// 即「所有键都不存在时的状态」，也是方案层增量覆盖的起点之一。
    /// </summary>
    public static WeaselStyle CreateInitial() => new()
    {
        LayoutType = WeaselLayoutType.Vertical,
        AlignType = WeaselLayoutAlignType.Bottom,
        VerticalTextLeftToRight = false,
        VerticalTextWithWrap = false,
        VerticalAutoReverse = false,
        InlinePreedit = false,
        MinWidth = 0,
        MaxWidth = 0,
        MinHeight = 0,
        MaxHeight = 0,
        Border = 0,
        MarginX = 0,
        MarginY = 0,
        Spacing = 0,
        CandidateSpacing = 0,
        HiliteSpacing = 0,
        HilitePaddingX = 0,
        HilitePaddingY = 0,
        RoundCorner = 0,
        RoundCornerEx = 0,
        ShadowRadius = 0,
        ShadowOffsetX = 0,
        ShadowOffsetY = 0,
        Baseline = 0,
        Linespacing = 0,
    };

    public WeaselStyle Clone() => (WeaselStyle)MemberwiseClone();

    /// <summary>
    /// 逐字段比较，返回与 other 不同的字段名。
    /// 用途：面板提示「当前输入方案覆盖了以下外观项，改动全局设置不会生效」。
    /// </summary>
    public IReadOnlyList<string> Differences(WeaselStyle other)
    {
        var diff = new List<string>();
        if (LayoutType != other.LayoutType) diff.Add(nameof(LayoutType));
        if (AlignType != other.AlignType) diff.Add(nameof(AlignType));
        if (VerticalTextLeftToRight != other.VerticalTextLeftToRight) diff.Add(nameof(VerticalTextLeftToRight));
        if (VerticalTextWithWrap != other.VerticalTextWithWrap) diff.Add(nameof(VerticalTextWithWrap));
        if (VerticalAutoReverse != other.VerticalAutoReverse) diff.Add(nameof(VerticalAutoReverse));
        if (InlinePreedit != other.InlinePreedit) diff.Add(nameof(InlinePreedit));
        if (MinWidth != other.MinWidth) diff.Add(nameof(MinWidth));
        if (MaxWidth != other.MaxWidth) diff.Add(nameof(MaxWidth));
        if (MinHeight != other.MinHeight) diff.Add(nameof(MinHeight));
        if (MaxHeight != other.MaxHeight) diff.Add(nameof(MaxHeight));
        if (Border != other.Border) diff.Add(nameof(Border));
        if (MarginX != other.MarginX) diff.Add(nameof(MarginX));
        if (MarginY != other.MarginY) diff.Add(nameof(MarginY));
        if (Spacing != other.Spacing) diff.Add(nameof(Spacing));
        if (CandidateSpacing != other.CandidateSpacing) diff.Add(nameof(CandidateSpacing));
        if (HiliteSpacing != other.HiliteSpacing) diff.Add(nameof(HiliteSpacing));
        if (HilitePaddingX != other.HilitePaddingX) diff.Add(nameof(HilitePaddingX));
        if (HilitePaddingY != other.HilitePaddingY) diff.Add(nameof(HilitePaddingY));
        if (RoundCorner != other.RoundCorner) diff.Add(nameof(RoundCorner));
        if (RoundCornerEx != other.RoundCornerEx) diff.Add(nameof(RoundCornerEx));
        if (ShadowRadius != other.ShadowRadius) diff.Add(nameof(ShadowRadius));
        if (ShadowOffsetX != other.ShadowOffsetX) diff.Add(nameof(ShadowOffsetX));
        if (ShadowOffsetY != other.ShadowOffsetY) diff.Add(nameof(ShadowOffsetY));
        if (Baseline != other.Baseline) diff.Add(nameof(Baseline));
        if (Linespacing != other.Linespacing) diff.Add(nameof(Linespacing));
        return diff;
    }
}
