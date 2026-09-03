//  Weasel Panel — 小狼毫（Rime Weasel）Windows 图形化设置面板
//
//  自定义配色编辑器页。
//
//  ── 这一页管什么、不管什么 ────────────────────────────────────────────
//  管：方案**定义**的增删改（<用户目录>/user_color_schemes.json），
//      以及把定义注入 weasel.custom.yaml 的 preset_color_schemes/<id>。
//  不管：style/color_scheme（当前用哪一套）—— 那是「外观」页的键。
//      项目铁律：两个页面不得抢同一 YAML 键。本页只读它，用来标「当前生效」。
//
//  ── 编辑模型 ──────────────────────────────────────────────────────────
//  22 个通道各自独立：显式设值就落盘，清空则交回 Rime 的回退链（显示成「继承」）。
//  预览与色块一律走 ColorSchemeResolver（含 alpha 混合），保证「面板所见 = 候选窗所得」。
//
//  ── 脏值判定 ──────────────────────────────────────────────────────────
//  按内容比，不记「改过」标记：用户把颜色改回去又改回来，保存按钮不该还亮着。
//  做法与「应用选项」页一致 —— 载入/保存后各算一次全量签名，比较签名。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>左侧列表里的一行。包装 UserColorScheme 是为了拿到属性变更通知。</summary>
public sealed class SchemeRow : INotifyPropertyChanged
{
    public UserColorScheme Scheme { get; }
    private bool _isActive;

    public SchemeRow(UserColorScheme scheme) => Scheme = scheme;

    public string Id => Scheme.Id;

    public string Name
    {
        get => Scheme.DisplayName;
        set
        {
            if (Scheme.Name == value) return;
            Scheme.Name = value;
            Notify(nameof(Name));
        }
    }

    /// <summary>当前 style/color_scheme 指向这一套（由 VM 在刷新时统一赋值）。</summary>
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; Notify(nameof(IsActive)); Notify(nameof(BadgeText)); }
    }

    public string BadgeText => _isActive ? L10n.Instance.T("ColorSchemes.Badge.Active") : "";

    /// <summary>副标题：通道数 + 字节序。字节序非默认时才显示，避免每行都挂个 "abgr"。</summary>
    public string Subtitle => Scheme.Format == RimeColorFormat.Abgr
        ? L10n.Instance.T("ColorSchemes.Row.Channels", Scheme.Colors.Count)
        : L10n.Instance.T("ColorSchemes.Row.ChannelsFormat",
            Scheme.Colors.Count, Scheme.Format.ToConfigName());

    public void NotifySubtitle() => Notify(nameof(Subtitle));
    public void NotifyName() => Notify(nameof(Name));
    public void RefreshTexts() { Notify(nameof(BadgeText)); Notify(nameof(Subtitle)); }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>右侧 22 行中的一行：一个可编辑的颜色通道。</summary>
public sealed class ChannelRow : INotifyPropertyChanged
{
    private readonly UserColorScheme _scheme;
    private readonly Action _onChanged;
    private bool _hasValue;
    private bool _isValid = true;

    public ChannelRow(string key, UserColorScheme scheme, Action onChanged)
    {
        Key = key;
        _scheme = scheme;
        _onChanged = onChanged;
        Refresh();
    }

    public string Key { get; }

    public string Label => L10n.Instance.T("ColorSchemes.Ch." + Key);

    /// <summary>提示 = 通道用途 + 原始键名（键名给会手改 YAML 的人对号入座）。</summary>
    public string Tip =>
        L10n.Instance.T("ColorSchemes.ChTip." + Key) + "\n" + Key;

    /// <summary>显式值；空串表示「继承」，即交给回退链。</summary>
    public string Literal
    {
        get => _scheme.Colors.TryGetValue(Key, out var v) ? v : "";
        set
        {
            var next = (value ?? "").Trim();
            if (next.Length == 0)
            {
                if (!_scheme.Colors.Remove(Key) && !_hasValue) return;
            }
            else
            {
                _isValid = RimeColor.TryParseAbgr(next, _scheme.Format, out _);
                if (_scheme.Colors.TryGetValue(Key, out var old) && old == next) return;
                _scheme.Colors[Key] = next;
            }
            Refresh();
            _onChanged();
        }
    }

    /// <summary>生效值：显式值优先，否则是回退链算出来的结果（供「继承」时展示）。</summary>
    public string EffectiveLiteral { get; private set; } = "";

    public bool HasValue
    {
        get => _hasValue;
        private set { if (_hasValue == value) return; _hasValue = value; Notify(nameof(HasValue)); Notify(nameof(IsInherited)); Notify(nameof(SwatchOpacity)); }
    }

    public bool IsInherited => !_hasValue;

    public bool IsValid
    {
        get => _isValid;
        private set { if (_isValid == value) return; _isValid = value; Notify(nameof(IsValid)); }
    }

