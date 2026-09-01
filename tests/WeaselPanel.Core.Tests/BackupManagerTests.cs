//
//  BackupManagerTests.cs
//  WeaselPanel.Core.Tests
//
//  备份管理器的行为测试。全部跑在临时目录上，不依赖本机是否安装了小狼毫。
//

namespace WeaselPanel.Core.Tests;

public class BackupManagerTests
{
    // MARK: - 排除规则

    [Theory]
    [InlineData("weasel.custom.yaml", false)]
    [InlineData("default.custom.yaml", false)]
    [InlineData("rime_ice.custom.yaml", false)]
    [InlineData("custom_phrase.txt", false)]
    [InlineData("installation.yaml", false)]
    [InlineData("weasel.yaml", false)]
    // 排除项
    [InlineData("zh.gram", true)]
    [InlineData("build/default.yaml", true)]
    [InlineData("cn_dicts/ice.dict.yaml", true)]
    [InlineData("en_dicts/en.dict.yaml", true)]
    [InlineData("opencc/t2s.json", true)]
    [InlineData("luna_pinyin.schema.yaml", true)]
    [InlineData("weasel.yaml.bak", true)]
    [InlineData("backups/20260101-120000/weasel.custom.yaml", true)]
    [InlineData("__pycache__/x.pyc", true)]
    [InlineData(".restore_temp_1/weasel.yaml", true)]
    public void 排除规则按预期判定(string relativePath, bool shouldExclude)
    {
        Assert.Equal(shouldExclude, BackupManager.ShouldExclude(relativePath));
    }

    [Fact]
    public void Windows路径分隔符同样生效()
    {
        // Windows 上相对路径由 Path.GetRelativePath 产出，分隔符是 '\'
        Assert.True(BackupManager.ShouldExclude("build\\default.yaml"));
        Assert.True(BackupManager.ShouldExclude("cn_dicts\\ice.dict.yaml"));
        Assert.False(BackupManager.ShouldExclude("weasel.custom.yaml"));
    }

    [Fact]
    public void userdb目录整棵子树被排除()
    {
        // 这是 Windows 侧对 macOS 原版的修正（见 BackupManager.cs 文件头第 2 条）：
        // librime 的学习词库在磁盘上是目录，其下文件路径后缀不是 .userdb，
        // 原实现按「整条路径后缀」判定会失配，导致数百 MB 被复制进备份。
        Assert.True(BackupManager.ShouldExclude("rime_ice.userdb/rime_ice.kct"));
        Assert.True(BackupManager.ShouldExclude("rime_ice.userdb\\LOCK"));
        Assert.True(BackupManager.ShouldExclude("luna_pinyin.userdb/info"));

        // 名为 xxx.userdb 的文件本身同样被排除（不削弱原行为）
        Assert.True(BackupManager.ShouldExclude("luna_pinyin.userdb"));
    }

    // MARK: - 创建备份

    [Fact]
    public void 创建备份只收录可编辑配置文件()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 14\n");
        temp.Write("default.custom.yaml", "patch:\n  menu/page_size: 7\n");
        temp.Write("custom_phrase.txt", "#@/db_name\tcustom_phrase.txt\n测试\tceshi\n");
        // 应被排除的
        temp.Write("zh.gram", "binary");
        temp.Write("build/default.yaml", "x");
        temp.Write("luna_pinyin.schema.yaml", "x");
        temp.Write("luna_pinyin.userdb/rime_ice.kct", "x");
        temp.Write("cn_dicts/ice.dict.yaml", "x");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup("改动前");

        Assert.Equal(3, info.FileCount);
        Assert.Equal("改动前", info.Label);
        Assert.True(info.SizeBytes > 0);

