//
//  WeaselPaths.cs
//  WeaselPanel.Core
//
//  探测本机小狼毫（Rime Weasel）的安装状态与各项路径。
//  本面板不链接 librime，所有信息均通过文件系统、注册表与官方命令行接口获取。
//
//  ── Windows 侧与 macOS 版的差异（均据 rime/weasel 源码核实，勿凭直觉修改）──
//
//  1) 用户目录：HKCU\Software\Rime\Weasel\RimeUserDir（REG_SZ，非空即采用）
//     → 回退 ExpandEnvironmentStrings("%AppData%\Rime")。
//     源码：RimeWithWeasel/WeaselUtility.cpp  WeaselUserDataPath()
//
//     ⚠️ 取到注册表值后上游 **不做** 环境变量展开（ExpandEnvironmentStringsW
//     只出现在回退分支里）。此处严格复刻：面板必须与 Weasel 本体解析出**同一个**
//     目录，若我们自作聪明去展开 %AppData%，当注册表里存的正是含 % 的字面值时
//     就会与本体指向不同目录，比不展开严重得多。
//
//  2) 共享数据目录 = 模块同级 data\，不是程序目录下固定子目录。
//     源码：RimeWithWeasel/WeaselUtility.cpp  WeaselSharedDataPath()
//     （GetModuleFileName(NULL) → remove_filename → append("data")）
//     对装在 %PROGRAMFILES64%\Rime 的 WeaselServer.exe 而言即 %PROGRAMFILES64%\Rime\data。
//
//  3) 注册表视图（关键陷阱）：output/install.nsi 默认 SetRegView 32
//     （第 177 行显式 "recover back to 32bit view"），因此
//     WriteRegStr HKLM SOFTWARE\Rime\Weasel "InstallDir" 在 64 位系统上
//     实际写入 HKLM\SOFTWARE\WOW6432Node\Rime\Weasel —— 脚本第 208 行的注释
//     正是这个意思。故本实现 **两个视图都要试**，且优先 32 位视图（与上游一致）。
//
//  4) WeaselDeployer.exe / WeaselServer.exe 的落点随 CPU 架构分支：
//     Win11 + (ARM64|AMD64) → 程序目录根；其余 → 程序目录下 Win32\ 子目录。
//     源码：output/install.nsi 第 248–276 行。故按「根 → Win32\」顺序取候选。
//
//  5) 日志目录：librime 写 %TEMP%\rime.weasel\rime.weasel.*.INFO / .ERROR
//     （macOS 版是 $TMPDIR/rime.squirrel）。
//

using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WeaselPanel.Core.Platform;

/// <summary>
/// 小狼毫在本机的安装与运行环境。
/// 与 macOS 版的 <c>RimeEnvironment</c> 对应；区别在于本类型可被显式构造，
/// 便于在测试中注入 fixture 目录（不依赖运行机器是否装了小狼毫）。
/// </summary>
public sealed class WeaselEnvironment
{
    /// <summary>用户配置目录（默认 %AppData%\Rime），一定非空。</summary>
    public required string UserDirectory { get; init; }

    /// <summary>程序安装目录（如 %PROGRAMFILES64%\Rime）；未安装为 null。</summary>
    public string? ProgramDirectory { get; init; }

    /// <summary>共享数据目录（程序目录\data），内置 weasel.yaml / default.yaml / 各输入方案；未安装为 null。</summary>
    public string? SharedDataDirectory { get; init; }

    /// <summary>WeaselDeployer.exe 的完整路径；未找到为 null。</summary>
    public string? DeployerPath { get; init; }

    /// <summary>WeaselServer.exe 的完整路径；未找到为 null。</summary>
    public string? ServerPath { get; init; }

    /// <summary>用户数据同步目录。</summary>
    public string SyncDirectory => Path.Combine(UserDirectory, "sync");

    /// <summary>librime 日志目录（%TEMP%\rime.weasel）。</summary>
    public string LogDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "rime.weasel");

    /// <summary>备份存放目录（位于用户目录内，创建/恢复时会自我排除）。</summary>
    public string BackupsDirectory => Path.Combine(UserDirectory, "backups");

    public bool IsInstalled => ProgramDirectory is not null;

    /// <summary>用户目录是否已初始化（首次部署后才会生成 default.yaml 等文件）。</summary>
    public bool IsUserDirectoryReady => Directory.Exists(UserDirectory);

    /// <summary>
    /// 按 Rime 的查找顺序返回某个配置文件的所有可能位置（用户目录优先于共享目录），
    /// 只返回实际存在的。对应 macOS 版 <c>RimeEnvironment.configSources(named:)</c>。
    /// </summary>
    public string[] ConfigSources(string name)
    {
        var candidates = new System.Collections.Generic.List<string>(2)
        {
            Path.Combine(UserDirectory, name)
        };
        if (SharedDataDirectory is not null)
        {
            candidates.Add(Path.Combine(SharedDataDirectory, name));
        }
        return candidates.FindAll(File.Exists).ToArray();
    }

    /// <summary>
    /// 构造一个以指定目录为用户目录的环境（仅供测试与「便携式用户目录」场景）。
    /// </summary>
    public static WeaselEnvironment WithUserDirectory(string userDirectory) =>
        new() { UserDirectory = userDirectory };
}

