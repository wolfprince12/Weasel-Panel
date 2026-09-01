//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  RimeConfigView 的合并与取值语义测试。
//  GPL-3.0。

using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;
using Xunit;

namespace WeaselPanel.Core.Tests;

public class RimeConfigViewTests
{
    private static RimeConfigView Parse(string yaml) => RimeConfigView.FromYaml(yaml);

    // ── 深度合并 ────────────────────────────────────────────────────────

    [Fact]
    public void 补丁只写一个键不会抹掉同级整棵子树()
    {
        var baseView = Parse("""
            style:
              font_point: 14
              layout:
                min_width: 160
                border_width: 3
            """);

        var merged = RimeConfigView.MergePatch(baseView, new Dictionary<string, object?>
        {
            ["style"] = new Dictionary<string, object?> { ["font_point"] = 20 },
        });

        // 覆盖生效
        Assert.True(merged.TryGetInt("style/font_point", out var point));
        Assert.Equal(20, point);

        // 同级的 layout 子树必须原样存活 —— 这是 Rime patch 深度合并的核心语义
        Assert.True(merged.TryGetInt("style/layout/min_width", out var minWidth));
        Assert.Equal(160, minWidth);
        Assert.True(merged.TryGetInt("style/layout/border_width", out var border));
        Assert.Equal(3, border);
    }

    [Fact]
    public void 列表是整体替换而非追加()
    {
        var baseView = Parse("""
            schema_list:
              - schema: luna_pinyin
              - schema: rime_ice
            """);

        var merged = RimeConfigView.MergePatch(baseView, new Dictionary<string, object?>
        {
            ["schema_list"] = new List<object?> { "terra_pinyin" },
        });

        Assert.True(merged.Contains("schema_list"));
        var list = Assert.IsType<List<object?>>(merged.Lookup("schema_list"));
        // 整体替换：只剩 1 项，而不是追加成 3 项
        Assert.Single(list);
    }

    [Fact]
    public void 补丁中的扁平键会被展开为嵌套结构()
    {
        var baseView = Parse("""
            style:
              font_point: 14
              layout:
                min_width: 160
            """);

        var merged = RimeConfigView.MergePatch(baseView, new Dictionary<string, object?>
        {
            ["style/font_point"] = 20,
        });

        Assert.True(merged.TryGetInt("style/font_point", out var point));
        Assert.Equal(20, point);
        // 展开后仍与既有嵌套键共存
        Assert.True(merged.TryGetInt("style/layout/min_width", out var minWidth));
        Assert.Equal(160, minWidth);
    }

    [Fact]
    public void 解析失败的YAML降级为空视图而非抛异常()
    {
        var view = Parse("style:\n  - [unterminated\n");
        Assert.False(view.Contains("style"));
        Assert.False(view.TryGetInt("style/font_point", out _));
    }

    // ── 键存在性（本类最重要的契约）────────────────────────────────────

    [Fact]
    public void 键缺失与键为false必须区分开()
    {
        var view = Parse("style:\n  inline_preedit: false\n");

        // 键存在且为 false → 返回 true，值为 false
        Assert.True(view.TryGetBool("style/inline_preedit", out var value));
        Assert.False(value);

        // 键不存在 → 返回 false
        Assert.False(view.TryGetBool("style/horizontal", out _));
        Assert.False(view.TryGetBool("style/layout/type", out _));
    }

    [Fact]
    public void 键存在但类型无法解析时视为取不到()
    {
        var view = Parse("style:\n  horizontal: maybe\n");

        // 与 librime 的 config_get_bool 一致：无法转成 bool 就当作取不到
        Assert.False(view.TryGetBool("style/horizontal", out _));
        // 但键本身是存在的
        Assert.True(view.Contains("style/horizontal"));
    }

    [Fact]
    public void 映射与列表不是标量取字符串应失败()
    {
        var view = Parse("""
            style:
              layout:
                min_width: 160
              schema_list:
                - a
            """);

        Assert.False(view.TryGetString("style/layout", out _));
        Assert.False(view.TryGetString("style/schema_list", out _));
        Assert.True(view.TryGetString("style/layout/min_width", out var text));
        Assert.Equal("160", text);
    }

    [Fact]
    public void 整数支持十进制与十六进制字面量()
    {
        var view = Parse("""
            style:
              font_point: 14
              layout:
                max_width: 0x100
            """);

        Assert.True(view.TryGetInt("style/font_point", out var dec));
        Assert.Equal(14, dec);

        Assert.True(view.TryGetInt("style/layout/max_width", out var hex));
        Assert.Equal(256, hex);
    }

    [Fact]
    public void 布尔兼容YAML11的yesnoonoff写法()
    {
        var view = Parse("""
            style:
              horizontal: yes
              vertical_text: off
            """);

        Assert.True(view.TryGetBool("style/horizontal", out var h));
        Assert.True(h);
        Assert.True(view.TryGetBool("style/vertical_text", out var v));
        Assert.False(v);
    }

    [Fact]
    public void 空补丁与空基础配置都安全()
    {
        var view = RimeConfigView.Empty;
        Assert.False(view.Contains("style"));

        var based = Parse("style:\n  font_point: 14\n");
        var merged = RimeConfigView.MergePatch(based, null);
        Assert.True(merged.TryGetInt("style/font_point", out var p));
        Assert.Equal(14, p);
    }
}
