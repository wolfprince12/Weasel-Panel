//
//  WeaselPathsTests.cs
//  WeaselPanel.Core.Tests
//
//  路径探测的**跨平台安全性**测试。
//
//  核心断言：本类库虽面向 Windows，但所有路径 API 在 macOS / Linux 上
//  **不得抛异常**——这是「Core 层可在本机（Mac）编译并跑全套测试」的前提，
//  也是 GitHub Actions Linux runner 上 CI 能跑通的前提。
//  注册表读取在非 Windows 上会被内部守卫拦下并走回退分支。
//

namespace WeaselPanel.Core.Tests;

public class WeaselPathsTests
{
    [Fact]
    public void 探测用户目录在非Windows上也不抛异常()
    {
        var dir = WeaselPaths.DetectUserDirectory();

        Assert.False(string.IsNullOrWhiteSpace(dir));
        // 非 Windows：APPDATA 通常不存在 → 回退到 ~/Rime；
        // Windows：注册表或 %AppData%\Rime。两者都以 Rime 结尾。
        Assert.EndsWith("Rime", dir);
    }

    [Fact]
    public void 探测程序目录在非Windows上返回null而不抛异常()
    {
        // 非 Windows 上不存在 %ProgramFiles%，注册表守卫也会直接短路
        var dir = WeaselPaths.DetectProgramDirectory();

        if (OperatingSystem.IsWindows())
        {
            // Windows 上若未安装小狼毫则为 null，装了则为存在的目录
            if (dir is not null) Assert.True(Directory.Exists(dir));
        }
        else
        {
            Assert.Null(dir);
        }
    }

    [Fact]
    public void 完整探测在非Windows上不抛异常()
    {
        var env = WeaselPaths.Detect();

        Assert.False(string.IsNullOrWhiteSpace(env.UserDirectory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(env.IsInstalled);
            Assert.Null(env.ProgramDirectory);
            Assert.Null(env.SharedDataDirectory);
            Assert.Null(env.DeployerPath);
            Assert.Null(env.ServerPath);
        }
    }

    [Fact]
    public void 派生目录位于用户目录之内()
    {
        using var temp = new TempDirectory();
        var env = WeaselEnvironment.WithUserDirectory(temp.Root);

        Assert.Equal(Path.Combine(temp.Root, "sync"), env.SyncDirectory);
        Assert.Equal(Path.Combine(temp.Root, "backups"), env.BackupsDirectory);
        Assert.False(env.IsInstalled);
    }

    [Fact]
    public void 日志目录指向临时目录下的rime_weasel()
    {
        using var temp = new TempDirectory();
        var env = WeaselEnvironment.WithUserDirectory(temp.Root);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "rime.weasel"), env.LogDirectory);
        Assert.Contains("rime.weasel", env.LogDirectory);
    }

    [Fact]
    public void 用户目录就绪状态反映实际存在性()
    {
        using var temp = new TempDirectory();
        Assert.True(WeaselEnvironment.WithUserDirectory(temp.Root).IsUserDirectoryReady);
        Assert.False(WeaselEnvironment.WithUserDirectory(Path.Combine(temp.Root, "不存在")).IsUserDirectoryReady);
    }

    [Fact]
    public void 配置文件查找用户目录优先于共享目录()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.yaml", "用户版");

        var shared = Path.Combine(temp.Root, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "weasel.yaml"), "共享版");

        var env = new WeaselEnvironment
        {
            UserDirectory = temp.Root,
            SharedDataDirectory = shared,
        };

        var sources = env.ConfigSources("weasel.yaml");

        Assert.Equal(2, sources.Length);
        Assert.Equal(Path.Combine(temp.Root, "weasel.yaml"), sources[0]);
        Assert.Equal(Path.Combine(shared, "weasel.yaml"), sources[1]);
    }

    [Fact]
    public void 配置文件查找只返回实际存在的()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.yaml", "用户版");

        var env = WeaselEnvironment.WithUserDirectory(temp.Root);

        Assert.Equal([Path.Combine(temp.Root, "weasel.yaml")], env.ConfigSources("weasel.yaml"));
        Assert.Empty(env.ConfigSources("default.yaml"));
    }
}
