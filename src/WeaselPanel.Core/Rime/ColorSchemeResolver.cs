//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  配色方案的解析与回退链。
//
//  GPL-3.0。直译自上游 RimeWithWeasel/RimeWithWeasel.cpp 的
//  _UpdateUIStyleColor()（约第 1361–1418 行）与 blend_colors()（约第 940–970 行）。
//
//  ── 为什么必须复刻回退链 ──────────────────────────────────────────────
//  小狼毫的 YAML 里绝大多数配色只写了 6–10 个颜色键，剩下的全靠回退补齐。
//  面板若只「读到什么显示什么」，预览会与实际候选窗差别极大
//  （例如只写 text_color / back_color 的方案，其 label_color 并非某个固定色，
//   而是 blend_colors(candidate_text_color, candidate_back_color) 算出来的）。
//
//  ── 顺序依赖警告（改动前必读）────────────────────────────────────────
//  下列回退引用的是 **前序已解析结果**，不是常量：
//      candidate_text_color ← text_color
//      border_color         ← text_color
//      hilited_text_color   ← text_color
//      hilited_back_color   ← back_color
//      hilited_candidate_text_color ← hilited_text_color
//      hilited_candidate_back_color ← hilited_back_color
//      label_color          ← blend(candidate_text_color, candidate_back_color)
//      hilited_label_color  ← blend(hilited_candidate_text_color, hilited_candidate_back_color)
//      comment_text_color   ← label_text_color（即上一步的结果）
//      hilited_comment_text_color ← hilited_label_text_color
//  因此本类**必须按上游声明顺序逐条解析**，任何重排都会静默改变配色。
//  测试 `回退链顺序不可交换` 守着这一点。

using System;
using System.Globalization;

namespace WeaselPanel.Core.Rime;

/// <summary>一套配色解析完成后的 22 个颜色通道，一律为 ABGR 打包值（0xAABBGGRR）。</summary>
public sealed class ResolvedColorScheme
{
    public uint BackColor { get; internal set; }
    public uint ShadowColor { get; internal set; }
    public uint PrevPageColor { get; internal set; }
    public uint NextPageColor { get; internal set; }
    public uint TextColor { get; internal set; }
    public uint CandidateTextColor { get; internal set; }
    public uint CandidateBackColor { get; internal set; }
    public uint BorderColor { get; internal set; }
    public uint HilitedTextColor { get; internal set; }
    public uint HilitedBackColor { get; internal set; }
    public uint HilitedCandidateTextColor { get; internal set; }
    public uint HilitedCandidateBackColor { get; internal set; }
    public uint HilitedCandidateShadowColor { get; internal set; }
    public uint HilitedShadowColor { get; internal set; }
    public uint CandidateShadowColor { get; internal set; }
    public uint CandidateBorderColor { get; internal set; }
    public uint HilitedCandidateBorderColor { get; internal set; }
    public uint LabelTextColor { get; internal set; }
    public uint HilitedLabelTextColor { get; internal set; }
    public uint CommentTextColor { get; internal set; }
    public uint HilitedCommentTextColor { get; internal set; }
    public uint HilitedMarkColor { get; internal set; }

    /// <summary>本方案最终采用的字节序（供回写时使用）。</summary>
    public RimeColorFormat Format { get; internal set; }
}

public static class ColorSchemeResolver
{
    /// <summary>上游常量：全透明。</summary>
    public const uint TransparentColor = 0x00000000u;

