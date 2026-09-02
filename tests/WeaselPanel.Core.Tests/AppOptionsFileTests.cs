//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。app_options 的读取、落盘规则与「不碰用户手写内容」底线测试。
//
//  全部用例跑在临时目录，不依赖本机是否装了小狼毫 —— 与本项目其它 Core 测试一致。

using WeaselPanel.Core.Rime;

namespace WeaselPanel.Core.Tests;

public class AppOptionsFileTests : IDisposable
{
    private readonly string _dir;

    // 官方 weasel.yaml 出厂就带的这一段（2026-09-02 从 rime/weasel 拉取核对）
    private const string FactoryWeasel =
        "config_version: \"0.23\"\n" +
        "\n" +
        "app_options:\n" +
        "  cmd.exe:\n" +
        "    ascii_mode: true\n" +
        "  conhost.exe:\n" +
        "    ascii_mode: true\n" +
        "\n" +
        "style:\n" +
        "  font_point: 14\n";

    public AppOptionsFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "weasel-panel-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响测试结果 */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    /// <summary>以给定的 weasel.yaml 内容构造被测对象；补丁文件尚不存在。</summary>
    private AppOptionsFile Make(string? weaselYaml = null)
    {
        File.WriteAllText(Path_("weasel.yaml"), weaselYaml ?? FactoryWeasel);
        var baseView = RimeConfigView.FromYaml(File.ReadAllText(Path_("weasel.yaml")));
        return new AppOptionsFile(baseView, new CustomYamlFile(Path_("weasel.custom.yaml")));
    }

    private string PatchText => File.ReadAllText(Path_("weasel.custom.yaml"));

    // MARK: 读取

    [Fact]
    public void 读出厂条目并标记为内置()
    {
        var file = Make();
        var rows = file.Entries();

        Assert.Equal(2, rows.Count);
        Assert.Equal("cmd.exe", rows[0].ExeKey);
        Assert.True(rows[0].AsciiMode);
        Assert.True(rows[0].IsBuiltIn);
        Assert.False(rows[0].IsCustomized);   // 补丁里还没有任何东西
        Assert.Null(rows[0].InlinePreedit);   // 出厂没写 → 跟随全局
    }

    [Fact]
    public void 出厂条目按原始顺序排在前()
    {
        var file = Make();
        var rows = file.Entries();
        Assert.Equal(new[] { "cmd.exe", "conhost.exe" }, rows.Select(r => r.ExeKey));
    }

    [Fact]
    public void 补丁里的新条目读得出来并标记已定制()
    {
        File.WriteAllText(Path_("weasel.custom.yaml"),
            "patch:\n  app_options/notepad.exe/ascii_mode: true\n");
        var rows = Make().Entries();

        var row = rows.Single(r => r.ExeKey == "notepad.exe");
        Assert.True(row.AsciiMode);
        Assert.False(row.IsBuiltIn);
        Assert.True(row.IsCustomized);
    }

    // MARK: 落盘规则 —— 与出厂相同的值不写

    [Fact]
    public void 把出厂的true改成false才落盘()
    {
        var file = Make();
        var rows = file.Entries();
        rows[0].AsciiMode = false;          // cmd.exe：出厂 true → 关掉
        rows[1].AsciiMode = true;           // conhost.exe：保持出厂值
        file.Save(rows);

        var text = PatchText;
        Assert.Contains("app_options/cmd.exe/ascii_mode: false", text);
        Assert.DoesNotContain("conhost.exe", text);   // 与出厂一致 → 一个字都不写
    }

    [Fact]
    public void 改回出厂值会删掉补丁键()
    {
        File.WriteAllText(Path_("weasel.custom.yaml"),
            "patch:\n  app_options/cmd.exe/ascii_mode: false\n");
        var file = Make();

        var rows = file.Entries();
        Assert.False(rows[0].AsciiMode);    // 补丁生效中

        rows[0].AsciiMode = true;           // 改回出厂的 true
        file.Save(rows);

        Assert.DoesNotContain("ascii_mode", PatchText);
    }

    [Fact]
    public void 新增条目默认英文并落盘()
    {
        var file = Make();
        var rows = file.Entries();
        rows.Add(new AppOptionEntry
        {
            ExeKey = "code.exe",
            AsciiMode = AppOptionsFile.DefaultAsciiModeWhenAdded,
        });
        file.Save(rows);

        Assert.Contains("app_options/code.exe/ascii_mode: true", PatchText);
    }

