//
//  BackupManager.cs
//  WeaselPanel.Core
//
//  配置快照备份 / 恢复 / 对比。
//
//  设计目标：只备份用户**可编辑的配置文件**（yaml 配置、*.custom.yaml 覆盖、
//  custom_phrase.txt 等），写入 <用户目录>\backups\<时间戳>\，并写 manifest.json
//  记录元信息。刻意**排除**安装自带 / 大体积产物（语法模型、系统词典、build 目录、
//  userdb 学习词库、方案与词典定义等），避免每次备份膨胀到 GB 级。
//  支持整量恢复、按文件部分恢复、以及单文件行级 diff 预览。
//
//  ── 相对 macOS 版的差异 ──
//
//  1) 路径分隔符：Windows 用 `\`，但 Rime 配置里也可能出现 `/`。
//     排除判定改为「按组件比较」，同时切分两种分隔符。
//
//  2) userdb 排除补强（Windows 侧修正，建议同步回 Squirrel Panel）：
//     原实现用 `relativePath.hasSuffix(".userdb")` 判定，只能命中**文件**。
//     而 librime 的学习词库在磁盘上是**目录**（如 `rime_ice.userdb/` 内含
//     .kct / LOCK 等），其下文件路径形如 `rime_ice.userdb/rime_ice.kct`，
//     后缀是 `.kct`，原判定失配 → 整个 userdb 目录会被复制进备份，
//     体积可达数百 MB，且 LOCK 文件常被 librime 持有导致复制失败。
//     此处改为「任一路径组件以 .userdb 结尾即排除整棵子树」。
//     该改动不改变原实现对「名为 xxx.userdb 的文件」的排除行为。
//
//  3) 编码与换行：manifest.json 用 UTF-8 无 BOM；不改动被备份文件的字节内容
//     （一律整文件复制，不做任何转码）。
//
//  4) 原 macOS 版是全局静态类（依赖 RimeEnvironment.userDirectory）。
//     此处改为实例类并注入用户目录，使整套备份逻辑可在临时目录上做单元测试，
//     不依赖运行机器是否安装了小狼毫。
//

using System.Text.Json;

namespace WeaselPanel.Core.Backup;

/// <summary>
/// 配置备份管理器。构造时注入用户目录，调用方负责保证该目录存在。
/// </summary>
public sealed class BackupManager
{
    private readonly string _userDirectory;

    public BackupManager(string userDirectory)
    {
        _userDirectory = userDirectory ?? throw new ArgumentNullException(nameof(userDirectory));
    }

    /// <summary>备份根目录（位于用户目录内，但创建/恢复时会自我排除）。</summary>
    public string BackupsDirectory => Path.Combine(_userDirectory, "backups");

    // MARK: - 创建 / 列表

