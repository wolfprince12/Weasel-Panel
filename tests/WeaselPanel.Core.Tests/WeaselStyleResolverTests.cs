//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  WeaselStyleResolver 的布局派生测试。
//  GPL-3.0。
//
//  每个用例都标注了对应的上游行号与推导链，改实现前请先读懂推导 ——
//  这里的断言锁的是「小狼毫实际会怎么渲染」，不是「配置里写了什么」。

using WeaselPanel.Core.Rime;
using Xunit;

namespace WeaselPanel.Core.Tests;

public class WeaselStyleResolverTests
{
    private static WeaselStyle Resolve(string yaml) =>
        WeaselStyleResolver.ResolveGlobal(RimeConfigView.FromYaml(yaml));

    /// <summary>
    /// 上游 output/data/weasel.yaml 的 style 段（已剥离注释与 preset_color_schemes）。
    /// 用于验证「出厂配置」的派生结果。
    /// </summary>
    internal const string FactoryWeaselYaml = """
        style:
          antialias_mode: default
          ascii_tip_follow_cursor: false
          color_scheme: aqua
          font_face: Microsoft YaHei
          font_point: 14
          candidate_abbreviate_length: 0
          click_to_capture: false
          horizontal: false
          fullscreen: false
          inline_preedit: false
          preedit_type: composition
          display_tray_icon: false
          label_format: "%s."
          mark_text: ""
          hover_type: none
          paging_on_scroll: false
          vertical_auto_reverse: false
          vertical_text: false
          vertical_text_left_to_right: false
          vertical_text_with_wrap: false
          layout:
            align_type: center
            max_width: 0
            min_width: 160
            min_height: 0
            max_height: 0
            border_width: 3
            margin_x: 12
            margin_y: 12
            spacing: 10
            candidate_spacing: 5
            hilite_spacing: 4
            hilite_padding: 2
            round_corner: 4
            corner_radius: 4
            shadow_radius: 0
            shadow_offset_x: 4
            shadow_offset_y: 4
            linespacing: 0
            baseline: 0
        """;

    [Fact]
    public void 出厂配置的派生值全部可预期()
    {
        var s = Resolve(FactoryWeaselYaml);

        // 布局链：horizontal=false → Vertical；fullscreen/vertical_text 写 false 均不改
        Assert.Equal(WeaselLayoutType.Vertical, s.LayoutType);
        Assert.Equal(WeaselLayoutAlignType.Center, s.AlignType);

        // 尺寸键
        Assert.Equal(160, s.MinWidth);
        Assert.Equal(0, s.MaxWidth);
        Assert.Equal(0, s.MinHeight);
        Assert.Equal(0, s.MaxHeight);

        // 别名回退：主键 border 未写，落到别键 border_width
        Assert.Equal(3, s.Border);

        // padding 修正后（hilite_padding=2，远小于各 spacing，故不变）
        Assert.Equal(12, s.MarginX);
        Assert.Equal(12, s.MarginY);
        Assert.Equal(10, s.Spacing);
        Assert.Equal(5, s.CandidateSpacing);
        Assert.Equal(4, s.HiliteSpacing);

        // 别名回退：hilite_padding_x/y 均未写，落到 hilite_padding
        Assert.Equal(2, s.HilitePaddingX);
        Assert.Equal(2, s.HilitePaddingY);

        // 圆角：hilited_corner_radius 未写→round_corner；corner_radius 写了 4
        Assert.Equal(4, s.RoundCorner);
        Assert.Equal(4, s.RoundCornerEx);

        Assert.Equal(0, s.ShadowRadius);
        Assert.Equal(4, s.ShadowOffsetX);
        Assert.Equal(4, s.ShadowOffsetY);
    }

    // ── 布局类型 5 步覆盖链 ────────────────────────────────────────────

    [Fact]
    public void 第一步horizontal为真时得到横排()
    {
        var s = Resolve("style:\n  horizontal: true\n");
        Assert.Equal(WeaselLayoutType.Horizontal, s.LayoutType);
    }