    // MARK: inline_preedit 三态

    [Fact]
    public void inline_preedit不干预时不写键()
    {
        var file = Make();
        var rows = file.Entries();
        rows[0].InlinePreedit = null;
        file.Save(rows);

        Assert.DoesNotContain("inline_preedit", PatchText);
    }

    [Fact]
    public void inline_preedit显式false必须落盘()
    {
        // 这条规则最容易写错：若照抄 ascii_mode 的「与出厂相同就删」，
        // 出厂里没有该键就会被当成「相同」而删掉，用户要的显式关闭会静默失效。
        var file = Make();
        var rows = file.Entries();
        rows[0].InlinePreedit = false;
        file.Save(rows);

        Assert.Contains("app_options/cmd.exe/inline_preedit: false", PatchText);

        var reread = Make().Entries();
        Assert.False(reread[0].InlinePreedit);   // 回读仍是 false，不是 null
    }

    // MARK: 不碰用户手写内容

    [Fact]
    public void 删除条目只清托管键_保留用户手写键()
    {
        File.WriteAllText(Path_("weasel.custom.yaml"),
            "patch:\n" +
            "  app_options/foo.exe/ascii_mode: true\n" +
            "  app_options/foo.exe/my_own_option: true\n" +
            "  style/font_point: 16\n");

        var file = Make();
        var rows = file.Entries();
        var foo = rows.Single(r => r.ExeKey == "foo.exe");

        file.Save(rows.Where(r => r != foo).ToList());   // 界面上删掉这一行

        var text = PatchText;
        Assert.DoesNotContain("app_options/foo.exe/ascii_mode", text);
        Assert.Contains("my_own_option", text);          // 用户手写的键必须留着
        Assert.Contains("style/font_point: 16", text);   // 别的节点更不能动
    }

    [Fact]
    public void 清空托管键不影响其它节点()
    {
        File.WriteAllText(Path_("weasel.custom.yaml"),
            "patch:\n" +
            "  app_options/cmd.exe/ascii_mode: false\n" +
            "  app_options/foo.exe/vim_mode: true\n" +
            "  style/font_point: 16\n");

        var file = Make();
        file.ClearManaged();

        var text = PatchText;
        Assert.DoesNotContain("ascii_mode", text);
        Assert.DoesNotContain("vim_mode", text);
        Assert.Contains("style/font_point: 16", text);
    }

    // MARK: exe 名大小写

    [Fact]
    public void 大小写不同的同一exe合并为一条()
    {
        // 上游 map 用 CaseInsensitiveCompare 比较，且读进来会 to_lower，
        // 所以 CMD.exe 与 cmd.exe 是同一个程序 —— 界面上不能显示成两行。
        File.WriteAllText(Path_("weasel.custom.yaml"),
            "patch:\n  app_options/CMD.EXE/ascii_mode: false\n");
        var rows = Make().Entries();

        var cmd = Assert.Single(rows,
            r => r.ExeKey.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(cmd.AsciiMode);   // 补丁生效
    }

    [Fact]
    public void 写回时沿用出厂的原始大小写()
    {
        File.WriteAllText(Path_("weasel.yaml"),
            "app_options:\n  CMD.exe:\n    ascii_mode: true\n");
        var file = Make(File.ReadAllText(Path_("weasel.yaml")));

        var rows = file.Entries();
        Assert.Equal("CMD.exe", rows[0].ExeKey);

        rows[0].AsciiMode = false;
        file.Save(rows);

        // 写到同一个键上，而不是另开一个小写键（否则补丁里会出现两条重复感很强的键）
        Assert.Contains("app_options/CMD.exe/ascii_mode: false", PatchText);
        Assert.DoesNotContain("app_options/cmd.exe/", PatchText);
    }

    // MARK: 排序

    [Fact]
    public void 后加的条目排在出厂条目之后()
    {
        File.WriteAllText(Path_("weasel.custom.yaml"),
            "patch:\n" +
            "  app_options/aaa.exe/ascii_mode: true\n" +
            "  app_options/code.exe/ascii_mode: true\n");
        var rows = Make().Entries();

        Assert.Equal(
            new[] { "cmd.exe", "conhost.exe", "aaa.exe", "code.exe" },
            rows.Select(r => r.ExeKey));
    }
}