/// <summary>
/// 小狼毫路径探测。全部结果只读，不做任何写入。
/// </summary>
public static class WeaselPaths
{
    private const string WeaselRegKey = @"Software\Rime\Weasel";
    private const string RimeUserDirValue = "RimeUserDir";
    private const string InstallDirValue = "InstallDir";
    private const string WeaselRootValue = "WeaselRoot";

    /// <summary>探测本机环境。非 Windows 平台返回仅含用户目录回退值的空环境（供单元测试使用）。</summary>
    public static WeaselEnvironment Detect()
    {
        var userDirectory = DetectUserDirectory();
        var programDirectory = DetectProgramDirectory();

        string? shared = null;
        string? deployer = null;
        string? server = null;

        if (programDirectory is not null)
        {
            var data = Path.Combine(programDirectory, "data");
            if (Directory.Exists(data)) shared = data;

            // 架构分支：Win11 + (ARM64|AMD64) 装在根，其余装在 Win32\ 子目录。
            deployer = FirstExisting(
                Path.Combine(programDirectory, "WeaselDeployer.exe"),
                Path.Combine(programDirectory, "Win32", "WeaselDeployer.exe"));
            server = FirstExisting(
                Path.Combine(programDirectory, "WeaselServer.exe"),
                Path.Combine(programDirectory, "Win32", "WeaselServer.exe"));
        }

        return new WeaselEnvironment
        {
            UserDirectory = userDirectory,
            ProgramDirectory = programDirectory,
            SharedDataDirectory = shared,
            DeployerPath = deployer,
            ServerPath = server,
        };
    }

    /// <summary>
    /// 用户目录：HKCU\Software\Rime\Weasel\RimeUserDir → %AppData%\Rime → ~/Rime（非 Windows 回退）。
    /// 严格不展开注册表值中的环境变量（见文件头第 1 条）。
    /// </summary>
    public static string DetectUserDirectory()
    {
        // 平台守卫同时服务于静态分析（CA1416）与运行时：
        // 非 Windows 上根本不触碰注册表类型，直接走回退路径。
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = WindowsRegistry.TryGetString(
                RegistryHive.CurrentUser, WeaselRegKey, RimeUserDirValue);
            if (!string.IsNullOrWhiteSpace(fromRegistry)) return fromRegistry!;
        }

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData)) return Path.Combine(appData, "Rime");

        // 非 Windows（CI / 单元测试）下的最终回退
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Rime");
    }

    /// <summary>
    /// 程序目录：HKLM\Software\Rime\Weasel\WeaselRoot → InstallDir → %PROGRAMFILES%\Rime 系列候选。
    /// </summary>
    public static string? DetectProgramDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var root = WindowsRegistry.TryGetString(
                RegistryHive.LocalMachine, WeaselRegKey, WeaselRootValue);
            if (IsUsableDir(root)) return root;

            var installDir = WindowsRegistry.TryGetString(
                RegistryHive.LocalMachine, WeaselRegKey, InstallDirValue);
            if (IsUsableDir(installDir)) return installDir;
        }

        // 回退：按 Windows 惯例逐个试。注意 64 位系统 ProgramFiles 即 Program Files，
        // ProgramFiles(x86) 即 Program Files (x86)；32 位安装可能落在后者。
        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var dir = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(dir)) continue;
            var candidate = Path.Combine(dir, "Rime");
            if (IsUsableDir(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsUsableDir(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path!);

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// 注册表读取。全部调用均以 try/catch 包裹：
/// - 非 Windows 平台上 <c>RegistryKey.OpenBaseKey</c> 抛 PlatformNotSupportedException，
///   捕获后可让本类库在 macOS / Linux 上照常加载，单元测试得以运行；
/// - 权限不足、键不存在、值类型非 REG_SZ 同样一律视为「没读到」。
/// </summary>
internal static class WindowsRegistry
{
    /// <summary>
    /// 读取字符串值。先试 32 位视图再试 64 位视图——安装脚本默认 SetRegView 32，
    /// 64 位系统上键值实际落在 WOW6432Node 下（见 WeaselPaths.cs 文件头第 3 条）。
    /// </summary>
    /// <remarks>
    /// 方法本身不加 [SupportedOSPlatform("windows")]：那样会把平台约束传染给调用方。
    /// 改为在调用方用 <c>if (OperatingSystem.IsWindows())</c> 显式守卫，
    /// 既满足 CA1416 静态分析，也保证非 Windows 上不会执行到这里。
    /// 方法内的守卫是运行时的第二道保险。
    /// </remarks>
    public static string? TryGetString(RegistryHive hive, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string text && text.Length > 0)
                {
                    return text;
                }
            }
            catch (PlatformNotSupportedException) { return null; }
            catch (System.Security.SecurityException) { /* 忽略，试下一个视图 */ }
            catch (IOException) { /* 忽略，试下一个视图 */ }
            catch (UnauthorizedAccessException) { /* 忽略，试下一个视图 */ }
        }
        return null;
    }
}
