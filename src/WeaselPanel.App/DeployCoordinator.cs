using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App;

/// <summary>
/// 可经全局部署栏统一应用的面板契约。各配置面板实现它，
/// 由 <see cref="DeployCoordinator"/> 在「应用并重新部署」时统一调用。
/// </summary>
public interface IPanelActions
{
    bool IsDirty { get; }
    Task ApplyAsync();
    Task ReloadAsync();
}

/// <summary>
/// 全局部署协调器：仿鼠须管侧栏底部的「应用并重新部署」统一栏。
/// 只应用 IsDirty 的面板（绝不把用户没改过的配置用内存默认值覆盖回磁盘），
/// 然后统一重新部署一次。「放弃更改」重新加载全部面板（脏标记复位）。
/// </summary>
public sealed class DeployCoordinator
{
    private readonly WeaselEnvironment _environment;
    private readonly List<IPanelActions> _panels = new();

    private readonly RelayCommand _applyDeployCommand;
    private readonly RelayCommand _discardCommand;
    private readonly DelegateCommand _viewYamlCommand;

    public DeployCoordinator(WeaselEnvironment environment)
    {
        _environment = environment;
        _applyDeployCommand = new RelayCommand(ApplyAllAsync, () => CanWrite && AnyDirty);
        _discardCommand = new RelayCommand(DiscardAllAsync);
        _viewYamlCommand = new DelegateCommand(ViewYaml);
    }

    /// <summary>注册一个可部署面板（其 ViewModel 须实现 <see cref="IPanelActions"/>）。</summary>
    public void Register(IPanelActions panel)
    {
        _panels.Add(panel);
        // 任何属性变化都让按钮的 CanExecute 重新求值（脏标记随之变，按钮跟着亮/灰）。
        // 本项目 RelayCommand 的 CanExecuteChanged 不链 CommandManager.RequerySuggested，
        // 必须显式 RaiseCanExecuteChanged 才刷新按钮启用态。
        if (panel is ViewModelBase vm)
        {
            vm.PropertyChanged += (_, _) => RefreshCommands();
        }
    }

    public bool CanWrite => _environment.DeployerPath is not null;
    public bool AnyDirty => _panels.Any(p => p.IsDirty);

    public ICommand ApplyDeployCommand => _applyDeployCommand;
    public ICommand DiscardCommand => _discardCommand;
    public ICommand ViewYamlCommand => _viewYamlCommand;

    private void RefreshCommands()
    {
        _applyDeployCommand.RaiseCanExecuteChanged();
        _discardCommand.RaiseCanExecuteChanged();
    }

    private async Task ApplyAllAsync()
    {
        foreach (var panel in _panels.Where(p => p.IsDirty))
        {
            await panel.ApplyAsync();
        }

        await DeployAsync();

        // 各面板 ApplyAsync 内部可能用 MarkLoaded() 清零脏标记（不触发 PropertyChanged），
        // 这里强制让按钮的 CanExecute 重新求值，使「应用并重新部署」在写完後正确变灰。
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DiscardAllAsync()
    {
        foreach (var panel in _panels)
        {
            await panel.ReloadAsync();
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DeployAsync()
    {
        if (_environment.DeployerPath is null) return;
        await Task.Run(() => ProbeService.ProbeDeployer(_environment.DeployerPath));
    }

    private void ViewYaml()
    {
        try
        {
            if (!string.IsNullOrEmpty(_environment.UserDirectory))
                Process.Start(new ProcessStartInfo(_environment.UserDirectory) { UseShellExecute = true });
        }
        catch
        {
            // 打开文件管理器失败不视为错误
        }
    }
}
