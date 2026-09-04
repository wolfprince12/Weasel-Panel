//
//  AboutViewModel.cs
//  WeaselPanel.App
//
//  关于页。除了展示版本与当前环境，这里还是**语言选择器的唯一入口**。
//
//  语言选择的持久化走 PanelSettings（%APPDATA%\WeaselPanel\settings.json），
//  绝不写进 Rime 用户目录 —— 那个目录同时是同步目录和备份目录，
//  往里塞面板自己的偏好会污染同步、被备份打包、还会被部署器扫描到。
//
//  本页内容对齐 macOS 鼠须管面板的 About 页：开发者、更多作品（推广）、
//  运行状态、关于项目、相关链接。语言切换时通过 RefreshTexts 重建所有文案。
//
// 顶部两张「更新检查」卡片（自身 + 小狼毫输入法本体）由本 VM 持有的两个
// ReleaseUpdateChecker 驱动：checker 在 GitHub 镜像上完成后 marshal 回
// UI 线程刷新文案；状态机的枚举与按钮可见性按 macOS UpdateCenter 同款。
//

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Config;
using WeaselPanel.Core.Net;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.ViewModels;

/// <summary>语言选择器里的一项。语言名不翻译（跟 Windows 语言列表的惯例一致）。</summary>
public sealed class LanguageOption
{
    public required string Code { get; init; }
    public required string DisplayName { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>关于页「更多作品」里的一条推广。</summary>
public sealed class PromoItem
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Description { get; init; }
    /// <summary>为空表示没有可跳转的链接（如微信服务号，只能描述）。</summary>
    /// <summary>推广条目。Url 留 null 表示该项无外链（面板不渲染「访问」行）。</summary>
    public Uri? Url { get; init; }
}

public sealed class AboutViewModel : ViewModelBase, ILanguageAware
{
    private readonly WeaselEnvironment _environment;

    // ── 更新检查（自身 + 小狼毫输入法本体）───────────────────────

    /// <summary>面板自身（WeaselPanel）的 GitHub Release 检查器。仓库是 wolfprince12/Weasel-Panel。</summary>
    private readonly ReleaseUpdateChecker _panelChecker;

    /// <summary>小狼毫输入法本体的 GitHub Release 检查器。仓库是 rime/weasel。</summary>
    private readonly ReleaseUpdateChecker _weaselChecker;

    private CancellationTokenSource? _checkerCts;

    // ── 状态字段 ────────────────────────────────────────────────

    private string _selectedLanguage;
    private string _languageNote = "";
    private string _developerName = "";
    private string _developerRole = "";
    private string _developerBio = "";
    private string _statusSummary = "";

    public AboutViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        LanguageOptions = new ObservableCollection<LanguageOption>(
            L10n.SupportedLanguages.Select(c => new LanguageOption
            {
                Code = c,
                DisplayName = L10n.DisplayNameOf(c),
            }));

        PromoItems = new ObservableCollection<PromoItem>();

        // 设置里存的是用户的选择（"auto" / "zh-Hans" / null）。
        // 语言包里存的 null 与 "auto" 是同一回事，统一归一成 "auto" 以免 ComboBox 选不中。
        var stored = PanelSettings.Load().Language;
        _selectedLanguage = string.IsNullOrWhiteSpace(stored) ? L10n.AutoLanguage : stored;

        var fetch = new GitHubMirrorFetch();
        _panelChecker = new ReleaseUpdateChecker(fetch, GetPanelVersion, "wolfprince12/Weasel-Panel");
        _weaselChecker = new ReleaseUpdateChecker(
            fetch,
            () => _environment.Version ?? string.Empty,
            "rime/weasel");

        // 在后台线程上完成 GitHub 拉取 → marshal 回 UI 线程刷新界面。
        // Checker 自身不做线程切换（Core 层不应该 reference WPF），
        // 故本 VM 在收到 PropertyChanged 时手动 BeginInvoke。
        _panelChecker.PropertyChanged += OnCheckerChangedOnUi;
        _weaselChecker.PropertyChanged += OnCheckerChangedOnUi;

        // ── 命令 ──
        // CheckPanelUpdateCommand：面板自身更新检查。WeaselPanel 不存在"未安装"分支——它正在跑。
        CheckPanelUpdateCommand = new RelayCommand(
            execute: () => _panelChecker.CheckAsync(_checkerCts?.Token ?? CancellationToken.None));
        OpenPanelDownloadCommand = new DelegateCommand(OpenPanelRelease, () => _panelChecker.State == UpdateCheckState.Available);

