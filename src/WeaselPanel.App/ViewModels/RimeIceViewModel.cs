//
//  RimeIceViewModel.cs — 雾凇拼音配置页
//
//  ── 这一页管哪些键 ──────────────────────────────────────────────────
//  rime_ice.custom.yaml（6 个三态开关 / 词库 / 繁简 / Lua 滤镜 / 模糊音 /
//  speller 代数），外加 default.custom.yaml 的 switcher/save_options。
//  纠错（紫毫）模型的状态也寄存在同一份 yaml 里，但**本页不暴露它** —— 纠错是
//  独立的一页（紫毫纠错），两页都去写 CorrectionEnabled 会互相覆盖、UI 与磁盘脱钩。
//
//  ── 状态以 ViewModel 为权威，写盘前回灌 Core ──────────────────────────
//  RimeIceConfig 的字段是普通自动属性、不实现 INPC，勾一下不会通知外层 →
//  「应用」按钮永远点不亮。所以本 VM 自持一组可观察行/开关，Apply 时整体回灌
//  RimeIceConfig 再 WritePatch()，避免为每个字段都写一对代理属性。
//
//  ── 未安装：整段置灰但不藏 ────────────────────────────────────────────
//  没装 rime_ice.schema.yaml 时 IsInstalled=false：界面照常把 6 个开关展示出来
//  （让用户先看清面板提供哪些能力），但每个控件 IsEnabled={Binding IsInstalled}，
//  且本页任何路径都不会落盘（RimeIceConfig 在 !IsInstalled 时直接 return）。
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>基础开关行：Name 是配置键名，Mode 是三态（记忆 / 开 / 关）。</summary>
public sealed class RimeIceSwitchRow : INotifyPropertyChanged
{
    private SwitchDefaultMode _mode;

    public RimeIceSwitchRow(string name, string statesText, SwitchDefaultMode mode)
    {
        Name = name;
        StatesText = statesText;
        _mode = mode;
    }

    public string Name { get; }
    public string StatesText { get; }

    /// <summary>标题：ascii_mode 这类名字要本地化成「中英切换」，切语言时由 VM 改写。</summary>
    public string TitleText { get; set; } = "";

    public SwitchDefaultMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeName));
        }
    }

    public string ModeName { get; set; } = "";

    /// <summary>三态下拉项（记忆 / 开 / 关），每行的下拉都绑它。语言切换时由 RefreshTexts 刷新 Name。</summary>
    public ObservableCollection<ValueOption<SwitchDefaultMode>> ModeOptions { get; } =
    [
        new(SwitchDefaultMode.Remember, ""),
        new(SwitchDefaultMode.On, ""),
        new(SwitchDefaultMode.Off, ""),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshTexts()
    {
        TitleText = L10n.Instance.T(SwitchTitleKey(Name));
        foreach (var o in ModeOptions)
            o.Name = L10n.Instance.T("RimeIce.Mode." + o.Id switch
            {
                SwitchDefaultMode.Remember => "Remember",
                SwitchDefaultMode.On => "On",
                _ => "Off",
            });
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(ModeOptions));
    }

    public static string SwitchTitleKey(string name) => "RimeIce.Switch." + name;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Lua 滤镜行。</summary>
public sealed class RimeIceLuaRow : INotifyPropertyChanged
{
    private bool _isOn;

    public RimeIceLuaRow(string key, bool isOn)
    {
        Key = key;
        _isOn = isOn;
    }

    public string Key { get; }

    public string TitleText { get; set; } = "";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshTexts()
    {
        TitleText = L10n.Instance.T("RimeIce.Lua." + Key);
        OnPropertyChanged(nameof(TitleText));
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>单条模糊音规则行。</summary>
public sealed class RimeIceFuzzyRow : INotifyPropertyChanged
{
    private bool _isOn;

    public RimeIceFuzzyRow(string rule, string label, FuzzyRuleGroup group, bool isOn)
    {
        Rule = rule;
        Label = label;
        Group = group;
        _isOn = isOn;
    }

    public string Rule { get; }
    public string Label { get; }
    public FuzzyRuleGroup Group { get; }

    /// <summary>被纠错强制注入（与纠错规则原文重叠且纠错已开）：界面显示勾选且禁用。</summary>
    public bool Locked { get; set; }

    /// <summary>给 XAML 的 IsEnabled 直接用 —— 不要在 IsEnabled 上套 Visibility 转换器。</summary>
    public bool NotLocked => !Locked;

    public bool IsOn
    {
        get => _isOn || Locked;
        set
        {
            if (Locked) return;
            if (_isOn == value) return;
            _isOn = value;
            OnPropertyChanged();
        }
    }

    /// <summary>用户实际勾选值（不含纠错强制），回灌 Core 时用它。</summary>
    public bool IsOnRaw => _isOn;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>模糊音分组（声母 / 韵母 / 音节），界面按组渲染。</summary>
public sealed class RimeIceFuzzyGroup : INotifyPropertyChanged
{
    public RimeIceFuzzyGroup(FuzzyRuleGroup group)
    {
        Group = group;
        Rows = new ObservableCollection<RimeIceFuzzyRow>();
        // 组头的「已启用 N 项」要跟着勾选实时走，否则用户勾了半屏计数还停在载入时的值。
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (var it in e.NewItems?.Cast<RimeIceFuzzyRow>() ?? Enumerable.Empty<RimeIceFuzzyRow>())
                it.PropertyChanged += OnRowChanged;
            foreach (var it in e.OldItems?.Cast<RimeIceFuzzyRow>() ?? Enumerable.Empty<RimeIceFuzzyRow>())
                it.PropertyChanged -= OnRowChanged;
        };
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RimeIceFuzzyRow.IsOn)) return;
        RefreshCount();
    }

