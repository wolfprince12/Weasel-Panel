using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using WeaselPanel.App.Localization;

namespace WeaselPanel.App.Services;

public enum ProbeStatus
{
    Ok,
    Warn,
    Fail,
    Skipped,
}

public sealed class ProbeResult
{
    public required string Name { get; init; }
    public required ProbeStatus Status { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyList<string> Details { get; init; } = Array.Empty<string>();

    public string StatusText => Status switch
    {
        ProbeStatus.Ok => L10n.Instance.T("Probe.Status.Ok"),
        ProbeStatus.Warn => L10n.Instance.T("Probe.Status.Warn"),
        ProbeStatus.Fail => L10n.Instance.T("Probe.Status.Fail"),
        _ => L10n.Instance.T("Probe.Status.Skip"),
    };
}

/// <summary>
/// P0 探针：在真实 Windows 机器上验证那些「无法靠读源码确定」的行为。
///
/// 设计原则 —— **发现优先于验证**：
/// 不假设自己知道答案，而是把所有合理候选都试一遍，报告哪一个通了。
/// 这样即使此前的推断是错的，用户跑一次就能拿到真实值，而不是只拿到「失败」。
///
/// 每一项都标注了上游出处与推断依据，便于对照源码复核。
/// </summary>
public static class ProbeService
{
    // 上游 include/WeaselIPC.h:10 + GetPipeName()（:178-184）
    //   \\.\pipe\<用户名>\WeaselNamedPipe
    // 用户名来自 GetUserName()（include/WeaselUtility.h:14），即 Windows 登录名（SAM）。
    public const string PipeBaseName = "WeaselNamedPipe";

    public static string CurrentUserName =>
        Environment.UserName;

    /// <summary>按上游规则拼出的「理论正确」管道名。</summary>
    public static string ExpectedPipeName =>
        $@"\\.\pipe\{CurrentUserName}\{PipeBaseName}";

    /// <summary>
    /// 探测 1：命名管道连通性。
    /// 依次尝试多个候选名，返回第一个连通的；全不通则列出所有尝试过的名字。
    /// </summary>
    public static ProbeResult ProbeNamedPipe(int timeoutMs = 800)
    {
        var candidates = new List<(string Name, string Reason)>
        {
            (ExpectedPipeName, L10n.Instance.T("Probe.Pipe.ReasonUpstream")),
            ($@"\\.\pipe\{PipeBaseName}", L10n.Instance.T("Probe.Pipe.ReasonNoPrefix")),
        };

        var tried = new List<string>();
        foreach (var (name, reason) in candidates)
        {
            string outcome;
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeNameWithoutPrefix(name), PipeDirection.InOut, PipeOptions.None);
                client.Connect(timeoutMs);
                outcome = client.IsConnected ? L10n.Instance.T("Probe.Pipe.Connected")
                                         : L10n.Instance.T("Probe.Pipe.NotConnected");
                if (client.IsConnected)
                {
                    tried.Add($"✅ {name}  [{reason}] → {outcome}");
                    return new ProbeResult
                    {
                        Name = L10n.Instance.T("Probe.Name.Pipe"),
                        Status = ProbeStatus.Ok,
                        Summary = L10n.Instance.T("Probe.Pipe.Ok", name),
                        Details = tried,
                    };
                }
            }
            catch (TimeoutException) { outcome = L10n.Instance.T("Probe.Pipe.Timeout"); }
            catch (UnauthorizedAccessException) { outcome = L10n.Instance.T("Probe.Pipe.Denied"); }
            catch (Exception ex) { outcome = L10n.Instance.T("Probe.Exception", ex.GetType().Name, ex.Message); }

            tried.Add($"❌ {name}  [{reason}] → {outcome}");
        }

        var asciiOnly = CurrentUserName.All(c => c < 128);

        // 区分两种截然不同的成因，给出可操作的下一步：
        // (a) WeaselServer 进程根本没跑 → 去启动小狼毫；
        // (b) 进程在跑但连不上 → 才是管道名/权限问题。
        // 真机验证（2026-09-02）：VM 上小狼毫未启动时报此失败，启动后立即连通，
        // 属成因 (a)。此前文案只说「可能未运行」，用户不知道该做什么。
        var serverRunning = IsProcessRunning("WeaselServer");
        var advice = serverRunning
            ? L10n.Instance.T("Probe.Pipe.HintRunning")
            : L10n.Instance.T("Probe.Pipe.HintNotRunning");
        tried.Add("→ " + advice);

