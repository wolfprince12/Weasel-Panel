using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
/// <remarks>
/// 实现 INPC 是为了让侧栏底部「状态行」（isApplying 进度圈 + 状态文本）
/// 与「横幅区」（未装小狼毫 / 用户目录未就绪）能在 IsApplying / AnyDirty 变化时
/// 实时刷新，否则那些 DataTrigger 不会重新求值。
/// </remarks>
public sealed class DeployCoordinator : INotifyPropertyChanged
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
        Banners = BuildBanners(environment);
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

    // ── 状态行（仿 squirrel sidebarFooter 顶部 ProgressView + 状态文字）────────
    private bool _isApplying;
    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (_isApplying == value) return;
            _isApplying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    /// <summary>
    /// 状态行文本：进行中 → 应用中…；有未保存改动 → "N 项待保存"；否则 → "无未保存改动"。
    /// 绑到 XAML 即可，刷新由 IsApplying 改写 + RefreshCommands 同步驱动。
    /// </summary>
    public string StatusMessage
    {
        get
        {
            if (IsApplying) return L10n.Instance.T("Deploy.Status.Applying");
            if (AnyDirty)
            {
                var count = _panels.Count(p => p.IsDirty);
                return L10n.Instance.T("Deploy.Status.Dirty", count);
            }
            return L10n.Instance.T("Deploy.Status.Clean");
        }
    }

    /// <summary>状态行用的颜色 brush：进行中用次文字色，有未保存改动用橙色（仿 squirrel footer.dirty 橙色），
    /// 否则用次文字色。返回已解析的 Brush，XAML 直接绑 Foreground。</summary>
    public Brush StatusBrush => (IsApplying || !AnyDirty)
        ? (Brush)(Application.Current.FindResource("TextSecondaryBrush") ?? SystemColors.ControlTextBrush)
        : (Brush)(Application.Current.FindResource("WarningBrush") ?? SystemColors.ControlTextBrush);

    // ── 横幅区（仿 squirrel 顶部 Banner — 未装小狼毫 / 用户目录未就绪）────────
    public IReadOnlyList<BannerItem> Banners { get; }

    /// <summary>是否有横幅可显示。Banners 在 ctor 内一次性构建、运行期不变，故无需通知。</summary>
    public bool HasBanners => Banners.Count > 0;

    private static IReadOnlyList<BannerItem> BuildBanners(WeaselEnvironment env)
    {
        var list = new List<BannerItem>();
        if (!env.IsInstalled)
        {
            list.Add(new BannerItem(
                kind: BannerKind.Warning,
                textKey: "Banner.WeaselNotInstalled"));
        }
        if (!env.IsUserDirectoryReady)
        {
            list.Add(new BannerItem(
                kind: BannerKind.Info,
                textKey: "Banner.UserDirNotReady"));
        }
        return list;
    }

    // ── INotifyPropertyChanged ─────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RefreshCommands()
    {
        _applyDeployCommand.RaiseCanExecuteChanged();
        _discardCommand.RaiseCanExecuteChanged();
            // 状态行的 AnyDirty / 颜色 / 文案都依赖面板脏标记，必须主动通知。
            OnPropertyChanged(nameof(AnyDirty));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(StatusBrush));
    }

    private async Task ApplyAllAsync()
    {
        IsApplying = true;
        try
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
        finally
        {
            IsApplying = false;
        }
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

/// <summary>
/// 单条横幅（仿 squirrel Banner 结构）。文案与图标键由 DeployCoordinator.BuildBanners 决定，
/// XAML 直接绑 IsVisible / Text / KindBrushKey。
/// </summary>
public sealed class BannerItem
{
    public BannerItem(BannerKind kind, string textKey)
    {
        Kind = kind;
        TextKey = textKey;
    }

    public BannerKind Kind { get; }
    public string TextKey { get; }

    public string Text => L10n.Instance.T(TextKey);

    /// <summary>XAML 用来挑画刷的键名（与 App.xaml 的 SolidColorBrush x:Key 对齐）。</summary>
    public string TintBrushKey => Kind switch
    {
        BannerKind.Warning => "BannerWarningBrush",
        BannerKind.Info => "BannerInfoBrush",
        _ => "BannerInfoBrush",
    };

    /// <summary>XAML 用来挑字色（图标与文字）的键名。</summary>
    public string TextBrushKey => Kind switch
    {
        BannerKind.Warning => "BannerWarningTextBrush",
        BannerKind.Info => "BannerInfoTextBrush",
        _ => "BannerInfoTextBrush",
    };

    /// <summary>已解析的浅底画刷（横幅卡片背景）。每次取资源，暗色主题切换后也能跟随。</summary>
    public Brush TintBrush =>
        (Brush)(Application.Current.FindResource(TintBrushKey) ?? SystemColors.ControlBrush);

    /// <summary>已解析的字色画刷（图标与文字）。</summary>
    public Brush TextBrush =>
        (Brush)(Application.Current.FindResource(TextBrushKey) ?? SystemColors.ControlTextBrush);

    /// <summary>Segoe MDL2 Assets 字形（Warning / Info）。</summary>
    public string IconGlyph => Kind switch
    {
        BannerKind.Warning => "\uE7BA",  // Warning
        BannerKind.Info => "\uE946",     // Info
        _ => "\uE946",
    };
}

public enum BannerKind
{
    Info,
    Warning,
}