    [Fact]
    public void 第二步fullscreen的分支依赖上一步是横排还是竖排()
    {
        // 横排 + 全屏 → 横排全屏
        Assert.Equal(WeaselLayoutType.HorizontalFullscreen,
            Resolve("style:\n  horizontal: true\n  fullscreen: true\n").LayoutType);

        // 竖排 + 全屏 → 竖排全屏
        Assert.Equal(WeaselLayoutType.VerticalFullscreen,
            Resolve("style:\n  horizontal: false\n  fullscreen: true\n").LayoutType);
    }

    [Fact]
    public void 第三步vertical_text为真可压掉fullscreen()
    {
        // ①②③：Horizontal → HorizontalFullscreen → VerticalText
        // 反直觉但真实：vertical_text 的 falseValue 是当前值，
        // 只有 true 才会改写，故它能覆盖掉 ② 的全屏结果。
        var s = Resolve("""
            style:
              horizontal: true
              fullscreen: true
              vertical_text: true
            """);
        Assert.Equal(WeaselLayoutType.VerticalText, s.LayoutType);
    }

    [Fact]
    public void vertical_text写false无法取消fullscreen()
    {
        // 这是面板最容易踩的坑：用户勾掉「竖排文字」期待回到全屏竖排，
        // 但 false 的映射值就是当前值，等于什么都没做 —— 仍是竖排全屏。
        var s = Resolve("""
            style:
              horizontal: false
              fullscreen: true
              vertical_text: false
            """);
        Assert.Equal(WeaselLayoutType.VerticalFullscreen, s.LayoutType);
    }

    [Fact]
    public void 第五步layout_type优先级最高()
    {
        var s = Resolve("""
            style:
              horizontal: true
              fullscreen: true
              vertical_text: true
              text_orientation: vertical
              layout:
                type: vertical+fullscreen
            """);
        Assert.Equal(WeaselLayoutType.VerticalFullscreen, s.LayoutType);
    }

    [Fact]
    public void 第四步text_orientation只有vertical会产生副作用()
    {
        // "vertical" → 强制竖排文字
        Assert.Equal(WeaselLayoutType.VerticalText,
            Resolve("style:\n  text_orientation: vertical\n").LayoutType);

        // "horizontal" → 不改（保持出厂的竖排）
        Assert.Equal(WeaselLayoutType.Vertical,
            Resolve("style:\n  text_orientation: horizontal\n").LayoutType);

        // text_orientation 在 layout/type 之前，故会被后者覆盖
        Assert.Equal(WeaselLayoutType.Horizontal, Resolve("""
            style:
              text_orientation: vertical
              layout:
                type: horizontal
            """).LayoutType);
    }

    [Fact]
    public void 非法的选项值等于没写()
    {
        // layout/type 不在 5 个取值内 → 落到 fallback（当前值）
        Assert.Equal(WeaselLayoutType.Vertical,
            Resolve("style:\n  layout:\n    type: diagonal\n").LayoutType);

        // align_type 非法 → 保持出厂初值 Bottom
        Assert.Equal(WeaselLayoutAlignType.Bottom,
            Resolve("style:\n  layout:\n    align_type: topleft\n").AlignType);

        // text_orientation 非法 → 不改
        Assert.Equal(WeaselLayoutType.Vertical,
            Resolve("style:\n  text_orientation: sideways\n").LayoutType);
    }

    // ── fullscreen 三处副作用 ──────────────────────────────────────────

    [Fact]
    public void 全屏会把最大宽度行内编码与阴影半径一并归零()
    {
        var s = Resolve("""
            style:
              horizontal: true
              fullscreen: true
              inline_preedit: true
              layout:
                max_width: 800
                shadow_radius: 8
            """);

        Assert.Equal(WeaselLayoutType.HorizontalFullscreen, s.LayoutType);
        Assert.Equal(0, s.MaxWidth);       // 否则全屏窗口会被 800px 卡住
        Assert.False(s.InlinePreedit);     // 否则无候选时会死锁（CHANGELOG 第 579 行）
        Assert.Equal(0, s.ShadowRadius);   // 否则全屏窗口外沿出现阴影
    }

