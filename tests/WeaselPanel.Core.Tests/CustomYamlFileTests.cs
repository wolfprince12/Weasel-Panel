//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//  GPL-3.0。补丁文件的读写、安全底线与写后校验测试。
//
//  Fixture 注入：全部用例跑在 %TEMP% 下的临时目录，
//  不依赖本机是否安装小狼毫 —— CI 在干净 runner 上必须全绿。

using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.Core.Tests;

public class CustomYamlFileTests : IDisposable
{
    private readonly string _dir;

    public CustomYamlFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "weasel-panel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响测试结果 */ }
    }

    private string File_(string name) => Path.Combine(_dir, name);

    private static string Write(string path, string content)
    {
        File.WriteAllText(path, content);
        return path;
    }

    // MARK: 安全底线

    [Fact]
    public void 无法解析的文件进入只读且拒绝写入()
    {
        var path = Write(File_("weasel.custom.yaml"), "patch:\n  - a\n ::: broken :::\n\t- bad\n");
        var file = new CustomYamlFile(path);

        Assert.Equal(CustomYamlLoadState.Unparsable, file.State);
        Assert.False(file.IsWritable);
        Assert.Throws<PanelException>(() => file.Save());
    }

    [Fact]
    public void 拒绝写入时绝不覆盖原文件()
    {
        const string original = "patch:\n  style/font_point: 15\n";
        var path = Write(File_("weasel.custom.yaml"), original + "  : broken: :\n");
        var file = new CustomYamlFile(path);

        try { file.Save(); } catch (PanelException) { /* 预期 */ }

        Assert.Equal(File.ReadAllText(path), original + "  : broken: :\n");
    }

    [Fact]
    public void 文件不存在视为空补丁且可写()
    {
        var file = new CustomYamlFile(File_("absent.custom.yaml"));

        Assert.Equal(CustomYamlLoadState.Absent, file.State);
        Assert.True(file.IsWritable);
    }

    [Fact]
    public void 顶层不是映射则判为无法解析()
    {
        var path = Write(File_("weasel.custom.yaml"), "- a\n- b\n");
        var file = new CustomYamlFile(path);

        Assert.Equal(CustomYamlLoadState.Unparsable, file.State);
    }

    // MARK: 逐行手术式写入

    [Fact]
    public void ApplyLineEdits_保留用户手写注释与其它键()
    {
        var path = Write(File_("weasel.custom.yaml"), """
            # 我自己的备忘
            patch:
              # 字号别乱动
              style/font_point: 15
              style/horizontal: true
            my_own_key: 保留我
            """);
        var file = new CustomYamlFile(path);

        var set = new PatchSet();
        set.Set("style/font_point", PatchValue.Of(18));
        file.ApplyLineEdits(set);

        var text = File.ReadAllText(path);
        Assert.Contains("# 我自己的备忘", text);
        Assert.Contains("# 字号别乱动", text);
        Assert.Contains("my_own_key: 保留我", text);
        Assert.Contains("style/horizontal: true", text);
        Assert.Contains("style/font_point: 18", text);
    }

    [Fact]
    public void ApplyLineEdits_写入后可回读到新值()
    {
        var path = Write(File_("weasel.custom.yaml"), "patch:\n  style/font_point: 15\n");
        var file = new CustomYamlFile(path);

        var set = new PatchSet();
        set.Set("style/font_point", PatchValue.Of(20));
        file.ApplyLineEdits(set);

        Assert.Equal(20, new CustomYamlFile(path).IntForPath("style/font_point"));
    }

    [Fact]
    public void ApplyLineEdits_传null删除该键()
    {
        var path = Write(File_("weasel.custom.yaml"), """
            patch:
              style/font_point: 15
              style/horizontal: true
            """);
        var file = new CustomYamlFile(path);

        var set = new PatchSet();
        set.Remove("style/font_point");
        file.ApplyLineEdits(set);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("style/font_point", text);
        Assert.Contains("style/horizontal: true", text);
    }

    [Fact]
    public void ApplyLineEdits_写入前留下bak备份()
    {
        var path = Write(File_("weasel.custom.yaml"), "patch:\n  style/font_point: 15\n");
        var file = new CustomYamlFile(path);

        var set = new PatchSet();
        set.Set("style/font_point", PatchValue.Of(21));
        file.ApplyLineEdits(set);

        Assert.True(File.Exists(path + ".bak"));
        Assert.Contains("15", File.ReadAllText(path + ".bak"));
    }

    // MARK: 关键回归 —— 0x 颜色绝不能被改写成十进制

    [Fact]
    public void 裸写的0x颜色值不会被解析成十进制整数()
    {
        var path = Write(File_("weasel.custom.yaml"), "patch:\n  style/back_color: 0x1A2B3C\n");
        var file = new CustomYamlFile(path);

        Assert.Equal("0x1A2B3C", file.StringForPath("style/back_color"));
    }

    [Fact]
    public void 面板写入的0x颜色值往返不失真()
    {
        var path = Write(File_("weasel.custom.yaml"), "patch:\n");
        var file = new CustomYamlFile(path);

        var set = new PatchSet();
        set.Set("style/back_color", PatchValue.Of("0x1A2B3C"));
        file.ApplyLineEdits(set);

        Assert.Equal("0x1A2B3C", new CustomYamlFile(path).StringForPath("style/back_color"));
    }

    [Fact]
    public void 非color键的整数值不被误加引号()
    {
        var path = Write(File_("weasel.custom.yaml"), "patch:\n  style/font_point: 15\n");
        var file = new CustomYamlFile(path);

        Assert.Equal(15, file.IntForPath("style/font_point"));
        Assert.Contains("style/font_point: 15", File.ReadAllText(path));
    }

    // MARK: 整文件重写（Save）

    [Fact]
    public void Save_首次写入带注释头且可回读()
    {
        var path = File_("weasel.custom.yaml");
        var file = new CustomYamlFile(path);

        file.Set("style/font_point", 16);
        file.Save();

        var text = File.ReadAllText(path);
        Assert.Contains("小狼毫控制面板", text);
        Assert.Contains("style/font_point: 16", text);
        Assert.Equal(16, new CustomYamlFile(path).IntForPath("style/font_point"));
    }

    [Fact]
    public void Save_清空patch后移除patch节点但保留头()
    {
        var path = File_("weasel.custom.yaml");
        var file = new CustomYamlFile(path);
        file.Set("style/font_point", 16);
        file.Save();

        var again = new CustomYamlFile(path);
        again.Set("style/font_point", null);
        again.Save();

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("style/font_point", text);
        Assert.Contains("小狼毫控制面板", text);
    }

    // MARK: 内存态操作

    [Fact]
    public void Set_扁平路径不与嵌套写法共存()
    {
        var file = new CustomYamlFile(File_("absent.custom.yaml"));
        file.Set("grammar/language", "zh");

        Assert.True(file.Patch.ContainsKey("grammar/language"));
        Assert.False(file.Patch.ContainsKey("grammar"));
    }

    [Fact]
    public void RemoveAllWithPrefix_按前缀清除app适配键()
    {
        var file = new CustomYamlFile(File_("absent.custom.yaml"));
        file.Set("app_options/cmd.exe", true);
        file.Set("app_options/notepad.exe", true);
        file.Set("style/font_point", 15);

        file.RemoveAllWithPrefix("app_options/cmd.exe");

        Assert.False(file.Patch.ContainsKey("app_options/cmd.exe"));
        Assert.True(file.Patch.ContainsKey("app_options/notepad.exe"));
        Assert.True(file.Patch.ContainsKey("style/font_point"));
    }

    [Fact]
    public void RemoveGrammar_清空后移除空节点()
    {
        var file = new CustomYamlFile(File_("absent.custom.yaml"));
        file.Set("grammar/language", "zh");
        file.Set("grammar/collocation_prism", "octagram");

        file.RemoveGrammar();

        Assert.Empty(file.Patch);
    }

    // MARK: 读取兼容性

    [Fact]
    public void 混合扁平与嵌套写法都能读到()
    {
        var path = Write(File_("weasel.custom.yaml"), """
            patch:
              engine:
                filters:
                  - simplifier
              style/font_point: 15
            """);
        var file = new CustomYamlFile(path);

        Assert.Equal(15, file.IntForPath("style/font_point"));
        Assert.NotNull(file.ValueForPath("engine/filters"));
    }

    [Fact]
    public void 布尔值支持多种写法()
    {
        var path = Write(File_("weasel.custom.yaml"), """
            patch:
              a: true
              b: "yes"
              c: 1
            """);
        var file = new CustomYamlFile(path);

        Assert.True(file.BoolForPath("a"));
        Assert.True(file.BoolForPath("b"));
    }
}