    /// <summary>只刷计数，不动标题 —— 勾选时走这条，切语言时才走 RefreshTexts。</summary>
    public void RefreshCount()
    {
        CountText = L10n.Instance.T("RimeIce.Fuzzy.Count", Rows.Count(r => r.IsOn));
        OnPropertyChanged(nameof(CountText));
    }

    public FuzzyRuleGroup Group { get; }
    public ObservableCollection<RimeIceFuzzyRow> Rows { get; }

    public string TitleText { get; set; } = "";
    public string CountText { get; set; } = "";

    public void RefreshTexts()
    {
        TitleText = L10n.Instance.T("RimeIce.Fuzzy.Group." + Group switch
        {
            FuzzyRuleGroup.Initials => "Initials",
            FuzzyRuleGroup.Finals => "Finals",
            _ => "Syllables",
        });
        OnPropertyChanged(nameof(TitleText));
        // 规则文本（zh → z）本身与语言无关，行内没有需要重译的字，只刷计数即可。
        RefreshCount();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class RimeIceViewModel : ViewModelBase, ILanguageAware
{
    private readonly WeaselEnvironment _environment;
    private RimeIceConfig _config;

    private bool _isBusy;
    private string _statusText = "";
    private string _baseline = "";
    private string _rawText = "";
    private string? _rawError;

    public RimeIceViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        _config = new RimeIceConfig(environment);

        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy && IsDirty);
        ReloadCommand = new DelegateCommand(Load);

        SwitchRows = new ObservableCollection<RimeIceSwitchRow>();
        LuaRows = new ObservableCollection<RimeIceLuaRow>();
        FuzzyGroups = new ObservableCollection<RimeIceFuzzyGroup>();
        OpenccOptions = new ObservableCollection<ValueOption<string>>();

        StatusText = StatusFromKey("RimeIce.Status.Ready");
    }

    public WeaselEnvironment Environment => _environment;

    // ── 集合 ──────────────────────────────────────────────────────────
    public ObservableCollection<RimeIceSwitchRow> SwitchRows { get; }
    public ObservableCollection<RimeIceLuaRow> LuaRows { get; }
    public ObservableCollection<RimeIceFuzzyGroup> FuzzyGroups { get; }
    public ObservableCollection<ValueOption<string>> OpenccOptions { get; }

    public RelayCommand ApplyCommand { get; }
    public DelegateCommand ReloadCommand { get; }

    // ── 原子状态（代理到 _config，供 XAML 双向绑）────────────────────────
    public bool IsInstalled => _config.IsInstalled;
    public bool IsDoublePinyinActive => _config.IsDoublePinyinActive;

    public bool EnableMeltEng
    {
        get => _config.EnableMeltEng;
        set { _config.EnableMeltEng = value; OnPropertyChanged(); MarkChanged(); }
    }

    public bool EnableCnEn
    {
        get => _config.EnableCnEn;
        set { _config.EnableCnEn = value; OnPropertyChanged(); MarkChanged(); }
    }

    public bool EnableRadical
    {
        get => _config.EnableRadical;
        set { _config.EnableRadical = value; OnPropertyChanged(); MarkChanged(); }
    }

    public bool EnableEmojiDict
    {
        get => _config.EnableEmojiDict;
        set { _config.EnableEmojiDict = value; OnPropertyChanged(); MarkChanged(); }
    }

    public string Opencc
    {
        get => _config.Opencc;
        set { _config.Opencc = value; OnPropertyChanged(); MarkChanged(); }
    }

