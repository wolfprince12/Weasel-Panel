//
//  MaintenanceViewModel.cs — 维护页（部署 / 同步 / 日志 / 打开目录）
//
//  ── 部署器参数的来源 ────────────────────────────────────────────────────
//  全部取自上游 WeaselDeployer.cpp 的 WinMain 参数分支（2026-09-02 核对）：
//      /deploy    Update Workspace      —— 重新部署，改完配置后点这个
//      /sync      Sync user data        —— 同步用户数据
//      /install   Install (Initial deployment) —— 初始部署
//      /dict      Manage dictionary     —— 弹出词典管理对话框（GUI，不适合本面板调）
//  **不要凭印象增删参数** —— 传一个不认识的参数，部署器不会报错，而是静默
//  只显示帮助文本然后退出，用户看到的就是「点了没反应」。
//  所以这一页只暴露 /deploy /sync /install 三个，/dict 是弹窗不在此列。
//

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.ViewModels;

public sealed class LogFileRow
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string ModifiedText { get; init; }
    public required string FullPath { get; init; }
}

/// <summary>目录卡片里的一行。用数据驱动而不是在 XAML 里抄五行同样的 Grid ——
/// 标签要随语言切换重建，写在 XAML 里就只能在切语言时整页重建。</summary>
public sealed class FolderRow
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    public required System.Windows.Input.ICommand OpenCommand { get; init; }
}

public sealed class MaintenanceViewModel : ViewModelBase, ILanguageAware
{
    private readonly WeaselEnvironment _environment;
    private bool _isBusy;
    private string _statusText = "";
    private string _outputText = "";
    private string _currentAction = "";
    private long _logTotalBytes;

    public MaintenanceViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        DeployCommand = new RelayCommand(() => RunDeployerAsync("/deploy"), () => CanRunDeployer);
        ReinstallCommand = new RelayCommand(() => RunDeployerAsync("/install"), () => CanRunDeployer);
        SyncCommand = new RelayCommand(() => RunDeployerAsync("/sync"), () => CanRunDeployer);

        OpenUserDirCommand = new DelegateCommand(() => OpenFolder(_environment.UserDirectory));
        OpenProgramDirCommand = new DelegateCommand(
            () => OpenFolder(_environment.ProgramDirectory),
            () => _environment.ProgramDirectory is not null);
        OpenSharedDirCommand = new DelegateCommand(
            () => OpenFolder(_environment.SharedDataDirectory),
            () => _environment.SharedDataDirectory is not null);
        OpenLogDirCommand = new DelegateCommand(() => OpenFolder(_environment.LogDirectory));
        // 同步目录一定在用户目录之下，但单独给一个命令 —— 用户点「打开同步」
        // 期待的是进 sync 子目录，不是回到用户目录根。
        OpenSyncDirCommand = new DelegateCommand(() => OpenFolder(_environment.SyncDirectory));

        RefreshLogsCommand = new RelayCommand(RefreshLogsAsync, () => !_isBusy);
        ClearLogsCommand = new DelegateCommand(ClearLogs, () => LogFiles.Count > 0);

