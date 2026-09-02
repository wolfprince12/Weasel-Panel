//
//  DictionaryPackageManager.cs — 词库包管理器（Windows / 小狼毫版）
//
//  移植自 macOS 鼠须管控制面板的 DictionaryPackageManager.swift，随 Weasel Panel
//  以 GPL-3.0 分发。职责是把「精选词库包」（如雾凇拼音 rime-ice）安装 / 更新 / 卸载
//  到本机 Rime 用户目录，全程基于「快照 + 备份 + 清单」三件套，严守底线：
//    · 只删除本面板自己装进去的文件；
//    · 安装前备份会被覆盖的文件，卸载时还原；
//    · 绝不动用户自己原有的其它文件。
//
//  ── 与 macOS 版的关键差异（均为有意为之）─────────────────────────────────
//   1) 解压：macOS 用 ditto，Windows 用 System.IO.Compression.ZipFile。
//   2) 下载：macOS 用 GitHubMirrorFetch（URLSession 封装）；
//      这里直接复用 WeaselPanel.Core.Net.GitHubMirrorFetch（HttpClient 封装，
//      已带镜像候选回退）。南大镜像 / CNB 镜像等候选 URL 在此构造。
//   3) 托管目录：macOS 用 .squirrel-panel，Windows 用 .weasel-panel（与产品名一致）。
//   4) 部署：macOS 用 SquirrelBridge.deploy，这里用 WeaselDeployer.RunAsync。
//   5) 清单 / 路径：全部落在 Rime 用户目录下（与 macOS 一致，不在 %APPDATA%\WeaselPanel）。
//   6) 不引入 Token：更新检查走 HEAD 重定向解析 release tag（无需 GitHub API Token），
//      语法模型走 Content-Length 比对，均不依赖鉴权，避免额外 UI 与密钥存储。
//
//  ⚠️ 本文件只依赖 BCL，不引用任何 WPF / Windows 专有程序集（Core 跨平台铁律）。
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WeaselPanel.Core.Net;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.Core.Rime;

// MARK: - 数据模型

/// <summary>注册表里描述的一个词库包（来自嵌入式 DictionaryPackages.json）。</summary>
public sealed class DictionaryPackage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("name_en")] public string NameEn { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("description_en")] public string DescriptionEn { get; set; } = "";
    [JsonPropertyName("sourceURL")] public string SourceUrl { get; set; } = "";
    [JsonPropertyName("releaseAsset")] public string? ReleaseAsset { get; set; }
    [JsonPropertyName("repoOwner")] public string? RepoOwner { get; set; }
    [JsonPropertyName("repoName")] public string? RepoName { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
    [JsonPropertyName("defaultSchema")] public string DefaultSchema { get; set; } = "";
    [JsonPropertyName("homepage")] public string Homepage { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>是否语法模型包（万象等）。语法模型不走整包解压，只下单个 .gram。</summary>
    public bool IsGrammar => Type == "grammar";

    /// <summary>语法模型的语言名：去掉 .gram 后缀（如 wanxiang-lts-zh-hans）。</summary>
    public string GrammarLanguage => (ReleaseAsset ?? "wanxiang-lts-zh-hans.gram").Replace(".gram", "");

    /// <summary>更新检查用的 GitHub 仓库路径（owner/name），缺信息时返回 null。</summary>
    public string? RepoPath =>
        !string.IsNullOrEmpty(RepoOwner) && !string.IsNullOrEmpty(RepoName)
            ? $"{RepoOwner}/{RepoName}"
            : null;
}

/// <summary>安装清单：记录本面板装了哪些文件、备份在哪、锁定的版本，用于更新比对与卸载还原。</summary>
public sealed class PackageManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("addedFiles")] public List<string> AddedFiles { get; set; } = new();
    [JsonPropertyName("backupDir")] public string BackupDir { get; set; } = "";
    [JsonPropertyName("defaultSchema")] public string DefaultSchema { get; set; } = "";
    [JsonPropertyName("installedAt")] public DateTime InstalledAt { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "0.3.0";
    [JsonPropertyName("installedCommit")] public string? InstalledCommit { get; set; }
    [JsonPropertyName("installedTag")] public string? InstalledTag { get; set; }
    [JsonPropertyName("installedSize")] public long? InstalledSize { get; set; }
}

