//
//  CorrectionAssets.cs — 紫毫纠错引擎资源的落地服务
//
//  ── 为什么要「先解到缓存目录、再由 Core 拷进 Rime 用户目录」───────────
//  Core 的 RimeIceConfig.DeployCorrectionAssets(assetRoot) 收的是一个**目录**
//  （里面要有 lua/amethyst_corrector.lua 与 data/correction_pinyin.txt）。
//  而本项目是 PublishSingleFile 单文件发布，exe 旁边不会有任何附带文件 ——
//  用户只从 dist/ 拿走一个 exe。所以引擎资源必须走 EmbeddedResource 进 exe，
//  运行时先解到本机缓存目录，再把那个目录交给 Core。
//
//  多一次落盘（1MB 词表）换来的是「拿一个 exe 就能用」，值。
//
//  ── 为什么要把用户目录烧进 lua ────────────────────────────────────────
//  lua 运行在 librime 里，读不了注册表。小狼毫允许用 HKCU\Software\Rime\Weasel
//  的 RimeUserDir 把用户目录改到任意位置，那种机器上 %APPDATA%\Rime 是空的，
//  纠错会「开了但一条候选都不出」，且完全静默。
//  面板读得了注册表（WeaselPaths.Detect 已经做了），所以解压时顺手把探测到的
//  真实路径替换进 lua 的占位符，作为 rime_api.get_user_data_dir() 之后的第二道
//  保险。见 lua 文件头的「为什么要三层兜底」。
//
//  ── 幂等与缓存失效 ────────────────────────────────────────────────────
//  写一个 stamp 文件记录「引擎版本 + 目标用户目录」。两者都没变且文件都在，
//  就直接复用，不重复解 1MB 词表。版本号变了（改过 lua）或用户换了 Rime
//  目录，stamp 不匹配 → 重解。
//

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WeaselPanel.App.Services;

/// <summary>紫毫纠错引擎资源（lua + 正向词表）的解压与缓存。</summary>
public static class CorrectionAssets
{
    /// <summary>
    /// 引擎版本。**改动 lua 或词表后必须 +1**，否则老机器上的缓存不会刷新，
    /// 用户装了新版面板却仍在跑旧 lua —— 这类「以为修了其实没修」最难查。
    /// </summary>
    private const int EngineVersion = 3;

    private const string LuaResourceSuffix = "Assets.CorrectionEngine.lua.amethyst_corrector.lua";
    private const string DictResourceSuffix = "Assets.CorrectionEngine.data.correction_pinyin.txt";

    /// <summary>lua 里等待被替换成真实用户目录的占位符。</summary>
    private const string UserDirPlaceholder = "@@USER_DIR@@";

    /// <summary>最近一次失败原因（成功时为 null），供界面显示而不是静默失败。</summary>
    public static string? LastError { get; private set; }

    /// <summary>缓存根目录：%LOCALAPPDATA%\WeaselPanel\CorrectionEngine。</summary>
    public static string CacheRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeaselPanel", "CorrectionEngine");

    public static string LuaPath => Path.Combine(CacheRoot, "lua", "amethyst_corrector.lua");
    public static string DictPath => Path.Combine(CacheRoot, "data", "correction_pinyin.txt");

    /// <summary>嵌入资源本身是否存在（构建时漏声明 EmbeddedResource 会让它为 false）。</summary>
    public static bool IsEmbedded =>
        FindResourceName(LuaResourceSuffix) is not null && FindResourceName(DictResourceSuffix) is not null;

    /// <summary>缓存已就绪（两个文件都在）。</summary>
    public static bool IsCached => File.Exists(LuaPath) && File.Exists(DictPath);

    /// <summary>
    /// 确保引擎资源已解到缓存目录，返回可直接交给
    /// <c>RimeIceConfig.DeployCorrectionAssets</c> 的根目录；失败返回 null
    /// （原因见 <see cref="LastError"/>）。
    /// </summary>
    /// <param name="rimeUserDirectory">探测到的 Rime 用户目录，会被烧进 lua 作为兜底路径。</param>
    public static string? EnsureExtracted(string rimeUserDirectory)
    {
        LastError = null;
        try
        {
            var stampPath = Path.Combine(CacheRoot, "stamp.txt");
            var stamp = $"v{EngineVersion}\t{rimeUserDirectory}";

            if (IsCached && File.Exists(stampPath)
                && string.Equals(File.ReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal))
            {
                return CacheRoot;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LuaPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(DictPath)!);

            // lua：读出来做占位符替换，**必须无 BOM** —— Lua 5.1 的 loadfile 不会跳过
            // BOM，带上就是一个语法错误，整个 schema 编译失败、连中文都打不出来。
            var luaText = ReadResourceText(LuaResourceSuffix)
                          ?? throw new InvalidOperationException("amethyst_corrector.lua 未嵌入 exe");
            luaText = luaText.Replace(UserDirPlaceholder, rimeUserDirectory, StringComparison.Ordinal);
            File.WriteAllText(LuaPath, luaText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // 词表：逐字节拷贝。里面靠 TAB 分列，任何「读文本再写文本」的中转都可能
            // 被换行/编码规整改动一个字节，这里不给自己留这个机会。
            CopyResourceBytes(DictResourceSuffix, DictPath);

            File.WriteAllText(stampPath, stamp);
            return CacheRoot;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    // ── 资源读取 ──────────────────────────────────────────────────────

    /// <summary>
    /// 按后缀找 manifest 资源名。
    /// 不写死全名的原因：资源名由 RootNamespace + 目录拼出，改一次命名空间或
    /// 挪一层目录就全都对不上，而且**运行时才失效、编译期完全无感**。
    /// </summary>
    private static string? FindResourceName(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static string? ReadResourceText(string suffix)
    {
        var name = FindResourceName(suffix);
        if (name is null) return null;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void CopyResourceBytes(string suffix, string destination)
    {
        var name = FindResourceName(suffix)
                   ?? throw new InvalidOperationException($"资源未嵌入 exe：{suffix}");

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"资源读取失败：{name}");
        using var file = File.Create(destination);
        stream.CopyTo(file);
    }
}
