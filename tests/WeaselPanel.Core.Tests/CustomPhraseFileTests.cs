//
//  CustomPhraseFileTests.cs
//  WeaselPanel.Core.Tests
//
//  自定义短语文件（Rime tabledb 文本词典）的解析与写盘测试。
//  全部跑在临时目录上。
//
//  重点覆盖 Windows 侧相对 macOS 版新增的两条约束：
//   1. CRLF 输入不得污染字段（否则「码」会带 '\r'）
//   2. 写盘必须是 UTF-8 无 BOM（否则 Rime 读不到首行 #@/db_name 指令）
//

namespace WeaselPanel.Core.Tests;

public class CustomPhraseFileTests
{
    // MARK: - 载入

    [Fact]
    public void 文件不存在时生成默认头部且db_name取文件名()
    {
        using var temp = new TempDirectory();
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        Assert.False(file.Exists);
        var header = file.Lines.Select(l => l.Serialized);
        Assert.Contains("#@/db_name\tcustom_phrase.txt", header);
        Assert.Contains("#@/db_type\ttabledb", header);
        Assert.Equal(0, file.EntryCount);
    }

    [Fact]
    public void 双拼文件的db_name随之变化()
    {
        using var temp = new TempDirectory();
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase_double.txt"));

        Assert.Contains("#@/db_name\tcustom_phrase_double.txt", file.Lines.Select(l => l.Serialized));
    }

