using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

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
        ProbeStatus.Ok => "正常",
        ProbeStatus.Warn => "警告",
        ProbeStatus.Fail => "失败",
        _ => "跳过",
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
            (ExpectedPipeName, "上游规则（含用户名前缀）"),
            ($@"\\.\pipe\{PipeBaseName}", "无用户名前缀（早期版本格式）"),
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
                outcome = client.IsConnected ? "已连接" : "未连接";
                if (client.IsConnected)
                {
                    tried.Add($"✅ {name}  [{reason}] → {outcome}");
                    return new ProbeResult
                    {
                        Name = "命名管道",
                        Status = ProbeStatus.Ok,
                        Summary = $"连通：{name}",
                        Details = tried,
                    };
                }
            }
            catch (TimeoutException) { outcome = "超时（管道不存在或服务端未监听）"; }
            catch (UnauthorizedAccessException) { outcome = "拒绝访问"; }
            catch (Exception ex) { outcome = ex.GetType().Name + "：" + ex.Message; }

            tried.Add($"❌ {name}  [{reason}] → {outcome}");
        }

        var asciiOnly = CurrentUserName.All(c => c < 128);
        return new ProbeResult
        {
            Name = "命名管道",
            Status = ProbeStatus.Fail,
            Summary = asciiOnly
                ? "全部候选均无法连通（小狼毫可能未运行）"
                : $"全部候选均无法连通 —— 当前用户名「{CurrentUserName}」含非 ASCII 字符，重点怀疑编码问题",
            Details = tried,
        };
    }

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
                Name = "部署器 /deploy",
                Status = ProbeStatus.Skipped,
                Summary = "未找到 WeaselDeployer.exe",
            };

        if (!File.Exists(deployerPath))
            return new ProbeResult
            {
                Name = "部署器 /deploy",
                Status = ProbeStatus.Skipped,
                Summary = "路径存在但文件不存在：" + deployerPath,
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
                    Name = "部署器 /deploy",
                    Status = ProbeStatus.Fail,
                    Summary = "进程启动失败",
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
                    Name = "部署器 /deploy",
                    Status = ProbeStatus.Fail,
                    Summary = $"超时（{timeoutMs / 1000} 秒未完成）—— 可能卡在互斥体等待或弹出对话框",
                    Details = new[] { $"已运行：{sw.Elapsed.TotalSeconds:F1} 秒" },
                };
            }

            var code = process.ExitCode;
            var details = new List<string>
            {
                $"命令行：\"{deployerPath}\" /deploy",
                $"退出码：{code}",
                $"耗时：{sw.Elapsed.TotalSeconds:F1} 秒",
            };
            // 上游语义：0=成功，1=已有部署器实例在运行
            var text = stdout.ToString().Trim();
            var errText = stderr.ToString().Trim();
            if (text.Length > 0) details.Add("标准输出：" + text);
            if (errText.Length > 0) details.Add("标准错误：" + errText);

            return new ProbeResult
            {
                Name = "部署器 /deploy",
                Status = code == 0 ? ProbeStatus.Ok : ProbeStatus.Warn,
                Summary = code == 0
                    ? $"成功（退出码 0，耗时 {sw.Elapsed.TotalSeconds:F1} 秒）"
                    : $"退出码 {code} —— 按上游语义为「已有部署器实例在运行」或互斥体创建失败",
                Details = details,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProbeResult
            {
                Name = "部署器 /deploy",
                Status = ProbeStatus.Fail,
                Summary = "调用异常：" + ex.Message,
                Details = new[] { ex.GetType().FullName ?? ex.GetType().Name },
            };
        }
    }

    /// <summary>
    /// 探测 3：%TEMP% 下 rime.weasel 日志的实际文件名格式。
    /// 上游用 google-glog，文件名形如 rime.weasel.<机器名>.<用户名>.log.<级别>.<日期>-<时间>.<pid>
    /// 但具体格式要实测确认，面板的错误正则必须与之匹配。
    /// </summary>
    public static ProbeResult ProbeLogFiles(string? logDirectory)
    {
        var dir = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(Path.GetTempPath(), "rime.weasel")
            : logDirectory;

        var candidates = new List<string> { dir, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "Rime") };
        var found = new List<string>();

        foreach (var d in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(d)) continue;
            try
            {
                var files = Directory.GetFiles(d, "rime.weasel*", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(d, "*.log", SearchOption.TopDirectoryOnly))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(File.GetLastWriteTime)
                    .Take(6);
                foreach (var f in files)
                    found.Add($"{Path.GetFileName(f)}    ({new FileInfo(f).Length / 1024} KB, {File.GetLastWriteTime(f):yyyy-MM-dd HH:mm})");
            }
            catch (Exception ex)
            {
                found.Add($"[{d}] 读取失败：{ex.Message}");
            }
        }

        if (found.Count == 0)
            return new ProbeResult
            {
                Name = "日志文件",
                Status = ProbeStatus.Warn,
                Summary = "未找到任何 rime.weasel 日志（可能尚未产生错误，或日志目录不在预期位置）",
                Details = new[] { "已扫描：" + string.Join(" | ", candidates) },
            };

        return new ProbeResult
        {
            Name = "日志文件",
            Status = ProbeStatus.Ok,
            Summary = $"找到 {found.Count} 个相关文件",
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
        }
        Add(programDirectory);
        Add(sharedDataDirectory);
        if (!string.IsNullOrWhiteSpace(sharedDataDirectory))
            Add(Path.GetDirectoryName(sharedDataDirectory));
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
                        found.Add(Path.GetFileName(f) + "    ← " + d);
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
        details.Add("已扫描目录：" + (scanned.Count == 0 ? "（无有效目录）" : string.Join(" | ", scanned)));
        if (found.Count > 0)
        {
            details.Add("发现的 lua 相关文件：");
            details.AddRange(found);
        }
        else
        {
            details.Add("未在候选目录发现 lua 插件文件。");
        }
        if (luaRefs.Count > 0)
        {
            details.Add($"配置中的 lua 引用（{luaRefs.Count} 处，前 8 条）：");
            details.AddRange(luaRefs.Take(8));
        }

        var ok = found.Count > 0;
        return new ProbeResult
        {
            Name = "librime-lua 插件",
            Status = ok ? ProbeStatus.Ok : (luaRefs.Count > 0 ? ProbeStatus.Warn : ProbeStatus.Fail),
            Summary = ok
                ? "发现 lua 插件文件，紫毫纠错具备启用条件"
                : luaRefs.Count > 0
                    ? "未发现插件文件，但配置中已有 lua 引用 —— 需人工确认插件实际位置"
                    : "未发现 lua 插件，紫毫纠错不可用（面板应禁用相关功能）",
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
                Name = "部署器 /deploy",
                Status = ProbeStatus.Skipped,
                Summary = "已跳过（部署会修改用户数据，请手动点击）",
            });

        results.Add(ProbeLogFiles(logDirectory));
        results.Add(ProbeLuaPlugin(programDirectory, sharedDataDirectory, userDirectory));
        return results;
    }
}
