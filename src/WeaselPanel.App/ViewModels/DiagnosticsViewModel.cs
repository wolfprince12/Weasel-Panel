using System.Collections.ObjectModel;
using System.Windows;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.ViewModels;

public sealed class EnvironmentRow
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public bool IsWarning { get; init; }
}

public sealed class DiagnosticsViewModel : ViewModelBase
{
    private bool _isBusy;
    private string _statusText = "点击「开始探测」以验证本机小狼毫环境";

    public DiagnosticsViewModel()
    {
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
            EnvironmentRows.Add(new EnvironmentRow { Key = "探测失败", Value = ex.Message, IsWarning = true });
            return;
        }

        Environment = env;
        EnvironmentRows.Clear();
        void Add(string key, string? value, bool warn = false) =>
            EnvironmentRows.Add(new EnvironmentRow
            {
                Key = key,
                Value = string.IsNullOrWhiteSpace(value) ? "（未找到）" : value,
                IsWarning = warn || string.IsNullOrWhiteSpace(value),
            });

        Add("已安装小狼毫", env.IsInstalled ? "是" : "否", !env.IsInstalled);
        Add("程序目录", env.ProgramDirectory);
        Add("共享数据目录", env.SharedDataDirectory);
        Add("用户目录", env.UserDirectory, !env.IsUserDirectoryReady);
        Add("用户目录状态", env.IsUserDirectoryReady ? "已就绪" : "尚不存在（需先部署一次）", !env.IsUserDirectoryReady);
        Add("同步目录", env.SyncDirectory);
        Add("备份目录", env.BackupsDirectory);
        Add("日志目录", env.LogDirectory);
        Add("部署器", env.DeployerPath);
        Add("当前用户名", ProbeService.CurrentUserName);
        Add("预期管道名", ProbeService.ExpectedPipeName);

        OnPropertyChanged(nameof(Environment));
    }

    private async Task RunProbeAsync()
    {
        IsBusy = true;
        StatusText = "探测中……";
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
                ? $"探测完成，共 {results.Count} 项（部署项需手动执行）"
                : $"探测完成，{failed} 项失败 —— 请把报告发给我";
        }
        catch (Exception ex)
        {
            StatusText = "探测异常：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunDeployAsync()
    {
        IsBusy = true;
        StatusText = "正在执行部署（可能耗时数十秒）……";
        try
        {
            var env = Environment;
            var result = await Task.Run(() => ProbeService.ProbeDeployer(env?.DeployerPath));
            Results.Insert(0, result);
            StatusText = result.Status == ProbeStatus.Ok ? "部署完成" : result.Summary;
        }
        catch (Exception ex)
        {
            StatusText = "部署异常：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("小狼毫控制面板 — 环境诊断报告");
        sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        sb.AppendLine("── 环境 ──");
        foreach (var r in EnvironmentRows) sb.AppendLine($"{r.Key}：{r.Value}");
        sb.AppendLine();
        sb.AppendLine("── 探测结果 ──");
        foreach (var r in Results)
        {
            sb.AppendLine($"[{r.StatusText}] {r.Name}：{r.Summary}");
            foreach (var d in r.Details) sb.AppendLine("    " + d);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = "报告已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }
}
