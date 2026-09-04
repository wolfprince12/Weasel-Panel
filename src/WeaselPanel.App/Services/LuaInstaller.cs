//
//  LuaInstaller.cs — 部署 librime-lua 的「Lua 运行时」lua54.dll 到小狼毫安装目录
//
//  设计铁律（2026-09-04 推翻重做）：
//  - 绝不直接覆盖核心 rime.dll。历史方案覆盖 rime.dll，一旦版本/位数错配，
//    WeaselDeployer 启动必加载 rime.dll 即抛 0xC000007B，整个输入法崩溃。
//  - 改为只部署「动态 lua54.dll」：rime.dll 始终是用户系统自带的正确版本，Lua 作为
//    可选插件按需 LoadLibrary。即使 lua54.dll 版本微差，最坏只是紫毫不工作，
//    绝不会让输入法启动失败——这是彻底消除 0xC000007B 的根治方案。
//  - lua54.dll 必须与系统架构一致（x64），故部署前强制 PE 架构校验。
//
//  本类只在该 Windows 面板上被调用，故全程 [SupportedOSPlatform("windows")]。

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace WeaselPanel.App.Services;

public static class LuaInstaller
{
    /// <summary>
    /// 最近一次失败原因。安装流程把它当上下文抛出，调用方（如 InstallLuaAsync）
    /// 决定是否把它翻译成 L10n 的具体错误文案（架构不匹配、提权拒绝、…）。
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>PE Machine 字段 → 架构名（用于错误文案）。</summary>
    private static string ArchName(ushort machine) => machine switch
    {
        0x8664 => "x64",
        0x014c => "x86",
        0xaa64 => "ARM64",
        0x01c0 => "ARM",
        _ => $"0x{machine:X4}",
    };