/// <summary>包的安装状态。</summary>
public enum PackageStatusKind
{
    NotInstalled,
    Installed,   // 由本面板安装并管理
    External,    // 文件已存在，但非本面板安装
}

/// <summary>包状态：携带清单（若已安装）。</summary>
public sealed class PackageStatus
{
    public PackageStatusKind Kind { get; init; }
    public PackageManifest? Manifest { get; init; }
    public bool IsInstalled => Kind == PackageStatusKind.Installed;
    public bool IsExternal => Kind == PackageStatusKind.External;
}

/// <summary>单个包的检查更新结果。</summary>
public enum PackageUpdateKind
{
    NotApplicable, // 未安装 / 外部安装，无需检查
    Checking,     // 正在检查
    UpToDate,     // 已是最新
    Available,    // 有更新
    Unknown,      // 无法判断（未记录版本等），但允许手动更新
    Failed,       // 检查失败（网络 / 限流等）
}

/// <summary>更新状态：Failed 时携带错误信息。</summary>
public sealed class PackageUpdateState
{
    public static readonly PackageUpdateState NotApplicable = new(PackageUpdateKind.NotApplicable);
    public static readonly PackageUpdateState Checking = new(PackageUpdateKind.Checking);
    public static readonly PackageUpdateState UpToDate = new(PackageUpdateKind.UpToDate);
    public static readonly PackageUpdateState Unknown = new(PackageUpdateKind.Unknown);
    public static readonly PackageUpdateState Available = new(PackageUpdateKind.Available);

    public static PackageUpdateState Failed(string? message) => new(PackageUpdateKind.Failed, message);

    public PackageUpdateKind Kind { get; init; }
    public string? Message { get; init; }
    public bool IsChecking => Kind == PackageUpdateKind.Checking;

    public PackageUpdateState(PackageUpdateKind kind, string? message = null)
    {
        Kind = kind;
        Message = message;
    }
}

/// <summary>词库包管理器的错误。携带本地化键，App 层直接据此渲染文案。</summary>
public sealed class PackageManagerException : Exception
{
    public string L10nKey { get; }
    public object?[] Args { get; }

    public PackageManagerException(string l10nKey, params object?[] args)
        : base(l10nKey)
    {
        L10nKey = l10nKey;
        Args = args;
    }
}

// MARK: - 管理器

/// <summary>词库包安装 / 更新 / 卸载。全部静态方法；网络与磁盘 IO 均为异步。</summary>
public static class DictionaryPackageManager
{
    private const string ManagedDirName = ".weasel-panel";
    private const string ManifestVersion = "0.3.0";

    /// <summary>安装时跳过的非运行时文件 / 目录（陈旧编译产物、仓库元数据、庞杂素材等）。</summary>
    private static readonly HashSet<string> ExcludeFromInstall = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", ".git", ".github", "others", "__MACOSX",
        "AGENTS.md", "README.md", "LICENSE", ".gitignore", "recipe.yaml", "Thumbs.db",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly GitHubMirrorFetch Fetch = new();

    // MARK: - 注册表

    private static IReadOnlyList<DictionaryPackage>? _registryCache;

    /// <summary>读取内置注册表（嵌入式 DictionaryPackages.json）。解析失败返回空列表。</summary>
    public static IReadOnlyList<DictionaryPackage> LoadRegistry()
    {
        if (_registryCache is not null) return _registryCache;

        var asm = typeof(DictionaryPackageManager).Assembly;
        using var stream = asm.GetManifestResourceStream("WeaselPanel.Core.Resources.DictionaryPackages.json");
        if (stream is null)
        {
            _registryCache = Array.Empty<DictionaryPackage>();
            return _registryCache;
        }

        try
        {
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var list = JsonSerializer.Deserialize<List<DictionaryPackage>>(json);
            _registryCache = list ?? new List<DictionaryPackage>();
        }
        catch
        {
            _registryCache = Array.Empty<DictionaryPackage>();
        }
        return _registryCache;
    }

    // MARK: - 路径

    private static string ManagedRoot(WeaselEnvironment env) =>
        Path.Combine(env.UserDirectory, ManagedDirName);

