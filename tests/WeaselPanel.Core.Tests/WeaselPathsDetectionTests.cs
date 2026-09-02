//
//  WeaselPathsDetectionTests.cs
//  WeaselPanel.Core.Tests
//
//  锁死**安装目录识别**的真机行为（区别于 WeaselPathsTests 的跨平台安全性）。
//  全部用例跑在 fixture 目录上，不触碰本机真实安装。
//
//  核心规则的来源见 WeaselPaths.cs 文件头第 6 条：真正的安装目录是
//  `Program Files\Rime\weasel-<版本>`，由 2026-09-02 真机报告（weasel-0.17.4）证实。
//  在此之前的实现只检查 `Rime\` 目录存在，导致真机上误报「已安装」、
//  而共享目录与部署器全部「未找到」。
//

using WeaselPanel.Core.Platform;

namespace WeaselPanel.Core.Tests;

public class WeaselPathsDetectionTests
{
    /// <summary>建一个含 WeaselServer.exe 的目录，使其通过安装目录特征验证。</summary>
    private static void MakeInstallDir(string path)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "WeaselServer.exe"), "");
    }

    // ── 空壳目录不得误报为已安装 ────────────────────────────────

    [Fact]
    public void 只有版本号子目录的外层目录不应被当成安装目录()
    {
        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        // 外层目录存在但空无一物 —— 这正是真机上 C:\Program Files\Rime 的形态
        Directory.CreateDirectory(outer);

        Assert.False(WeaselPaths.LooksLikeWeaselInstallDir(outer));
        Assert.Null(WeaselPaths.FindInRoots(new[] { outer }));
    }

    [Fact]
    public void 外层目录为壳时应返回其下的版本子目录()
    {
        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        var inner = Path.Combine(outer, "weasel-0.17.4");
        MakeInstallDir(inner);

        // 2026-09-02 真机实测形态：Rime\ 是壳，weasel-0.17.4 才是真身
        var found = WeaselPaths.FindInRoots(new[] { outer });

        Assert.Equal(inner, found);
    }

    [Fact]
    public void 目录不存在时返回空()
    {
        using var tmp = new TempDirectory();
        var missing = Path.Combine(tmp.Root, "no-such-dir");

        Assert.Null(WeaselPaths.FindInRoots(new[] { missing }));
        Assert.False(WeaselPaths.LooksLikeWeaselInstallDir(missing));
        Assert.False(WeaselPaths.LooksLikeWeaselInstallDir(null));
        Assert.False(WeaselPaths.LooksLikeWeaselInstallDir("   "));
    }

    // ── 多版本并存时必须挑最新 ──────────────────────────────────

    [Fact]
    public void 多个版本子目录时取版本号最大的()
    {
        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        MakeInstallDir(Path.Combine(outer, "weasel-0.14.3"));
        MakeInstallDir(Path.Combine(outer, "weasel-0.17.4"));
        MakeInstallDir(Path.Combine(outer, "weasel-0.9.0"));

        var found = WeaselPaths.FindInRoots(new[] { outer });

        // 0.17.4 最大。若按字符串排序会错选 0.9.0，故必须按数值分段比较
        Assert.Equal(Path.Combine(outer, "weasel-0.17.4"), found);
    }

    [Fact]
    public void 版本号按数值比较而非字符串比较()
    {
        // 1.10 > 1.9，但字符串比较会得出相反结论
        Assert.Equal(new[] { 1, 10 }, WeaselPaths.ParseVersion("weasel-1.10"));
        Assert.Equal(new[] { 1, 9 }, WeaselPaths.ParseVersion("weasel-1.9"));

        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        MakeInstallDir(Path.Combine(outer, "weasel-1.9"));
        MakeInstallDir(Path.Combine(outer, "weasel-1.10"));

        Assert.Equal(Path.Combine(outer, "weasel-1.10"), WeaselPaths.FindInRoots(new[] { outer }));
    }

    [Fact]
    public void 位数不等的版本号按零补齐()
    {
        Assert.Equal(new[] { 1, 2, 0 }, WeaselPaths.ParseVersion("weasel-1.2.0"));
        Assert.Equal(new[] { 1, 2 }, WeaselPaths.ParseVersion("weasel-1.2"));

        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        MakeInstallDir(Path.Combine(outer, "weasel-1.2"));
        MakeInstallDir(Path.Combine(outer, "weasel-1.2.1"));

        Assert.Equal(Path.Combine(outer, "weasel-1.2.1"), WeaselPaths.FindInRoots(new[] { outer }));
    }

    [Fact]
    public void 无法解析的版本号排在数字版本之后()
    {
        // weasel-dev 解析失败 → 空数组，语义是「版本未知」，应让位于任何数字版本
        Assert.Empty(WeaselPaths.ParseVersion("weasel-dev"));

        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        MakeInstallDir(Path.Combine(outer, "weasel-dev"));
        MakeInstallDir(Path.Combine(outer, "weasel-0.17.4"));

        Assert.Equal(Path.Combine(outer, "weasel-0.17.4"), WeaselPaths.FindInRoots(new[] { outer }));
    }

    [Fact]
    public void 版本号子目录本身是空壳时应跳过()
    {
        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        // 旧版本卸载后残留的空目录
        Directory.CreateDirectory(Path.Combine(outer, "weasel-0.14.3"));
        MakeInstallDir(Path.Combine(outer, "weasel-0.17.4"));

        Assert.Equal(Path.Combine(outer, "weasel-0.17.4"), WeaselPaths.FindInRoots(new[] { outer }));
    }

    // ── 安装目录特征识别 ────────────────────────────────────────

    [Fact]
    public void 含任一特征文件即判定为安装目录()
    {
        using var tmp = new TempDirectory();

        var onlyData = Path.Combine(tmp.Root, "a");
        Directory.CreateDirectory(Path.Combine(onlyData, "data"));
        Assert.True(WeaselPaths.LooksLikeWeaselInstallDir(onlyData));

        var onlyRimeDll = Path.Combine(tmp.Root, "b");
        Directory.CreateDirectory(onlyRimeDll);
        File.WriteAllText(Path.Combine(onlyRimeDll, "rime.dll"), "");
        Assert.True(WeaselPaths.LooksLikeWeaselInstallDir(onlyRimeDll));

        // 非 Win11 的 32 位布局：exe 在 Win32\ 子目录（install.nsi 第 261-262 行）
        var win32Layout = Path.Combine(tmp.Root, "c");
        Directory.CreateDirectory(Path.Combine(win32Layout, "Win32"));
        File.WriteAllText(Path.Combine(win32Layout, "Win32", "WeaselServer.exe"), "");
        Assert.True(WeaselPaths.LooksLikeWeaselInstallDir(win32Layout));

        var onlyDeployer = Path.Combine(tmp.Root, "d");
        Directory.CreateDirectory(onlyDeployer);
        File.WriteAllText(Path.Combine(onlyDeployer, "WeaselDeployer.exe"), "");
        Assert.True(WeaselPaths.LooksLikeWeaselInstallDir(onlyDeployer));
    }

    [Fact]
    public void 没有任何特征的目录不算安装目录()
    {
        using var tmp = new TempDirectory();
        var junk = Path.Combine(tmp.Root, "junk");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "readme.txt"), "");

        Assert.False(WeaselPaths.LooksLikeWeaselInstallDir(junk));
    }

    [Fact]
    public void 根目录自身即安装目录时优先返回根目录()
    {
        using var tmp = new TempDirectory();
        var outer = Path.Combine(tmp.Root, "Rime");
        MakeInstallDir(outer);
        MakeInstallDir(Path.Combine(outer, "weasel-0.17.4"));

        // 根目录自身合格时直接返回，不再下钻
        Assert.Equal(outer, WeaselPaths.FindInRoots(new[] { outer }));
    }

    [Fact]
    public void 多个根候选中前一个不合格时继续找下一个()
    {
        using var tmp = new TempDirectory();
        var empty = Path.Combine(tmp.Root, "Rime-x86");
        Directory.CreateDirectory(empty);
        var good = Path.Combine(tmp.Root, "Rime");
        MakeInstallDir(good);

        Assert.Equal(good, WeaselPaths.FindInRoots(new[] { empty, good }));
    }

    // ── 共享数据目录 ────────────────────────────────────────────

    [Fact]
    public void 共享数据目录优先取程序目录下的data()
    {
        using var tmp = new TempDirectory();
        var program = Path.Combine(tmp.Root, "weasel-0.17.4");
        Directory.CreateDirectory(Path.Combine(program, "data"));

        Assert.Equal(Path.Combine(program, "data"), WeaselPaths.DetectSharedDataDirectory(program));
    }

    [Fact]
    public void 程序目录为空时共享目录探测不抛异常()
    {
        Assert.Null(WeaselPaths.DetectSharedDataDirectory(null));
        Assert.Null(WeaselPaths.DetectSharedDataDirectory(""));
    }
}