    public SolidColorBrush Swatch { get; private set; } = Brushes.Transparent;

    /// <summary>继承态的色块压到 45% 不透明度：它是「算出来的」，不是用户定的。</summary>
    public double SwatchOpacity => _hasValue ? 1.0 : 0.45;

    /// <summary>从颜色对话框/其它途径设值后重新同步显示。</summary>
    public void Refresh()
    {
        var resolved = _scheme.Resolve();
        var abgr = resolved.AbgrForKey(Key) ?? 0;

        HasValue = _scheme.Colors.ContainsKey(Key);
        EffectiveLiteral = RimeColor.FromAbgr(abgr).Literal(_scheme.Format);
        Swatch = ToBrush(abgr);

        // 显式值的合法性只对「用户真的写了东西」时才有意义；继承态恒为合法
        if (!_hasValue) IsValid = true;

        Notify(nameof(Literal));
        Notify(nameof(EffectiveLiteral));
        Notify(nameof(Swatch));
    }

    public void RefreshTexts()
    {
        Notify(nameof(Label));
        Notify(nameof(Tip));
    }

    internal static SolidColorBrush ToBrush(uint abgr)
    {
        var c = RimeColor.FromAbgr(abgr);
        byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0d, 1d) * 255d);
        var brush = new SolidColorBrush(Color.FromArgb(B(c.Alpha), B(c.Red), B(c.Green), B(c.Blue)));
        brush.Freeze();
        return brush;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>右侧编辑区的一组通道（外框 / 文字 / 高亮 / 其余）。</summary>
/// <remarks>
/// 22 个通道平铺成一列没人看得下去，按「画面上的哪个部位」分四组。
/// 分组定义来自 Core 的 <see cref="ColorSchemeFields.Groups"/>，这里只负责按语言取标题。
/// </remarks>
public sealed class ChannelGroup : INotifyPropertyChanged
{
    private readonly string _group;

    public ChannelGroup(string group) => _group = group;

    public string Title => L10n.Instance.T("ColorSchemes.Group." + _group);

    public ObservableCollection<ChannelRow> Rows { get; } = new();

    public void RefreshTexts()
    {
        Notify(nameof(Title));
        foreach (var row in Rows) row.RefreshTexts();
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ColorSchemesViewModel : ViewModelBase, ILanguageAware
{
    private readonly WeaselEnvironment _environment;
    private UserColorSchemeStore _store;
    private string _baseline = "";
    private SchemeRow? _selected;
    private string _statusText = "";
    private bool _isBusy;
    private string _activeSchemeId = "";
    private ColorSchemeCatalog _catalog = ColorSchemeCatalog.Empty;
    private bool _templateSourceFound;
    private string _templateSourcePath = "";

    public ColorSchemesViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        _store = new UserColorSchemeStore(environment);

        BuildOptions();

        SaveCommand = new RelayCommand(SaveAsync, () => !IsBusy && IsDirty);
        ReloadCommand = new DelegateCommand(Load);
        // 「新建」统一走 NewFromTemplate：模板下拉选了就以它为底子，没选就建空白。
        // 拆成两个按钮的话，用户每次新建都得先在下拉里选一下，多一步没必要的决策。
        NewCommand = new DelegateCommand(NewFromTemplate, () => !IsBusy);
        DuplicateCommand = new DelegateCommand(Duplicate, () => !IsBusy && Selected is not null);
        DeleteCommand = new DelegateCommand(Delete, () => !IsBusy && Selected is not null);
        ImportCommand = new DelegateCommand(Import, () => !IsBusy);
        ExportCommand = new DelegateCommand(Export, () => !IsBusy && Selected is not null);
        ClearAllCommand = new DelegateCommand(ClearAll, () => !IsBusy && Schemes.Count > 0);
        DeployCommand = new RelayCommand(DeployAsync, () => !IsBusy && environment.DeployerPath is not null);
        PickColorCommand = new DelegateCommand<ChannelRow>(PickColor, _ => !IsBusy && Selected is not null);
        ClearChannelCommand = new DelegateCommand<ChannelRow>(ClearChannel, _ => !IsBusy && Selected is not null);

        StatusText = StatusFromKey("ColorSchemes.Status.Ready");
    }

    public WeaselEnvironment Environment => _environment;

    public ObservableCollection<SchemeRow> Schemes { get; } = new();
    public ObservableCollection<ChannelRow> Channels { get; } = new();

    /// <summary>分组后的通道，界面直接绑它。分组漏键由 Core 的单测守住。</summary>
    public ObservableCollection<ChannelGroup> ChannelGroups { get; } = new();

    public ObservableCollection<ValueOption<RimeColorFormat>> FormatOptions { get; } = new();
    public ObservableCollection<ValueOption<RimeColorSpace>> SpaceOptions { get; } = new();
    public ObservableCollection<ValueOption<string>> Templates { get; } = new();

    public RelayCommand SaveCommand { get; }
    public DelegateCommand ReloadCommand { get; }
    public DelegateCommand NewCommand { get; }
    public DelegateCommand DuplicateCommand { get; }
    public DelegateCommand DeleteCommand { get; }
    public DelegateCommand ImportCommand { get; }
    public DelegateCommand ExportCommand { get; }
    public DelegateCommand ClearAllCommand { get; }
    public RelayCommand DeployCommand { get; }

    /// <summary>某个通道的「取色」按钮。参数是那一行的 ChannelRow。</summary>
    public DelegateCommand<ChannelRow> PickColorCommand { get; }

    /// <summary>某个通道的「清除」按钮：清空即交回 Rime 的回退链。</summary>
    public DelegateCommand<ChannelRow> ClearChannelCommand { get; }

    // ── 选中与编辑 ──────────────────────────────────────────────────────

    public SchemeRow? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(EditorTitle));
            RebuildChannels();
            RefreshPreview();
            RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    /// <summary>编辑区标题：跟着选中的方案名走，用户才知道自己在改哪一套。</summary>
    public string EditorTitle => Selected is null
        ? L10n.Instance.T("ColorSchemes.Editor.Empty")
        : L10n.Instance.T("ColorSchemes.Editor.Title", Selected.Name);