    [Fact]
    public void 非全屏时阴影半径保持用户设定()
    {
        var s = Resolve("style:\n  layout:\n    shadow_radius: 8\n");
        Assert.Equal(8, s.ShadowRadius);
    }

    [Fact]
    public void 阴影半径为负时先取绝对值()
    {
        Assert.Equal(8, Resolve("style:\n  layout:\n    shadow_radius: -8\n").ShadowRadius);
    }

    // ── padding / margin 修正 ──────────────────────────────────────────

    [Fact]
    public void 竖排时高亮内边距会把纵向间距顶上去()
    {
        // Vertical 分支：spacing 与 candidate_spacing 都按 hilite_padding_y*2 取 max
        var s = Resolve("""
            style:
              horizontal: false
              layout:
                spacing: 10
                candidate_spacing: 5
                hilite_padding: 8
            """);

        Assert.Equal(16, s.Spacing);           // max(10, 8*2)
        Assert.Equal(16, s.CandidateSpacing);  // max(5, 8*2)
    }

    [Fact]
    public void 横排时候选间距按横向内边距修正()
    {
        var s = Resolve("""
            style:
              horizontal: true
              layout:
                candidate_spacing: 5
                hilite_padding_x: 9
                hilite_padding_y: 1
            """);

        // Horizontal 走 else 分支：按 hilite_padding_x*2
        Assert.Equal(18, s.CandidateSpacing);  // max(5, 9*2)
        Assert.Equal(2, s.Spacing);            // max(0, 1*2) —— spacing 始终看 y
    }

    [Fact]
    public void 竖排文字时xy的主次关系与横排相反()
    {
        // 与横排的对照：
        //   横排     spacing←y*2, candidate_spacing←x*2, hilite_spacing←x
        //   竖排文字 spacing←x*2, candidate_spacing←x*2, hilite_spacing←y
        // 注意 hilite_spacing 在两分支里看的轴不同（上游第 1337 行 vs 第 1349 行）。
        var s = Resolve("""
            style:
              vertical_text: true
              layout:
                spacing: 3
                candidate_spacing: 4
                hilite_padding_x: 7
                hilite_padding_y: 6
            """);

        Assert.Equal(WeaselLayoutType.VerticalText, s.LayoutType);
        Assert.Equal(14, s.Spacing);           // max(3, 7*2) —— 看 x
        Assert.Equal(14, s.CandidateSpacing);  // max(4, 7*2) —— 看 x
        Assert.Equal(6, s.HiliteSpacing);      // max(0, 6)   —— 看 y，不是 x
    }

    [Fact]
    public void 竖排文字开启折行时纵向内边距也参与候选间距修正()
    {
        var s = Resolve("""
            style:
              vertical_text: true
              vertical_text_with_wrap: true
              layout:
                candidate_spacing: 4
                hilite_padding_x: 1
                hilite_padding_y: 9
            """);

        // 先按 x 修正得 max(4, 2)=4，再因折行按 y 修正得 max(4, 18)=18
        Assert.Equal(18, s.CandidateSpacing);
    }

    [Fact]
    public void 行内编码开启时高亮间距不再被内边距顶高()
    {
        var without = Resolve("""
            style:
              inline_preedit: false
              layout:
                hilite_spacing: 1
                hilite_padding: 6
            """);
        Assert.Equal(6, without.HiliteSpacing);  // max(1, 6)

        var with = Resolve("""
            style:
              inline_preedit: true
              layout:
                hilite_spacing: 1
                hilite_padding: 6
            """);
        Assert.Equal(1, with.HiliteSpacing);     // inline_preedit 为真 → 跳过修正
    }

    [Fact]
    public void 负边距在修正后依然保持为负()
    {
        // 用户设 margin_x=-3（贴左边缘），但 hilite_padding=5 更大
        // → 实际生效的是 -5，符号不变。面板若只显示 -3 就会与实际渲染错位。
        var s = Resolve("""
            style:
              layout:
                margin_x: -3
                margin_y: -3
                hilite_padding: 5
            """);

        Assert.Equal(-5, s.MarginX);
        Assert.Equal(-5, s.MarginY);
    }

