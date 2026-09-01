//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。YamlLineEditor 的保注释写入行为测试。
//
//  这些用例对应的都是 macOS 侧真实踩过的坑，Windows 侧必须逐条复现：
//  用户手写在 *.custom.yaml 里的注释、引号风格、行尾注释，一次编辑都不能丢。

using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Tests;

public class YamlLineEditorTests
{
    private const string Sample = """
        # 顶部说明，必须保留
        patch:
          # 配色相关，必须保留
          style/color_scheme: "aqua"   # 这是我最爱的配色
          style/font_point: 15
          # 结尾说明，必须保留
        other_key: keep
        """;

    [Fact]
    public void SetScalar_保留其它行的注释与顺序()
    {
        var editor = new YamlLineEditor(Sample);
        editor.SetScalar("patch", "style/color_scheme", YamlScalar.Str("ink"));

        var text = editor.Text;
        Assert.Contains("# 顶部说明，必须保留", text);
        Assert.Contains("# 配色相关，必须保留", text);
        Assert.Contains("# 结尾说明，必须保留", text);
        Assert.Contains("other_key: keep", text);
    }

    [Fact]
    public void SetScalar_保留行尾注释及其前方空白()
    {
        var editor = new YamlLineEditor(Sample);
        editor.SetScalar("patch", "style/color_scheme", YamlScalar.Str("ink"));

        foreach (var line in editor.Lines)
            if (line.Contains("style/color_scheme"))
                Assert.Contains("# 这是我最爱的配色", line);
    }

    [Fact]
    public void SetScalar_保留原值的引号风格()
    {
        var editor = new YamlLineEditor(Sample);
        editor.SetScalar("patch", "style/color_scheme", YamlScalar.Str("ink"));

        var line = editor.Lines.First(l => l.Contains("style/color_scheme"));
        // 原值写作 "aqua"（双引号），新值应保持双引号风格
        Assert.Contains("\"ink\"", line);
    }

    [Fact]
    public void SetScalar_裸值键写入时不加多余引号()
    {
        var editor = new YamlLineEditor(Sample);
        editor.SetScalar("patch", "style/font_point", YamlScalar.Number("18"));

        var line = editor.Lines.First(l => l.Contains("style/font_point"));
        Assert.Equal("  style/font_point: 18", line);
    }

    [Fact]
    public void SetScalar_节不存在时追加新节()
    {
        var editor = new YamlLineEditor("patch:\n  a: 1\n");
        editor.SetScalar("new_section", "key", YamlScalar.BoolOf(true));

        Assert.Contains("new_section:", editor.Text);
        Assert.Contains("  key: true", editor.Text);
    }

    [Fact]
    public void RemoveKeyAtPath_删除键但保留同级节注释()
    {
        var editor = new YamlLineEditor(Sample);
        editor.RemoveKeyAtPath(new[] { "patch", "style/color_scheme" });

        var text = editor.Text;
        Assert.DoesNotContain("style/color_scheme", text);
        // 节级注释（缩进 <= 被删键）必须保留
        Assert.Contains("# 配色相关，必须保留", text);
        Assert.Contains("# 结尾说明，必须保留", text);
    }

    [Fact]
    public void RemoveKeyAtPath_块内更深缩进注释随块删除()
    {
        const string text = """
            patch:
              grammar:
                # 块内子注释
                language: zh
              style/font_point: 15
            """;
        var editor = new YamlLineEditor(text);
        editor.RemoveKeyAtPath(new[] { "patch", "grammar" });

        Assert.DoesNotContain("# 块内子注释", editor.Text);
        Assert.Contains("style/font_point: 15", editor.Text);
    }

    [Fact]
    public void RemoveKeyAtPath_键不存在时幂等无操作()
    {
        var editor = new YamlLineEditor(Sample);
        var before = editor.Text;
        editor.RemoveKeyAtPath(new[] { "patch", "no_such_key" });
        Assert.Equal(before, editor.Text);
    }

    [Fact]
    public void ReplaceBlockVerbatim_替换列表块且不误伤兄弟键()
    {
        const string text = """
            patch:
              schema_list:
                - schema: luna_pinyin
                - schema: pinyin_simp
              style/font_point: 15
            """;
        var editor = new YamlLineEditor(text);
        editor.ReplaceBlockVerbatim(
            new[] { "patch", "schema_list" },
            new[] { "- schema: terra_pinyin", "- schema: double_pinyin" });

        Assert.DoesNotContain("luna_pinyin", editor.Text);
        Assert.Contains("- schema: terra_pinyin", editor.Text);
        Assert.Contains("style/font_point: 15", editor.Text);
    }

    [Fact]
    public void ReplaceBlockVerbatim_空列表删除整块含键行()
    {
        const string text = """
            patch:
              schema_list:
                - schema: luna_pinyin
              style/font_point: 15
            """;
        var editor = new YamlLineEditor(text);
        editor.ReplaceBlockVerbatim(new[] { "patch", "schema_list" }, Array.Empty<string>());

        Assert.DoesNotContain("schema_list", editor.Text);
        Assert.Contains("style/font_point: 15", editor.Text);
    }

    [Fact]
    public void ParseLine_制表符缩进抛异常()
    {
        var editor = new YamlLineEditor("patch:\n\tkey: 1\n");
        Assert.Throws<YamlLineEditorException>(() => editor.ParseLine("\tkey: 1"));
    }

    [Fact]
    public void Text_文件末尾恒有换行且幂等()
    {
        var editor = new YamlLineEditor("patch:\n  a: 1\n");
        var once = editor.Text;
        Assert.EndsWith("\n", once);

        var twice = new YamlLineEditor(once).Text;
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("aqua", true)]      // 普通标识符可裸写
    [InlineData("", false)]         // 空串必须加引号
    [InlineData("true", false)]     // 保留字
    [InlineData("no", false)]       // YAML 1.1 布尔
    [InlineData("15", false)]       // 会被读成数字
    [InlineData("0x1A2B3C", false)] // 会被读成十六进制整数
    [InlineData("微软雅黑", true)]   // 中文可裸写
    [InlineData("Microsoft YaHei", false)] // 含空格需引号
    [InlineData("_my_key", true)]
    public void IsPlainSafe_判定与macOS侧一致(string value, bool expected)
    {
        Assert.Equal(expected, YamlLineEditor.IsPlainSafe(value));
    }

    [Fact]
    public void Emit_十六进制颜色生成大写0x字面量()
    {
        var literal = YamlScalar.HexColor(0xAABBCCDD).Payload;
        Assert.Equal("0xAABBCCDD", literal);
    }
}
