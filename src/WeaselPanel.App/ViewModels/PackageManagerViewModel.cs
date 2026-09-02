//
//  PackageManagerViewModel.cs — 词库包页
//
//  ── 这一页和「词典」页的分工 ──────────────────────────────────────────
//  「词典」页管的是 custom_phrase.txt（用户自己一条条敲的词组）；
//  这一页管的是**整包词库**（雾凇拼音这类几十 MB 的方案包）与**语法模型**
//  （万象 .gram）。两者落盘位置、体积、更新方式全然不同，混在一页里
//  会让「清空词典」这种按钮变得极度危险 —— 所以分开。
//
//  ── 为什么每行都要显示状态点，而不只是按钮 ────────────────────────────
//  词库包有三态：未安装 / 本面板安装 / 外部已存在。第三态是关键：用户可能
//  自己手动解压过 rime-ice，此时面板没有清单，也就无从知道哪些文件是它装的
//  —— 这种情况下**绝不能**提供卸载按钮，否则会误删用户自己的文件。
//  界面必须把这个区别显示出来，而不是让用户点下去才发现按钮是灰的。
//
//  ── 阻塞关系（界面必须提前讲明白，而不是等报错）─────────────────────
//   · 语法模型依赖雾凇拼音：没装 rime-ice 时，万象那行的安装按钮置灰 + 给提示；
//   · 反向依赖：装了万象时，rime-ice 的**卸载**按钮置灰（先卸语法模型），
//     但**更新**按钮照常可用 —— 更新不会破坏依赖。
//
//  ── 线程 ──────────────────────────────────────────────────────────
//  安装 = 下载几十 MB + 解压上千文件 + 部署，全程走 Task.Run 推到线程池。
//  Core 的 InstallAsync 里有同步解压段，直接在 UI 线程 await 会把界面冻住
//  十几秒（看起来就是「点了没反应」），必须包一层。
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>词库包列表里的一行。状态 / 按钮可用性 / 提示语全在这里算好，XAML 只管画。</summary>
public sealed class PackageRow : ViewModelBase
{
    private readonly PackageManagerViewModel _owner;

    private PackageStatus _status = new() { Kind = PackageStatusKind.NotInstalled };
    private PackageUpdateState _update = PackageUpdateState.NotApplicable;
    private bool _isBusy;
    private string? _blockedKey;        // 整行被阻塞（安装/更新都不行）
    private string? _uninstallBlockedKey; // 只有卸载被阻塞

    public PackageRow(PackageManagerViewModel owner, DictionaryPackage package)
    {
        _owner = owner;
        Package = package;

        InstallCommand = new RelayCommand(() => _owner.InstallAsync(this), () => CanInstall);
        UpdateCommand = new RelayCommand(() => _owner.UpdateAsync(this), () => CanUpdate);
        UninstallCommand = new RelayCommand(() => _owner.UninstallAsync(this), () => CanUninstall);
        CheckCommand = new RelayCommand(() => _owner.CheckAsync(this), () => CanCheck);
        HomepageCommand = new DelegateCommand(OpenHomepage, () => Package.Homepage.Length > 0);
    }

    public DictionaryPackage Package { get; }

    public RelayCommand InstallCommand { get; }
    public RelayCommand UpdateCommand { get; }
    public RelayCommand UninstallCommand { get; }
    public RelayCommand CheckCommand { get; }
    public DelegateCommand HomepageCommand { get; }

    // ── 展示文本 ──────────────────────────────────────────────────────

    /// <summary>包名跟界面语言走：中文界面用 name，英文界面用 name_en（缺失时回落）。</summary>
    public string DisplayName =>
        IsEnglishUi && Package.NameEn.Length > 0 ? Package.NameEn : Package.Name;

    public string DisplayDescription =>
        IsEnglishUi && Package.DescriptionEn.Length > 0 ? Package.DescriptionEn : Package.Description;

