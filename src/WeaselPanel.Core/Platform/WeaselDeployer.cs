//
//  WeaselDeployer.cs — 小狼毫部署器调用
//
//  词库包安装 / 更新 / 卸载后需要重新部署，让 librime 重新编译词典与方案。
//  这里把「调 WeaselDeployer.exe /deploy」封装成一个跨平台安全的静态方法，
//  供词典包管理器（与将来的其它写盘流程）复用，避免各处重复这段 Process 样板。
//
//  语义与 MaintenanceViewModel 完全一致：0 = 成功；1 = 已有部署器实例在跑
//  （不算失败）；-1 = 找不到部署器；-2 = 超时（已 kill）。
//
//  ⚠️ 本文件只依赖 BCL，不引用任何 WPF / Windows 专有程序集 ——
//  Core 是跨平台纯逻辑层，全量单元测试必须在 macOS / Linux 上也能跑。
//  CreateNoWindow 等 Windows 专有属性已用 OperatingSystem.IsWindows() 守卫。
//

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WeaselPanel.Core.Platform;

/// <summary>小狼毫部署器（WeaselDeployer.exe）的进程调用封装。</summary>
public static class WeaselDeployer
{
    public const int ExitAlreadyRunning = 1;
    public const int ExitNotFound = -1;
    public const int ExitTimeout = -2;

    /// <summary>部署可能要编译词典，耗时以分钟计；沿用 ProbeService 的 180 秒上限，不要往下调。</summary>
    private const int DeployTimeoutMs = 180_000;

    /// <summary>
    /// 以给定参数运行部署器（通常是 <c>/deploy</c>）。找不到部署器或超时返回负码，
    /// 调用方按语义决定是报错还是静默忽略（退出码 1 = 已有实例，不算失败）。
    /// </summary>
    public static async Task<int> RunAsync(
        WeaselEnvironment environment,
        string argument,
        CancellationToken cancellationToken = default)
    {
        var deployer = environment.DeployerPath;
        if (string.IsNullOrWhiteSpace(deployer) || !File.Exists(deployer))
            return ExitNotFound;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = deployer,
            Arguments = argument,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = OperatingSystem.IsWindows(),
        };

        if (!process.Start())
            return ExitNotFound;

        var so = new StringBuilder();
        var se = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) so.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) se.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = await Task.Run(() => process.WaitForExit(DeployTimeoutMs), cancellationToken)
            .ConfigureAwait(false);

        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            return ExitTimeout;
        }

        return process.ExitCode;
    }
}