    private static string ManifestsDir(WeaselEnvironment env) =>
        Path.Combine(ManagedRoot(env), "manifests");

    private static string BackupDirFor(WeaselEnvironment env, string id) =>
        Path.Combine(ManagedRoot(env), "backups", id);

    private static string ManifestPath(WeaselEnvironment env, string id) =>
        Path.Combine(ManifestsDir(env), id + ".json");

    private static string SchemaFile(WeaselEnvironment env, string id) =>
        Path.Combine(env.UserDirectory, $"{id}.schema.yaml");

    /// <summary>
    /// 本面板的托管目录（清单 + 备份都在这里）。界面要把它显示给用户 ——
    /// 卸载时「还原备份」这件事只有让用户看见备份放在哪，才谈得上可信。
    /// </summary>
    public static string ManagedDirectory(WeaselEnvironment env) => ManagedRoot(env);

    /// <summary>语法模型依赖雾凇拼音（rime_ice）；其 schema 文件存在即视为已装。</summary>
    public static bool IsRimeIceInstalled(WeaselEnvironment env) =>
        File.Exists(SchemaFile(env, "rime_ice"));

    /// <summary>某 id 是否由本面板安装。</summary>
    public static bool IsPackageInstalled(string id, WeaselEnvironment env) =>
        StatusOf(LoadRegistry().FirstOrDefault(p => p.Id == id), env) is { IsInstalled: true };

    // MARK: - 状态

    /// <summary>查询某包的安装状态：有清单 = 本面板安装；否则若默认 schema 文件存在 = 外部安装；否则未安装。</summary>
    public static PackageStatus StatusOf(DictionaryPackage? pkg, WeaselEnvironment env)
    {
        if (pkg is null) return new PackageStatus { Kind = PackageStatusKind.NotInstalled };

        var mPath = ManifestPath(env, pkg.Id);
        if (File.Exists(mPath))
        {
            try
            {
                var json = File.ReadAllText(mPath);
                var manifest = JsonSerializer.Deserialize<PackageManifest>(json);
                if (manifest is not null)
                    return new PackageStatus { Kind = PackageStatusKind.Installed, Manifest = manifest };
            }
            catch
            {
                // 清单损坏：当作未安装，让用户可以重新安装
            }
        }

        if (!string.IsNullOrEmpty(pkg.DefaultSchema) && File.Exists(SchemaFile(env, pkg.DefaultSchema)))
            return new PackageStatus { Kind = PackageStatusKind.External };

        return new PackageStatus { Kind = PackageStatusKind.NotInstalled };
    }

    // MARK: - 安装

