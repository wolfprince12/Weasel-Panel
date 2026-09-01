//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。颜色字节序、补丁值深比较、YAML 文本预处理测试。
//
//  颜色三格式（abgr / argb / rgba）是小狼毫独有于鼠须管的能力
//  （weasel.yaml 支持 style/color_format），故测试用例比 macOS 侧更严。

using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Tests;

public class RimeColorTests
{
    [Fact]
    public void ABGR_生成八位字面量()
    {
        var c = new RimeColor(0xDD / 255.0, 0xCC / 255.0, 0xBB / 255.0, 0xAA / 255.0);
        Assert.Equal("0xAABBCCDD", c.Literal(RimeColorFormat.Abgr));
    }

    [Fact]
    public void ABGR_不透明时输出六位()
    {
        var c = new RimeColor(0xDD / 255.0, 0xCC / 255.0, 0xBB / 255.0);
        Assert.Equal("0xBBCCDD", c.Literal(RimeColorFormat.Abgr));
    }

    [Fact]
    public void ARGB_生成与解析()
    {
        var c = new RimeColor(0xDD / 255.0, 0xCC / 255.0, 0xBB / 255.0, 0xAA / 255.0);
        Assert.Equal("0xAADDCCBB", c.Literal(RimeColorFormat.Argb));

        var parsed = RimeColor.FromYamlValue("0xAADDCCBB", RimeColorFormat.Argb);
        Assert.NotNull(parsed);
        Assert.Equal(c, parsed!.Value);
    }

    [Fact]
    public void RGBA_生成与解析()
    {
        var c = new RimeColor(0xDD / 255.0, 0xCC / 255.0, 0xBB / 255.0, 0xAA / 255.0);
        Assert.Equal("0xDDCCBBAA", c.Literal(RimeColorFormat.Rgba));

        var parsed = RimeColor.FromYamlValue("0xDDCCBBAA", RimeColorFormat.Rgba);
        Assert.NotNull(parsed);
        Assert.Equal(c, parsed!.Value);
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0xAABBCCDDu)]
    [InlineData(0x01020304u)]
    [InlineData(0x7F7F7F7Fu)]
    public void 三种字节序均支持往返(uint packed)
    {
        foreach (var format in new[] { RimeColorFormat.Abgr, RimeColorFormat.Argb, RimeColorFormat.Rgba })
        {
            var original = RimeColor.FromYamlValue("0x" + packed.ToString("X8"), format)!.Value;
            var roundTripped = RimeColor.FromYamlValue(original.Literal(format), format)!.Value;
            Assert.Equal(original, roundTripped);
        }
    }

    [Fact]
    public void 从整数解析颜色()
    {
        // YAML 1.1 会把裸 0x 读成整数，故 FromYamlValue 必须同时接受整数
        var c = RimeColor.FromYamlValue(0xAABBCCDD, RimeColorFormat.Abgr);
        Assert.NotNull(c);
        Assert.Equal("0xAABBCCDD", c!.Value.Literal(RimeColorFormat.Abgr));
    }

    [Fact]
    public void 非法值返回null()
    {
        Assert.Null(RimeColor.FromYamlValue("not-a-color"));   // 无 0x 前缀
        Assert.Null(RimeColor.FromYamlValue(null));
        Assert.Null(RimeColor.FromYamlValue("0xGGGGGG"));      // 非十六进制数字
        Assert.Null(RimeColor.FromYamlValue("0x12345"));       // 位数既非 6 也非 8
    }

    [Fact]
    public void 不超过六位的整数按无alpha解析()
    {
        var c = RimeColor.FromYamlValue(42, RimeColorFormat.Abgr);
        Assert.NotNull(c);
        Assert.Equal(1.0, c!.Value.Alpha);          // 不透明
        Assert.Equal("0x00002A", c.Value.Literal(RimeColorFormat.Abgr));
    }

    [Fact]
    public void ColorFormat_名称解析与回退()
    {
        Assert.Equal(RimeColorFormat.Argb, RimeColorFormatExtensions.FromName("argb"));
        Assert.Equal(RimeColorFormat.Rgba, RimeColorFormatExtensions.FromName("RGBA"));
        Assert.Equal(RimeColorFormat.Abgr, RimeColorFormatExtensions.FromName("srgb"));  // 未知回退
        Assert.Equal(RimeColorFormat.Abgr, RimeColorFormatExtensions.FromName(null));
        Assert.Equal("argb", RimeColorFormat.Argb.ToConfigName());
    }
}