    /// <summary>「以此为基础新建」用的模板：内置方案 + 已有自定义方案。</summary>
    public string? TemplatePick { get; set; }

    public string NameText
    {
        get => Selected?.Scheme.Name ?? "";
        set
        {
            if (Selected is null || Selected.Scheme.Name == value) return;
            Selected.Scheme.Name = value;
            Selected.NotifyName();
            OnPropertyChanged(nameof(EditorTitle));
            MarkChanged();
        }
    }

    public string AuthorText
    {
        get => Selected?.Scheme.Author ?? "";
        set
        {
            if (Selected is null || Selected.Scheme.Author == value) return;
            Selected.Scheme.Author = value;
            MarkChanged();
        }
    }

    public RimeColorFormat Format
    {
        get => Selected?.Scheme.Format ?? RimeColorFormat.Abgr;
        set
        {
            if (Selected is null || Selected.Scheme.Format == value) return;
            var previous = Selected.Scheme.Format;
            Selected.Scheme.Format = value;

            // 换字节序要连同字面量一起换算，不能只改标记。
            // "0x112233" 在 abgr 下是 R=33 G=22 B=11，在 argb 下是 R=11 G=22 B=33 ——
            // 只改标记会让整套配色在切换字节序的瞬间全部变色，而用户并没有要求改颜色。
            foreach (var key in ColorSchemeFields.ColorKeys)
            {
                if (!Selected.Scheme.Colors.TryGetValue(key, out var literal)) continue;
                if (!RimeColor.TryParseAbgr(literal, previous, out var abgr)) continue;
                Selected.Scheme.Colors[key] = RimeColor.FromAbgr(abgr).Literal(value);
            }

            RefreshAllChannels();
            RefreshPreview();
            MarkChanged();
            OnPropertyChanged();
        }
    }

    public RimeColorSpace ColorSpace
    {
        get => Selected?.Scheme.ColorSpace ?? RimeColorSpace.Srgb;
        set
        {
            if (Selected is null || Selected.Scheme.ColorSpace == value) return;
            Selected.Scheme.ColorSpace = value;
            MarkChanged();
            OnPropertyChanged();
        }
    }

    public string IdText => Selected?.Id ?? "";

    /// <summary>当前生效的方案（style/color_scheme）。本页只读不改 —— 见文件头。</summary>
    public string ActiveSchemeText => _activeSchemeId.Length == 0
        ? L10n.Instance.T("ColorSchemes.Active.Unknown")
        : L10n.Instance.T("ColorSchemes.Active.Value", _activeSchemeId);

    public string RegistryPath => _store.RegistryPath;

    public string TemplateSourceText => _templateSourceFound
        ? _templateSourcePath
        : L10n.Instance.T("ColorSchemes.TemplateMissing");

    // ── 预览画笔（与外观页同一套取名，便于对照）────────────────────────
    private SolidColorBrush _backBrush = Brushes.White;
    public SolidColorBrush BackBrush { get => _backBrush; private set => Set(ref _backBrush, value); }

    private SolidColorBrush _textBrush = Brushes.Black;
    public SolidColorBrush TextBrush { get => _textBrush; private set => Set(ref _textBrush, value); }

    private SolidColorBrush _candidateTextBrush = Brushes.Black;
    public SolidColorBrush CandidateTextBrush { get => _candidateTextBrush; private set => Set(ref _candidateTextBrush, value); }

    private SolidColorBrush _hilitedTextBrush = Brushes.White;
    public SolidColorBrush HilitedTextBrush { get => _hilitedTextBrush; private set => Set(ref _hilitedTextBrush, value); }