        return new ProbeResult
        {
            Name = L10n.Instance.T("Probe.Name.Pipe"),
            Status = ProbeStatus.Fail,
            Summary = asciiOnly
                ? (serverRunning ? L10n.Instance.T("Probe.Pipe.FailRunning")
                                 : L10n.Instance.T("Probe.Pipe.FailNotRunning"))
                : L10n.Instance.T("Probe.Pipe.FailNonAscii", CurrentUserName),
            Details = tried,
        };
    }

    /// <summary>
    /// 指定名称的进程是否在运行。仅用于诊断提示，失败一律视为「没在跑」
    /// （宁可给出保守建议，也不该因为查询失败就报出误导性结论）。
    /// </summary>
    private static bool IsProcessRunning(string processName) =>
        System.Diagnostics.Process.GetProcessesByName(processName).Length > 0;

    private static string PipeNameWithoutPrefix(string fullName)
    {
        const string prefix = @"\\.\pipe\";
        return fullName.StartsWith(prefix, StringComparison.Ordinal)
            ? fullName[prefix.Length..]
            : fullName;
    }

    /// <summary>
    /// 探测 2：WeaselDeployer.exe /deploy 的退出码。
    ///
    /// 上游核实（WeaselDeployer/WeaselDeployer.cpp）：
    /// - :83-86  `!wcscmp(L"/deploy", lpCmdLine)` → `configurator.UpdateWorkspace()`
    /// - lpCmdLine 来自 _tWinMain 第三参，**不含 exe 路径**，故传 "/deploy" 即可精确匹配
    /// - Configurator.h:11 `UpdateWorkspace(bool report_errors = false)`
    ///   → 命令行调用用默认值，**不会弹 MessageBox**，不会阻塞面板
    /// - Configurator.cpp:116-156 返回值：0=成功；1=已有部署器在跑（互斥体）或创建互斥体失败
    /// </summary>
    public static ProbeResult ProbeDeployer(string? deployerPath, int timeoutMs = 180_000)
    {
        if (string.IsNullOrWhiteSpace(deployerPath))
            return new ProbeResult
            {
                Name = L10n.Instance.T("Probe.Name.Deployer"),
                Status = ProbeStatus.Skipped,
                Summary = L10n.Instance.T("Probe.Deployer.NotFound"),
            };

        if (!File.Exists(deployerPath))
            return new ProbeResult
            {
                Name = L10n.Instance.T("Probe.Name.Deployer"),
                Status = ProbeStatus.Skipped,
                Summary = L10n.Instance.T("Probe.Deployer.PathMissing", deployerPath),
            };

        var sw = Stopwatch.StartNew();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = deployerPath,
                Arguments = "/deploy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 部署器是 GUI 子系统，输出可能为空白；仍需重定向以便捕获
            };

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
                return new ProbeResult
                {
                    Name = L10n.Instance.T("Probe.Name.Deployer"),
                    Status = ProbeStatus.Fail,
                    Summary = L10n.Instance.T("Probe.Deployer.StartFailed"),
                };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = process.WaitForExit(timeoutMs);
            sw.Stop();
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return new ProbeResult
                {
                    Name = L10n.Instance.T("Probe.Name.Deployer"),
                    Status = ProbeStatus.Fail,
                    Summary = L10n.Instance.T("Probe.Deployer.Timeout", timeoutMs / 1000),
                    Details = new[] { L10n.Instance.T("Probe.Deployer.Elapsed", sw.Elapsed.TotalSeconds.ToString("F1")) },
                };
            }

            var code = process.ExitCode;
            var details = new List<string>
            {
                L10n.Instance.T("Probe.Deployer.CommandLine", deployerPath),
                L10n.Instance.T("Probe.Deployer.ExitCodeLine", code),
                L10n.Instance.T("Probe.Deployer.ElapsedSec", sw.Elapsed.TotalSeconds.ToString("F1")),
            };
            // 上游语义：0=成功，1=已有部署器实例在运行
            var text = stdout.ToString().Trim();
            var errText = stderr.ToString().Trim();
            if (text.Length > 0) details.Add(L10n.Instance.T("Probe.Deployer.StdOut", text));
            if (errText.Length > 0) details.Add(L10n.Instance.T("Probe.Deployer.StdErr", errText));

            return new ProbeResult
            {
                Name = L10n.Instance.T("Probe.Name.Deployer"),
                Status = code == 0 ? ProbeStatus.Ok : ProbeStatus.Warn,
                Summary = code == 0
                    ? L10n.Instance.T("Probe.Deployer.Ok", code, sw.Elapsed.TotalSeconds.ToString("F1"))
                    : L10n.Instance.T("Probe.Deployer.Fail", code),
                Details = details,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult
            {
                Name = L10n.Instance.T("Probe.Name.Deployer"),
                Status = ProbeStatus.Fail,
                Summary = L10n.Instance.T("Probe.Deployer.Exception", ex.Message),
                Details = new[] { ex.GetType().FullName ?? ex.GetType().Name },
            };
        }
    }

    /// <summary>
    /// 探测 3：%TEMP% 下 rime.weasel 日志的实际文件名格式。
    /// 上游用 google-glog，文件名形如 rime.weasel.<机器名>.<用户名>.log.<级别>.<日期>-<时间>.<pid>
    /// 但具体格式要实测确认，面板的错误正则必须与之匹配。
    /// </summary>
    /// <summary>
    /// 日志文件探测。
    /// </summary>
    /// <remarks>
    /// ⚠️ **只认 librime 自己的日志命名**：<c>rime.weasel.&lt;主机&gt;.&lt;用户&gt;.log.&lt;级别&gt;.&lt;日期&gt;.&lt;pid&gt;.log</c>。
    /// 绝不能用裸 <c>*.log</c> 去扫 <c>%TEMP%</c> —— 那是全系统的临时目录，Chrome 安装器、
    /// Tauri 构建脚本等都会往里写 .log。2026-09-02 真机报告里就混进了
    /// <c>chrome_installer.log</c>、<c>tauri-build-final6.log</c>、<c>push.log</c>
    /// 等 6 个无关文件，而真正的 rime 日志只有 3 个 —— 用户根本无法据此判断。
    /// </remarks>
    public static ProbeResult ProbeLogFiles(string? logDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(Path.GetTempPath(), "rime.weasel")
            : logDirectory;

        // 三个候选：约定的 rime.weasel 子目录 → %TEMP% 根 → %TEMP%\Rime。
        // 后两者仅作兜底（部分便携版会改变日志落点），且一律只用 rime.weasel* 匹配。
        var candidates = new List<string> { dir, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "Rime") };

        var found = new List<string>();
        var scanned = new List<string>();

        foreach (var d in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(d)) continue;
            scanned.Add(d);
            try
            {
                // 只取 librime 日志；不递归（日志不分层）
                var files = Directory
                    .GetFiles(d, "rime.weasel*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(6);

                foreach (var f in files)
                {
                    var size = new FileInfo(f).Length;
                    var when = File.GetLastWriteTime(f);
                    found.Add($"{Path.GetFileName(f)}    ({size / 1024} KB, {when:yyyy-MM-dd HH:mm})  ← {d}");
                }
            }
            catch (Exception ex)
            {
                found.Add(L10n.Instance.T("Probe.Logs.ReadFailed", d, ex.Message));
            }
        }

        if (found.Count == 0)
            return new ProbeResult
            {
                Name = L10n.Instance.T("Probe.Name.Logs"),
                // 没有日志不是错误：librime 只在确有内容时才写文件，
                // 全新部署且未出错时本就一个都没有。故降级为提示。
                Status = ProbeStatus.Warn,
                Summary = L10n.Instance.T("Probe.Logs.None"),
                Details = new[] { L10n.Instance.T("Probe.Logs.Scanned", string.Join(" | ", scanned)) },
            };

        return new ProbeResult
        {
            Name = L10n.Instance.T("Probe.Name.Logs"),
            Status = ProbeStatus.Ok,
            Summary = L10n.Instance.T("Probe.Logs.Found", found.Count),
            Details = found,
        };
    }

    /// <summary>
    /// 探测 4：librime-lua 插件是否可用。
    /// 这决定「紫毫纠错」能否落地 —— 若未启用，面板必须禁用相关功能而不是让用户配了不生效。
    /// 扫描策略同样是发现式：列出所有候选目录下含 lua 字样的动态库。
    /// </summary>
    public static ProbeResult ProbeLuaPlugin(string? programDirectory, string? sharedDataDirectory, string? userDirectory)
    {
        var searchDirs = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            searchDirs.Add(p);
            searchDirs.Add(Path.Combine(p, "plugins"));
            searchDirs.Add(Path.Combine(p, "lib", "rime-plugins"));
            searchDirs.Add(Path.Combine(p, "data", "plugins"));
            searchDirs.Add(Path.Combine(p, "data", "lua"));
            searchDirs.Add(Path.Combine(p, "lua"));
            searchDirs.Add(Path.Combine(p, "lua", "lib"));
        }
        Add(programDirectory);
        Add(sharedDataDirectory);
        if (!string.IsNullOrWhiteSpace(sharedDataDirectory))
            Add(Path.GetDirectoryName(sharedDataDirectory));
        // 程序目录的父目录：weasel-<版本>\ 之上一层有时放共享的 plugins
        if (!string.IsNullOrWhiteSpace(programDirectory))
            Add(Path.GetDirectoryName(programDirectory));
        Add(userDirectory);

        var found = new List<string>();
        var scanned = new List<string>();
        foreach (var d in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(d)) continue;
            scanned.Add(d);
            try
            {
                foreach (var f in Directory.GetFiles(d, "*lua*", SearchOption.TopDirectoryOnly))
                {
                    if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                        found.Add(L10n.Instance.T("Probe.Lua.FileAt", Path.GetFileName(f), d));
                }
            }
            catch { /* 无权限则跳过 */ }
        }

        // 另一条证据：用户配置里是否已有 lua 引用（说明插件肯定装过）
        var luaRefs = new List<string>();
        foreach (var name in new[] { "rime_ice.schema.yaml", "default.custom.yaml", "default.yaml" })
        {
            var path = string.IsNullOrWhiteSpace(userDirectory) ? null : Path.Combine(userDirectory, name);
            if (path is null || !File.Exists(path)) continue;
            try
            {
                var lineNo = 0;
                foreach (var line in File.ReadLines(path))
                {
                    lineNo++;
                    if (line.Contains("lua_", StringComparison.OrdinalIgnoreCase))
                        luaRefs.Add($"{name}:{lineNo}  {line.Trim()}");
                }
            }
            catch { /* 忽略 */ }
        }

        var details = new List<string>();
        details.Add(L10n.Instance.T("Probe.Lua.Scanned")
                    + (scanned.Count == 0 ? L10n.Instance.T("Probe.Lua.NoDir") : string.Join(" | ", scanned)));
        if (found.Count > 0)
        {
            details.Add(L10n.Instance.T("Probe.Lua.FoundFiles"));
            details.AddRange(found);
        }
        else
        {
            details.Add(L10n.Instance.T("Probe.Lua.NoneFound"));
        }
        if (luaRefs.Count > 0)
        {
            details.Add(L10n.Instance.T("Probe.Lua.Refs", luaRefs.Count));
            details.AddRange(luaRefs.Take(8));
        }

        // 状态判定的关键：**未发现 lua 插件本身不是故障**。
        // librime-lua 是可选插件，官方发行包并不必然内置（上游 rime/weasel 仓内
        // 根本没有 lua 相关文件，它由 librime 侧单独提供）。真正的故障只有一种 ——
        // 配置里引用了 lua（如 rime_ice 的 lua_filter）却找不到插件，那会导致部署失败。
        var ok = found.Count > 0;
        var configuredButMissing = !ok && luaRefs.Count > 0;

        if (ok)
        {
            details.Add(L10n.Instance.T("Probe.Lua.Ready"));
        }
        else if (configuredButMissing)
        {
            details.Add(L10n.Instance.T("Probe.Lua.ConfiguredButMissing"));
        }
        else
        {
            details.Add(L10n.Instance.T("Probe.Lua.Explain"));
        }

        return new ProbeResult
        {
            Name = L10n.Instance.T("Probe.Name.Lua"),
            Status = ok ? ProbeStatus.Ok
                        : configuredButMissing ? ProbeStatus.Fail
                        : ProbeStatus.Warn,
            Summary = ok
                ? L10n.Instance.T("Probe.Lua.SummaryOk")
                : configuredButMissing
                    ? L10n.Instance.T("Probe.Lua.SummaryMissing")
                    : L10n.Instance.T("Probe.Lua.Missing"),
            Details = details,
        };
    }

    /// <summary>一次性跑完四项。</summary>
    public static List<ProbeResult> RunAll(
        string? deployerPath, string? logDirectory,
        string? programDirectory, string? sharedDataDirectory, string? userDirectory,
        bool includeDeploy = true)
    {
        var results = new List<ProbeResult>
        {
            ProbeNamedPipe(),
        };

        results.Add(includeDeploy
            ? ProbeDeployer(deployerPath)
            : new ProbeResult
            {
                Name = L10n.Instance.T("Probe.Name.Deployer"),
                Status = ProbeStatus.Skipped,
                Summary = L10n.Instance.T("Probe.Deployer.Skipped"),
            });

        results.Add(ProbeLogFiles(logDirectory));
        results.Add(ProbeLuaPlugin(programDirectory, sharedDataDirectory, userDirectory));
        return results;
    }
}
