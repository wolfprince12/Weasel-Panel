//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。配色回退链、alpha 混合与内置配色目录测试。
//
//  这些用例守的是「面板预览 == 真实候选窗」：
//  上游绝大多数配色只写 6–10 个键，其余全靠回退链补齐，
//  少继承一层，预览就会和实际渲染对不上。

using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

public class ColorSchemeResolverTests
{
    private static ResolvedColorScheme Resolve(IReadOnlyDictionary<string, object?> scheme) =>
        ColorSchemeResolver.Resolve(key => scheme.TryGetValue(key, out var v) ? v : null);

    private static Dictionary<string, object?> Scheme(params (string Key, object? Value)[] entries)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    // ── 最小配色：验证整条回退链 ────────────────────────────────────────

    [Fact]
    public void 只给文字色与背景色时其余通道按上游回退()
    {
        var s = Resolve(Scheme(
            ("text_color", "0xff112233"),
            ("back_color", "0xff445566")));

        Assert.Equal(0xff112233u, s.TextColor);
        Assert.Equal(0xff445566u, s.BackColor);

        // candidate_text_color ← text_color
        Assert.Equal(s.TextColor, s.CandidateTextColor);
        // border_color ← text_color
        Assert.Equal(s.TextColor, s.BorderColor);
        // hilited_text_color ← text_color
        Assert.Equal(s.TextColor, s.HilitedTextColor);
        // hilited_back_color ← back_color
        Assert.Equal(s.BackColor, s.HilitedBackColor);
        // hilited_candidate_text_color ← hilited_text_color
        Assert.Equal(s.HilitedTextColor, s.HilitedCandidateTextColor);
        // hilited_candidate_back_color ← hilited_back_color
        Assert.Equal(s.HilitedBackColor, s.HilitedCandidateBackColor);
        // comment_text_color ← label_text_color
        Assert.Equal(s.LabelTextColor, s.CommentTextColor);
        // hilited_comment_text_color ← hilited_label_text_color
        Assert.Equal(s.HilitedLabelTextColor, s.HilitedCommentTextColor);

        // 未指定且无派生来源的，一律为全透明
        Assert.Equal(0u, s.ShadowColor);
        Assert.Equal(0u, s.PrevPageColor);
        Assert.Equal(0u, s.NextPageColor);
        Assert.Equal(0u, s.CandidateBackColor);
        Assert.Equal(0u, s.HilitedShadowColor);
        Assert.Equal(0u, s.CandidateShadowColor);
        Assert.Equal(0u, s.CandidateBorderColor);
        Assert.Equal(0u, s.HilitedCandidateBorderColor);
        Assert.Equal(0u, s.HilitedMarkColor);
    }

    [Fact]
    public void 文本色默认值是不透明黑_背景色默认值是不透明白()
    {
        var s = Resolve(Scheme());
        Assert.Equal(0xff000000u, s.TextColor);
        Assert.Equal(0xffffffffu, s.BackColor);
    }

    // ── label / comment 是混合出来的，不是简单继承 ──────────────────────

    [Fact]
    public void 序号色由候选文字色与候选背景色混合而来()
    {
        var s = Resolve(Scheme(
            ("text_color", "0xff000000"),
            ("candidate_text_color", "0x80112233"),   // 半透明
            ("candidate_back_color", "0xff445566")));

        // 既不等于前景，也不等于背景 —— 证明确实发生了混合而非继承
        Assert.NotEqual(s.CandidateTextColor, s.LabelTextColor);
        Assert.NotEqual(s.CandidateBackColor, s.LabelTextColor);

        // 混合结果的 alpha 应为不透明（背景不透明 → 结果不透明）
        Assert.Equal(0xFFu, (s.LabelTextColor >> 24) & 0xFF);

        // 各通道落在前景与背景之间
        foreach (var shift in new[] { 0, 8, 16 })
        {
            var fg = (s.CandidateTextColor >> shift) & 0xFF;
            var bg = (s.CandidateBackColor >> shift) & 0xFF;
            var got = (s.LabelTextColor >> shift) & 0xFF;
            Assert.InRange(got, Math.Min(fg, bg), Math.Max(fg, bg));
        }

        // comment 再继承 label
        Assert.Equal(s.LabelTextColor, s.CommentTextColor);
    }

    [Fact]
    public void 显式指定序号色时不走混合()
    {
        var s = Resolve(Scheme(
            ("candidate_text_color", "0x80112233"),
            ("candidate_back_color", "0xff445566"),
            ("label_color", "0xff0000ff")));

        Assert.Equal(0xff0000ffu, s.LabelTextColor);
        Assert.Equal(s.LabelTextColor, s.CommentTextColor);
    }

    [Fact]
    public void 回退链顺序不可交换_注释色依赖序号色而非候选文字色()
    {
        // 若实现把 comment_text_color 写成回退到 candidate_text_color，
        // 本用例会失败 —— 这正是上游的声明顺序所禁止的。
        var s = Resolve(Scheme(
            ("label_color", "0xff00ff00"),
            ("candidate_text_color", "0xff0000ff")));

        Assert.Equal(0xff00ff00u, s.CommentTextColor);
        Assert.NotEqual(s.CandidateTextColor, s.CommentTextColor);
    }