        BuildFolders();
    }

    // ── 数据 ────────────────────────────────────────────────────────────

    public ObservableCollection<LogFileRow> LogFiles { get; } = new();

    /// <summary>目录卡片的行。切语言时整表重建（行不多，代价可忽略）。</summary>
    public ObservableCollection<FolderRow> Folders { get; } = new();

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            RaiseCanExecutes();
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>部署器都找不到时，三个部署按钮一起禁掉，而不是点了才报错。</summary>
    public bool CanRunDeployer =>
        !_isBusy && _environment.DeployerPath is not null
        && File.Exists(_environment.DeployerPath);

    public string DeployerPath => _environment.DeployerPath ?? L10n.Instance.T("Common.NotFound");
    public string UserDirectory => _environment.UserDirectory;
    public string LogDirectory => _environment.LogDirectory;
    public string SyncDirectory => _environment.SyncDirectory;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>部署器 / 同步的输出。GUI 子系统程序的 stdout 常常是空的，
    /// 所以「没输出」不等于「没干活」，不能据此判定失败。</summary>
    public string OutputText
    {
        get => _outputText;
        private set => Set(ref _outputText, value);
    }

    public bool HasOutput => _outputText.Length > 0;

    /// <summary>正在跑哪个动作，用于忙碌提示里那行字。</summary>
    public string CurrentAction
    {
        get => _currentAction;
        private set => Set(ref _currentAction, value);
    }

    public string LogTotalText =>
        L10n.Instance.T("Maintenance.LogTotal", LogFiles.Count, FormatBytes(_logTotalBytes));

    public bool ShowNoLogs => LogFiles.Count == 0;

    // ── 命令 ────────────────────────────────────────────────────────────

    public System.Windows.Input.ICommand DeployCommand { get; }
    public System.Windows.Input.ICommand ReinstallCommand { get; }
    public System.Windows.Input.ICommand SyncCommand { get; }
    public System.Windows.Input.ICommand OpenUserDirCommand { get; }
    public System.Windows.Input.ICommand OpenProgramDirCommand { get; }
    public System.Windows.Input.ICommand OpenSharedDirCommand { get; }
    public System.Windows.Input.ICommand OpenLogDirCommand { get; }
    public System.Windows.Input.ICommand OpenSyncDirCommand { get; }
    public System.Windows.Input.ICommand RefreshLogsCommand { get; }
    public System.Windows.Input.ICommand ClearLogsCommand { get; }

    /// <summary>重建目录卡片的行。首次构造与每次切语言都会跑一遍。</summary>
    private void BuildFolders()
    {
        var notFound = L10n.Instance.T("Common.NotFound");

        Folders.Clear();
        Folders.Add(new FolderRow
        {
            Label = L10n.Instance.T("Maintenance.Folder.User"),
            Path = _environment.UserDirectory,
            OpenCommand = OpenUserDirCommand,
        });
        Folders.Add(new FolderRow
        {
            Label = L10n.Instance.T("Maintenance.Folder.Program"),
            Path = _environment.ProgramDirectory ?? notFound,
            OpenCommand = OpenProgramDirCommand,
        });
        Folders.Add(new FolderRow
        {
            Label = L10n.Instance.T("Maintenance.Folder.Shared"),
            Path = _environment.SharedDataDirectory ?? notFound,
            OpenCommand = OpenSharedDirCommand,
        });
        Folders.Add(new FolderRow
        {
            Label = L10n.Instance.T("Maintenance.Folder.Sync"),
            Path = _environment.SyncDirectory,
            OpenCommand = OpenSyncDirCommand,
        });
        Folders.Add(new FolderRow
        {
            Label = L10n.Instance.T("Maintenance.Folder.Log"),
            Path = _environment.LogDirectory,
            OpenCommand = OpenLogDirCommand,
        });
    }

    // ── 加载 ────────────────────────────────────────────────────────────

    private bool _hasLoaded;

    public void Load()
    {
        if (_hasLoaded) return;
        _hasLoaded = true;
        _ = RefreshLogsAsync();
    }

    private async Task RefreshLogsAsync()
    {
        IsBusy = true;
        try
        {
            var dir = _environment.LogDirectory;
            var rows = await Task.Run(() =>
            {
                if (!Directory.Exists(dir)) return Array.Empty<LogFileRow>();
                return Directory.GetFiles(dir)
                    .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc)
                    .Select(p =>
                    {
                        var fi = new FileInfo(p);
                        return new LogFileRow
                        {
                            Name = fi.Name,
                            SizeText = FormatBytes(fi.Length),
                            // FileInfo 只有 LastWriteTime（本地时间）与 LastWriteTimeUtc，
                            // 没有 LastWriteTimeLocal 这个属性 —— 别照着 LocalDateTime 的命名去猜。
                            ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                            FullPath = p,
                        };
                    })
                    .ToArray();
            });

            LogFiles.Clear();
            _logTotalBytes = 0;
            foreach (var r in rows)
            {
                LogFiles.Add(r);
                try { _logTotalBytes += new FileInfo(r.FullPath).Length; }
                catch (IOException) { /* 读不到就当 0，不影响汇总 */ }
            }

            StatusText = StatusFromKey("Maintenance.Status.LogsRefreshed", LogFiles.Count);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Maintenance.Status.LogScanFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowNoLogs));
            OnPropertyChanged(nameof(LogTotalText));
            RaiseCanExecutes();
        }
    }

    private void ClearLogs()
    {
        // 删文件是不可逆操作，必须让用户明确确认一次。
        var answer = MessageBox.Show(
            L10n.Instance.T("Maintenance.ClearConfirmBody", LogFiles.Count, _environment.LogDirectory),
            L10n.Instance.T("Maintenance.ClearConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            StatusText = StatusFromKey("Maintenance.Status.Cancelled");
            return;
        }

        var removed = 0;
        var failed = 0;
        foreach (var row in LogFiles.ToArray())
        {
            try
            {
                File.Delete(row.FullPath);
                removed++;
            }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }

        StatusText = failed == 0
            ? StatusFromKey("Maintenance.Status.Cleared", removed)
            : StatusFromKey("Maintenance.Status.ClearPartial", removed, failed);

        _ = RefreshLogsAsync();
    }

    // ── 部署器 ──────────────────────────────────────────────────────────

    private async Task RunDeployerAsync(string argument)
    {
        var deployer = _environment.DeployerPath;
        if (string.IsNullOrWhiteSpace(deployer) || !File.Exists(deployer))
        {
            StatusText = StatusFromKey("Maintenance.Status.NoDeployer");
            return;
        }

        // /install 是「初始部署」，会重建整个工作区，跑起来比普通部署慢得多。
        // 它跟 /deploy 在按钮上长得差不多，不加确认的话很容易被误点。
        if (argument == "/install")
        {
            var answer = MessageBox.Show(
                L10n.Instance.T("Maintenance.InstallConfirmBody"),
                L10n.Instance.T("Maintenance.InstallConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText = StatusFromKey("Maintenance.Status.Cancelled");
                return;
            }
        }

        IsBusy = true;
        CurrentAction = argument switch
        {
            "/deploy" => L10n.Instance.T("Maintenance.Action.Deploying"),
            "/sync" => L10n.Instance.T("Maintenance.Action.Syncing"),
            "/install" => L10n.Instance.T("Maintenance.Action.Installing"),
            _ => L10n.Instance.T("Common.Working"),
        };
        StatusText = CurrentAction;
        OutputText = "";

        try
        {
            var (code, stdout, stderr, elapsed) = await Task.Run(() =>
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = deployer,
                    Arguments = argument,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var so = new System.Text.StringBuilder();
                var se = new System.Text.StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) so.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) se.AppendLine(e.Data); };

                if (!process.Start())
                    return (-1, "", L10n.Instance.T("Maintenance.Err.StartFailed"), TimeSpan.Zero);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var sw = Stopwatch.StartNew();
                // 部署可能要几分钟（要编译词典）。180 秒的超时是沿用
                // ProbeService.ProbeDeployer 的取值，不要往下调 ——
                // 缩短只会让正常部署被误判成超时。
                var exited = process.WaitForExit(180_000);
                sw.Stop();
                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                    return (-2, so.ToString(), L10n.Instance.T("Maintenance.Err.Timeout"), sw.Elapsed);
                }

                return (process.ExitCode, so.ToString(), se.ToString(), sw.Elapsed);
            });

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(L10n.Instance.T("Maintenance.Out.CommandLine", deployer, argument));
            sb.AppendLine(L10n.Instance.T("Maintenance.Out.ExitCode", code));
            sb.AppendLine(L10n.Instance.T("Maintenance.Out.Elapsed", elapsed.TotalSeconds.ToString("F1")));
            var outText = stdout.Trim();
            var errText = stderr.Trim();
            if (outText.Length > 0) sb.AppendLine(L10n.Instance.T("Maintenance.Out.StdOut", outText));
            if (errText.Length > 0) sb.AppendLine(L10n.Instance.T("Maintenance.Out.StdErr", errText));
            OutputText = sb.ToString().TrimEnd();

            // 上游语义：0 = 成功；1 = 已有部署器实例在运行（不是失败）
            StatusText = code switch
            {
                0 => StatusFromKey("Maintenance.Status.Done"),
                1 => StatusFromKey("Maintenance.Status.AnotherInstance"),
                -2 => StatusFromKey("Maintenance.Status.Timeout"),
                _ => StatusFromKey("Maintenance.Status.ExitCode", code),
            };
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Maintenance.Status.Exception", ex.Message);
        }
        finally
        {
            IsBusy = false;
            CurrentAction = "";
            RaiseCanExecutes();
        }
    }

    // ── 打开目录 ────────────────────────────────────────────────────────

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path))
            {
                // 目录还不存在（比如没部署过）时，往上退到最近的存在目录，
                // 比直接什么都不发生要好。
                var parent = Directory.GetParent(path.TrimEnd('\\'));
                if (parent is not null && parent.Exists) path = parent.FullName;
                else return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器不算错误，静默即可 —— 弹一堆框反而烦人
        }
    }

    private void RaiseCanExecutes()
    {
        ((RelayCommand)DeployCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ReinstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SyncCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RefreshLogsCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)ClearLogsCommand).RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    // ── 本地化 ──────────────────────────────────────────────────────────

    public void RefreshTexts()
    {
        StatusText = Restatus();
        // 目录行的标签是「拼好的字符串」，语言一变就得整表重建，
        // 否则会出现「标签是英文、路径是中文系统目录」这种半吊子状态。
        BuildFolders();
        OnPropertyChanged(nameof(LogTotalText));
        OnPropertyChanged(nameof(DeployerPath));
    }
}