    /// <summary>
    /// 解析一套配色。
    /// </summary>
    /// <param name="lookup">
    /// 取本方案节点下的键（如 "text_color"、"color_format"），返回 YAML 原始值；
    /// 键不存在返回 null。用委托而非字典，是为了让本类与 YAML 实现解耦、便于测试。
    /// </param>
    public static ResolvedColorScheme Resolve(Func<string, object?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var format = RimeColorFormatExtensions.FromName(lookup("color_format") as string);
        var scheme = new ResolvedColorScheme { Format = format };

        // 与上游一致：解析失败（非法字面量、类型不符）一律用回退值，不抛异常。
        uint Color(string key, uint fallback) =>
            RimeColor.TryParseAbgr(lookup(key), format, out var abgr) ? abgr : fallback;

        // ⚠️ 以下 22 行的顺序与上游 COLOR() 宏逐一对应，不可重排。
        scheme.BackColor = Color("back_color", 0xFFFFFFFFu);
        scheme.ShadowColor = Color("shadow_color", TransparentColor);
        scheme.PrevPageColor = Color("prevpage_color", TransparentColor);
        scheme.NextPageColor = Color("nextpage_color", TransparentColor);
        scheme.TextColor = Color("text_color", 0xFF000000u);
        scheme.CandidateTextColor = Color("candidate_text_color", scheme.TextColor);
        scheme.CandidateBackColor = Color("candidate_back_color", TransparentColor);
        scheme.BorderColor = Color("border_color", scheme.TextColor);
        scheme.HilitedTextColor = Color("hilited_text_color", scheme.TextColor);
        scheme.HilitedBackColor = Color("hilited_back_color", scheme.BackColor);
        scheme.HilitedCandidateTextColor = Color("hilited_candidate_text_color", scheme.HilitedTextColor);
        scheme.HilitedCandidateBackColor = Color("hilited_candidate_back_color", scheme.HilitedBackColor);
        scheme.HilitedCandidateShadowColor = Color("hilited_candidate_shadow_color", TransparentColor);
        scheme.HilitedShadowColor = Color("hilited_shadow_color", TransparentColor);
        scheme.CandidateShadowColor = Color("candidate_shadow_color", TransparentColor);
        scheme.CandidateBorderColor = Color("candidate_border_color", TransparentColor);
        scheme.HilitedCandidateBorderColor = Color("hilited_candidate_border_color", TransparentColor);

        // 序号 / 注释色不是简单回退，而是前景与背景的 alpha 混合
        scheme.LabelTextColor = Color("label_color",
            BlendColors(scheme.CandidateTextColor, scheme.CandidateBackColor));
        scheme.HilitedLabelTextColor = Color("hilited_label_color",
            BlendColors(scheme.HilitedCandidateTextColor, scheme.HilitedCandidateBackColor));
        scheme.CommentTextColor = Color("comment_text_color", scheme.LabelTextColor);
        scheme.HilitedCommentTextColor = Color("hilited_comment_text_color", scheme.HilitedLabelTextColor);
        scheme.HilitedMarkColor = Color("hilited_mark_color", TransparentColor);

        return scheme;
    }

    /// <summary>
    /// 前景与背景按 alpha 混合，返回 ABGR。
    /// 直译自上游 blend_colors()：输入与输出都在 ABGR 空间（上游注释写作
    /// "Extract ARGB channels"，实际按 A/B/G/R 取值，即 ABGR，与返回值注释一致）。
    /// </summary>
    public static uint BlendColors(uint fcolor, uint bcolor)
    {
        byte fA = (byte)((fcolor >> 24) & 0xFF);
        byte fB = (byte)((fcolor >> 16) & 0xFF);
        byte fG = (byte)((fcolor >> 8) & 0xFF);
        byte fR = (byte)(fcolor & 0xFF);
        byte bA = (byte)((bcolor >> 24) & 0xFF);
        byte bB = (byte)((bcolor >> 16) & 0xFF);
        byte bG = (byte)((bcolor >> 8) & 0xFF);
        byte bR = (byte)(bcolor & 0xFF);

        float fAlpha = fA / 255.0f;
        float bAlpha = bA / 255.0f;

        float retAlpha = fAlpha + (1 - fAlpha) * bAlpha;
        if (retAlpha <= 1e-6f)
        {
            // 完全透明 —— 上游直接返回背景色本身
            return bcolor;
        }

        // 注意用 float 而非 double：上游是 float，换精度会与渲染结果差 1 个色阶
        byte Mix(float fc, float bc) =>
            (byte)Math.Floor((fc * fAlpha + bc * bAlpha * (1 - fAlpha)) / retAlpha);

        byte retR = Mix(fR, bR);
        byte retG = Mix(fG, bG);
        byte retB = Mix(fB, bB);
        byte outA = (byte)Math.Floor(retAlpha * 255.0f);

        return ((uint)outA << 24) | ((uint)retB << 16) | ((uint)retG << 8) | retR;
    }
}