    private SolidColorBrush _hilitedBackBrush = Brushes.DodgerBlue;
    public SolidColorBrush HilitedBackBrush { get => _hilitedBackBrush; private set => Set(ref _hilitedBackBrush, value); }

    private SolidColorBrush _labelBrush = Brushes.Gray;
    public SolidColorBrush LabelBrush { get => _labelBrush; private set => Set(ref _labelBrush, value); }

    private SolidColorBrush _hilitedLabelBrush = Brushes.White;
    public SolidColorBrush HilitedLabelBrush { get => _hilitedLabelBrush; private set => Set(ref _hilitedLabelBrush, value); }

    private SolidColorBrush _commentBrush = Brushes.DimGray;
    public SolidColorBrush CommentBrush { get => _commentBrush; private set => Set(ref _commentBrush, value); }

    private SolidColorBrush _borderBrush = Brushes.LightGray;
    public SolidColorBrush BorderBrush { get => _borderBrush; private set => Set(ref _borderBrush, value); }

    /// <summary>高亮候选前面的那个标记符（"*"）的颜色。外观页没这个通道，只有本页能改。</summary>
    private SolidColorBrush _hilitedMarkBrush = Brushes.White;
    public SolidColorBrush HilitedMarkBrush { get => _hilitedMarkBrush; private set => Set(ref _hilitedMarkBrush, value); }

    /// <summary>上/下翻页箭头的颜色。Rime 只在「候选多于 5 个」时才画这两个箭头，
    /// 预览里不画 —— 直接把颜色标在通道行的色块上更好懂。</summary>
    private SolidColorBrush _prevPageBrush = Brushes.Gray;
    public SolidColorBrush PrevPageBrush { get => _prevPageBrush; private set => Set(ref _prevPageBrush, value); }

    private SolidColorBrush _nextPageBrush = Brushes.Gray;
    public SolidColorBrush NextPageBrush { get => _nextPageBrush; private set => Set(ref _nextPageBrush, value); }

    // ── 预览用的字号：这一页不编辑字体，用一套固定的「像样」值让预览可看 ──
    // 名字与外观页对齐（FontPoint / FontFace / PreviewLabelFontPoint / …），
    // 这样两个页面的预览区 XAML 可以逐行对照，改一处不至于漏掉另一处。
    public int FontPoint => 15;
    public string FontFace => "Microsoft YaHei UI";
    public int PreviewLabelFontPoint => 13;
    public int PreviewCommentFontPoint => 12;
    public string PreviewMarkText => "*";
    public string PreviewLabel1 => "1.";
    public string PreviewLabel2 => "2.";
    public string PreviewLabel3 => "3.";

    // ── 状态 ────────────────────────────────────────────────────────────

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public new bool IsDirty => Signature() != _baseline;

    public bool ShowNoSchemes => Schemes.Count == 0;

    /// <summary>
    /// 任一方案里存在解析不了的字面量。
    /// </summary>
    /// <remarks>
    /// ⚠️ 必须在保存前拦一道：非法值写进 YAML 后 Rime 会把它当 0（全透明）处理，
    /// 面板上一切正常、保存也报成功，只有部署后候选窗糊成一片才会发现 ——
    /// 典型的「假成功」。宁可拦下并点名是哪一行，也不要写完才让用户自己找。
    /// </remarks>
    public bool HasInvalidChannels => InvalidChannels().Any();

    /// <summary>点名是哪几个「方案 · 通道」写错了，超过 3 个就用省略号收尾。</summary>
    public string InvalidChannelsText
    {
        get
        {
            var bad = InvalidChannels().ToList();
            if (bad.Count == 0) return "";

            var shown = bad.Take(3)
                .Select(b => L10n.Instance.T("ColorSchemes.Invalid.Item", b.Scheme, b.Channel))
                .ToList();
            if (bad.Count > shown.Count)
                shown.Add(L10n.Instance.T("ColorSchemes.Invalid.More", bad.Count - shown.Count));
            return string.Join("、", shown);
        }
    }

    private IEnumerable<(string Scheme, string Channel)> InvalidChannels()
    {
        foreach (var row in Schemes)
        foreach (var key in ColorSchemeFields.ColorKeys)
        {
            if (!row.Scheme.Colors.TryGetValue(key, out var literal)) continue;
            if (string.IsNullOrWhiteSpace(literal)) continue;
            if (RimeColor.TryParseAbgr(literal.Trim(), row.Scheme.Format, out _)) continue;
            yield return (row.Name, L10n.Instance.T("ColorSchemes.Ch." + key));
        }
    }

    public string CountText => L10n.Instance.T("ColorSchemes.Count", Schemes.Count);

    // ── 载入 ────────────────────────────────────────────────────────────

