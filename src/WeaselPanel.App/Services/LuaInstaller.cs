//
//  LuaInstaller.cs — librime-lua 的引导式安装（替换小狼毫安装目录的 rime.dll）
//
//  为什么是「引导式」而不是「全自动」：
//  - librime-lua 本质是「带 Lua 的 rime.dll」，必须覆盖到小狼毫安装目录；
//  - 版本必须与本机小狼毫完全匹配，否则整个输入法失效、候选框消失——
//    面板无法替用户判断他下到的那一份版本对不对，所以下载那一步交回用户手动点链接；
//  - 覆盖 Program Files 需要管理员权限，故用 runas 提权跑一段临时 bat
//    （停 WeaselServer → 备份 rime.dll → 覆盖），用户只点一次 UAC。
//
//  下载源（GitHub Releases，公开无需登录）：
//    https://github.com/rime/librime/releases  （含 lua / octagram / charcode 三插件的 dist/lib/rime.dll）
//  或社区维护的 hchunhui/librime-lua（GitHub Actions artifacts 需登录，已不优先）。
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
    /// 把用户选中的 rime.dll 覆盖到小狼毫安装目录。
    /// 内部流程（全部在提权 bat 内完成，用户只见一次 UAC）：
    ///   taskkill /f /im WeaselServer.exe  →  停算法服务（避免 rime.dll 被占用）
    ///   copy /Y 原rime.dll → rime.dll.bak  →  备份
    ///   copy /Y 选中的dll → rime.dll        →  覆盖
    ///   copy /Y 选中的lua54.dll → lua54.dll  →  一并带上 Lua 运行时（若有）
    /// 返回 true 表示覆盖后目标 rime.dll 与源字节数一致（UAC 取消则文件不变，判失败）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<bool> OverwriteWithElevationAsync(
        string destDir, string srcDll, string? srcLua)
    {
        var destDll = Path.Combine(destDir, "rime.dll");
        var destLua = Path.Combine(destDir, "lua54.dll");

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        // 先停服务：WeaselServer 常驻会锁住 rime.dll，不退出覆盖必失败（文件占用）。
        sb.AppendLine("taskkill /f /im WeaselServer.exe >nul 2>&1");
        // 给进程退出留一点时间，避免立刻 copy 仍报占用。
        sb.AppendLine("ping -n 2 127.0.0.1 >nul 2>&1");
        // 备份原 dll（踩过的坑：覆盖前不备份，一旦版本错配就再也回不去）。
        sb.AppendLine($"copy /Y \"{destDll}\" \"{destDll}.bak\" >nul 2>&1");
        // 覆盖（无论源文件名是什么，落点一律 rime.dll）。
        sb.AppendLine($"copy /Y \"{srcDll}\" \"{destDll}\" >nul 2>&1");
        if (!string.IsNullOrEmpty(srcLua))
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
            if (File.Exists(destDll) && File.Exists(srcDll)
                && new FileInfo(destDll).Length == new FileInfo(srcDll).Length)
                return true;
        }
        catch { /* 权限/占用，当作失败 */ }
        return false;
    }

    /// <summary>
    /// 从 .zip 里定位 rime.dll（librime 布局多为 dist/lib/rime.dll），
    /// 并顺带收集同包内的 lua54.dll（若有）。找不到 rime.dll 返回 (null, null)。
    /// .7z 本类不处理——交给调用方提示用户先解压（BCL 不支持 7z）。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<(string? Dll, string? Lua)> ExtractZipForRimeDllAsync(string zipPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "weasel_lua_extract_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tmp);
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tmp, overwriteFiles: true))
                .ConfigureAwait(false);
            return LocateRimeDll(tmp);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>在解压目录里找 rime.dll（根 + 一层子目录，兜底全树），并找 lua54.dll。</summary>
    private static (string? Dll, string? Lua) LocateRimeDll(string root)
    {
        string? dll = null;
        string? lua = null;

        var dirs = new List<string> { root };
        try { dirs.AddRange(Directory.EnumerateDirectories(root)); }
        catch { /* 无权限则只扫根 */ }

        foreach (var dir in dirs)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(f);
                    if (name.Equals("rime.dll", StringComparison.OrdinalIgnoreCase) && dll is null)
                        dll = f;
                    else if (name.Equals("lua54.dll", StringComparison.OrdinalIgnoreCase) && lua is null)
                        lua = f;
                }
            }
            catch { /* 跳过无权限目录 */ }
            if (dll is not null && lua is not null) break;
        }

        // 兜底：rime.dll 藏在更深目录时全树搜一次。
        if (dll is null)
        {
            try
            {
                dll = Directory.EnumerateFiles(root, "rime.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch { /* 忽略 */ }
        }
        return (dll, lua);
    }
}