    public string ActiveSchemaText => ActiveSchemaTitle();

    public bool ShowRawDoubleCode
    {
        get => _config.ShowRawDoubleCode;
        set { _config.ShowRawDoubleCode = value; OnPropertyChanged(); MarkChanged(); }
    }

    // ── 原始 YAML ─────────────────────────────────────────────────────
    public string RawText
    {
        get => _rawText;
        set
        {
            if (_rawText == value) return;
            _rawText = value;
            _rawError = _config.ValidateRawIce(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RawError));
            OnPropertyChanged(nameof(RawHasError));
            MarkChanged();
        }
    }

    public string? RawError => _rawError;
    public bool RawHasError => _rawError is not null;

    // ── 状态条 / 忙 ──────────────────────────────────────────────────
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) ApplyCommand.RaiseCanExecuteChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        // ⚠️ 必须走 Set()：早期写成裸赋值，结果「已写入 / 写入失败」全都停在初始文案，
        // 用户点了应用看不到任何反馈，误以为按钮没响应。
        private set => Set(ref _statusText, value);
    }

    public string FilePath => _config.IceCustomPath;

    // ── 脏值 ──────────────────────────────────────────────────────────
    public new bool IsDirty => Signature() != _baseline;

    private string Signature()
    {
        var switches = string.Join("|", SwitchRows.Select(r => r.Name + ":" + (int)r.Mode));
        var lua = string.Join("|", LuaRows.Select(r => r.Key + ":" + (r.IsOn ? 1 : 0)));
        // 与回灌保持同一口径：脏值只看用户自己勾的（IsOnRaw），否则纠错强制项会让
        // 「一进页面就是脏的」—— 应用按钮无缘无故点亮。
        var fuzzy = string.Join("|", FuzzyGroups.SelectMany(g => g.Rows)
            .OrderBy(r => r.Rule).Select(r => r.Rule + ":" + (r.IsOnRaw ? 1 : 0)));
        return string.Join("#",
            switches,
            EnableMeltEng ? 1 : 0, EnableCnEn ? 1 : 0, EnableRadical ? 1 : 0, EnableEmojiDict ? 1 : 0,
            Opencc,
            IsDoublePinyinActive ? (ShowRawDoubleCode ? 1 : 0) : -1,
            lua,
            fuzzy,
            RawText.GetHashCode().ToString(CultureInfo.InvariantCulture));
    }

    private void MarkChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        ApplyCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 把行对象的变更接到 VM 的脏值上。行是自持 INPC 的独立对象，不接这一手，
    /// 界面上勾了半天「应用」按钮仍是灰的。返回同一实例，便于写成 Add(Track(new ...))。
    /// </summary>
    private T Track<T>(T row) where T : INotifyPropertyChanged
    {
        row.PropertyChanged += (_, _) => MarkChanged();
        return row;
    }

    // ── 载入 ──────────────────────────────────────────────────────────
    public void Load()
    {
        _config = new RimeIceConfig(_environment);
        SwitchRows.Clear();
        LuaRows.Clear();
        FuzzyGroups.Clear();

        // ⚠️ 行是独立的 INPC 对象，它们变了 VM 自己不会知道 —— 不挂这个订阅，
        // 用户改三态下拉 / 勾 Lua / 勾模糊音，「应用」按钮会一直是灰的（点不动）。
        foreach (var s in _config.Switches)
            SwitchRows.Add(Track(new RimeIceSwitchRow(s.Name, string.Join(" / ", s.States), s.Mode)));

        foreach (var k in RimeIceConfig.LuaFilterKeys)
            LuaRows.Add(Track(new RimeIceLuaRow(k, _config.LuaFilters.TryGetValue(k, out var on) && on)));

        foreach (var group in new[] { FuzzyRuleGroup.Initials, FuzzyRuleGroup.Finals, FuzzyRuleGroup.Syllables })
        {
            var g = new RimeIceFuzzyGroup(group);
            foreach (var r in RimeIceConfig.FuzzyRules.Where(r => r.Group == group))
                g.Rows.Add(Track(new RimeIceFuzzyRow(r.Rule, r.Label, group, _config.FuzzySelection.Contains(r.Rule))
                {
                    Locked = _config.IsFuzzyRuleForcedByCorrection(r.Rule),
                }));
            FuzzyGroups.Add(g);
        }

        OpenccOptions.Clear();
        foreach (var o in RimeIceConfig.OpenccOptions)
            OpenccOptions.Add(new ValueOption<string>(o, ""));
        _rawText = _config.RawIceText();
        _rawError = null;

        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsDoublePinyinActive));
        OnPropertyChanged(nameof(EnableMeltEng));
        OnPropertyChanged(nameof(EnableCnEn));
        OnPropertyChanged(nameof(EnableRadical));
        OnPropertyChanged(nameof(EnableEmojiDict));
        OnPropertyChanged(nameof(Opencc));
        OnPropertyChanged(nameof(ShowRawDoubleCode));
        OnPropertyChanged(nameof(ActiveSchemaText));
        OnPropertyChanged(nameof(RawText));
        OnPropertyChanged(nameof(RawError));
        OnPropertyChanged(nameof(RawHasError));
        OnPropertyChanged(nameof(FilePath));

        RefreshTexts();
        _baseline = Signature();
        OnPropertyChanged(nameof(IsDirty));
        ApplyCommand.RaiseCanExecuteChanged();

        StatusText = IsInstalled
            ? StatusFromKey("RimeIce.Status.Ready")
            : StatusFromKey("RimeIce.Status.NotInstalled");
    }

    // ── 写盘 ──────────────────────────────────────────────────────────
    private async Task ApplyAsync()
    {
        if (!IsInstalled)
        {
            StatusText = StatusFromKey("RimeIce.Status.NotInstalled");
            return;
        }
        if (RawHasError)
        {
            StatusText = StatusFromKey("RimeIce.Status.InvalidRaw");
            return;
        }

        IsBusy = true;
        StatusText = StatusFromKey("RimeIce.Status.Writing");
        try
        {
            // 先把 VM 状态回灌 Core（纠错模型不在此页改，保持磁盘现状）
            _config.Switches = SwitchRows.Select(r =>
                new RimeIceSwitchItem(r.Name, r.StatesText.Split('/').Select(x => x.Trim()).ToList(), null, r.Mode)).ToList();
            _config.LuaFilters = LuaRows.ToDictionary(r => r.Key, r => r.IsOn, StringComparer.Ordinal);
            // ⚠️ 用 IsOnRaw 而不是 IsOn：被纠错强制勾上的规则界面显示已勾，但那是纠错带来的，
            // 不是用户主动选的。若按 IsOn 回灌，日后用户关掉纠错，这批规则会以「用户选过」
            // 的身份继续留在 speller/algebra 里，删不掉也说不清出处。
            _config.FuzzySelection = new HashSet<string>(
                FuzzyGroups.SelectMany(g => g.Rows).Where(r => r.IsOnRaw).Select(r => r.Rule), StringComparer.Ordinal);

            // 原始 YAML 若有改动，先按原始文本落盘（它会重载 Core），其余开关随后整段写补丁。
            if (RawText != _config.RawIceText())
            {
                _config.SaveRawIce(RawText);
                _config = new RimeIceConfig(_environment);
                // 回灌一次：SaveRawIce 已 Reload，VM 集合需与磁盘重新对齐
                RebuildFromConfig();
            }

            _config.WritePatch(null);
            _baseline = Signature();
            OnPropertyChanged(nameof(IsDirty));
            StatusText = StatusFromKey("RimeIce.Status.Saved");
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("RimeIce.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>把现有 VM 集合按当前 _config 重新对齐（SaveRawIce 后调用，避免重复 Load 抖动画面）。</summary>
    private void RebuildFromConfig()
    {
        for (var i = 0; i < SwitchRows.Count; i++)
        {
            var tpl = _config.Switches.FirstOrDefault(s => s.Name == SwitchRows[i].Name);
            if (tpl is not null) SwitchRows[i].Mode = tpl.Mode;
        }
        foreach (var r in LuaRows)
            if (_config.LuaFilters.TryGetValue(r.Key, out var on)) r.IsOn = on;
        foreach (var g in FuzzyGroups)
            foreach (var r in g.Rows)
                r.IsOn = _config.FuzzySelection.Contains(r.Rule);
    }

    private string ActiveSchemaTitle()
    {
        var id = _config.ActivePinyinSchemaId;
        if (id == "rime_ice") return L10n.Instance.T("RimeIce.Pinyin.Full");
        return L10n.Instance.T("RimeIce.Pinyin.Double", id);
    }

    // ── 语言切换 ──────────────────────────────────────────────────────
    public void RefreshTexts()
    {
        foreach (var r in SwitchRows) r.RefreshTexts();
        foreach (var r in LuaRows) r.RefreshTexts();
        foreach (var g in FuzzyGroups) g.RefreshTexts();
        foreach (var o in OpenccOptions) o.Name = L10n.Instance.T("RimeIce.Opencc." + o.Id.Replace(".json", ""));
        OnPropertyChanged(nameof(ActiveSchemaText));
        if (HasStatusKey) StatusText = Restatus();
    }
}