        var backupDir = Path.Combine(temp.Root, "backups", info.DirName);
        Assert.True(File.Exists(Path.Combine(backupDir, "weasel.custom.yaml")));
        Assert.True(File.Exists(Path.Combine(backupDir, "custom_phrase.txt")));
        Assert.False(File.Exists(Path.Combine(backupDir, "zh.gram")));
        Assert.False(Directory.Exists(Path.Combine(backupDir, "build")));
        Assert.False(Directory.Exists(Path.Combine(backupDir, "luna_pinyin.userdb")));
        Assert.False(Directory.Exists(Path.Combine(backupDir, "cn_dicts")));
    }

    [Fact]
    public void 创建备份会写入manifest()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n");

        var info = new BackupManager(temp.Root).CreateBackup();

        var manifestPath = Path.Combine(temp.Root, "backups", info.DirName, "manifest.json");
        Assert.True(File.Exists(manifestPath));
        var text = File.ReadAllText(manifestPath);
        Assert.Contains("\"fileCount\": 1", text);
        Assert.Contains("\"createdAt\"", text);
    }

    [Fact]
    public void 备份目录名是时间戳格式()
    {
        using var temp = new TempDirectory();
        var info = new BackupManager(temp.Root).CreateBackup();
        Assert.Matches(@"^\d{8}-\d{6}$", info.DirName);
    }

    [Fact]
    public void 备份目录自身不会被重复备份()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n");

        var manager = new BackupManager(temp.Root);
        var first = manager.CreateBackup();
        var second = manager.CreateBackup();

        // 第二次若把 backups/ 也纳入，文件数会翻倍
        Assert.Equal(1, second.FileCount);
        Assert.NotEqual(first.DirName, second.DirName);
    }

    // MARK: - 列表 / 删除

    [Fact]
    public void 列表按时间倒序且能读出标签()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n");

        var manager = new BackupManager(temp.Root);
        manager.CreateBackup("第一个");
        Thread.Sleep(1100);   // 时间戳精确到秒，避开同秒
        manager.CreateBackup("第二个");

        var list = manager.ListBackups();

        Assert.Equal(2, list.Count);
        Assert.Equal("第二个", list[0].Label);
        Assert.Equal("第一个", list[1].Label);
        Assert.True(list[0].CreatedAt >= list[1].CreatedAt);
    }

    [Fact]
    public void 无标签时回退为自动备份()
    {
        using var temp = new TempDirectory();
        var info = new BackupManager(temp.Root).CreateBackup();
        Assert.Null(info.Label);
        Assert.Equal("自动备份", info.LabelText);
    }

    [Fact]
    public void 同一秒内连续备份不会互相覆盖()
    {
        // 目录名精确到秒。若不处理同秒冲突，第二次备份会写进同一目录，
        // 把第一份静默覆盖掉（自动备份场景极易命中）。
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n");

        var manager = new BackupManager(temp.Root);
        var first = manager.CreateBackup("第一份");
        var second = manager.CreateBackup("第二份");

        Assert.NotEqual(first.DirName, second.DirName);

        var list = manager.ListBackups();
        Assert.Equal(2, list.Count);
        Assert.Equal("第一份", list.Single(b => b.DirName == first.DirName).Label);
        Assert.Equal("第二份", list.Single(b => b.DirName == second.DirName).Label);
    }

    [Fact]
    public void 列表能从manifest读回标签与文件数()
    {
        // 回归测试：值必须按 JsonElement 解析。若反序列化成 object，
        // System.Text.Json 会装箱成 JsonElement，导致 `is string` 恒为 false，
        // 界面上表现为「所有备份都叫自动备份、显示 0 个文件」。
        using var temp = new TempDirectory();
        temp.Write("a.yaml", "x\n");
        temp.Write("b.yaml", "y\n");

        var manager = new BackupManager(temp.Root);
        manager.CreateBackup("带标签");

        var info = Assert.Single(manager.ListBackups());

        Assert.Equal("带标签", info.Label);
        Assert.Equal(2, info.FileCount);
        Assert.True(info.SizeBytes > 0);
    }

    [Fact]
    public void 空目录下列表为空()
    {
        using var temp = new TempDirectory();
        Assert.Empty(new BackupManager(temp.Root).ListBackups());
    }

    [Fact]
    public void 删除备份移除整个目录()
    {
        using var temp = new TempDirectory();
        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        manager.DeleteBackup(info.DirName);

        Assert.False(Directory.Exists(Path.Combine(temp.Root, "backups", info.DirName)));
        Assert.Empty(manager.ListBackups());
    }

    // MARK: - 恢复

    [Fact]
    public void 整量恢复把备份内容写回用户目录()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 14\n");
        temp.Write("custom_phrase.txt", "#@/db_name\tcustom_phrase.txt\n测试\tceshi\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        // 改动一个文件、删除另一个文件
        temp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 20\n");
        File.Delete(Path.Combine(temp.Root, "custom_phrase.txt"));
        Assert.False(temp.Exists("custom_phrase.txt"));

        manager.RestoreBackup(info.DirName);

        Assert.Equal("patch:\n  style/font_point: 14\n", temp.Read("weasel.custom.yaml"));
        Assert.True(temp.Exists("custom_phrase.txt"));
    }

    [Fact]
    public void 部分恢复只覆盖指定文件()
    {
        using var temp = new TempDirectory();
        temp.Write("a.yaml", "原始A\n");
        temp.Write("b.yaml", "原始B\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        temp.Write("a.yaml", "改动A\n");
        temp.Write("b.yaml", "改动B\n");

        manager.RestoreBackup(info.DirName, files: ["a.yaml"]);

        Assert.Equal("原始A\n", temp.Read("a.yaml"));
        Assert.Equal("改动B\n", temp.Read("b.yaml"));   // 未被恢复
    }

    [Fact]
    public void 恢复不存在的备份抛异常()
    {
        using var temp = new TempDirectory();
        var manager = new BackupManager(temp.Root);
        Assert.Throws<DirectoryNotFoundException>(() => manager.RestoreBackup("20990101-000000"));
    }

    [Fact]
    public void 恢复时会跳过manifest()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();
        File.Delete(Path.Combine(temp.Root, "manifest.json"));

        manager.RestoreBackup(info.DirName);

        // manifest 是备份自身的元数据，绝不能泄漏进用户目录
        Assert.False(temp.Exists("manifest.json"));
    }

    // MARK: - 差异对比

    [Fact]
    public void 单列diff能识别新增与删除()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 14\n  style/horizontal: true\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        temp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 18\n");

        var diff = manager.CompareBackup(info.DirName, "weasel.custom.yaml");

        // 备份版比当前版多一行（horizontal），且 font_point 值不同 → 2 处删除 + 1 处新增
        Assert.Equal(2, diff.Count(l => l.Kind == DiffKind.Removed));
        Assert.Single(diff.Where(l => l.Kind == DiffKind.Added));
        Assert.Equal("  style/font_point: 14", diff.First(l => l.Kind == DiffKind.Removed).Text);
        Assert.Equal("  style/font_point: 18", diff.First(l => l.Kind == DiffKind.Added).Text);
        Assert.Contains(diff, l => l.Kind == DiffKind.Removed && l.Text.Contains("horizontal"));
    }

    [Fact]
    public void 文件未改动时双栏diff标记为完全相同()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n  style/font_point: 14\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        var (lines, identical) = manager.CompareBackupSideBySide(info.DirName, "weasel.custom.yaml");

        Assert.True(identical);
        Assert.All(lines, l => Assert.Equal(DiffKind.Equal, l.Kind));
    }

    [Fact]
    public void 文件被删除后diff把全文标为删除行()
    {
        using var temp = new TempDirectory();
        temp.Write("gone.yaml", "line1\nline2\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        File.Delete(Path.Combine(temp.Root, "gone.yaml"));

        var diff = manager.CompareBackup(info.DirName, "gone.yaml");

        Assert.Equal(2, diff.Count(l => l.Kind == DiffKind.Removed));
        Assert.Empty(diff.Where(l => l.Kind == DiffKind.Added));
    }

    [Fact]
    public void 列出备份文件时排除manifest()
    {
        using var temp = new TempDirectory();
        temp.Write("weasel.custom.yaml", "patch:\n");
        temp.Write("default.custom.yaml", "patch:\n");

        var manager = new BackupManager(temp.Root);
        var info = manager.CreateBackup();

        var files = manager.ListBackupFiles(info.DirName);

        Assert.DoesNotContain("manifest.json", files);
        Assert.Contains("weasel.custom.yaml", files);
        Assert.Contains("default.custom.yaml", files);
    }
}