        // CheckWeaselUpdateCommand：小狼毫本体更新检查。未安装时禁用——无 ProgramDirectory 也就无 Version，
        // 强行检查会拿到"current=空串"→ 永远 UpToDate，反而显得"已检查通过"，误导用户。
        CheckWeaselUpdateCommand = new RelayCommand(
            execute: () => _weaselChecker.CheckAsync(_checkerCts?.Token ?? CancellationToken.None),
            canExecute: () => _environment.IsInstalled);
        OpenWeaselDownloadCommand = new DelegateCommand(OpenWeaselRelease, () => _weaselChecker.State == UpdateCheckState.Available);

        RefreshTexts();

        // 启动时统一触发一次检查（与 macOS UpdateCenter.checkAllOnLaunch 同行为），
        // 不卡 UI：放到后台 Task.Run，错误由 checker 自己捕获转 Failed 状态。
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await _panelChecker.CheckAsync();
            if (_environment.IsInstalled)
            {
                await _weaselChecker.CheckAsync();
            }
        });
    }

    private void OnCheckerChangedOnUi(object? sender, EventArgs e)
    {
        // sender 来自任意线程。这里只在 UI 线程上 marshal 一个统一刷新动作，
        // 不区分是哪个 checker —— UpdateFromChecker 内部会按 sender 分发。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess())
        {
            RefreshPanelTexts();
            RefreshWeaselTexts();
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshPanelTexts();
                RefreshWeaselTexts();
            }));
        }
    }

    // ── 语言选择器 ────────────────────────────────────────────

    public ObservableCollection<LanguageOption> LanguageOptions { get; }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!Set(ref _selectedLanguage, value)) return;

            // 先落盘再切换：写盘失败最多下次重启丢设置，
            // 反过来「界面切了但没存住」会让用户以为设置坏了。
            var settings = PanelSettings.Load();
            settings.Language = value == L10n.AutoLanguage ? null : value;
            settings.Save();

            // SetLanguage 会触发 PropertyChanged → MainWindow 把变更派发给所有 ViewModel
            // （包括本类的 RefreshTexts），所以这里不用再手动刷一次。
            L10n.Instance.SetLanguage(value);
        }
    }

    public string LanguageNote
    {
        get => _languageNote;
        private set => Set(ref _languageNote, value);
    }

    // ── 展示项 ────────────────────────────────────────────────

    public string DeveloperName
    {
        get => _developerName;
        private set => Set(ref _developerName, value);
    }

    public string DeveloperRole
    {
        get => _developerRole;
        private set => Set(ref _developerRole, value);
    }

    public string DeveloperBio
    {
        get => _developerBio;
        private set => Set(ref _developerBio, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        private set => Set(ref _statusSummary, value);
    }

    /// <summary>「更多作品」推广卡片的内容（切语言时重建）。</summary>
    public ObservableCollection<PromoItem> PromoItems { get; }

    // ── 更新卡片绑定 ─────────────────────────────────────────────

    /// <summary>面板更新卡的主文字（"当前已是最新" / "发现新版本 v0.2.10" 等）。</summary>
    public string PanelUpdateText => ComputeUpdateText(_panelChecker.State, _panelChecker.LatestVersion);

    /// <summary>面板更新卡按钮文字（"检查更新" / "重试" / "前往下载"）。</summary>
    public string PanelUpdateButtonText => ComputeUpdateButtonText(_panelChecker.State);

    /// <summary>面板更新卡的副标题：当前本机版本号，从 csproj InformationalVersion 读取。</summary>
    public string PanelUpdateHint => L10n.Instance.T("About.PanelUpdate.Hint", GetPanelVersion());

    /// <summary>面板更新卡左侧状态字符（✓ / ↑ / ! / ⋯ / ?）。取自 macOS 同一五态。</summary>
    public string PanelUpdateGlyph => ComputeUpdateGlyph(_panelChecker.State);

    /// <summary>面板更新卡下载按钮可见性：仅 Available 时显示。</summary>
    public Visibility PanelDownloadVisibility =>
        _panelChecker.State == UpdateCheckState.Available ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>面板更新卡检查按钮可见性：非 Available 时显示（包含 Checking 状态由 canExecute 自动禁用按钮）。</summary>
    public Visibility PanelCheckVisibility =>
        _panelChecker.State == UpdateCheckState.Available ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>同上对小狼毫输入法。</summary>
    public string WeaselUpdateText => ComputeUpdateText(_weaselChecker.State, _weaselChecker.LatestVersion);

    public string WeaselUpdateButtonText => ComputeUpdateButtonText(_weaselChecker.State);

    public string WeaselUpdateHint => _environment.IsInstalled
        ? L10n.Instance.T("About.WeaselUpdate.Hint", _environment.Version ?? "?")
        : L10n.Instance.T("About.WeaselUpdate.NotInstalled");

    /// <summary>小狼毫输入法更新卡左侧状态字符（✓ / ↑ / ! / ⋯ / ?）。</summary>
    public string WeaselUpdateGlyph => ComputeUpdateGlyph(_weaselChecker.State);

    public Visibility WeaselDownloadVisibility =>
        _weaselChecker.State == UpdateCheckState.Available ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WeaselCheckVisibility =>
        _weaselChecker.State == UpdateCheckState.Available ? Visibility.Collapsed : Visibility.Visible;

    public ICommand CheckPanelUpdateCommand { get; }
    public ICommand OpenPanelDownloadCommand { get; }
    public ICommand CheckWeaselUpdateCommand { get; }
    public ICommand OpenWeaselDownloadCommand { get; }

    // ── 仓库地址 ──────────────────────────────────────────────

    /// <summary>仓库地址。不本地化 —— 它就是个 URL。</summary>
    /// <remarks>
    /// 用 static readonly Uri（不能 const —— Uri 不是编译期常量），
    /// 直接给 Hyperlink.NavigateUri 喂 Uri 类型对象，XAML attribute 解析时
    /// 完全不走 string→Uri TypeConverter，避免「"https://rime.im" 不是属性
    /// 'NavigateUri' 的有效值」之类的运行时崩盘。URL 一律带尾斜杠，避免
    /// UriTypeConverter 对无路径主机名的边界 case 拒识别。
    ///
    /// ⚠️ RepoUrl / IssuesUrl 共享 host 路径，要改必须同步两边。
    /// 为什么不拼插（$".../{...}/..."）：static readonly 字段初始化顺序由 CLR 保证，
    /// 但插值要在 IL 里走 string.Concat，并非所有人都熟；字面量更不容易踩坑。
    /// </remarks>
    public static readonly Uri RepoUrl = new("https://github.com/wolfprince12/Weasel-Panel/");
    public static readonly string RepoDisplay = "github.com/wolfprince12/Weasel-Panel";
    public static readonly Uri IssuesUrl = new("https://github.com/wolfprince12/Weasel-Panel/issues");

    public static readonly Uri RimeUrl = new("https://rime.im/");
    public static readonly Uri WeaselUrl = new("https://github.com/rime/weasel/");
    public static readonly Uri DealvUrl = new("https://www.dealv.cn/");
    public static readonly Uri DsonDtUrl = new("https://github.com/wolfprince12/DSonDT/");

    // ── 文本刷新 ──────────────────────────────────────────────

    /// <summary>
    /// 语言切换后重建本页文本。
    /// 「版本 / 技术栈 / 许可」三项看起来是常量，但许可名与技术栈在别的语言里
    /// 写法不同（GPL-3.0 本身不变，说明文字会变），故一并走语言包。
    /// </summary>
    public void RefreshTexts()
    {
        LanguageNote = L10n.Instance.T("Lang.Note");
        DeveloperName = L10n.Instance.T("About.Developer.Name");
        DeveloperRole = L10n.Instance.T("About.Developer.Role");
        DeveloperBio = L10n.Instance.T("About.Developer.Bio");
        StatusSummary = BuildStatusSummary();
        RefreshPanelTexts();
        RefreshWeaselTexts();
        RebuildPromo();
    }

    private void RefreshPanelTexts()
    {
        OnPropertyChanged(nameof(PanelUpdateText));
        OnPropertyChanged(nameof(PanelUpdateButtonText));
        OnPropertyChanged(nameof(PanelUpdateHint));
        OnPropertyChanged(nameof(PanelUpdateGlyph));
        OnPropertyChanged(nameof(PanelCheckVisibility));
        OnPropertyChanged(nameof(PanelDownloadVisibility));
    }

    private void RefreshWeaselTexts()
    {
        OnPropertyChanged(nameof(WeaselUpdateText));
        OnPropertyChanged(nameof(WeaselUpdateButtonText));
        OnPropertyChanged(nameof(WeaselUpdateHint));
        OnPropertyChanged(nameof(WeaselUpdateGlyph));
        OnPropertyChanged(nameof(WeaselCheckVisibility));
        OnPropertyChanged(nameof(WeaselDownloadVisibility));
    }

    private void RebuildPromo()
    {
        PromoItems.Clear();
        // 爻知云单独抽成「左文字右二维码」卡片（见 AboutView.xaml，对齐鼠须管 yaozhiCard），
        // 这里只放其余作品，避免二维码图混进通用列表的纯文字模板。
        PromoItems.Add(new PromoItem
        {
            Title = L10n.Instance.T("About.Promo.Dealv.Title"),
            Subtitle = L10n.Instance.T("About.Promo.Dealv.Subtitle"),
            Description = L10n.Instance.T("About.Promo.Dealv.Desc"),
            Url = DealvUrl,
        });
        PromoItems.Add(new PromoItem
        {
            Title = L10n.Instance.T("About.Promo.DsonDt.Title"),
            Subtitle = L10n.Instance.T("About.Promo.DsonDt.Subtitle"),
            Description = L10n.Instance.T("About.Promo.DsonDt.Desc"),
            Url = DsonDtUrl,
        });
    }

    private string BuildStatusSummary()
    {
        var installed = _environment.IsInstalled
            ? L10n.Instance.T("About.Status.Installed")
            : L10n.Instance.T("About.Status.NotInstalled");
        var userDir = _environment.IsUserDirectoryReady
            ? L10n.Instance.T("About.Status.UserDirReady")
            : L10n.Instance.T("About.Status.UserDirNotReady");
        return installed + "  ·  " + userDir;
    }

    // ── 助手 ─────────────────────────────────────────────────

    /// <summary>
    /// 当前面板版本字符串。优先取 InformationalVersion（"0.2.9"），降级到 Version 三段（"0.2.9"），
    /// 均去除 build meta（"+xxxx"）。用于与 GitHub release tag 直接比对。
    /// </summary>
    private static string GetPanelVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info!.IndexOf('+');
            return (plus >= 0 ? info[..plus] : info).Trim();
        }
        var v = asm.GetName().Version;
        return v is null ? string.Empty : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private string ComputeUpdateText(UpdateCheckState state, string? latestVersion)
    {
        var t = L10n.Instance;
        return state switch
        {
            UpdateCheckState.Idle      => t.T("Update.State.Idle"),
            UpdateCheckState.Checking  => t.T("Update.State.Checking"),
            UpdateCheckState.UpToDate  => t.T("Update.State.UpToDate"),
            UpdateCheckState.Available => t.T("Update.State.Available"),
            UpdateCheckState.Failed    => t.T("Update.State.Failed"),
            _                          => t.T("Update.State.Idle"),
        };
    }

    private string ComputeUpdateButtonText(UpdateCheckState state)
    {
        var t = L10n.Instance;
        return state switch
        {
            UpdateCheckState.Available => t.T("Update.Button.Download"),
            UpdateCheckState.Failed    => t.T("Update.Button.Retry"),
            _                          => t.T("Update.Button.Check"),
        };
    }

    private static string ComputeUpdateGlyph(UpdateCheckState state) => state switch
    {
        UpdateCheckState.Checking  => "⋯",
        UpdateCheckState.UpToDate  => "✓",
        UpdateCheckState.Available => "↑",
        UpdateCheckState.Failed    => "!",
        _                          => "?",
    };

    private void OpenPanelRelease()
    {
        var url = _panelChecker.HtmlUrl;
        if (string.IsNullOrEmpty(url))
        {
            url = $"https://github.com/{_panelChecker.Repo}/releases";
        }
        OpenUrl(url);
    }

    private void OpenWeaselRelease()
    {
        var url = _weaselChecker.HtmlUrl;
        if (string.IsNullOrEmpty(url))
        {
            url = $"https://github.com/{_weaselChecker.Repo}/releases";
        }
        OpenUrl(url);
    }

    /// <summary>统一外链入口：先 Process.Start，失败回退到 explorer.exe。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false }); }
            catch { /* 用户机器上 explorer 都不在的话就不追了 */ }
        }
    }
}