    [Fact]
    public void 解析三列条目()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "# Rime table\n测试\tceshi\t100\n\n");

        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        Assert.True(file.Exists);
        Assert.Equal(1, file.EntryCount);
        var entry = file.Lines.First(l => l.IsEntry);
        Assert.Equal("测试", entry.Word);
        Assert.Equal("ceshi", entry.Code);
        Assert.Equal("100", entry.Weight);
    }

    [Fact]
    public void 解析两列条目时权重为空()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "测试\tceshi\n");

        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));
        var entry = file.Lines.Single(l => l.IsEntry);

        Assert.Equal("测试", entry.Word);
        Assert.Equal("ceshi", entry.Code);
        Assert.Equal(string.Empty, entry.Weight);
    }

    [Fact]
    public void 注释行与空行原样保留()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "# 这是注释\n\n  # 缩进注释\n测试\tceshi\n");

        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        // 末尾的换行会切分出一个空元素，同样按原样保留（共 4 个非条目行：
        // 注释、空行、缩进注释、文件末尾的空元素）
        Assert.Equal(1, file.EntryCount);
        Assert.Equal(4, file.Lines.Count(l => !l.IsEntry));
        Assert.Contains("# 这是注释", file.Lines.Select(l => l.Serialized));
        Assert.Contains("  # 缩进注释", file.Lines.Select(l => l.Serialized));
    }

    [Fact]
    public void 无法解析的行原样保留不丢内容()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "这一行没有制表符\n测试\tceshi\n");

        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        Assert.Equal(1, file.EntryCount);
        Assert.Contains("这一行没有制表符", file.Lines.Select(l => l.Serialized));
    }

    [Fact]
    public void CRLF输入不得污染码字段()
    {
        // Windows 上用户可能用记事本编辑过而留下 CRLF。
        // 若只按 '\n' 切分，code 会变成 "ceshi\r"，写回时再次追加 '\r\n' 造成词典错乱。
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "#@/db_name\tcustom_phrase.txt\r\n测试\tceshi\t1\r\n");

        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));
        var entry = file.Lines.Single(l => l.IsEntry);

        Assert.Equal("测试", entry.Word);
        Assert.Equal("ceshi", entry.Code);
        Assert.DoesNotContain("\r", entry.Code);
    }

    [Fact]
    public void 单独的CR换行同样被正确切分()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "甲\tjia\r乙\tyi\r");

        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        Assert.Equal(2, file.EntryCount);
        Assert.All(file.Lines.Where(l => l.IsEntry), l => Assert.DoesNotContain("\r", l.Code));
    }

    // MARK: - 脏值判定

    [Fact]
    public void 刚载入时不是脏的()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "测试\tceshi\n");
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        Assert.False(file.IsDirty);
    }

    [Fact]
    public void 新增空条目也算脏值()
    {
        // 比对的是「界面快照」而非写盘文本：点了「+」还没填内容，
        // 保存按钮也必须点亮，否则用户以为面板卡住了。
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "测试\tceshi\n");
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        file.AddEntry();

        Assert.True(file.IsDirty);
    }

    [Fact]
    public void 保存后脏值归零()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "custom_phrase.txt");
        temp.Write("custom_phrase.txt", "测试\tceshi\n");
        var file = new CustomPhraseFile(path);

        file.AddEntry();
        file.Save();

        Assert.False(file.IsDirty);
    }

    // MARK: - 序列化与写盘

    [Fact]
    public void 序列化跳过空白条目避免写垃圾行()
    {
        using var temp = new TempDirectory();
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));
        file.AddEntry();                                   // 点了「+」没填
        file.AddEntry();
        file.Update(file.Lines[^1] with { Word = "测试", Code = "ceshi" });

        var text = file.Serialize();

        Assert.EndsWith("测试\tceshi", text);
        Assert.DoesNotContain("\t\t", text);               // 不该出现只含制表符的行
    }

    [Fact]
    public void 序列化保留三列顺序词码权重()
    {
        using var temp = new TempDirectory();
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));
        file.AddEntry();
        file.Update(file.Lines[^1] with { Word = "测试", Code = "ceshi", Weight = "99" });

        Assert.Contains("测试\tceshi\t99", file.Serialize());
    }

    [Fact]
    public void 保存写出LF结尾且不带BOM()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "custom_phrase.txt");
        var file = new CustomPhraseFile(path);
        file.AddEntry();
        file.Update(file.Lines[^1] with { Word = "测试", Code = "ceshi" });

        file.Save();

        var bytes = File.ReadAllBytes(path);
        // UTF-8 BOM 是 EF BB BF；带 BOM 会让 Rime 读不到首行 #@/db_name 指令
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'#', bytes[0]);

        var text = File.ReadAllText(path);
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\r\n", text);
    }

    [Fact]
    public void 保存前生成bak备份()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "custom_phrase.txt");
        temp.Write("custom_phrase.txt", "旧内容\tjiu\n");

        var file = new CustomPhraseFile(path);
        file.AddEntry();
        file.Update(file.Lines[^1] with { Word = "新内容", Code = "xin" });
        file.Save();

        Assert.True(File.Exists(path + ".bak"));
        Assert.Equal("旧内容\tjiu\n", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void 保存后重新载入内容一致()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "custom_phrase.txt");
        temp.Write("custom_phrase.txt", "# 头部注释\n甲\tjia\t1\n乙\tyi\n");

        var file = new CustomPhraseFile(path);
        file.AddEntry();
        file.Update(file.Lines[^1] with { Word = "丙", Code = "bing", Weight = "2" });
        file.Save();

        var reloaded = new CustomPhraseFile(path);

        Assert.False(reloaded.IsDirty);
        Assert.Equal(3, reloaded.EntryCount);
        Assert.Contains("# 头部注释", reloaded.Lines.Select(l => l.Serialized));
        Assert.Contains("丙\tbing\t2", reloaded.Serialize());
    }

    [Fact]
    public void 删除条目后不再出现在序列化结果中()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "甲\tjia\n乙\tyi\n");
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        var target = file.Lines.First(l => l.IsEntry);
        file.RemoveEntry(target.Id);

        Assert.Equal(1, file.EntryCount);
        Assert.DoesNotContain("甲\tjia", file.Serialize());
    }

    [Fact]
    public void 更新不存在的id不会抛异常()
    {
        using var temp = new TempDirectory();
        temp.Write("custom_phrase.txt", "甲\tjia\n");
        var file = new CustomPhraseFile(Path.Combine(temp.Root, "custom_phrase.txt"));

        file.Update(new PhraseLine { Word = "乙", Code = "yi" });

        Assert.Equal(1, file.EntryCount);
    }
}