    private static bool IsEnglishUi => L10n.Instance.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>「作者 · 类型」一行小字。作者名不翻译，类型翻译。</summary>
    public string MetaText { get; private set; } = "";

    /// <summary>状态文案：未安装 / 已安装 / 已存在（非本面板安装）。</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>给 XAML 的 DataTrigger 用，决定状态点颜色。刻意用字符串而不是 Brush —— VM 不碰 WPF 画刷。</summary>
    public string StatusKindName => _status.Kind.ToString();

    /// <summary>更新检查结果文案（未检查时为空串，界面上不占位）。</summary>
    public string UpdateText { get; private set; } = "";

    public bool HasUpdateText => UpdateText.Length > 0;

    /// <summary>有新版本 —— 界面用它把徽标点亮成主色。</summary>
    public bool IsUpdateAvailable => _update.Kind == PackageUpdateKind.Available;

    /// <summary>已装版本信息（tag / commit / 安装时间），未安装时为空。</summary>
    public string InstalledInfoText { get; private set; } = "";

    public bool HasInstalledInfo => InstalledInfoText.Length > 0;

    /// <summary>阻塞提示（依赖没满足时说明原因），无阻塞时为空。</summary>
    public string BlockedHintText { get; private set; } = "";

    public bool HasBlockedHint => BlockedHintText.Length > 0;

    // ── 状态位 ────────────────────────────────────────────────────────

    public bool IsBusy
    {
        get => _isBusy;
        internal set
        {
            if (!Set(ref _isBusy, value)) return;
            RaiseButtonStates();
        }
    }

    public bool IsInstalled => _status.IsInstalled;
    public bool IsExternal => _status.IsExternal;
    public bool IsNotInstalled => _status.Kind == PackageStatusKind.NotInstalled;

    /// <summary>显示「安装」按钮（仅未安装态）。外部安装态不给安装按钮 —— 会覆盖用户文件。</summary>
    public bool ShowInstall => IsNotInstalled;

    /// <summary>显示「更新 / 卸载 / 检查更新」三件套（仅本面板安装态）。</summary>
    public bool ShowManage => IsInstalled;

    public bool CanInstall => IsNotInstalled && !IsBusy && !_owner.IsBusy && _blockedKey is null;
    public bool CanUpdate => IsInstalled && !IsBusy && !_owner.IsBusy && _blockedKey is null;
    public bool CanCheck => IsInstalled && !IsBusy && !_owner.IsBusy;

    /// <summary>卸载有独立的阻塞条件（被别的包依赖时不许卸）。</summary>
    public bool CanUninstall =>
        IsInstalled && !IsBusy && !_owner.IsBusy && _uninstallBlockedKey is null;

    // ── 更新 / 刷新 ───────────────────────────────────────────────────