    /// <summary>
    /// 创建一次「配置文件」快照。只备份用户可编辑配置，排除安装产物与大体积数据。
    /// 单个文件复制失败（broken symlink / librime 持有的 LOCK / 权限不足）不阻断整次备份。
    /// </summary>
    /// <param name="label">备份标签；null 表示自动备份。</param>
    public BackupInfo CreateBackup(string? label = null)
    {
        var backupsDir = BackupsDirectory;
        Directory.CreateDirectory(backupsDir);

        // 目录名精确到秒。同一秒内再次备份（自动化场景如「部署前自动备份」
        // 很容易命中）若不处理，会写进同一个目录并把上一次备份静默覆盖掉
        // —— CreateDirectory 对已存在目录不报错，File.Copy 又是 overwrite:true。
        var dirName = UniqueBackupDirName(backupsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var dest = Path.Combine(backupsDir, dirName);
        Directory.CreateDirectory(dest);

        var count = 0;
        long size = 0;

        foreach (var (relativePath, fullPath) in EnumerateUserFiles())
        {
            if (ShouldExclude(relativePath)) continue;

            var destFile = Path.Combine(dest, relativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(fullPath, destFile, overwrite: true);
                count++;
                size += new FileInfo(fullPath).Length;
            }
            catch (IOException)
            {
                // 单文件失败不阻断整次备份：可能是 broken symlink /
                // librime 临时持有的 LOCK / 权限不足等。跳过该文件，备份继续。
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
        }

        var manifest = new Dictionary<string, object?>
        {
            ["createdAt"] = DateTimeOffset.Now.ToString("O"),
            ["label"] = label ?? "",
            ["fileCount"] = count,
        };
        File.WriteAllText(
            Path.Combine(dest, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return new BackupInfo
        {
            DirName = dirName,
            CreatedAt = DateTimeOffset.Now,
            Label = label,
            FileCount = count,
            SizeBytes = size,
        };
    }

    /// <summary>列出全部备份（按时间倒序）。</summary>
    public List<BackupInfo> ListBackups()
    {
        var result = new List<BackupInfo>();
        if (!Directory.Exists(BackupsDirectory)) return result;

        foreach (var dir in Directory.EnumerateDirectories(BackupsDirectory))
        {
            var manifest = LoadManifest(dir);
            if (manifest is null) continue;

            var createdAt = ReadCreatedAt(dir, manifest);

            var rawLabel = manifest.TryGetValue("label", out var labelEl)
                           && labelEl.ValueKind == JsonValueKind.String
                ? labelEl.GetString()
                : null;

            var fileCount = manifest.TryGetValue("fileCount", out var countEl)
                            && countEl.TryGetInt32(out var parsedCount)
                ? parsedCount
                : 0;

            result.Add(new BackupInfo
            {
                DirName = Path.GetFileName(dir),
                CreatedAt = createdAt,
                Label = string.IsNullOrEmpty(rawLabel) ? null : rawLabel,
                FileCount = fileCount,
                SizeBytes = DirectorySize(dir),
            });
        }

        result.Sort((x, y) => y.CreatedAt.CompareTo(x.CreatedAt));
        return result;
    }

    // MARK: - 恢复 / 删除

    /// <summary>
    /// 恢复备份。<paramref name="files"/> 为 null 表示整量恢复，否则只恢复指定相对路径的文件。
    /// </summary>
    public void RestoreBackup(string dirName, IEnumerable<string>? files = null)
    {
        var src = Path.Combine(BackupsDirectory, dirName);
        if (!Directory.Exists(src))
        {
            throw new DirectoryNotFoundException($"备份不存在：{dirName}");
        }

        if (files is not null)
        {
            foreach (var rel in files)
            {
                var from = Path.Combine(src, rel);
                var to = Path.Combine(_userDirectory, rel);
                if (!File.Exists(from)) continue;
                CopyOver(from, to);
            }
            return;
        }

        foreach (var (relativePath, fullPath) in EnumerateFiles(src))
        {
            if (string.IsNullOrEmpty(relativePath)) continue;
            if (relativePath == "manifest.json") continue;
            CopyOver(fullPath, Path.Combine(_userDirectory, relativePath));
        }
    }

    public void DeleteBackup(string dirName)
    {
        Directory.Delete(Path.Combine(BackupsDirectory, dirName), recursive: true);
    }

    /// <summary>列出某次备份内可被单文件对比的配置文件（顶层 *.yaml / *.txt 等）。</summary>
    public List<string> ListBackupFiles(string dirName)
    {
        var src = Path.Combine(BackupsDirectory, dirName);
        var list = new List<string>();
        if (!Directory.Exists(src)) return list;

        list.AddRange(Directory.EnumerateFiles(src)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name != "manifest.json")
            .Select(name => name!));
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // MARK: - 差异对比

    /// <summary>对某个文件做行级 diff：返回合并后的差异序列（备份版 vs 当前版）。</summary>
    public List<DiffLine> CompareBackup(string dirName, string fileName)
    {
        var (backupText, currentText) = ReadPair(dirName, fileName);
        return LineDiffer.Diff(LineDiffer.SplitLines(backupText), LineDiffer.SplitLines(currentText));
    }

    /// <summary>对某个文件做左右双栏 diff（备份版左栏 / 当前版右栏）。</summary>
    public (List<SideBySideLine> Lines, bool Identical) CompareBackupSideBySide(string dirName, string fileName)
    {
        var (backupText, currentText) = ReadPair(dirName, fileName);
        var lines = LineDiffer.DiffSideBySide(
            LineDiffer.SplitLines(backupText), LineDiffer.SplitLines(currentText));
        return (lines, lines.All(l => l.Kind == DiffKind.Equal));
    }

    // MARK: - 内部

    private (string BackupText, string CurrentText) ReadPair(string dirName, string fileName)
    {
        var backupPath = Path.Combine(BackupsDirectory, dirName, fileName);
        var currentPath = Path.Combine(_userDirectory, fileName);
        return (ReadTextOrEmpty(backupPath), ReadTextOrEmpty(currentPath));
    }

    private static string ReadTextOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>遍历用户目录下所有文件，返回 (相对路径, 完整路径)。相对路径一律用 '/' 分隔。</summary>
    private IEnumerable<(string Relative, string Full)> EnumerateUserFiles()
    {
        if (!Directory.Exists(_userDirectory)) yield break;

        foreach (var (relative, full) in EnumerateFiles(_userDirectory))
        {
            yield return (relative, full);
        }
    }

    private static IEnumerable<(string Relative, string Full)> EnumerateFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // 对应 Swift 的 .skipsHiddenFiles；Windows 上另加 System 属性
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
            yield return (relative, path);
        }
    }

    /// <summary>
    /// 生成一个不冲突的备份目录名：基准名已存在时追加 -2、-3 … 序号。
    /// 正常情况下永不触发，仅在同秒并发或自动化连续备份时兜底。
    /// </summary>
    private static string UniqueBackupDirName(string backupsDir, string baseName)
    {
        var candidate = baseName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(backupsDir, candidate)))
        {
            suffix++;
            candidate = $"{baseName}-{suffix}";
        }
        return candidate;
    }

