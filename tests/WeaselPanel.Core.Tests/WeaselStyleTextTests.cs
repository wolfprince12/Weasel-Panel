using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

/// <summary>
/// 字体与文本样式。每条都对应上游一处「按字段名想当然就会写错」的实现。
/// </summary>
public class WeaselStyleTextTests
{
    private static WeaselStyle Resolve(string yaml) =>
        WeaselStyleResolver.ResolveGlobal(RimeConfigView.FromYaml(yaml));

    // ── 字号 ────────────────────────────────────────────────────────────

    [Fact]
    public void 未配置字号时兜底为12而非出厂的14()
    {
        // RimeWithWeasel.cpp:1184-1185 `if (font_point <= 0) font_point = 12;`
        // ⚠️ 这是硬编码兜底，与 weasel.yaml 出厂写的 14 是两回事。
        //    面板把两者混用会导致「用户没设过字号」被误判成「用户设了 14」。
        Assert.Equal(12, Resolve("style:\n  horizontal: true\n").FontPoint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 字号为非正数时一律兜底为12(int configured)
    {
        var style = Resolve($"style:\n  font_point: {configured}\n");
        Assert.Equal(12, style.FontPoint);
    }

    [Fact]
    public void 序号与注释字号取绝对值并回退主字号()
    {
        var style = Resolve("""
            style:
              font_point: 16
              label_font_point: -10
            """);

        Assert.Equal(16, style.FontPoint);
        Assert.Equal(10, style.LabelFontPoint);     // _abs
        Assert.Equal(16, style.CommentFontPoint);   // 主键缺失 → 回退 font_point
    }

    // ── 字体族 ──────────────────────────────────────────────────────────

    [Fact]
    public void 序号与注释字体为空时回退主字体()
    {
        // RimeWithWeasel.cpp:1178-1181，读取之后无条件执行
        var style = Resolve("style:\n  font_face: Microsoft YaHei\n");

        Assert.Equal("Microsoft YaHei", style.FontFace);
        Assert.Equal("Microsoft YaHei", style.LabelFontFace);
        Assert.Equal("Microsoft YaHei", style.CommentFontFace);
    }

    [Fact]
    public void 字体族会移除逗号冒号与首尾周围的空白()
    {
        // 上游 rmspace：\s*(,|:|^|$)\s* → $1（RimeWithWeasel.cpp:1166-1168）
        var style = Resolve("style:\n  font_face: '  Microsoft YaHei ,  SimSun  '\n");

        // ⚠️ 反直觉：正则在分隔符**两侧**都有 \s*，故逗号后的空格也会被吃掉，
        //    结果是 "YaHei,SimSun" 而非 "YaHei, SimSun"。词内空格保留。
        //    面板做字符串比较时必须按这个结果来，否则会误判为配置被改动。
        Assert.Equal("Microsoft YaHei,SimSun", style.FontFace);
    }

    [Fact]
    public void 显式设置的注释字体优先于主字体()
    {
        var style = Resolve("""
            style:
              font_face: Microsoft YaHei
              comment_font_face: SimSun
            """);

        Assert.Equal("Microsoft YaHei", style.FontFace);
        Assert.Equal("Microsoft YaHei", style.LabelFontFace);   // 未设 → 回退
        Assert.Equal("SimSun", style.CommentFontFace);          // 已设 → 不回退
    }

    // ── 序号格式与高亮标记 ──────────────────────────────────────────────

    [Fact]
    public void 序号格式的键名是label_format而非字段名()
    {
        // ⚠️ RimeWithWeasel.cpp:1254 用的是 "style/label_format"，
        //    而 UIStyle 的字段名是 label_text_format。按字段名写键会静默失效。
        var style = Resolve("style:\n  label_format: '%s)'\n");
        Assert.Equal("%s)", style.LabelTextFormat);

        // 反向验证：写字段名无效
        var byFieldName = Resolve("style:\n  label_text_format: '%s]'\n");
        Assert.Equal("%s.", byFieldName.LabelTextFormat);
    }

    [Fact]
    public void 序号格式未配置时为百分号s加句点()
    {
        // 构造初值 label_text_format(L"%s.")（WeaselIPCData.h:316）
        Assert.Equal("%s.", Resolve("style:\n  horizontal: true\n").LabelTextFormat);
    }

    [Fact]
    public void 高亮标记为空时等价于星号()
    {
        // 上游在使用处兜底：mark_text.empty() ? L"*" : mark_text（第 848-851 行）
        Assert.Equal("*", Resolve("style:\n  horizontal: true\n").EffectiveMarkText);
        Assert.Equal(">", Resolve("style:\n  mark_text: '>'\n").EffectiveMarkText);
        // 原始字段仍保存空串，兜底只在 EffectiveMarkText 上发生
        Assert.Equal("", Resolve("style:\n  horizontal: true\n").MarkText);
    }

    // ── 三个枚举 ────────────────────────────────────────────────────────

    [Fact]
    public void 三个枚举按配置串解析()
    {
        var style = Resolve("""
            style:
              preedit_type: preview_all
              antialias_mode: cleartype
              hover_type: hilite
            """);

        Assert.Equal(WeaselPreeditType.PreviewAll, style.PreeditType);
        Assert.Equal(WeaselAntiAliasMode.ClearType, style.AntiAliasMode);
        Assert.Equal(WeaselHoverType.Hilite, style.HoverType);
    }

    [Fact]
    public void 枚举值非法时保持原值等于没写()
    {
        // _RimeParseStringOptWithFallback 的 fallback 传的是当前值
        var style = Resolve("""
            style:
              preedit_type: preview
              antialias_mode: 不存在的模式
              hover_type: 不存在的模式
            """);

        Assert.Equal(WeaselPreeditType.Preview, style.PreeditType);   // 合法 → 生效
        Assert.Equal(WeaselAntiAliasMode.Default, style.AntiAliasMode); // 非法 → 原值
        Assert.Equal(WeaselHoverType.None, style.HoverType);           // 非法 → 原值
    }

    [Fact]
    public void 抗锯齿默认值是default不是force_dword()
    {
        // ⚠️ 防回归：上游查表数组（第 1202-1206 行）里 force_dword 排第一，
        //    但真实枚举是 DEFAULT = 0（WeaselIPCData.h:196-202）。
        //    若按数组顺序定义枚举，默认值会变成 force_dword —— 编译能过、行为全错。
        Assert.Equal(0, (int)WeaselAntiAliasMode.Default);
        Assert.Equal(1, (int)WeaselAntiAliasMode.ClearType);
        Assert.Equal(2, (int)WeaselAntiAliasMode.Grayscale);
        Assert.Equal(3, (int)WeaselAntiAliasMode.Aliased);
        Assert.Equal(unchecked((int)0xFFFFFFFF), (int)WeaselAntiAliasMode.ForceDword);

        Assert.Equal(WeaselAntiAliasMode.Default,
            Resolve("style:\n  horizontal: true\n").AntiAliasMode);
    }

    // ── 方案层 ──────────────────────────────────────────────────────────

    [Fact]
    public void 方案层缺失字体键时保留全局值()
    {
        var global = Resolve("""
            style:
              font_face: Microsoft YaHei
              font_point: 18
            """);

        var schema = WeaselStyleResolver.ResolveSchemaOverlay(
            global, RimeConfigView.FromYaml("style:\n  color_scheme: aqua\n"));

        Assert.Equal("Microsoft YaHei", schema.FontFace);
        Assert.Equal(18, schema.FontPoint);
        Assert.Empty(global.Differences(schema));
    }
}