    internal void SetStatus(PackageStatus status, string? blockedKey, string? uninstallBlockedKey)
    {
        _status = status;
        _blockedKey = blockedKey;
        _uninstallBlockedKey = uninstallBlockedKey;

        // 状态一变，之前那次检查结果就作废了（比如刚卸载完，还挂着「有新版本」很荒唐）
        if (!status.IsInstalled) _update = PackageUpdateState.NotApplicable;

        RefreshTexts();

        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsExternal));
        OnPropertyChanged(nameof(IsNotInstalled));
        OnPropertyChanged(nameof(ShowInstall));
        OnPropertyChanged(nameof(ShowManage));
        OnPropertyChanged(nameof(StatusKindName));
        RaiseButtonStates();
    }

    internal void SetUpdate(PackageUpdateState state)
    {
        _update = state;
        RefreshTexts();
        OnPropertyChanged(nameof(IsUpdateAvailable));
    }

    /// <summary>页面级忙状态变了，行上的按钮可用性要跟着重算。</summary>
    internal void NotifyOwnerBusyChanged() => RaiseButtonStates();

    private void RaiseButtonStates()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanCheck));
        InstallCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
        CheckCommand.RaiseCanExecuteChanged();
    }

    /// <summary>重建所有本地化文案（语言切换 / 状态变化都要调）。</summary>
    public void RefreshTexts()
    {
        var typeText = L10n.Instance.T(Package.IsGrammar
            ? "Packages.Type.Grammar"
            : "Packages.Type.Dictionary");

        MetaText = Package.Author.Length > 0
            ? L10n.Instance.T("Packages.Meta.Line", typeText, Package.Author)
            : typeText;

        StatusText = L10n.Instance.T(_status.Kind switch
        {
            PackageStatusKind.Installed => "Packages.Status.Installed",
            PackageStatusKind.External => "Packages.Status.External",
            _ => "Packages.Status.NotInstalled",
        });

        UpdateText = _update.Kind switch
        {
            PackageUpdateKind.Checking => L10n.Instance.T("Packages.Update.Checking"),
            PackageUpdateKind.UpToDate => L10n.Instance.T("Packages.Update.UpToDate"),
            PackageUpdateKind.Available => L10n.Instance.T("Packages.Update.Available"),
            PackageUpdateKind.Unknown => L10n.Instance.T("Packages.Update.Unknown"),
            PackageUpdateKind.Failed => L10n.Instance.T("Packages.Update.Failed"),
            _ => "",
        };

        InstalledInfoText = BuildInstalledInfo();

        BlockedHintText = (_blockedKey ?? _uninstallBlockedKey) is { } key
            ? L10n.Instance.T(key)
            : IsExternal
                ? L10n.Instance.T("Packages.Hint.External")
                : "";

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(UpdateText));
        OnPropertyChanged(nameof(HasUpdateText));
        OnPropertyChanged(nameof(InstalledInfoText));
        OnPropertyChanged(nameof(HasInstalledInfo));
        OnPropertyChanged(nameof(BlockedHintText));
        OnPropertyChanged(nameof(HasBlockedHint));
    }

    private string BuildInstalledInfo()
    {
        if (_status.Manifest is not { } m) return "";

        // 版本标识优先级：release tag > commit 短号 > 无。都没有时只报安装时间 ——
        // 空着会让用户以为装了个来路不明的东西。
        var version = !string.IsNullOrWhiteSpace(m.InstalledTag)
            ? m.InstalledTag!
            : !string.IsNullOrWhiteSpace(m.InstalledCommit)
                ? m.InstalledCommit![..Math.Min(7, m.InstalledCommit!.Length)]
                : null;

        var when = m.InstalledAt == default
            ? ""
            : m.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        return version is null
            ? (when.Length == 0 ? "" : L10n.Instance.T("Packages.Meta.InstalledAt", when))
            : L10n.Instance.T("Packages.Meta.Version", version, when);
    }

    private void OpenHomepage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Package.Homepage) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不是能修的错，也不值得弹窗打断用户 —— 静默
        }
    }
}

public sealed class PackageManagerViewModel : ViewModelBase, ILanguageAware
{
    private readonly WeaselEnvironment _environment;
    private bool _isBusy;
    private string _statusText = "";

    public PackageManagerViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        Packages = new ObservableCollection<PackageRow>();
        CheckAllCommand = new RelayCommand(CheckAllAsync, () => !IsBusy && Packages.Any(p => p.IsInstalled));
        ReloadCommand = new DelegateCommand(Load, () => !IsBusy);