    /// <summary>安装一个包（按类型分派到整包 / 语法模型分支）。</summary>
    public static async Task<PackageManifest> InstallAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        if (!env.IsInstalled) throw new PackageManagerException("Packages.Error.WeaselNotInstalled");
        return pkg.IsGrammar
            ? await InstallGrammarAsync(pkg, env)
            : await InstallDictionaryAsync(pkg, env);
    }

    private static async Task<PackageManifest> InstallDictionaryAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var rime = env.UserDirectory;
        Directory.CreateDirectory(rime);
        Directory.CreateDirectory(ManifestsDir(env));
        Directory.CreateDirectory(BackupDirFor(env, pkg.Id));

        // 1. 取上游版本信息：release asset 包锁定 tag；commit-based 包锁定 SHA。
        string? releaseTag = null;
        string? commitSha = null;
        if (UsesReleaseAsset(pkg))
        {
            try { releaseTag = (await FetchLatestReleaseTagAsync(pkg))?.Tag; } catch { /* 版本记录失败不阻塞安装 */ }
        }
        else
        {
            try { commitSha = (await FetchLatestCommitShaAsync(pkg))?.Sha; } catch { /* 同上 */ }
        }

        // 2. 下载
        var zipPath = await DownloadWithCandidatesAsync(InstallDownloadUrls(pkg, releaseTag, commitSha));

        // 3. 解压到临时目录
        var stage = Path.Combine(Path.GetTempPath(), $"weasel-panel-{pkg.Id}-{Guid.NewGuid():N}");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, stage, overwriteFiles: true);

            // 4. 定位包根（处理内层文件夹 rime-ice-main / rime-ice-<sha>）
            var packageRoot = LocatePackageRoot(stage);

            // 5. 枚举要安装的文件（排除非运行时文件 / 元数据）
            var allFiles = SnapshotFiles(packageRoot);
            var filesToInstall = allFiles.Where(ShouldInstall).ToList();

            // 6. 备份被覆盖的文件 + 复制
            var addedFiles = ApplyPackageFiles(packageRoot, filesToInstall, rime, BackupDirFor(env, pkg.Id), overwrite: false);

            // 7. 启用默认方案
            EnableSchema(pkg.DefaultSchema, env);

            // 8. 重新部署
            await WeaselDeployer.RunAsync(env, "/deploy");

            // 9. 写清单
            var manifest = new PackageManifest
            {
                Id = pkg.Id,
                AddedFiles = addedFiles,
                BackupDir = BackupDirFor(env, pkg.Id),
                DefaultSchema = pkg.DefaultSchema,
                InstalledAt = DateTime.Now,
                Version = ManifestVersion,
                InstalledCommit = commitSha,
                InstalledTag = releaseTag,
            };
            WriteManifest(env, pkg.Id, manifest);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(stage)) { try { Directory.Delete(stage, true); } catch { /* 忽略 */ } }
            if (File.Exists(zipPath)) { try { File.Delete(zipPath); } catch { /* 忽略 */ } }
        }
    }

    // MARK: - 语法模型（万象等）

    private static async Task<PackageManifest> InstallGrammarAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        // 语法模型必须挂载在雾凇（rime_ice）方案上；雾凇未装则直接拒绝。
        if (!IsRimeIceInstalled(env))
            throw new PackageManagerException("Packages.Error.GrammarRequiresRimeIce");

        var rime = env.UserDirectory;
        Directory.CreateDirectory(rime);
        Directory.CreateDirectory(ManifestsDir(env));

        var asset = pkg.ReleaseAsset ?? "wanxiang-lts-zh-hans.gram";
        var language = pkg.GrammarLanguage;

        // 安装前备份将被修改的 rime_ice.custom.yaml（save 也会留 .bak，这里显式兜底）
        var schemaFile = Path.Combine(rime, "rime_ice.custom.yaml");
        if (File.Exists(schemaFile))
        {
            var bak = schemaFile + ".bak";
            try { if (File.Exists(bak)) File.Delete(bak); File.Copy(schemaFile, bak); } catch { /* 忽略 */ }
        }

        var fileUrl = await DownloadWithCandidatesAsync(GrammarCandidateUrls(pkg));
        var dst = Path.Combine(rime, asset);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) { try { File.Delete(dst); } catch { /* 忽略 */ } }
        File.Copy(fileUrl, dst);

        ApplyGrammarPatch(language, env);

        await WeaselDeployer.RunAsync(env, "/deploy");

        long? installedSize = null;
        if (File.Exists(dst))
        {
            try { installedSize = new FileInfo(dst).Length; } catch { /* 忽略 */ }
        }

        var manifest = new PackageManifest
        {
            Id = pkg.Id,
            AddedFiles = new List<string> { asset },
            BackupDir = BackupDirFor(env, pkg.Id),
            DefaultSchema = "",
            InstalledAt = DateTime.Now,
            Version = ManifestVersion,
            InstalledTag = "LTS",
            InstalledSize = installedSize,
        };
        WriteManifest(env, pkg.Id, manifest);

        if (File.Exists(fileUrl)) { try { File.Delete(fileUrl); } catch { /* 忽略 */ } }
        return manifest;
    }

    private static async Task<PackageManifest> UpdateGrammarAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var rime = env.UserDirectory;
        var asset = pkg.ReleaseAsset ?? "wanxiang-lts-zh-hans.gram";
        var language = pkg.GrammarLanguage;

        var fileUrl = await DownloadWithCandidatesAsync(GrammarCandidateUrls(pkg));
        var dst = Path.Combine(rime, asset);
        if (File.Exists(dst)) { try { File.Delete(dst); } catch { /* 忽略 */ } }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(fileUrl, dst);
        ApplyGrammarPatch(language, env);

        await WeaselDeployer.RunAsync(env, "/deploy");

        var mPath = ManifestPath(env, pkg.Id);
        PackageManifest? manifest = null;
        if (File.Exists(mPath))
        {
            try { manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(mPath)); } catch { /* 忽略 */ }
        }
        manifest ??= new PackageManifest { Id = pkg.Id, AddedFiles = new List<string> { asset }, BackupDir = BackupDirFor(env, pkg.Id) };
        manifest.InstalledAt = DateTime.Now;
        if (File.Exists(dst)) { try { manifest.InstalledSize = new FileInfo(dst).Length; } catch { /* 忽略 */ } }
        WriteManifest(env, pkg.Id, manifest);

        if (File.Exists(fileUrl)) { try { File.Delete(fileUrl); } catch { /* 忽略 */ } }
        return manifest;
    }

    private static void ApplyGrammarPatch(string language, WeaselEnvironment env)
    {
        var rime = env.UserDirectory;

        // 正确位置：方案级补丁 rime_ice.custom.yaml
        var schemaPatch = new CustomYamlFile(Path.Combine(rime, "rime_ice.custom.yaml"));
        schemaPatch.Set("grammar/language", language);
        schemaPatch.Set("grammar/collocation_prism", "rime_ice.prism");
        schemaPatch.Save();

        // 兼容清理：旧位置（v1.2.3 测试版误写入 default.custom.yaml）的遗留键
        var defaultPatch = new CustomYamlFile(Path.Combine(rime, "default.custom.yaml"));
        if (defaultPatch.StringForPath("grammar/language") is not null)
        {
            defaultPatch.Set("grammar/language", null);
            try { defaultPatch.Save(); } catch { /* 忽略 */ }
        }
    }

    // MARK: - 更新

    /// <summary>更新已安装的包到上游最新版本。</summary>
    public static async Task<PackageManifest> UpdateAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var mPath = ManifestPath(env, pkg.Id);
        if (!File.Exists(mPath))
            throw new PackageManagerException("Packages.Error.NotManaged");
        if (!env.IsInstalled) throw new PackageManagerException("Packages.Error.WeaselNotInstalled");

        return pkg.IsGrammar
            ? await UpdateGrammarAsync(pkg, env)
            : await UpdateDictionaryAsync(pkg, env);
    }

    private static async Task<PackageManifest> UpdateDictionaryAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var rime = env.UserDirectory;
        Directory.CreateDirectory(rime);

        string? releaseTag = null;
        string? commitSha = null;
        if (UsesReleaseAsset(pkg))
        {
            try { releaseTag = (await FetchLatestReleaseTagAsync(pkg))?.Tag; } catch { /* 同上 */ }
        }
        else
        {
            try { commitSha = (await FetchLatestCommitShaAsync(pkg))?.Sha; } catch { /* 同上 */ }
        }

        var zipPath = await DownloadWithCandidatesAsync(InstallDownloadUrls(pkg, releaseTag, commitSha));

        var stage = Path.Combine(Path.GetTempPath(), $"weasel-panel-update-{pkg.Id}-{Guid.NewGuid():N}");
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, stage, overwriteFiles: true);
            var packageRoot = LocatePackageRoot(stage);

            var allFiles = SnapshotFiles(packageRoot);
            var filesToInstall = allFiles.Where(ShouldInstall).ToList();

            // 删除「旧版本有、新版本没有」的文件（仅动我们追踪的）
            var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(ManifestPath(env, pkg.Id)))!;
            var oldSet = new HashSet<string>(manifest.AddedFiles, StringComparer.Ordinal);
            var newSet = new HashSet<string>(filesToInstall, StringComparer.Ordinal);
            foreach (var rel in oldSet.Where(r => !newSet.Contains(r)))
            {
                var dst = Path.Combine(rime, rel);
                if (File.Exists(dst)) { try { File.Delete(dst); } catch { /* 忽略 */ } }
            }

            // 覆盖写入（保留首次安装时的原始备份）
            ApplyPackageFiles(packageRoot, filesToInstall, rime, manifest.BackupDir, overwrite: true);

            EnableSchema(pkg.DefaultSchema, env);
            await WeaselDeployer.RunAsync(env, "/deploy");

            manifest.AddedFiles = filesToInstall;
            manifest.InstalledCommit = commitSha;
            manifest.InstalledTag = releaseTag;
            manifest.InstalledAt = DateTime.Now;
            manifest.Version = "0.3.2";
            WriteManifest(env, pkg.Id, manifest);
            return manifest;
        }
        finally
        {
            if (Directory.Exists(stage)) { try { Directory.Delete(stage, true); } catch { /* 忽略 */ } }
            if (File.Exists(zipPath)) { try { File.Delete(zipPath); } catch { /* 忽略 */ } }
        }
    }

    // MARK: - 卸载

    /// <summary>卸载一个包。雾凇拼音被万象依赖，须先卸万象。</summary>
    public static async Task UninstallAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var mPath = ManifestPath(env, pkg.Id);
        if (!File.Exists(mPath))
            throw new PackageManagerException("Packages.Error.NotManaged");

        // 卸载雾凇前必须先卸载万象语法模型，否则 .gram 与 rime_ice.custom.yaml 注入脱节，模型残留失效。
        if (pkg.Id == "rime-ice" && IsPackageInstalled("wanxiang-grammar", env))
            throw new PackageManagerException("Packages.Error.GrammarFirst");

        if (pkg.IsGrammar)
        {
            await UninstallGrammarAsync(pkg, env);
            return;
        }

        var rime = env.UserDirectory;
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(mPath))!;
        var backupBase = manifest.BackupDir;

        // 1. 删除本面板新增的文件
        foreach (var rel in manifest.AddedFiles)
        {
            var dst = Path.Combine(rime, rel);
            if (File.Exists(dst)) { try { File.Delete(dst); } catch { /* 忽略 */ } }
        }

        // 2. 还原被覆盖的原始文件
        foreach (var rel in manifest.AddedFiles)
        {
            var backup = Path.Combine(backupBase, rel);
            var dst = Path.Combine(rime, rel);
            if (File.Exists(backup))
            {
                if (File.Exists(dst)) { try { File.Delete(dst); } catch { /* 忽略 */ } }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(backup, dst);
                }
                catch { /* 忽略 */ }
            }
        }

        // 3. 从 schema_list 移除该默认方案（但不允许列表变空）
        DisableSchema(pkg.DefaultSchema, env);

        // 4. 重新部署
        await WeaselDeployer.RunAsync(env, "/deploy");

        // 5. 删除清单
        if (File.Exists(mPath)) { try { File.Delete(mPath); } catch { /* 忽略 */ } }
    }

    private static async Task UninstallGrammarAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var rime = env.UserDirectory;
        var mPath = ManifestPath(env, pkg.Id);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(mPath))!;

        foreach (var rel in manifest.AddedFiles)
        {
            var dst = Path.Combine(rime, rel);
            if (File.Exists(dst)) { try { File.Delete(dst); } catch { /* 忽略 */ } }
        }

        // 移除方案级配置（grammar/language 与 grammar/collocation_prism）
        var schemaPatch = new CustomYamlFile(Path.Combine(rime, "rime_ice.custom.yaml"));
        schemaPatch.RemoveGrammar();
        try { schemaPatch.Save(); } catch { /* 忽略 */ }

        // 兼容清理旧位置
        var defaultPatch = new CustomYamlFile(Path.Combine(rime, "default.custom.yaml"));
        defaultPatch.RemoveGrammar();
        try { defaultPatch.Save(); } catch { /* 忽略 */ }

        await WeaselDeployer.RunAsync(env, "/deploy");

        if (File.Exists(mPath)) { try { File.Delete(mPath); } catch { /* 忽略 */ } }
    }

    // MARK: - 更新检查

    /// <summary>检查某包是否有更新。未安装返回 NotApplicable；网络失败返回 Failed。</summary>
    public static async Task<PackageUpdateState> CheckUpdateAsync(DictionaryPackage pkg, WeaselEnvironment env)
    {
        var status = StatusOf(pkg, env);
        if (!status.IsInstalled || status.Manifest is null)
            return PackageUpdateState.NotApplicable;

        try
        {
            if (pkg.IsGrammar)
            {
                // 语法模型：比对远程 .gram 文件大小与本地记录
                var size = await Fetch.ContentLengthAsync(GrammarCandidateUrls(pkg));
                if (size is null) return PackageUpdateState.Unknown;
                var local = status.Manifest.InstalledSize;
                return local is null || local != size
                    ? PackageUpdateState.Available
                    : PackageUpdateState.UpToDate;
            }

            // 整包：比对远程 release tag 与本地记录
            var latest = await FetchLatestReleaseTagAsync(pkg);
            if (latest?.Tag is null) return PackageUpdateState.Unknown;
            return string.Equals(latest.Tag, status.Manifest.InstalledTag, StringComparison.Ordinal)
                ? PackageUpdateState.UpToDate
                : PackageUpdateState.Available;
        }
        catch (Exception ex)
        {
            return PackageUpdateState.Failed(ex.Message);
        }
    }

    // MARK: - 方案启用辅助

    private static void EnableSchema(string id, WeaselEnvironment env)
    {
        if (string.IsNullOrEmpty(id)) return;
        var schemaFile = SchemaFile(env, id);
        if (!File.Exists(schemaFile)) return; // 方案文件不存在则不写（避免写入不存在的 id）

        var catalog = SchemaCatalog.Build(env.UserDirectory, env.SharedDataDirectory);
        var ids = new List<string>(catalog.EffectiveActiveIds);
        if (!ids.Contains(id, StringComparer.Ordinal))
        {
            ids.Insert(0, id);
            WriteSchemaList(env, ids);
        }
    }

    private static void DisableSchema(string id, WeaselEnvironment env)
    {
        if (string.IsNullOrEmpty(id)) return;
        var catalog = SchemaCatalog.Build(env.UserDirectory, env.SharedDataDirectory);
        var ids = new List<string>(catalog.EffectiveActiveIds);
        if (ids.Remove(id))
        {
            if (ids.Count == 0) ids.Add(id); // 不允许方案列表变空，否则输入法彻底打不出字
            WriteSchemaList(env, ids);
        }
    }

    private static void WriteSchemaList(WeaselEnvironment env, List<string> ids)
    {
        var path = Path.Combine(env.UserDirectory, "default.custom.yaml");
        var custom = new CustomYamlFile(path);
        if (custom.State == CustomYamlLoadState.Absent) custom.Load();
        if (!custom.IsWritable) return;

        var set = new PatchSet();
        set.Set("schema_list", PatchValue.SchemaList(ids));
        custom.ApplyLineEdits(set);
    }

    // MARK: - 下载

    /// <summary>依次尝试候选 URL 下载；任一成功即返回本地临时文件路径，全部失败抛错。</summary>
    private static async Task<string> DownloadWithCandidatesAsync(IReadOnlyList<string> candidates, int timeoutSeconds = 120)
    {
        if (candidates.Count == 0) throw new PackageManagerException("Packages.Error.Download", "");
        var lastUrl = candidates[0];
        foreach (var url in candidates)
        {
            lastUrl = url;
            try
            {
                var dest = Path.Combine(Path.GetTempPath(), $"weasel-panel-{Guid.NewGuid():N}.dl");
                await Fetch.DownloadAsync(url, dest, timeoutSeconds);
                return dest;
            }
            catch
            {
                // 继续尝试下一个候选
            }
        }
        throw new PackageManagerException("Packages.Error.Download", lastUrl);
    }

    private static bool UsesReleaseAsset(DictionaryPackage pkg) =>
        !string.IsNullOrEmpty(pkg.ReleaseAsset);

    /// <summary>release asset 包的候选下载 URL：南大镜像优先，其次原始 URL（其内部再走镜像前缀回退）。</summary>
    private static IReadOnlyList<string> ReleaseAssetUrls(DictionaryPackage pkg)
    {
        if (string.IsNullOrEmpty(pkg.ReleaseAsset) || string.IsNullOrEmpty(pkg.RepoPath))
            return new List<string> { pkg.SourceUrl };
        var original = $"https://github.com/{pkg.RepoPath}/releases/latest/download/{pkg.ReleaseAsset}";
        var nju = $"https://mirror.nju.edu.cn/github-release/{pkg.RepoPath}/LatestRelease/{pkg.ReleaseAsset}";
        return new List<string> { nju, original };
    }

    private static IReadOnlyList<string> InstallDownloadUrls(DictionaryPackage pkg, string? releaseTag, string? commitSha)
    {
        if (UsesReleaseAsset(pkg))
            return ReleaseAssetUrls(pkg);

        if (!string.IsNullOrEmpty(commitSha) && !string.IsNullOrEmpty(pkg.RepoPath))
        {
            var original = $"https://github.com/{pkg.RepoPath}/archive/{commitSha}.zip";
            return new List<string> { original };
        }
        return new List<string> { pkg.SourceUrl };
    }

    /// <summary>语法模型候选地址：CNB 大陆镜像优先，其次原始 URL 与其镜像前缀。</summary>
    private static IReadOnlyList<string> GrammarCandidateUrls(DictionaryPackage pkg)
    {
        var asset = pkg.ReleaseAsset ?? "wanxiang-lts-zh-hans.gram";
        var cnb = $"https://cnb.cool/amzxyz/rime-wanxiang/-/releases/download/model/{asset}";
        var candidates = new List<string> { cnb, pkg.SourceUrl };
        candidates.AddRange(GitHubMirrorFetch.CandidateUrls(pkg.SourceUrl));
        return candidates;
    }

    private static async Task<LatestRelease?> FetchLatestReleaseTagAsync(DictionaryPackage pkg)
    {
        if (pkg.RepoPath is null) return null;
        try { return await Fetch.FetchLatestReleaseAsync(pkg.RepoPath); }
        catch { return null; }
    }

    private static async Task<LatestCommit?> FetchLatestCommitShaAsync(DictionaryPackage pkg)
    {
        if (string.IsNullOrEmpty(pkg.RepoOwner) || string.IsNullOrEmpty(pkg.RepoName) || string.IsNullOrEmpty(pkg.Branch))
            return null;
        try { return await Fetch.FetchLatestCommitAsync(pkg.RepoOwner, pkg.RepoName, pkg.Branch); }
        catch { return null; }
    }

    // MARK: - 快照 / 文件

    /// <summary>递归列出目录下所有文件，返回相对路径（以 / 分隔）。</summary>
    private static List<string> SnapshotFiles(string root)
    {
        var result = new List<string>();
        if (!Directory.Exists(root)) return result;

        var rootDir = new DirectoryInfo(root);
        foreach (var file in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if ((file.Attributes & FileAttributes.Directory) != 0) continue;
            var rel = file.FullName.Substring(rootDir.FullName.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            result.Add(rel.Replace(Path.DirectorySeparatorChar, '/'));
        }
        return result;
    }

    private static bool ShouldInstall(string rel)
    {
        var parts = rel.Split('/');
        if (parts.Length == 0) return false;
        var top = parts[0];
        if (ExcludeFromInstall.Contains(top)) return false;
        // 排除 AppleDouble 资源分支文件（._*）与隐藏文件
        var last = parts[^1];
        if (last.StartsWith(".", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>把包内文件复制到 Rime 目录。overwrite=false 时先备份被覆盖的文件（首次安装）；
    /// 返回实际写入的相对路径列表。源文件缺失则跳过，不终止整个流程。</summary>
    private static List<string> ApplyPackageFiles(string packageRoot, List<string> files, string rime, string backupDir, bool overwrite)
    {
        var written = new List<string>();
        foreach (var rel in files)
        {
            var src = Path.Combine(packageRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(rime, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue; // 源文件缺失（zip 损坏/不完整）→ 跳过并继续

            if (!overwrite && File.Exists(dst))
            {
                var backup = Path.Combine(backupDir, rel.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Copy(dst, backup);
                }
                catch { /* 备份失败不阻断安装 */ }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst)) File.Delete(dst);
                File.Copy(src, dst);
                written.Add(rel);
            }
            catch { /* 单文件复制失败不阻断整包 */ }
        }
        return written;
    }

    private static void WriteManifest(WeaselEnvironment env, string id, PackageManifest manifest)
    {
        Directory.CreateDirectory(ManifestsDir(env));
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(ManifestPath(env, id), json);
    }

    /// <summary>定位包根：若解压后只有一个顶层目录（rime-ice-main 之类），深入该目录；否则用 stage 根。</summary>
    private static string LocatePackageRoot(string stage)
    {
        if (!Directory.Exists(stage)) return stage;
        var dirs = Directory.GetDirectories(stage);
        if (dirs.Length == 1) return dirs[0];
        return stage;
    }
}