    public void Load()
    {
        var keepId = Selected?.Id;

        _store = new UserColorSchemeStore(_environment);
        LoadCatalog();
        LoadActiveScheme();

        Schemes.Clear();
        foreach (var scheme in _store.Registry.Schemes) Schemes.Add(new SchemeRow(scheme));
        SyncActiveFlag();

        RefreshTemplates();

        Selected = keepId is null ? Schemes.FirstOrDefault() : Schemes.FirstOrDefault(r => r.Id == keepId) ?? Schemes.FirstOrDefault();

        // Selected 的 setter 已经重建过通道；没选中方案时这里补一次清空
        if (Selected is null) Channels.Clear();

        OnPropertyChanged(nameof(ShowNoSchemes));
        OnPropertyChanged(nameof(CountText));
        RebindEditor();

        _baseline = Signature();
        OnPropertyChanged(nameof(IsDirty));
        RaiseCanExecuteChanged();

        StatusText = _store.Registry.IsCorrupt
            ? StatusFromKey("ColorSchemes.Status.RegistryCorrupt", _store.Registry.LoadError)
            : StatusFromKey("ColorSchemes.Status.Loaded", Schemes.Count);
    }

    /// <summary>内置配色目录：与外观页同样优先读共享目录的 weasel.yaml。</summary>
    private void LoadCatalog()
    {
        _catalog = ColorSchemeCatalog.Empty;
        _templateSourcePath = "";
        _templateSourceFound = false;

        var shared = string.IsNullOrWhiteSpace(_environment.SharedDataDirectory)
            ? null
            : Path.Combine(_environment.SharedDataDirectory, "weasel.yaml");
        if (shared is not null && File.Exists(shared))
        {
            try
            {
                _catalog = ColorSchemeCatalog.Parse(File.ReadAllText(shared));
                _templateSourcePath = shared;
                _templateSourceFound = true;
                return;
            }
            catch { /* 落到下一候选 */ }
        }

        var userWeasel = Path.Combine(_environment.UserDirectory, "weasel.yaml");
        if (File.Exists(userWeasel))
        {
            try
            {
                _catalog = ColorSchemeCatalog.Parse(File.ReadAllText(userWeasel));
                _templateSourcePath = userWeasel;
                _templateSourceFound = true;
            }
            catch { /* 忽略：没有模板只是不能「以内置方案新建」，不影响编辑已有方案 */ }
        }
    }

    /// <summary>读出当前 style/color_scheme。本页只读它，改它是外观页的事。</summary>
    private void LoadActiveScheme()
    {
        _activeSchemeId = "";
        try
        {
            RimeConfigView baseView = RimeConfigView.Empty;
            var shared = string.IsNullOrWhiteSpace(_environment.SharedDataDirectory)
                ? null
                : Path.Combine(_environment.SharedDataDirectory, "weasel.yaml");
            if (shared is not null && File.Exists(shared))
                baseView = RimeConfigView.FromYaml(File.ReadAllText(shared));

            var customPath = Path.Combine(_environment.UserDirectory, "weasel.custom.yaml");
            var custom = new CustomYamlFile(customPath);
            var merged = RimeConfigView.MergePatch(baseView, custom.Patch);
            _activeSchemeId = merged.Lookup("style/color_scheme") as string ?? "";
        }
        catch { /* 读不到就不标「当前生效」，不影响编辑 */ }
    }

    private void SyncActiveFlag()
    {
        foreach (var row in Schemes)
            row.IsActive = string.Equals(row.Id, _activeSchemeId, StringComparison.Ordinal);
    }

    // ── 通道编辑 ────────────────────────────────────────────────────────

    private void RebuildChannels()
    {
        Channels.Clear();
        ChannelGroups.Clear();
        if (Selected is null) return;

        var rows = new Dictionary<string, ChannelRow>(StringComparer.Ordinal);
        foreach (var key in ColorSchemeFields.ColorKeys)
        {
            var row = new ChannelRow(key, Selected.Scheme, OnChannelChanged);
            Channels.Add(row);
            rows[key] = row;
        }

        // 分组只做「行的重新排列」，行对象是同一批 ——
        // 这样 RefreshAllChannels 遍历 Channels 就能刷到界面上显示的那些行。
        foreach (var (group, keys) in ColorSchemeFields.Groups)
        {
            var section = new ChannelGroup(group);
            foreach (var key in keys)
                if (rows.TryGetValue(key, out var row))
                    section.Rows.Add(row);
            ChannelGroups.Add(section);
        }
    }

    private void RefreshAllChannels()
    {
        foreach (var ch in Channels) ch.Refresh();
    }

    private void OnChannelChanged()
    {
        // 改一个通道会改变回退链的下游结果：label_color 依赖
        // candidate_text_color / candidate_back_color，改后者必须让前者的「继承值」跟着变。
        RefreshAllChannels();
        RefreshPreview();
        Selected?.NotifySubtitle();
        MarkChanged();
    }