        StatusText = StatusFromKey("Packages.Status.Ready");
    }

    public ObservableCollection<PackageRow> Packages { get; }

    public RelayCommand CheckAllCommand { get; }
    public DelegateCommand ReloadCommand { get; }

    /// <summary>没检测到小狼毫就没有可写入的用户目录，整页置灰。</summary>
    public bool IsWeaselInstalled => _environment.IsInstalled;

    /// <summary>清单与备份的落盘位置。要显示给用户 —— 卸载能还原这件事得有据可查。</summary>
    public string ManagedPath => DictionaryPackageManager.ManagedDirectory(_environment);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            CheckAllCommand.RaiseCanExecuteChanged();
            ReloadCommand.RaiseCanExecuteChanged();
            foreach (var row in Packages) row.NotifyOwnerBusyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ── 载入 ──────────────────────────────────────────────────────────

    public void Load()
    {
        var registry = DictionaryPackageManager.LoadRegistry();

        // 注册表是嵌入式常量，进程内不会变；行对象只在首次建，之后只刷状态 ——
        // 每次重建会丢掉「刚检查出的更新结果」，用户切个页面回来发现徽标没了。
        if (Packages.Count == 0)
        {
            foreach (var pkg in registry)
                Packages.Add(new PackageRow(this, pkg));
        }

        RefreshStatuses();
        RefreshTexts();

        StatusText = IsWeaselInstalled
            ? StatusFromKey("Packages.Status.Ready")
            : StatusFromKey("Packages.Status.WeaselMissing");
    }

    /// <summary>重算每行的安装状态与依赖阻塞。任何一次装 / 卸之后都要调。</summary>
    private void RefreshStatuses()
    {
        var iceInstalled = DictionaryPackageManager.IsRimeIceInstalled(_environment);
        var grammarInstalled = DictionaryPackageManager
            .LoadRegistry()
            .Where(p => p.IsGrammar)
            .Any(p => DictionaryPackageManager.StatusOf(p, _environment).IsInstalled);

        foreach (var row in Packages)
        {
            var status = DictionaryPackageManager.StatusOf(row.Package, _environment);

            string? blocked = null;
            string? uninstallBlocked = null;

            if (row.Package.IsGrammar && !iceInstalled)
            {
                // 语法模型挂在 rime_ice.custom.yaml 上，没有雾凇拼音就没有挂载点
                blocked = "Packages.Hint.NeedsRimeIce";
            }
            else if (!row.Package.IsGrammar && row.Package.DefaultSchema == "rime_ice" && grammarInstalled)
            {
                // 反向依赖：先卸语法模型再卸雾凇，否则 grammar/language 会指向不存在的方案
                uninstallBlocked = "Packages.Hint.GrammarFirst";
            }

            row.SetStatus(status, blocked, uninstallBlocked);
        }

        OnPropertyChanged(nameof(IsWeaselInstalled));
        OnPropertyChanged(nameof(ManagedPath));
        CheckAllCommand.RaiseCanExecuteChanged();
    }

    // ── 装 / 更 / 卸 ──────────────────────────────────────────────────

    internal Task InstallAsync(PackageRow row) => RunAsync(row,
        "Packages.Status.Installing",
        "Packages.Status.InstallDone",
        () => DictionaryPackageManager.InstallAsync(row.Package, _environment));

    internal Task UpdateAsync(PackageRow row) => RunAsync(row,
        "Packages.Status.Updating",
        "Packages.Status.UpdateDone",
        () => DictionaryPackageManager.UpdateAsync(row.Package, _environment));

    internal Task UninstallAsync(PackageRow row) => RunAsync(row,
        "Packages.Status.Uninstalling",
        "Packages.Status.UninstallDone",
        () => DictionaryPackageManager.UninstallAsync(row.Package, _environment));

    /// <summary>
    /// 三个操作的共同外壳：置忙 → 报进度 → 推线程池执行 → 刷状态 → 报结果。
    /// 返回值一律不要（清单已经落盘，界面靠 RefreshStatuses 重读，不靠内存里那份），
    /// 所以签名统一收成 Func&lt;Task&gt; —— 装/更返回 Task&lt;PackageManifest&gt; 也能直接传进来。
    /// </summary>
    private async Task RunAsync(PackageRow row, string busyKey, string doneKey, Func<Task> work)
    {
        if (!IsWeaselInstalled)
        {
            StatusText = StatusFromKey("Packages.Status.WeaselMissing");
            return;
        }

        IsBusy = true;
        row.IsBusy = true;
        StatusText = StatusFromKey(busyKey, row.DisplayName);

        try
        {
            // ⚠️ 必须 Task.Run：Core 里的解压与文件复制是同步段，
            // 直接 await 会在 UI 线程上跑完几千个文件，界面冻死十几秒。
            await Task.Run(work);
            StatusText = StatusFromKey(doneKey, row.DisplayName);
        }
        catch (PackageManagerException ex)
        {
            StatusText = StatusFromKey("Packages.Status.Failed", RenderError(ex));
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Packages.Status.Failed", ex.Message);
        }
        finally
        {
            row.IsBusy = false;
            IsBusy = false;
            RefreshStatuses();
        }
    }

    // ── 检查更新 ──────────────────────────────────────────────────────

    internal async Task CheckAsync(PackageRow row)
    {
        row.SetUpdate(PackageUpdateState.Checking);
        try
        {
            var state = await Task.Run(() => DictionaryPackageManager.CheckUpdateAsync(row.Package, _environment));
            row.SetUpdate(state);
            StatusText = state.Kind switch
            {
                PackageUpdateKind.Available => StatusFromKey("Packages.Status.UpdateFound", row.DisplayName),
                PackageUpdateKind.UpToDate => StatusFromKey("Packages.Status.UpToDate", row.DisplayName),
                PackageUpdateKind.Failed => StatusFromKey("Packages.Status.CheckFailed", state.Message ?? ""),
                _ => StatusFromKey("Packages.Status.CheckDone"),
            };
        }
        catch (Exception ex)
        {
            row.SetUpdate(PackageUpdateState.Failed(ex.Message));
            StatusText = StatusFromKey("Packages.Status.CheckFailed", ex.Message);
        }
    }

    private async Task CheckAllAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Packages.Status.Checking");
        try
        {
            // 串行而非并行：两个包都打 GitHub，并发请求更容易撞上限流，
            // 换来的几百毫秒不值得让用户看到「检查失败」。
            foreach (var row in Packages.Where(r => r.IsInstalled).ToList())
                await CheckAsync(row);

            StatusText = StatusFromKey("Packages.Status.CheckDone");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 错误渲染 ──────────────────────────────────────────────────────

    /// <summary>
    /// 把 Core 抛出的本地化键翻成人话。刻意写成白名单 switch 而不是直接
    /// T(ex.L10nKey) —— 后者一旦 Core 加了新键而语言包忘了补，界面就会
    /// 露出裸键名（"Packages.Error.Xxx"）给用户看。白名单能兜住。
    /// </summary>
    private static string RenderError(PackageManagerException ex) => ex.L10nKey switch
    {
        "Packages.Error.WeaselNotInstalled" => L10n.Instance.T("Packages.Error.WeaselNotInstalled"),
        "Packages.Error.GrammarRequiresRimeIce" => L10n.Instance.T("Packages.Error.GrammarRequiresRimeIce"),
        "Packages.Error.GrammarFirst" => L10n.Instance.T("Packages.Error.GrammarFirst"),
        "Packages.Error.NotManaged" => L10n.Instance.T("Packages.Error.NotManaged"),
        "Packages.Error.Download" => L10n.Instance.T("Packages.Error.Download",
            ex.Args.Length > 0 ? ex.Args[0]?.ToString() ?? "" : ""),
        _ => L10n.Instance.T("Packages.Error.Unknown", ex.L10nKey),
    };

    // ── 语言切换 ──────────────────────────────────────────────────────

    public void RefreshTexts()
    {
        foreach (var row in Packages) row.RefreshTexts();

        OnPropertyChanged(nameof(ManagedPath));
        if (HasStatusKey) StatusText = Restatus();
    }
}
