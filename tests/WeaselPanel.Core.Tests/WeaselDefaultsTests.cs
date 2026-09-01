//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。出厂默认值表测试（含别名回退 —— 面板写回时最容易踩的坑）。

using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

public class WeaselDefaultsTests
{
    private const string Fixture = """
        config_version: "0.23"
        style:
          color_scheme: aqua
          font_face: Microsoft YaHei
          font_point: 14
          horizontal: false
          inline_preedit: false
          layout:
            align_type: center
            border_width: 3
            hilite_padding: 2
            round_corner: 4
            margin_x: 12
        preset_color_schemes:
          aqua:
            back_color: 0xECEEEE
        """;

    private static WeaselDefaults Defaults() => WeaselDefaults.Parse(Fixture);

    // ── 路径查找 ──────────────────────────────────────────────────────

    [Fact]
    public void 读取标量与嵌套路径()
    {
        var d = Defaults();
        Assert.Equal(14, d.Int("style/font_point", -1));
        Assert.Equal("Microsoft YaHei", d.Str("style/font_face", null));
        Assert.False(d.Bool("style/horizontal", true));
        Assert.Equal("center", d.Str("style/layout/align_type", null));
        Assert.Equal(12, d.Int("style/layout/margin_x", -1));
    }

    [Fact]
    public void 缺失键返回调用方给定的兜底值()
    {
        var d = Defaults();
        Assert.Equal(99, d.Int("style/layout/spacing", 99));
        Assert.True(d.Bool("style/not_exist", true));
        Assert.Equal("fb", d.Str("style/not_exist", "fb"));
    }

    [Fact]
    public void 非标量路径按对象返回而不是抛异常()
    {
        var d = Defaults();
        Assert.NotNull(d.Lookup("style/layout"));
    }

    // ── 别名回退（高危）────────────────────────────────────────────────

    [Fact]
    public void border回退到border_width()
    {
        // ⚠️ weasel.yaml 出厂写的是 border_width，而 UIStyle 字段名是 border。
        // 面板若按字段名去读/写，必须命中的是 border_width。
        Assert.Equal(3, Defaults().Int("style/layout/border", -1));
    }

    [Fact]
    public void hilite_padding_xy共同回退到hilite_padding()
    {
        var d = Defaults();
        Assert.Equal(2, d.Int("style/layout/hilite_padding_x", -1));
        Assert.Equal(2, d.Int("style/layout/hilite_padding_y", -1));
    }

    [Fact]
    public void 两个圆角键都回退到round_corner()
    {
        var d = Defaults();
        Assert.Equal(4, d.Int("style/layout/corner_radius", -1));
        Assert.Equal(4, d.Int("style/layout/hilited_corner_radius", -1));
    }

    [Fact]
    public void 主键存在时优先于别键()
    {
        var d = WeaselDefaults.Parse("""
            style:
              layout:
                border_width: 3
                border: 9
            """);

        Assert.Equal(9, d.Int("style/layout/border", -1));
    }

    [Fact]
    public void 别名表覆盖上游全部五组回退()
    {
        Assert.Equal(5, WeaselDefaults.Aliases.Count);
        Assert.Equal("style/layout/border_width", WeaselDefaults.Aliases["style/layout/border"]);
        Assert.Equal("style/layout/round_corner", WeaselDefaults.Aliases["style/layout/corner_radius"]);
        Assert.Equal("style/layout/round_corner", WeaselDefaults.Aliases["style/layout/hilited_corner_radius"]);
        Assert.Equal("style/layout/hilite_padding", WeaselDefaults.Aliases["style/layout/hilite_padding_x"]);
        Assert.Equal("style/layout/hilite_padding", WeaselDefaults.Aliases["style/layout/hilite_padding_y"]);
    }

    // ── C++ 初值（weasel.yaml 里没有的键）──────────────────────────────

    [Fact]
    public void enhanced_position取自C加加初值而非YAML()
    {
        Assert.True(Defaults().Bool("style/enhanced_position", false));
    }

    // ── 出厂态判定（支撑「与出厂默认相同的值不落盘」）───────────────────

    [Fact]
    public void 相同值判为出厂态()
    {
        Assert.True(Defaults().IsFactoryValue("style/font_point", 14L));
        Assert.True(Defaults().IsFactoryValue("style/color_scheme", "aqua"));
        Assert.False(Defaults().IsFactoryValue("style/horizontal", true));
    }

    [Fact]
    public void 不同值判为已修改()
    {
        Assert.False(Defaults().IsFactoryValue("style/font_point", 16L));
        Assert.False(Defaults().IsFactoryValue("style/color_scheme", "azure"));
    }

    [Fact]
    public void 别名键也能正确判定出厂态()
    {
        Assert.True(Defaults().IsFactoryValue("style/layout/border", 3L));
        Assert.False(Defaults().IsFactoryValue("style/layout/border", 5L));
    }

    [Fact]
    public void 颜色字面量忽略大小写差异()
    {
        // 用户手写 0xECEEEE，面板生成 0xeceeee —— 不应判为「已修改」
        Assert.True(Defaults().IsFactoryValue("preset_color_schemes/aqua/back_color", "0xeceeee"));
        Assert.False(Defaults().IsFactoryValue("preset_color_schemes/aqua/back_color", "0x123456"));
    }

    [Fact]
    public void 双方都缺失才算出厂态()
    {
        Assert.True(Defaults().IsFactoryValue("style/layout/spacing", null));
        Assert.False(Defaults().IsFactoryValue("style/layout/spacing", 10L));
    }

    // ── 降级 ──────────────────────────────────────────────────────────

    [Fact]
    public void 空表与非法YAML不抛异常()
    {
        Assert.Equal(7, WeaselDefaults.Empty.Int("style/font_point", 7));
        Assert.Equal(7, WeaselDefaults.Parse("style:\n  - [broken\n").Int("style/font_point", 7));
        Assert.Equal(7, WeaselDefaults.Parse("").Int("style/font_point", 7));
    }
}