    /// <summary>打开颜色对话框改某个通道。</summary>
    public void PickColor(ChannelRow channel)
    {
        if (Selected is null) return;

        var resolved = Selected.Scheme.Resolve();
        var current = RimeColor.FromAbgr(resolved.AbgrForKey(channel.Key) ?? 0);

        var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(
                (int)Math.Round(current.Alpha * 255),
                (int)Math.Round(current.Red * 255),
                (int)Math.Round(current.Green * 255),
                (int)Math.Round(current.Blue * 255))
        };

        // ⚠️ 原生颜色对话框没有 alpha 通道（Windows 的 CHOOSECOLOR 结构里根本没有这个字段）。
        //    若原色是半透明的，直接覆写会把 alpha 悄悄丢掉 ——
        //    「半透明高亮底色」用对话框改一次就变不透明了，用户只会觉得面板坏了。
        //    所以保留原 alpha，只替换 RGB。
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var value = new RimeColor(
            dialog.Color.R / 255.0,
            dialog.Color.G / 255.0,
            dialog.Color.B / 255.0,
            current.Alpha);

        // setter 内部会回调 OnChannelChanged，这里不要再调一次（整套重解析很贵）
        channel.Literal = value.Literal(Selected.Scheme.Format);
    }

    /// <summary>清空某个通道，交回回退链。</summary>
    public void ClearChannel(ChannelRow channel) => channel.Literal = "";

    // ── 方案增删改 ──────────────────────────────────────────────────────

    private void NewBlank() => AddScheme(
        UserColorScheme.FromResolved(
            _store.Registry.UniqueId(L10n.Instance.T("ColorSchemes.DefaultName")),
            L10n.Instance.T("ColorSchemes.DefaultName"),
            ColorSchemeResolver.Resolve(_ => null)),
        select: true);

    /// <summary>以某个已有方案（内置或自定义）为模板新建。</summary>
    public void NewFromTemplate()
    {
        var name = TemplatePick;
        if (string.IsNullOrEmpty(name)) { NewBlank(); return; }

        var custom = _store.Registry.Get(name);
        if (custom is not null)
        {
            var newId = _store.Registry.UniqueId(custom.DisplayName);
            var copy = custom.CloneAs(newId, L10n.Instance.T("ColorSchemes.CopySuffix", custom.DisplayName));
            AddScheme(copy, select: true);
            return;
        }

        var resolved = _catalog.Resolve(name);
        if (resolved is null) { NewBlank(); return; }

        AddScheme(
            UserColorScheme.FromResolved(
                _store.Registry.UniqueId(name),
                L10n.Instance.T("ColorSchemes.CopySuffix", _catalog.DisplayName(name)),
                resolved),
            select: true);
    }

    private void Duplicate()
    {
        if (Selected is null) return;
        var newId = _store.Registry.UniqueId(Selected.Scheme.DisplayName);
        var copy = Selected.Scheme.CloneAs(newId,
            L10n.Instance.T("ColorSchemes.CopySuffix", Selected.Scheme.DisplayName));
        AddScheme(copy, select: true);
    }

    private void AddScheme(UserColorScheme scheme, bool select)
    {
        _store.Registry.Add(scheme);
        var row = new SchemeRow(scheme);
        Schemes.Add(row);
        RefreshTemplates();
        OnPropertyChanged(nameof(ShowNoSchemes));
        OnPropertyChanged(nameof(CountText));

        if (select) Selected = row;
        MarkChanged();
        StatusText = StatusFromKey("ColorSchemes.Status.Added", scheme.DisplayName);
    }

    private void Delete()
    {
        if (Selected is null) return;

        var name = Selected.Name;
        var answer = MessageBox.Show(
            L10n.Instance.T("ColorSchemes.ConfirmDeleteBody", name),
            L10n.Instance.T("ColorSchemes.ConfirmDeleteTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _store.Registry.Remove(Selected.Id);
        Schemes.Remove(Selected);
        Selected = Schemes.FirstOrDefault();
        RefreshTemplates();
        OnPropertyChanged(nameof(ShowNoSchemes));
        OnPropertyChanged(nameof(CountText));
        MarkChanged();
        StatusText = StatusFromKey("ColorSchemes.Status.Deleted", name);
    }

    private void ClearAll()
    {
        var answer = MessageBox.Show(
            L10n.Instance.T("ColorSchemes.ConfirmClearBody", Schemes.Count),
            L10n.Instance.T("ColorSchemes.ConfirmClearTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var path = Path.Combine(_environment.UserDirectory, "weasel.custom.yaml");
            Directory.CreateDirectory(_environment.UserDirectory);
            var custom = new CustomYamlFile(path);
            if (!custom.IsWritable)
            {
                StatusText = StatusFromKey("ColorSchemes.Status.ParseFailed", custom.LoadError);
                return;
            }
            var result = _store.ClearAll(custom);
            Load();
            StatusText = StatusFromKey("ColorSchemes.Status.Cleared", result.Removed);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("ColorSchemes.Status.WriteFailed", ex.Message);
        }
    }

    // ── 导入 / 导出 ─────────────────────────────────────────────────────

    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.Instance.T("ColorSchemes.ImportTitle"),
            Filter = L10n.Instance.T("ColorSchemes.FileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var result = _store.ImportYaml(File.ReadAllText(dialog.FileName));
            Load();
            StatusText = StatusFromKey("ColorSchemes.Status.Imported", result.Added, result.Skipped);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("ColorSchemes.Status.ImportFailed", ex.Message);
        }
    }

    private void Export()
    {
        if (Selected is null) return;

        var dialog = new SaveFileDialog
        {
            Title = L10n.Instance.T("ColorSchemes.ExportTitle"),
            Filter = L10n.Instance.T("ColorSchemes.FileFilter"),
            FileName = Selected.Id + ".yaml",
            DefaultExt = ".yaml",
            AddExtension = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName,
                UserColorSchemeStore.ExportYaml(new[] { Selected.Scheme }));
            StatusText = StatusFromKey("ColorSchemes.Status.Exported", dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("ColorSchemes.Status.ExportFailed", ex.Message);
        }
    }

    // ── 保存 ────────────────────────────────────────────────────────────

    private async Task SaveAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("ColorSchemes.Status.Writing");

        // 第一道闸：非法色值。宁可拦下并点名，也不要写进去让 Rime 当 0 处理。
        var invalid = InvalidChannelsText;
        if (invalid.Length > 0)
        {
            IsBusy = false;
            StatusText = StatusFromKey("ColorSchemes.Status.InvalidValue", invalid);
            return;
        }

        try
        {
            var result = await Task.Run(() =>
            {
                Directory.CreateDirectory(_environment.UserDirectory);
                var path = Path.Combine(_environment.UserDirectory, "weasel.custom.yaml");
                var custom = new CustomYamlFile(path);
                if (!custom.IsWritable)
                    throw new InvalidOperationException(custom.LoadError ?? "无法解析");

                // 先存注册表 JSON，再注入 YAML。顺序反过来会在 YAML 写成功、
                // JSON 写失败时留下「磁盘上有一套注册表里没有的方案」，清不掉。
                _store.Registry.Save();
                return _store.Apply(custom);
            });

            // 与「应用选项」页同理：写盘后必须回读。
            // 「与出厂相同就删键」的规则会让某些键在磁盘上消失，
            // 不回读的话界面会一直显示「已修改」，刷新后又叫「未改」，像见鬼。
            var keepId = Selected?.Id;
            Load();
            Selected = Schemes.FirstOrDefault(r => r.Id == keepId) ?? Schemes.FirstOrDefault();

            StatusText = StatusFromKey("ColorSchemes.Status.Written",
                result.Written, result.Removed, result.FilePath);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("ColorSchemes.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeployAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("ColorSchemes.Status.Deploying");
        try
        {
            var result = await Task.Run(() => ProbeService.ProbeDeployer(_environment.DeployerPath));
            StatusText = result.Status == ProbeStatus.Ok
                ? StatusFromKey("ColorSchemes.Status.DeployOk")
                : StatusFromKey("ColorSchemes.Status.DeployResult", result.Summary);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("ColorSchemes.Status.DeployException", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 预览 ────────────────────────────────────────────────────────────

    private void RefreshPreview()
    {
        var resolved = Selected?.Scheme.Resolve();
        if (resolved is null) return;

        BackBrush = ChannelRow.ToBrush(resolved.BackColor);
        TextBrush = ChannelRow.ToBrush(resolved.TextColor);
        CandidateTextBrush = ChannelRow.ToBrush(resolved.CandidateTextColor);
        HilitedTextBrush = ChannelRow.ToBrush(resolved.HilitedCandidateTextColor);
        HilitedBackBrush = ChannelRow.ToBrush(resolved.HilitedCandidateBackColor);
        LabelBrush = ChannelRow.ToBrush(resolved.LabelTextColor);
        HilitedLabelBrush = ChannelRow.ToBrush(resolved.HilitedLabelTextColor);
        CommentBrush = ChannelRow.ToBrush(resolved.CommentTextColor);
        BorderBrush = ChannelRow.ToBrush(resolved.BorderColor);
        HilitedMarkBrush = ChannelRow.ToBrush(resolved.HilitedMarkColor);
        PrevPageBrush = ChannelRow.ToBrush(resolved.PrevPageColor);
        NextPageBrush = ChannelRow.ToBrush(resolved.NextPageColor);
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    /// <summary>选中项变化后把编辑区的字段整体重取一次（选中的方案换了，值当然要跟着换）。</summary>
    private void RebindEditor()
    {
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(AuthorText));
        OnPropertyChanged(nameof(Format));
        OnPropertyChanged(nameof(ColorSpace));
        OnPropertyChanged(nameof(IdText));
        OnPropertyChanged(nameof(EditorTitle));
        RefreshPreview();
    }

    private void MarkChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasInvalidChannels));
        OnPropertyChanged(nameof(InvalidChannelsText));
        RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 全量内容签名。按内容比而不是记「改过」标记：
    /// 用户改来改去回到原值时，保存按钮应该自己熄灭。
    /// </summary>
    private string Signature()
    {
        var parts = _store.Registry.Schemes.Select(s =>
        {
            var colors = string.Join(",", ColorSchemeFields.ColorKeys.Select(k =>
                k + "=" + (s.Colors.TryGetValue(k, out var v) ? v : "")));
            return string.Join('\u0001', s.Id, s.Name, s.Author,
                ((int)s.Format).ToString(CultureInfo.InvariantCulture),
                ((int)s.ColorSpace).ToString(CultureInfo.InvariantCulture), colors);
        });
        return string.Join('\u0002', parts);
    }

    private void BuildOptions()
    {
        FormatOptions.Add(new ValueOption<RimeColorFormat>(RimeColorFormat.Abgr, ""));
        FormatOptions.Add(new ValueOption<RimeColorFormat>(RimeColorFormat.Argb, ""));
        FormatOptions.Add(new ValueOption<RimeColorFormat>(RimeColorFormat.Rgba, ""));
        SpaceOptions.Add(new ValueOption<RimeColorSpace>(RimeColorSpace.Srgb, ""));
        SpaceOptions.Add(new ValueOption<RimeColorSpace>(RimeColorSpace.DisplayP3, ""));
        RefreshOptionTexts();
    }

    private void RefreshOptionTexts()
    {
        // ⚠️ 这个函数叫 SetText 而不是 Set，是有意的：
        // tools/check_lang_keys.py 只认 T( / StatusFromKey( / Add( / SetText( 这几个
        // 调用点里的键形字面量。写成一个泛泛的 Set(...)，这 5 个键就不会被当成引用，
        // 体检会把它们报成孤儿键 —— 孤儿一多就没人看，而真正的风险是反过来的：
        // 键拼错了也查不出来，界面上静默掉回英文。
        SetText(FormatOptions, RimeColorFormat.Abgr, "ColorSchemes.Format.Abgr");
        SetText(FormatOptions, RimeColorFormat.Argb, "ColorSchemes.Format.Argb");
        SetText(FormatOptions, RimeColorFormat.Rgba, "ColorSchemes.Format.Rgba");
        SetText(SpaceOptions, RimeColorSpace.Srgb, "ColorSchemes.Space.Srgb");
        SetText(SpaceOptions, RimeColorSpace.DisplayP3, "ColorSchemes.Space.DisplayP3");

        static void SetText<T>(ObservableCollection<ValueOption<T>> list, T id, string key)
        {
            foreach (var item in list)
                if (EqualityComparer<T>.Default.Equals(item.Id, id))
                    item.Name = L10n.Instance.T(key);
        }
    }

    /// <summary>模板下拉：内置方案在前，已有自定义方案在后。</summary>
    private void RefreshTemplates()
    {
        var wanted = new List<string>();
        foreach (var n in _catalog.Names) wanted.Add(n);
        foreach (var row in Schemes) if (!wanted.Contains(row.Id)) wanted.Add(row.Id);

        if (Templates.Count == 0)
        {
            foreach (var n in wanted) Templates.Add(new ValueOption<string>(n, TemplateLabel(n)));
            return;
        }

        // 只做增量：重建集合会让下拉框先变空再回填，闪一下
        var existing = Templates.Select(t => t.Id).ToList();
        foreach (var n in wanted)
        {
            if (existing.Contains(n)) continue;
            Templates.Add(new ValueOption<string>(n, TemplateLabel(n)));
        }
        foreach (var t in Templates.ToList())
        {
            if (wanted.Contains(t.Id)) continue;
            Templates.Remove(t);
        }
        foreach (var t in Templates) t.Name = TemplateLabel(t.Id);
    }

    private string TemplateLabel(string id) =>
        _catalog.Contains(id) ? _catalog.DisplayName(id) : (_store.Registry.Get(id)?.DisplayName ?? id);

    private void RaiseCanExecuteChanged()
    {
        SaveCommand.RaiseCanExecuteChanged();
        NewCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        ClearAllCommand.RaiseCanExecuteChanged();
        DeployCommand.RaiseCanExecuteChanged();
        PickColorCommand.RaiseCanExecuteChanged();
        ClearChannelCommand.RaiseCanExecuteChanged();
    }

    public void RefreshTexts()
    {
        StatusText = Restatus();
        RefreshOptionTexts();
        RefreshTemplates();

        foreach (var row in Schemes) row.RefreshTexts();
        foreach (var group in ChannelGroups) group.RefreshTexts();

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ActiveSchemeText));
        OnPropertyChanged(nameof(TemplateSourceText));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(ShowNoSchemes));
        OnPropertyChanged(nameof(HasInvalidChannels));
        OnPropertyChanged(nameof(InvalidChannelsText));
    }
}