    // ── per-scheme 的 color_format ─────────────────────────────────────

    [Fact]
    public void 指定argb时按ARGB解释()
    {
        var s = Resolve(Scheme(
            ("color_format", "argb"),
            ("text_color", "0xff112233")));

        Assert.Equal(RimeColorFormat.Argb, s.Format);
        // 0xff112233 (argb) → A=FF R=11 G=22 B=33 → 归一到 ABGR 为 0xff332211
        Assert.Equal(0xff332211u, s.TextColor);
    }

    [Fact]
    public void 未指定color_format时默认abgr()
    {
        var s = Resolve(Scheme(("text_color", "0xff112233")));
        Assert.Equal(RimeColorFormat.Abgr, s.Format);
        Assert.Equal(0xff112233u, s.TextColor);
    }

    [Fact]
    public void 未知color_format回退abgr而不是报错()
    {
        var s = Resolve(Scheme(
            ("color_format", "srgb"),
            ("text_color", "0xff112233")));

        Assert.Equal(RimeColorFormat.Abgr, s.Format);
        Assert.Equal(0xff112233u, s.TextColor);
    }

    // ── 非法值 ─────────────────────────────────────────────────────────

    [Fact]
    public void 非法颜色字面量落回退色而不是抛异常()
    {
        var s = Resolve(Scheme(("text_color", "not-a-color")));
        Assert.Equal(0xff000000u, s.TextColor);
    }

    // ── blend_colors（对齐上游同名函数）────────────────────────────────

    [Fact]
    public void 混合_前景不透明时结果等于前景()
    {
        Assert.Equal(0xff112233u, ColorSchemeResolver.BlendColors(0xff112233u, 0x00445566u));
    }

    [Fact]
    public void 混合_前景全透明背景不透明时结果等于背景且alpha升为不透明()
    {
        Assert.Equal(0xff445566u, ColorSchemeResolver.BlendColors(0x00112233u, 0xff445566u));
    }

    [Fact]
    public void 混合_两者都全透明时直接返回背景()
    {
        // 上游：retAlpha <= 1e-6 → return bcolor（原样，不补 alpha）
        Assert.Equal(0x00445566u, ColorSchemeResolver.BlendColors(0x00112233u, 0x00445566u));
    }

    [Fact]
    public void 混合_半透明黑压不透明白得到中灰()
    {
        var got = ColorSchemeResolver.BlendColors(0x80000000u, 0xffffffffu);
        Assert.Equal(0xFFu, (got >> 24) & 0xFF);
        // 精确值依赖 float 逐位行为，故只锁区间
        Assert.InRange((got >> 16) & 0xFF, 126u, 128u);
        Assert.InRange((got >> 8) & 0xFF, 126u, 128u);
        Assert.InRange(got & 0xFF, 126u, 128u);
    }

    [Fact]
    public void 混合不满足交换律_前景与背景次序不可颠倒()
    {
        Assert.NotEqual(
            ColorSchemeResolver.BlendColors(0x80000000u, 0xffffffffu),
            ColorSchemeResolver.BlendColors(0xffffffffu, 0x80000000u));
    }
}

public class ColorSchemeCatalogTests
{
    private const string Fixture = """
        config_version: "0.23"
        style:
          color_scheme: aqua
          font_point: 14
        preset_color_schemes:
          aqua:
            name: 碧水／Aqua
            author: 佛振 <chen.sst@gmail.com>
            text_color: 0x000000
            back_color: 0xeceeee
            hilited_candidate_back_color: 0xfa3a0a
          minimal:
            text_color: 0xff112233
        """;

    [Fact]
    public void 解析出全部方案且保持声明顺序()
    {
        var catalog = ColorSchemeCatalog.Parse(Fixture);
        Assert.Equal(new[] { "aqua", "minimal" }, catalog.Names);
    }

    [Fact]
    public void 取显示名与作者()
    {
        var catalog = ColorSchemeCatalog.Parse(Fixture);
        Assert.Equal("碧水／Aqua", catalog.DisplayName("aqua"));
        Assert.Equal("佛振 <chen.sst@gmail.com>", catalog.Author("aqua"));
        // 无 name 字段时回退为 id
        Assert.Equal("minimal", catalog.DisplayName("minimal"));
    }

    [Fact]
    public void 解析结果套用了回退链()
    {
        var catalog = ColorSchemeCatalog.Parse(Fixture);

        var aqua = catalog.Resolve("aqua");
        Assert.NotNull(aqua);
        // 0x000000 → 6 位 → |0xff000000 → 不透明黑
        Assert.Equal(0xff000000u, aqua!.TextColor);
        Assert.Equal(0xffeceeeeu, aqua.BackColor);
        Assert.Equal(0xfffa3a0au, aqua.HilitedCandidateBackColor);
        // 未写的 hilited_text_color 回退到 text_color
        Assert.Equal(aqua.TextColor, aqua.HilitedTextColor);

        var minimal = catalog.Resolve("minimal");
        Assert.NotNull(minimal);
        Assert.Equal(0xff112233u, minimal!.TextColor);
        // 未写 back_color → 上游默认不透明白
        Assert.Equal(0xffffffffu, minimal.BackColor);
    }