public class PatchValueTests
{
    // 回归用例：macOS 侧曾因 mapList 内的 states 数组不参与比较，
    // 导致 isDirty 恒为真、保存按钮永远亮着。这里必须能判等。
    [Fact]
    public void MapList_含嵌套数组时仍能判等()
    {
        static Dictionary<string, object?> Make() => new()
        {
            ["name"] = "ascii_mode",
            ["states"] = new List<object?> { "中文", "西文" }
        };

        var a = PatchValue.MapList(new[] { Make() });
        var b = PatchValue.MapList(new[] { Make() });

        Assert.True(a.Equals(b));
    }

    [Fact]
    public void MapList_内容不同则判不等()
    {
        var a = PatchValue.MapList(new[]
        {
            new Dictionary<string, object?> { ["states"] = new List<object?> { "中文", "西文" } }
        });
        var b = PatchValue.MapList(new[]
        {
            new Dictionary<string, object?> { ["states"] = new List<object?> { "中文", "英文" } }
        });

        Assert.False(a.Equals(b));
    }

    [Fact]
    public void 不同类型判不等()
    {
        Assert.False(PatchValue.Of(1).Equals(PatchValue.Of("1")));
        Assert.False(PatchValue.Of(true).Equals(PatchValue.Of("true")));
    }

    [Fact]
    public void 数值类型差异被归一化()
    {
        // 1 与 1.0 在 Rime 语义下是同一个值
        Assert.True(RimeValue.ValueEquals(1L, 1.0));
        Assert.True(RimeValue.ValueEquals(1, 1L));
    }

    [Fact]
    public void SchemaList_相等性按内容比较()
    {
        var a = PatchValue.SchemaList(new[] { "luna_pinyin", "terra_pinyin" });
        var b = PatchValue.SchemaList(new[] { "luna_pinyin", "terra_pinyin" });
        var c = PatchValue.SchemaList(new[] { "terra_pinyin", "luna_pinyin" });

        Assert.True(a.Equals(b));
        Assert.False(a.Equals(c));   // 顺序不同即不同（方案顺序有语义）
    }

    [Fact]
    public void OnlyScalar类型标记正确()
    {
        Assert.True(PatchValue.Of(1).IsScalar);
        Assert.True(PatchValue.Of("x").IsScalar);
        Assert.True(PatchValue.Of(1.5).IsScalar);
        Assert.True(PatchValue.Of(true).IsScalar);
        Assert.False(PatchValue.StringList(new[] { "a" }).IsScalar);
        Assert.False(PatchValue.SchemaList(new[] { "a" }).IsScalar);
    }
}

public class YamlTextTests
{
    [Fact]
    public void 行首特殊空格被归一化为普通空格()
    {
        // U+2005（四分之一空格）—— 旧版 rime-settings 用过的缩进字符
        var input = "patch:\n\u2005\u2005style/font_point: 15\n";
        var output = YamlText.NormalizeIndentation(input);

        Assert.Contains("  style/font_point: 15", output);
        Assert.DoesNotContain('\u2005', output);
    }

    [Fact]
    public void 值时内的特殊空格不被改动()
    {
        var input = "patch:\n  style/label_format: \"a\u2005b\"\n";
        var output = YamlText.NormalizeIndentation(input);

        Assert.Contains("a\u2005b", output);
    }

    [Fact]
    public void 全角空格缩进也被归一化()
    {
        var input = "patch:\n\u3000style/font_point: 15\n";
        Assert.Contains(" style/font_point: 15", YamlText.NormalizeIndentation(input));
    }

    [Fact]
    public void 十六进制颜色字面量被加上引号()
    {
        var input = "patch:\n  style/back_color: 0x1A2B3C\n";
        Assert.Contains("\"0x1A2B3C\"", YamlText.QuoteHexColorLiterals(input));
    }

    [Fact]
    public void 已加引号的颜色不会被重复加引号()
    {
        var input = "patch:\n  style/back_color: \"0x1A2B3C\"\n";
        var once = YamlText.QuoteHexColorLiterals(input);
        var twice = YamlText.QuoteHexColorLiterals(once);

        Assert.Equal(once, twice);
        Assert.Equal(1, twice.Split("\"0x1A2B3C\"").Length - 1);
    }

    [Fact]
    public void 非color键的十六进制值不受影响()
    {
        var input = "patch:\n  some_counter: 0x1A2B\n";
        Assert.DoesNotContain("\"", YamlText.QuoteHexColorLiterals(input));
    }

    [Fact]
    public void 支持下划线分隔的十六进制颜色()
    {
        var input = "patch:\n  style/back_color: 0xee_fa_3a_0a\n";
        Assert.Contains("\"0xee_fa_3a_0a\"", YamlText.QuoteHexColorLiterals(input));
    }
}
