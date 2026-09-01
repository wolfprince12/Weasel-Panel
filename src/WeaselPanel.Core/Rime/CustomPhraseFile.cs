//
//  CustomPhraseFile.cs
//  WeaselPanel.Core
//
//  自定义短语文件：全拼是 <用户目录>\custom_phrase.txt，
//  双拼是 <用户目录>\custom_phrase_double.txt（两者是各自独立的词典）。
//
//  这是 Rime 的 tabledb 文本词典，格式为「词<Tab>码<Tab>权重」，
//  文件头部的 `#@/db_name`、`#@/db_type` 指令行必须原样保留，否则词典不会被加载。
//
//  设计底线（与 CustomYamlFile 一致）：
//    1. 注释行、空行、无法识别的行一律原样保留，绝不丢用户内容；
//    2. 写盘前先留一份 .bak；
//    3. 是否「有未保存改动」用序列化结果与载入基线比对得出，不手工标脏。
//
//  ── 相对 macOS 版的差异 ──
//
//  1) 换行：Mac 上这类文件几乎都是 LF，Windows 上用户可能用记事本编辑而留下 CRLF。
//     读取时统一按 LF / CRLF / CR 归一化切分（见 Text/LineSplitter.cs），
//     写盘一律 LF。否则「码」字段会残留 '\r'，词典条目错乱。
//
//  2) 编码：写入 UTF-8 **无 BOM**。带 BOM 会让 Rime 把首行 `#@/db_name` 的
//     第一个字节读成 BOM，导致指令行失配、词典不被加载。读取时仍容忍 BOM。
//
//  3) 原子替换：Swift 用 write(to:atomically:)，此处用「写临时文件 + 原子移动」
//     等价实现。Windows 上 File.Move(overwrite: true) 走 MoveFileEx 语义，
//     同卷内为原子重命名。
//

using WeaselPanel.Core.Text;

namespace WeaselPanel.Core.Rime;

/// <summary>
/// 短语文件中的一行。
/// <see cref="Verbatim"/> 非 null 表示这是注释 / 空行 / 无法解析的行，
/// 界面不展示、写盘时原样输出。
/// </summary>
/// <remarks>
/// 声明为 record：与 Swift 原版的 <c>struct PhraseLine</c> 一致地提供值语义，
/// 并支持 <c>with</c> 表达式做「改一个字段、其余不变」的更新——界面编辑条目时
/// 必须保留 <see cref="Id"/>，用 with 比手工复制字段更不容易漏。
/// 注意 <see cref="Id"/> 是只读字段初始化，with 拷贝时不会被重新生成。
/// </remarks>
public sealed record PhraseLine
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>原文（注释行 / 空行 / 无法解析的行）；可编辑条目为 null。</summary>
    public string? Verbatim { get; set; }

    public string Word { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Weight { get; set; } = string.Empty;

    public PhraseLine() { }

    public PhraseLine(string? verbatim, string word = "", string code = "", string weight = "")
    {
        Verbatim = verbatim;
        Word = word;
        Code = code;
        Weight = weight;
    }

    /// <summary>可编辑的短语条目（相对于注释 / 空行）。</summary>
    public bool IsEntry => Verbatim is null;

    /// <summary>
    /// 词与码都为空的条目：界面上点了「+」还没填内容，写盘时应当整行丢弃，
    /// 否则会往 tabledb 里塞一行只含制表符的垃圾数据。
    /// </summary>
    public bool IsBlankEntry =>
        Verbatim is null
        && Word.Trim().Length == 0
        && Code.Trim().Length == 0;

    /// <summary>写回文件时的一行文本。</summary>
    public string Serialized
    {
        get
        {
            if (Verbatim is not null) return Verbatim;
            var fields = new List<string>(3) { Word, Code };
            var trimmedWeight = Weight.Trim();
            if (trimmedWeight.Length != 0) fields.Add(trimmedWeight);
            return string.Join("\t", fields);
        }
    }
}

/// <summary>
/// 自定义短语文件的内存模型。文件路径在构造时注入，便于在临时目录上做单元测试。
/// </summary>
public sealed class CustomPhraseFile
{
    public string FilePath { get; }

    /// <summary>磁盘上是否已存在该文件。</summary>
    public bool Exists { get; private set; }

    /// <summary>全部行（含注释、空行），界面只渲染 <see cref="PhraseLine.IsEntry"/> 为 true 的行。</summary>
    public List<PhraseLine> Lines { get; } = [];

    /// <summary>载入（或上次保存）时的界面快照基线，用于脏值判断。</summary>
    private string _baseline = string.Empty;