    [Fact]
    public void 正边距取内边距与设定值的较大者()
    {
        var s = Resolve("""
            style:
              layout:
                margin_x: 20
                margin_y: 2
                hilite_padding: 5
            """);

        Assert.Equal(20, s.MarginX);  // max(5, 20)
        Assert.Equal(5, s.MarginY);   // max(5, 2) —— 内边距把过小的边距顶上去
    }

    // ── 别键回退 ───────────────────────────────────────────────────────

    [Fact]
    public void 主键优先于别键()
    {
        var s = Resolve("""
            style:
              layout:
                border: 9
                border_width: 3
            """);
        // 主键 border 存在 → 用 9，别键 border_width 被忽略
        Assert.Equal(9, s.Border);
    }

    [Fact]
    public void 别键在主键缺失时生效()
    {
        var s = Resolve("style:\n  layout:\n    border_width: 3\n");
        Assert.Equal(3, s.Border);
    }

    [Fact]
    public void 圆角的两个主键共用同一个别键()
    {
        var s = Resolve("style:\n  layout:\n    round_corner: 7\n");
        // hilited_corner_radius 与 corner_radius 都未写，双双落到 round_corner
        Assert.Equal(7, s.RoundCorner);
        Assert.Equal(7, s.RoundCornerEx);
    }

    // ── 两层：全局 + 方案覆盖 ──────────────────────────────────────────

    private const string GlobalYaml = """
        style:
          horizontal: true
          inline_preedit: true
          layout:
            min_width: 160
            border_width: 3
        """;

    [Fact]
    public void 方案层只覆盖显式存在的键其余保留全局值()
    {
        var global = Resolve(GlobalYaml);
        Assert.Equal(WeaselLayoutType.Horizontal, global.LayoutType);
        Assert.Equal(160, global.MinWidth);
        Assert.Equal(3, global.Border);

        // 方案里只改 horizontal，不碰 min_width / border_width
        var schema = WeaselStyleResolver.ResolveSchemaOverlay(
            global, RimeConfigView.FromYaml("style:\n  horizontal: false\n"));

        Assert.Equal(WeaselLayoutType.Vertical, schema.LayoutType);
        Assert.Equal(160, schema.MinWidth);   // 保留全局
        Assert.Equal(3, schema.Border);       // 保留全局
    }

    [Fact]
    public void 方案未覆盖任何布局项时差异列表为空()
    {
        var global = Resolve(GlobalYaml);

        var schema = WeaselStyleResolver.ResolveSchemaOverlay(
            global, RimeConfigView.FromYaml("style:\n  font_point: 20\n"));

        Assert.Empty(global.Differences(schema));
    }

    [Fact]
    public void 可检测出方案覆盖了哪些外观项()
    {
        var global = Resolve(GlobalYaml);

        var schema = WeaselStyleResolver.ResolveSchemaOverlay(
            global, RimeConfigView.FromYaml("""
                style:
                  horizontal: false
                  layout:
                    min_width: 200
                """));

        var diff = global.Differences(schema);
        Assert.Contains("LayoutType", diff);
        Assert.Contains("MinWidth", diff);
        // 未被方案改写的项不应出现在差异里
        Assert.DoesNotContain("Border", diff);
    }

    [Fact]
    public void 方案层的全屏设置同样会触发三处副作用()
    {
        var global = Resolve(GlobalYaml);

        var schema = WeaselStyleResolver.ResolveSchemaOverlay(
            global, RimeConfigView.FromYaml("style:\n  fullscreen: true\n"));

        // 全局是 horizontal:true → 叠加 fullscreen 得横排全屏
        Assert.Equal(WeaselLayoutType.HorizontalFullscreen, schema.LayoutType);
        Assert.Equal(0, schema.MaxWidth);
        Assert.False(schema.InlinePreedit);   // 全局是 true，被副作用归零
        Assert.Equal(0, schema.ShadowRadius);
    }
}
