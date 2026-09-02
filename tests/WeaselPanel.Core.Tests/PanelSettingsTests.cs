//
//  PanelSettingsTests.cs
//  WeaselPanel.Core.Tests
//
//  锁死「面板自身设置」的三件事：
//    1. 落盘位置在 %APPDATA%\WeaselPanel，**绝不能**是 Rime 用户目录
//       （那会混进备份、也会被部署器扫描）；
//    2. 读失败（文件不存在 / JSON 损坏）一律回落默认值，不能让面板起不来；
//    3. 写失败静默吞掉 —— 语言偏好存不下不该打断用户当前操作。
//

using WeaselPanel.Core.Config;

namespace WeaselPanel.Core.Tests;

public class PanelSettingsTests
{
    [Fact]
    public void 设置目录不应落在Rime用户目录下()
    {
        // Rime 用户目录 = %APPDATA%\Rime；面板设置必须在它之外
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rimeUser = Path.Combine(appData, "Rime");

        Assert.NotEqual(rimeUser, PanelSettings.SettingsDirectory);
        Assert.StartsWith(appData, PanelSettings.SettingsDirectory);
        Assert.EndsWith("WeaselPanel", PanelSettings.SettingsDirectory);
    }

    [Fact]
    public void 首次启动时语言默认为空即跟随系统()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Root, "settings.json");

        var loaded = PanelSettings.LoadFrom(path);

        Assert.Null(loaded.Language);
    }

    [Fact]
    public void 保存后可原样读回()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Root, "nested", "settings.json");

        new PanelSettings { Language = "zh-Hant" }.SaveTo(path);
        var loaded = PanelSettings.LoadFrom(path);

        Assert.Equal("zh-Hant", loaded.Language);
    }

    [Fact]
    public void JSON损坏时回落默认设置而不是抛异常()
    {
        using var tmp = new TempDirectory();
        var path = tmp.Write("settings.json", "{ this is not json");

        var loaded = PanelSettings.LoadFrom(path);

        Assert.Null(loaded.Language);
    }

    [Fact]
    public void 大小写不同的键也能读出来()
    {
        using var tmp = new TempDirectory();
        var path = tmp.Write("settings.json", "{ \"Language\": \"en\" }");

        var loaded = PanelSettings.LoadFrom(path);

        Assert.Equal("en", loaded.Language);
    }

    [Fact]
    public void 写到不可写路径时应静默失败而不是抛异常()
    {
        using var tmp = new TempDirectory();
        // 把文件当目录用，写入必然失败
        var blocker = tmp.Write("blocker", "i am a file");
        var path = Path.Combine(blocker, "settings.json");

        var settings = new PanelSettings { Language = "zh-Hans" };

        var ex = Record.Exception(() => settings.SaveTo(path));

        Assert.Null(ex);
    }

    [Fact]
    public void 保存时忽略空语言字段()
    {
        using var tmp = new TempDirectory();
        var path = Path.Combine(tmp.Root, "settings.json");

        new PanelSettings().SaveTo(path);

        Assert.DoesNotContain("language", tmp.Read("settings.json"));
    }
}
