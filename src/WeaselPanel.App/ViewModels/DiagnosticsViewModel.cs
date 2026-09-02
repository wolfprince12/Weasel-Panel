using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.ViewModels;

public sealed class EnvironmentRow
{
    /// <summary>本地化后的标签。</summary>
    public required string Key { get; init; }
    public required string Value { get; init; }
    public bool IsWarning { get; init; }
}

public sealed class DiagnosticsViewModel : ViewModelBase, ILanguageAware
{
    private bool _isBusy;
    private string _statusText = "";

    public DiagnosticsViewModel()
    {
        StatusText = StatusFromKey("Diag.HintClick");

        RefreshEnvironment();
        RunProbeCommand = new RelayCommand(RunProbeAsync, () => !IsBusy);
        RunDeployCommand = new RelayCommand(RunDeployAsync, () => !IsBusy && Environment?.DeployerPath is not null);
        CopyReportCommand = new DelegateCommand(CopyReport);
    }

    public ObservableCollection<EnvironmentRow> EnvironmentRows { get; } = new();
    public ObservableCollection<ProbeResult> Results { get; } = new();

    public RelayCommand RunProbeCommand { get; }
    public RelayCommand RunDeployCommand { get; }
    public DelegateCommand CopyReportCommand { get; }

    public WeaselEnvironment? Environment { get; private set; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !IsBusy;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void RefreshEnvironment()
    {
        WeaselEnvironment env;
        try
        {
            env = WeaselPaths.Detect();
        }
        catch (Exception ex)
        {
            EnvironmentRows.Clear();
            EnvironmentRows.Add(new EnvironmentRow
            {
                Key = L10n.Instance.T("Diag.DetectFailed"),
                Value = ex.Message,
                IsWarning = true,
            });
            return;
        }

        Environment = env;
        EnvironmentRows.Clear();
        void Add(string key, string? value, bool warn = false) =>
            EnvironmentRows.Add(new EnvironmentRow
            {
                Key = L10n.Instance.T(key),
                Value = string.IsNullOrWhiteSpace(value) ? L10n.Instance.T("Common.NotFound") : value,
                IsWarning = warn || string.IsNullOrWhiteSpace(value),
            });

        Add("Diag.Row.Installed", env.IsInstalled ? L10n.Instance.T("Diag.Value.Yes") : L10n.Instance.T("Diag.Value.No"), !env.IsInstalled);
        Add("Diag.Row.ProgramDir", env.ProgramDirectory);
        Add("Diag.Row.SharedDir", env.SharedDataDirectory);
        Add("Diag.Row.UserDir", env.UserDirectory, !env.IsUserDirectoryReady);
        Add("Diag.Row.UserDirState",
            env.IsUserDirectoryReady ? L10n.Instance.T("Diag.Value.Ready") : L10n.Instance.T("Diag.Value.NotExist"),
            !env.IsUserDirectoryReady);
        Add("Diag.Row.SyncDir", env.SyncDirectory);
        Add("Diag.Row.BackupDir", env.BackupsDirectory);
        Add("Diag.Row.LogDir", env.LogDirectory);
        Add("Diag.Row.Deployer", env.DeployerPath);
        Add("Diag.Row.UserName", ProbeService.CurrentUserName);
        Add("Diag.Row.PipeName", ProbeService.ExpectedPipeName);

        OnPropertyChanged(nameof(Environment));
    }

    private async Task RunProbeAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Diag.Probing");
        Results.Clear();
        try
        {
            var env = Environment;
            var results = await Task.Run(() => ProbeService.RunAll(
                env?.DeployerPath, env?.LogDirectory,
                env?.ProgramDirectory, env?.SharedDataDirectory, env?.UserDirectory,
                includeDeploy: false));

            foreach (var r in results) Results.Add(r);
            var failed = results.Count(r => r.Status == ProbeStatus.Fail);
            StatusText = failed == 0
                ? StatusFromKey("Diag.ProbeDone", results.Count)
                : StatusFromKey("Diag.ProbeFailed", failed);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Diag.ProbeException", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunDeployAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Diag.Deploying");
        try
        {
            var env = Environment;
            var result = await Task.Run(() => ProbeService.ProbeDeployer(env?.DeployerPath));
            Results.Insert(0, result);
            StatusText = result.Status == ProbeStatus.Ok
                ? StatusFromKey("Diag.DeployDone")
                : StatusFromKey("Diag.DeployResult", result.Summary);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Diag.DeployException", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L10n.Instance.T("Diag.ReportTitle"));
        sb.AppendLine(L10n.Instance.T("Diag.RowFormat",
    L10n.Instance.T("Diag.ReportTime"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine();
        sb.AppendLine(L10n.Instance.T("Diag.ReportEnv"));
        foreach (var r in EnvironmentRows)
            sb.AppendLine(L10n.Instance.T("Diag.RowFormat", r.Key, r.Value));
        sb.AppendLine();
        sb.AppendLine(L10n.Instance.T("Diag.ReportProbe"));
        foreach (var r in Results)
        {
            sb.AppendLine(L10n.Instance.T("Diag.ProbeFormat", r.StatusText, r.Name, r.Summary));
            foreach (var d in r.Details) sb.AppendLine("    " + d);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = StatusFromKey("Diag.Copied");
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Diag.CopyFailed", ex.Message);
        }
    }

    /// <summary>
    /// 语言切换后重跑：环境行标签与状态文本都是「取值那一刻拼好的字符串」，
    /// 不像 XAML 里的 {l10n:L} 能自动刷新，必须手动重建一次。
    /// 已跑出的探针结果不重跑（那要重新连管道、重新调部署器，代价太大且不必要）。
    /// </summary>
    public void RefreshTexts()
    {
        RefreshEnvironment();

        // Restatus() 而不是把状态重置回「点击开始探测」——
        // 用户刚跑完一轮探测再切语言，那句「探测完成，共 N 项」是有信息量的，不该被抹掉。
        StatusText = Restatus();
    }
}
