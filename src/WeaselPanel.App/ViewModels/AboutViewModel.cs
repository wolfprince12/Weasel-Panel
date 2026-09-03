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

using System;
using System.Collections.ObjectModel;
using System.Text;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Config;
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
    private string _selectedLanguage;
    private string _versionText = "";
    private string _techValue = "";
    private string _licenseValue = "";
    private string _environmentSummary = "";
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

        RefreshTexts();
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

    public string VersionText
    {
        get => _versionText;
        private set => Set(ref _versionText, value);
    }

    public string TechValue
    {
        get => _techValue;
        private set => Set(ref _techValue, value);
    }

    public string LicenseValue
    {
        get => _licenseValue;
        private set => Set(ref _licenseValue, value);
    }

    public string EnvironmentSummary
    {
        get => _environmentSummary;
        private set => Set(ref _environmentSummary, value);
    }

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

    /// <summary>
    /// 语言切换后重建本页文本。
    /// 「版本 / 技术栈 / 许可」三项看起来是常量，但许可名与技术栈在别的语言里
    /// 写法不同（GPL-3.0 本身不变，说明文字会变），故一并走语言包。
    /// </summary>
    public void RefreshTexts()
    {
        var v = App.ExecutableVersion;
        VersionText = L10n.Instance.T("About.VersionPreview", v.Major, v.Minor, v.Build);
        TechValue = L10n.Instance.T("About.TechValue");
        LicenseValue = L10n.Instance.T("About.LicenseValue");
        LanguageNote = L10n.Instance.T("Lang.Note");
        EnvironmentSummary = BuildEnvironmentSummary();
        DeveloperName = L10n.Instance.T("About.Developer.Name");
        DeveloperRole = L10n.Instance.T("About.Developer.Role");
        DeveloperBio = L10n.Instance.T("About.Developer.Bio");
        StatusSummary = BuildStatusSummary();
        RebuildPromo();
    }

    private void RebuildPromo()
    {
        PromoItems.Clear();
        PromoItems.Add(new PromoItem
        {
            Title = L10n.Instance.T("About.Promo.Yaozhi.Title"),
            Subtitle = L10n.Instance.T("About.Promo.Yaozhi.Subtitle"),
            Description = L10n.Instance.T("About.Promo.Yaozhi.Desc"),
        });
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

    private string BuildEnvironmentSummary()
    {
        var notFound = L10n.Instance.T("Common.NotFound");
        var sb = new StringBuilder();
        sb.AppendLine(L10n.Instance.T("About.RowFormat",
            L10n.Instance.T("About.ProgramDir"), _environment.ProgramDirectory ?? notFound));
        sb.AppendLine(L10n.Instance.T("About.RowFormat",
            L10n.Instance.T("About.SharedDir"), _environment.SharedDataDirectory ?? notFound));
        sb.AppendLine(L10n.Instance.T("About.RowFormat",
            L10n.Instance.T("About.UserDir"), _environment.UserDirectory));
        sb.AppendLine(L10n.Instance.T("About.RowFormat",
            L10n.Instance.T("About.Deployer"), _environment.DeployerPath ?? notFound));
        sb.AppendLine();
        sb.Append(L10n.Instance.T("About.DeployHint"));
        return sb.ToString();
    }
}
