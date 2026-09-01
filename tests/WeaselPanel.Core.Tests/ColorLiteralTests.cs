//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。颜色字面量解析测试（对齐上游 parse_color_code + _RimeGetColor）。

using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

public class ColorLiteralTests
{
    private static uint Abgr(object? yamlValue, RimeColorFormat format = RimeColorFormat.Abgr)
    {
        Assert.True(RimeColor.TryParseAbgr(yamlValue, format, out var abgr),
            $"应能解析：{yamlValue}（format={format}）");
        return abgr;
    }

    // ── 短格式扩展（上游：3/4 位每位重复两次）───────────────────────────

    [Fact]
    public void 井号前缀三位短格式按位扩展()
    {
        // #123 → "112233" → 6 位 → |0xff000000 → 0xFF112233
        Assert.Equal(0xFF112233u, Abgr("#123"));
    }

    [Fact]
    public void 零叉前缀四位短格式按位扩展()
    {
        // 0x1234 → "11223344" → 8 位 → 不补 alpha → 0x11223344
        Assert.Equal(0x11223344u, Abgr("0x1234"));
    }

    [Fact]
    public void 大写零叉前缀同样识别()
    {
        Assert.Equal(0x11223344u, Abgr("0X1234"));
    }

    [Fact]
    public void 六位与八位不走扩展()
    {
        Assert.Equal(0xFFAABBCCu, Abgr("0xAABBCC"));
        Assert.Equal(0x80AABBCCu, Abgr("0x80AABBCC"));
    }

    // ── ABGR 语义（最易踩的坑）─────────────────────────────────────────

    [Fact]
    public void ABGR下_零叉ff0000是蓝色不是红色()
    {
        // 与 CSS 直觉相反：Rime 的 ABGR 是 0xAABBGGRR。
        // 0xff0000 → |0xff000000 → 0xffff0000 → A=FF, B=FF, G=00, R=00 → 不透明蓝。
        var abgr = Abgr("0xff0000");
        Assert.Equal(0xFFFF0000u, abgr);
        Assert.Equal(0xFFu, (abgr >> 24) & 0xFF);   // A
        Assert.Equal(0xFFu, (abgr >> 16) & 0xFF);   // B（ABGR 的第 2 字节）
        Assert.Equal(0x00u, (abgr >> 8) & 0xFF);    // G
        Assert.Equal(0x00u, abgr & 0xFF);           // R
    }

    [Fact]
    public void RGBA六位值补的是尾部alpha()
    {
        // 0x112233 (rgba) → (v<<8)|0xff = 0x112233FF → RGBA2ABGR → 0xFF332211
        // 即 R=11 G=22 B=33 A=FF
        Assert.Equal(0xFF332211u, Abgr("0x112233", RimeColorFormat.Rgba));
    }

    [Fact]
    public void ARGB八位值按宏转换()
    {
        // 0xAADDCCBB (argb) → ARGB2ABGR → 0xAABBCCDD
        Assert.Equal(0xAABBCCDDu, Abgr("0xAADDCCBB", RimeColorFormat.Argb));
    }

    [Fact]
    public void RGBA八位值按宏转换()
    {
        // 0xDDCCBBAA (rgba) → RGBA2ABGR → 0xAABBCCDD
        Assert.Equal(0xAABBCCDDu, Abgr("0xDDCCBBAA", RimeColorFormat.Rgba));
    }

    // ── 非法字面量（上游会退回 config_get_int，最终用回退色）────────────

    [Theory]
    [InlineData("123456")]        // 无前缀
    [InlineData("0x12345")]       // 5 位
    [InlineData("0x123456789")]   // 9 位
    [InlineData("0xGGGGGG")]      // 非十六进制
    [InlineData("0x")]            // 空十六进制段
    [InlineData("#")]             // 空十六进制段
    [InlineData("")]              // 空串
    [InlineData("red")]           // 颜色名（Rime 不支持）
    public void 非法字面量一律判为解析失败(string literal)
    {
        Assert.False(RimeColor.TryParseAbgr(literal, RimeColorFormat.Abgr, out _));
    }

    [Fact]
    public void null值判为解析失败()
    {
        Assert.False(RimeColor.TryParseAbgr(null, RimeColorFormat.Abgr, out _));
    }

    // ── 整数分支（YAML 1.1 会把裸写的 0x… 读成整数）────────────────────

    [Fact]
    public void 整数六位值补alpha()
    {
        // YamlLoader 把 0xECEEEE 读成 long 15511278（≤0xffffff）
        Assert.Equal(0xFFECEEEEu, Abgr(0xECEEEEL));
    }

    [Fact]
    public void 整数八位值保持不补()
    {
        Assert.Equal(0x80AABBCCu, Abgr(0x80AABBCCu));
    }

    [Fact]
    public void 负数按位截断与上游一致()
    {
        // 上游：`value <= 0xffffff` 成立 → |0xff000000；-1 的位型是 0xFFFFFFFF
        Assert.Equal(0xFFFFFFFFu, Abgr(-1));
    }

    [Fact]
    public void 超过三十二位的ulong被截断()
    {
        // 0x1_0000_AABBCCDD 超出 32 位，上游按 `& 0xffffffff` 截断
        Assert.Equal(0xAABBCCDDu, Abgr(0x1_0000_AABBCCDDul));
    }

    // ── 往返一致性 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("0x000000", RimeColorFormat.Abgr)]
    [InlineData("0xffffff", RimeColorFormat.Abgr)]
    [InlineData("0x80aabbcc", RimeColorFormat.Abgr)]
    [InlineData("0x80aabbcc", RimeColorFormat.Argb)]
    [InlineData("0x80aabbcc", RimeColorFormat.Rgba)]
    [InlineData("#abc", RimeColorFormat.Abgr)]
    [InlineData("#abcd", RimeColorFormat.Abgr)]
    public void 解析再生成得到等价颜色(string literal, RimeColorFormat format)
    {
        var first = RimeColor.FromYamlValue(literal, format);
        Assert.NotNull(first);

        var regenerated = first!.Value.Literal(format);
        var second = RimeColor.FromYamlValue(regenerated, format);
        Assert.NotNull(second);

        // 生成时可能省略 alpha 段（如 6 位），故比较 ABGR 打包值而非字符串
        Assert.Equal(first.Value.ToAbgr(), second!.Value.ToAbgr());
    }

    // ── NormalizeColorCode（内部实现，直接锁规则）──────────────────────

    [Theory]
    [InlineData("0xabc", "aabbcc")]
    [InlineData("0XABC", "AABBCC")]
    [InlineData("#abc", "aabbcc")]
    [InlineData("0xabcd", "aabbccdd")]
    [InlineData("0xABCDEF", "ABCDEF")]
    [InlineData("0x00112233", "00112233")]
    public void 规范化结果符合预期(string input, string expected)
    {
        Assert.Equal(expected, RimeColor.NormalizeColorCode(input));
    }
}