    /// <summary>
    /// 只保留用户可编辑的配置文件，排除安装自带 / 大体积产物：
    /// - *.gram：语法模型（可达数百 MB）
    /// - cn_dicts / en_dicts / opencc：系统词典与字符转换表（安装自带）
    /// - build：Rime 编译产物
    /// - *.userdb：librime 运行时学习词库（目录或文件，LOCK 常被 librime 持有）
    /// - *.dict.yaml / *.schema.yaml：词库与方案定义（安装自带，体积大）
    /// - *.bak、__pycache__、backups、.restore_temp*：备份自身与缓存
    /// </summary>
    internal static bool ShouldExclude(string relativePath)
    {
        var components = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var lower = relativePath.ToLowerInvariant();

        // 目录名命中即排除整棵子树
        foreach (var name in new[] { "backups", "build", "cn_dicts", "en_dicts", "opencc", "__pycache__" })
        {
            if (components.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        }
        if (components.Any(c => c.StartsWith(".restore_temp", StringComparison.OrdinalIgnoreCase))) return true;

        // Windows 侧补强：userdb 作为目录时按组件排除（见文件头第 2 条）
        if (components.Any(c => c.EndsWith(".userdb", StringComparison.OrdinalIgnoreCase))) return true;

        if (lower.EndsWith(".bak", StringComparison.Ordinal)) return true;
        if (lower.EndsWith(".gram", StringComparison.Ordinal)) return true;
        if (lower.EndsWith(".dict.yaml", StringComparison.Ordinal)) return true;
        if (lower.EndsWith(".schema.yaml", StringComparison.Ordinal)) return true;
        return false;
    }

    private static void CopyOver(string from, string to)
    {
        var dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        TryDelete(to);
        File.Copy(from, to, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); }
        catch (IOException) { /* 目标被占用：交由 File.Copy(overwrite) 决定成败 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
    }

    /// <summary>
    /// 读取备份创建时间：优先 manifest 中的 createdAt，缺失时回退到目录修改时间。
    /// </summary>
    private static DateTimeOffset ReadCreatedAt(string dir, Dictionary<string, JsonElement> manifest)
    {
        if (manifest.TryGetValue("createdAt", out var el)
            && el.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(el.GetString(), out var parsed))
        {
            return parsed;
        }
        return new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
    }

    /// <summary>
    /// 反序列化 manifest.json。
    /// ⚠️ 值类型必须是 <see cref="JsonElement"/> 而不是 object：
    /// System.Text.Json 在反序列化到 <c>Dictionary&lt;string, object?&gt;</c> 时，
    /// 会把每个值装箱为 JsonElement（而非 string / int），此时用 `is string`
    /// 判断恒为 false —— 会让 label / createdAt / fileCount 全部读不出来，
    /// 界面上表现为「备份列表里所有备份都叫自动备份、显示 0 个文件」。
    /// </summary>
    private static Dictionary<string, JsonElement>? LoadManifest(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                try { total += new FileInfo(path).Length; }
                catch (IOException) { /* 忽略无法访问的文件 */ }
            }
        }
        catch (IOException) { return total; }
        catch (UnauthorizedAccessException) { return total; }
        return total;
    }
}