    [Fact]
    public void 方案不存在时返回null而不是抛异常()
    {
        var catalog = ColorSchemeCatalog.Parse(Fixture);
        Assert.False(catalog.Contains("nope"));
        Assert.Null(catalog.Resolve("nope"));
    }

    [Fact]
    public void 空文本或非法YAML降级为空目录()
    {
        Assert.Empty(ColorSchemeCatalog.Parse("").Names);
        Assert.Empty(ColorSchemeCatalog.Parse("style:\n  - [unterminated\n").Names);
        Assert.Empty(ColorSchemeCatalog.Parse("style:\n  color_scheme: aqua\n").Names);
    }

    // ── 上游真实方案抽样 ────────────────────────────────────────────────
    //
    // 以下两套取自上游 output/data/weasel.yaml（曾以原始 18KB 文件全量验证：
    // 36 套方案全部解析成功、0 异常）。此处只抽样保留，避免把上游数据整体搬进仓库。

    [Fact]
    public void 上游aqua方案解析结果与出厂值一致()
    {
        // 上游 aqua：text_color 0x000000 / back_color 0xeceeee /
        // hilited_candidate_back_color 0xfa3a0a，其余靠回退。
        var aqua = ColorSchemeCatalog.Parse("""
            preset_color_schemes:
              aqua:
                name: 碧水／Aqua
                text_color: 0x000000
                back_color: 0xeceeee
                hilited_candidate_back_color: 0xfa3a0a
            """).Resolve("aqua");

        Assert.NotNull(aqua);
        Assert.Equal(0xFF000000u, aqua!.TextColor);                  // 0x000000 → 不透明黑
        Assert.Equal(0xFFECEEEEu, aqua.BackColor);
        Assert.Equal(0xFFFA3A0Au, aqua.HilitedCandidateBackColor);
        // 未写的 hilited_back_color ← back_color
        Assert.Equal(aqua.BackColor, aqua.HilitedBackColor);
        // candidate_text_color 未写 → 回退 text_color（不透明）→ 混合结果就是它本身
        Assert.Equal(0xFF000000u, aqua.LabelTextColor);
    }

    [Fact]
    public void 上游psionics方案的显式键全部命中()
    {
        var p = ColorSchemeCatalog.Parse("""
            preset_color_schemes:
              psionics:
                name: 幽能／Psionics
                text_color: 0xc2c2c2
                back_color: 0x444444
                border_color: 0x444444
                candidate_text_color: 0xeeeeee
                hilited_text_color: 0xeeeeee
                hilited_back_color: 0x444444
                hilited_candidate_label_color: 0xfafafa
                hilited_candidate_text_color: 0xfafafa
                hilited_candidate_back_color: 0xd8bf00
                comment_text_color: 0x808080
                hilited_comment_text_color: 0x444444
            """).Resolve("psionics");

        Assert.NotNull(p);
        Assert.Equal(0xFFC2C2C2u, p!.TextColor);
        Assert.Equal(0xFF444444u, p.BackColor);
        Assert.Equal(0xFF444444u, p.BorderColor);
        Assert.Equal(0xFFEEEEEEu, p.CandidateTextColor);
        Assert.Equal(0xFFEEEEEEu, p.HilitedTextColor);
        Assert.Equal(0xFF444444u, p.HilitedBackColor);
        Assert.Equal(0xFFFAFAFAu, p.HilitedCandidateTextColor);
        Assert.Equal(0xFFD8BF00u, p.HilitedCandidateBackColor);
        Assert.Equal(0xFF808080u, p.CommentTextColor);
        Assert.Equal(0xFF444444u, p.HilitedCommentTextColor);
    }

    [Fact]
    public void hilited_candidate_label_color是上游死键_面板不得读取()
    {
        // ⚠️ 上游 psionics 方案里写了 hilited_candidate_label_color，
        //    但 _UpdateUIStyleColor 的 COLOR() 列表中只有 hilited_label_color，
        //    没有 hilited_candidate_label_color —— 即该键小狼毫**根本不读**。
        //    面板若照着 YAML 字面读它，预览会与实际渲染不一致。
        var s = ColorSchemeResolver.Resolve(key => key switch
        {
            "hilited_candidate_label_color" => "0xff112233",   // 死键
            "hilited_candidate_text_color" => "0xff445566",
            "hilited_candidate_back_color" => "0xff778899",
            _ => null
        });

        Assert.Equal(0xFF445566u, s.HilitedCandidateTextColor);
        // 高亮序号色来自「高亮候选文字色 × 高亮候选背景色」的混合，与死键无关
        Assert.NotEqual(0xFF112233u, s.HilitedLabelTextColor);
        Assert.Equal(0xFF445566u, s.HilitedLabelTextColor);   // 前景不透明 → 结果即前景
    }
}