    public CustomPhraseFile(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Exists = File.Exists(filePath);

        if (Exists)
        {
            try
            {
                Lines.AddRange(Parse(File.ReadAllText(filePath)));
            }
            catch (IOException)
            {
                Exists = false;
                Lines.AddRange(DefaultHeader(FilePath).Select(v => new PhraseLine(v)));
            }
            catch (UnauthorizedAccessException)
            {
                Exists = false;
                Lines.AddRange(DefaultHeader(FilePath).Select(v => new PhraseLine(v)));
            }
        }
        else
        {
            Lines.AddRange(DefaultHeader(FilePath).Select(v => new PhraseLine(v)));
        }

        _baseline = UiSnapshot();
    }

    /// <summary>
    /// 文件不存在时新建所用的头部：Rime tabledb 必需的指令行。
    /// <c>#@/db_name</c> 必须与词典文件名一致，否则 Rime 不会加载该词典。
    /// 全拼用 custom_phrase.txt、双拼用 custom_phrase_double.txt，故由路径推导，
    /// 不能写死。
    /// 注：rime-ice 的约定是**带 .txt 后缀**——其出厂 custom_phrase.txt 头部即
    /// <c>#@/db_name&lt;Tab&gt;custom_phrase.txt</c>。
    /// </summary>
    internal static List<string> DefaultHeader(string filePath)
    {
        var dbName = Path.GetFileName(filePath);
        return
        [
            "# Rime table",
            "# coding: utf-8",
            $"#@/db_name\t{dbName}",
            "#@/db_type\ttabledb",
            "#",
            "# 由「小狼毫控制面板」创建。格式：词<Tab>码<Tab>权重（权重可省略）。",
            "",
        ];
    }

    private static List<PhraseLine> Parse(string text) =>
        LineSplitter.Split(text).Select(raw =>
        {
            var trimmed = raw.Trim();
            // 注释与空行原样保留
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) return new PhraseLine(raw);

            var fields = raw.Split('\t');
            // 至少要有「词 + 码」两列才当作可编辑条目，否则原样保留避免破坏用户内容
            if (fields.Length < 2) return new PhraseLine(raw);

            return new PhraseLine(
                verbatim: null,
                word: fields[0].Trim(),
                code: fields[1].Trim(),
                weight: fields.Length >= 3 ? fields[2].Trim() : string.Empty);
        }).ToList();

    // MARK: - 查询

    /// <summary>可编辑条目的数量。</summary>
    public int EntryCount => Lines.Count(l => l.IsEntry);

    /// <summary>
    /// 是否有未保存改动。
    /// 比对的是**界面快照**而不是写盘文本：刚点「+」还没填内容的空条目虽然不会被写进
    /// 文件（见 <see cref="Serialize"/>），但它确实是一次界面改动，「保存」按钮应当随之
    /// 点亮，否则用户填完第一格之前按钮是灰的，会以为面板卡住了。
    /// </summary>
    public bool IsDirty => UiSnapshot() != _baseline;

    // MARK: - 编辑

    /// <summary>在末尾追加一条空短语。</summary>
    public void AddEntry() => Lines.Add(new PhraseLine());

    /// <summary>删除指定 id 的短语行。</summary>
    public void RemoveEntry(Guid id) => Lines.RemoveAll(l => l.Id == id);

    /// <summary>更新指定 id 的短语行。</summary>
    public void Update(PhraseLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var index = Lines.FindIndex(l => l.Id == line.Id);
        if (index >= 0) Lines[index] = line;
    }

    // MARK: - 序列化与写盘

    /// <summary>
    /// 序列化为**写盘**文本。
    /// 词与码都为空的条目（点了「+」但没填）整行跳过，
    /// 避免往 tabledb 里写只含制表符的垃圾行。
    /// </summary>
    public string Serialize() =>
        LineSplitter.Join(Lines.Where(l => !l.IsBlankEntry).Select(l => l.Serialized));

    /// <summary>
    /// 脏值比对用的界面快照：与 <see cref="Serialize"/> 的唯一区别是**保留**未填写的
    /// 空条目，让「新增了一行」这件事本身也算一次改动。
    /// </summary>
    private string UiSnapshot() => LineSplitter.Join(Lines.Select(l => l.Serialized));

    /// <summary>写入磁盘：先备份（.bak），再原子替换。</summary>
    public void Save()
    {
        var text = Serialize();
        if (!text.EndsWith('\n')) text += "\n";

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(FilePath))
        {
            var backup = FilePath + ".bak";
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (IOException) { /* 备份失败不阻断保存 */ }
            catch (UnauthorizedAccessException) { /* 同上 */ }
        }

        AtomicWriteAllText(FilePath, text);
        Exists = true;
        _baseline = UiSnapshot();
    }

    /// <summary>
    /// 写入 UTF-8 无 BOM 文本，并原子替换目标文件。
    /// Windows 上带 BOM 会让 Rime 读不到首行 `#@/db_name` 指令（见文件头第 2 条）。
    /// </summary>
    internal static void AtomicWriteAllText(string path, string text)
    {
        var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, utf8NoBom);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }
}