    /// <summary>
    /// 裸读 PE 头的 Machine 字段，不依赖 PEReader。
    /// 用法：先读 e_lfanew（DOS 头 0x3C 起的 4 字节 LE int32），跳到 PE\0\0 ，
    /// 再读 IMAGE_FILE_HEADER.Machine（PE 签名 + 4 字节起的 2 字节 LE ushort）。
    /// 任何偏移越界或不匹配 PE\0\0 签名都返回 null（不是合法 PE）。
    /// </summary>
    private static ushort? PeMachine(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[0x40];
            if (fs.Read(dos) != dos.Length) return null;
            var e_lfanew = BitConverter.ToInt32(dos.Slice(0x3C, 4));
            if (e_lfanew < 0 || e_lfanew + 24 > fs.Length) return null;
            fs.Seek(e_lfanew, SeekOrigin.Begin);
            Span<byte> pe = stackalloc byte[24];
            if (fs.Read(pe) != pe.Length) return null;
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0)
                return null;
            return BitConverter.ToUInt16(pe.Slice(4, 2));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把用户选中的 lua54.dll 部署到小狼毫安装目录。
    /// ⚠️ 只动 lua54.dll，绝不覆盖核心 rime.dll——这是根治 0xC000007B 的关键。
    /// 部署前强制 PE 架构校验：lua54.dll 必须与系统架构一致（x64），否则拒绝并写 LastError。
    /// 内部流程（全部在提权 bat 内完成，用户只见一次 UAC）：
    ///   taskkill /f /im WeaselServer.exe  →  停算法服务（避免 lua54.dll 被占用）
    ///   copy /Y 选中的lua54.dll → lua54.dll  →  只部署 Lua 运行时
    /// 返回 true 表示部署后目标 lua54.dll 与源字节数一致（UAC 取消则文件不变，判失败）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<bool> DeployLuaLibraryAsync(
        string destDir, string srcLua)
    {
        // 每次调用重置 LastError，避免上一次失败原因污染下一次。
        LastError = null;

        // ── PE 架构校验 ────────────────────────────────────────
        // lua54.dll 一旦位数与系统（WeaselDeployer.exe / rime.dll）不匹配，
        // LoadLibrary 会失败——但**不会**像覆盖 rime.dll 那样让整个输入法崩，
        // 这里仍提前拦截，避免用户装了个根本加载不了的 dll。
        var srcMachine = PeMachine(srcLua);
        if (srcMachine is null)
        {
            LastError = "源文件不是有效的 PE 文件，无法读取 Machine 字段。";
            return false;
        }

        // 锚点优先用 WeaselDeployer.exe（小狼毫安装时自带的不可变二进制），
        // 退化用已装 rime.dll；两者都缺失则跳过架构校验（宽松，但极少见）。
        string? anchorPath = null;
        foreach (var name in new[]
        {
            "WeaselDeployer.exe", Path.Combine("Win32", "WeaselDeployer.exe"),
            "rime.dll", Path.Combine("Win32", "rime.dll"),
        })
        {
            var p = Path.Combine(destDir, name);
            if (File.Exists(p)) { anchorPath = p; break; }
        }
        var anchorMachine = anchorPath is null ? null : PeMachine(anchorPath);

        if (anchorMachine is not null && anchorMachine.Value != srcMachine.Value)
        {
            // 架构不匹配，绝不部署，避免装了个加载不了的 dll。
            LastError = string.Format(
                "源 lua54.dll 架构={0} 与系统（{1}）不匹配。",
                ArchName(srcMachine.Value),
                ArchName(anchorMachine.Value));
            return false;
        }
        // ── 架构校验结束 ────────────────────────────────────────

        var destLua = Path.Combine(destDir, "lua54.dll");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        // 先停服务：WeaselServer 常驻会锁住 lua54.dll，不退出覆盖必失败（文件占用）。
        sb.AppendLine("taskkill /f /im WeaselServer.exe >nul 2>&1");
        // 给进程退出留一点时间，避免立刻 copy 仍报占用。
        sb.AppendLine("ping -n 2 127.0.0.1 >nul 2>&1");
        // 只部署 Lua 运行时（无论源文件名是什么，落点一律 lua54.dll）。
        // 注意：绝不碰 rime.dll。
        sb.AppendLine($"copy /Y \"{srcLua}\" \"{destLua}\" >nul 2>&1");

        var bat = Path.Combine(Path.GetTempPath(), "weasel_install_lua.bat");
        await File.WriteAllTextAsync(bat, sb.ToString()).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                Verb = "runas",          // 触发 UAC 提权
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;   // 提权启动失败（如被策略禁用）
            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
        }
        finally
        {
            // 提权进程可能仍持有 bat，删除失败就忽略，下次覆盖会重写。
            try { File.Delete(bat); }
            catch { /* 忽略 */ }
        }

        // 以「目标与源字节数一致」作为成功判据：UAC 取消时文件不会变，自然判失败。
        try
        {
            if (File.Exists(destLua) && File.Exists(srcLua)
                && new FileInfo(destLua).Length == new FileInfo(srcLua).Length)
                return true;
        }
        catch { /* 权限/占用，当作失败 */ }
        return false;
    }

    /// <summary>
    /// 从 .zip 里定位 lua54.dll（librime 布局多为 dist/lib/lua54.dll），
    /// 找不到返回 null。.7z 本类不处理——交给调用方提示用户先解压（BCL 不支持 7z）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<string?> ExtractZipForLuaAsync(string zipPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "weasel_lua_extract_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmp);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmp, overwriteFiles: true))
                .ConfigureAwait(false);
            return LocateLuaDll(tmp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在解压目录里找 Lua 运行时 dll（lua54.dll / lua5.4.dll / lua.dll / librime-lua.dll）。
    /// 先扫根 + 一层子目录（多数构建落在这两层），找不到再全树兜底。</summary>
    private static string? LocateLuaDll(string root)
    {
        string? lua = null;

        bool IsLuaMarker(string name)
        {
            if (name.Equals("librime-lua.dll", StringComparison.OrdinalIgnoreCase)) return true;
            return name.StartsWith("lua", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        var dirs = new List<string> { root };
        try { dirs.AddRange(Directory.EnumerateDirectories(root)); }
        catch { /* 无权限则只扫根 */ }

        foreach (var dir in dirs)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    if (IsLuaMarker(Path.GetFileName(f)) && lua is null) { lua = f; break; }
                }
            }
            catch { /* 跳过无权限目录 */ }
            if (lua is not null) break;
        }

        if (lua is null)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
                {
                    if (IsLuaMarker(Path.GetFileName(f))) { lua = f; break; }
                }
            }
            catch { /* 忽略 */ }
        }
        return lua;
    }
}
