using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>
/// 外观页。配色所见即所得由下方 3 列色卡网格承担（套用上游完整回退链与
/// alpha 混合，保证「面板所见」=「候选窗所得」）。候选窗预览模块已移除：
/// 它既与色卡网格重复展示同一信息，又会因拖动字体/字号滑块时整块重渲染
/// 造成交互卡顿。
/// </summary>
public sealed class AppearanceViewModel : ViewModelBase, ILanguageAware, IPanelActions
{
    private readonly string _userDirectory;
    private string _fontFace = "Microsoft YaHei";
    private int _fontPoint = 14;
    private bool _inlinePreedit;
    private int _labelFontPoint;
    private int _commentFontPoint;
    private string _labelTextFormat = "%s.";
    private string _markText = "";
    private WeaselLayoutType _layoutType;
    private WeaselPreeditType _preeditType;
    private WeaselAntiAliasMode _antiAliasMode;
    private WeaselHoverType _hoverType;
    private int _linespacing;
    private bool _isBusy;
    private string _statusText = "";
    private bool _catalogLoaded;

    public AppearanceViewModel(WeaselEnvironment environment)
    {
        _userDirectory = environment.UserDirectory;
        Environment = environment;

        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy && SelectedScheme is not null);
        ReloadCommand = new DelegateCommand(LoadAll);
        DeployCommand = new RelayCommand(DeployAsync, () => !IsBusy && environment.DeployerPath is not null);

        // ⚠️ 直写字段而非 StatusText 属性 —— 后者走 Set<T>，会把 HasUnsavedChanges
        // 翻成 true，让面板在 ctor 阶段（LoadAll 还没跑/或 MarkLoaded 尚未生效时）被部署栏
        // 误判为脏。与 Input/Behavior 的 10d022d 修法一致：ctor 是初始化，不应触发脏标记；
        // LoadAll 末尾已有 MarkLoaded() 兜底，这里再从源头避免瞬态置脏。
        _statusText = StatusFromKey("Appearance.Status.Ready");
        LoadAll();
        _ = LoadFontFamiliesAsync();
    }

    public WeaselEnvironment Environment { get; }
    public ObservableCollection<string> SchemeNames { get; } = new();

    // ── 外观页 3 列色卡网格（仿鼠须管 SchemeSwatch）──────────────
    public ObservableCollection<SchemeCardItem> SchemeCards { get; } = new();

    /// <summary>
    /// 系统字体族列表。绑到候选字体的可编辑 ComboBox，让用户既能选现成字体，
    /// 也能直接粘自己写的家族名。
    /// ⚠️ v0.2.6 关键修复：原先是 <c>static readonly</c> 在类型加载时于 UI 线程
    /// 同步枚举 <see cref="Fonts.SystemFontFamilies"/>（几百~上千字体，每个
    /// <c>.Source</c> 会触发字体元数据加载）。字体多的机器上阻塞 UI 数秒 →
    /// 面板卡顿；字体服务未就绪或个别字体 <c>.Source</c> 抛异常时整段
    /// <c>.ToArray()</c> 失败/返回空 → 「看不到系统字体」。
    /// 现改为：构造时后台线程异步枚举 + 单字体容错 + 空列表兜底常用字体，
    /// 填充完成用 Dispatcher 回 UI 线程写集合。详见 #外观字体。
    /// </summary>
    public ObservableCollection<string> FontFamilies { get; } = new();

    /// <summary>系统字体一个都拿不到时，也要保证用户至少能看到这些常用字体，下拉不为空。</summary>
    private static readonly string[] FallbackFonts =
    {
        "Microsoft YaHei", "Microsoft YaHei UI", "SimSun", "SimHei",
        "Segoe UI", "Segoe UI Variable Text", "PingFang SC",
        "Microsoft JhengHei", "Microsoft JhengHei UI", "Consolas",
    };

    /// <summary>后台线程异步枚举系统字体：避免 UI 冻结；单字体容错 + 空列表兜底。</summary>
    private async Task LoadFontFamiliesAsync()
    {
        List<string> names;
        try
        {
            // 枚举移到线程池，打开外观面板不再卡 UI。
            names = await Task.Run(() =>
            {
                var collected = new List<string>();
                try
                {
                    foreach (var f in Fonts.SystemFontFamilies)
                    {
                        try
                        {
                            var src = f.Source;
                            if (!string.IsNullOrWhiteSpace(src)) collected.Add(src);
                        }
                        catch { /* 跳过损坏/读不了的字体，不能一个坏字体拖垮整张列表 */ }
                    }
                }
                catch { /* 整个枚举失败（字体服务未就绪等）→ 落兜底 */ }
                return collected;
            });
        }
        catch
        {
            names = new List<string>();
        }

        if (names.Count == 0)
        {
            // 系统字体一个都没拿到：直接给兜底，保证下拉不为空。
            names.AddRange(FallbackFonts);
        }
        else
        {
            // 把兜底里系统列表缺失的常用字体并回去，保证它们始终可选。
            foreach (var fb in FallbackFonts)
                if (!names.Contains(fb, StringComparer.OrdinalIgnoreCase)) names.Add(fb);
        }

        var ordered = names.Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                           .ToList();

        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
            app.Dispatcher.Invoke(() => FillFontList(ordered));
        else
            FillFontList(ordered);
    }

    private void FillFontList(List<string> ordered)
    {
        FontFamilies.Clear();
        foreach (var n in ordered) FontFamilies.Add(n);
        OnPropertyChanged(nameof(FontFamilies));
    }

    private SchemeCardItem? _selectedCard;

    /// <summary>当前选中的方案卡，是选色的唯一真源。ComboBox 已移除，由网格负责选择。</summary>
    public SchemeCardItem? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (!Set(ref _selectedCard, value) || value is null) return;
            OnPropertyChanged(nameof(SelectedScheme));
        }
    }

    public RelayCommand ApplyCommand { get; }
    public DelegateCommand ReloadCommand { get; }
    public RelayCommand DeployCommand { get; }

    /// <summary>当前生效/选中的方案名。由 <see cref="SelectedCard"/> 推导，供 Apply 使用。</summary>
    public string? SelectedScheme => _selectedCard?.Name;

    public string FontFace
    {
        get => _fontFace;
        set => Set(ref _fontFace, value);
    }

    public int FontPoint
    {
        get => _fontPoint;
        set
        {
            if (!Set(ref _fontPoint, Math.Clamp(value, 8, 48))) return;
            OnPropertyChanged(nameof(PreviewCommentFontPoint));
            OnPropertyChanged(nameof(PreviewLabelFontPoint));
        }
    }

    /// <summary>
    /// 字号滑块旁展示的「有效字号」。上游 label/comment 字号为 0 时表示「未单独设置」，
    /// 实际渲染跟随主字号，故这里也回退，否则会显示成 0（不可见）。
    /// </summary>
    public int PreviewCommentFontPoint => _commentFontPoint > 0 ? _commentFontPoint : _fontPoint;
    public int PreviewLabelFontPoint => _labelFontPoint > 0 ? _labelFontPoint : _fontPoint;

    public bool InlinePreedit
    {
        get => _inlinePreedit;
        set => Set(ref _inlinePreedit, value);
    }

    public int LabelFontPoint
    {
        get => _labelFontPoint;
        set
        {
            if (!Set(ref _labelFontPoint, Math.Clamp(value, 0, 48))) return;
            OnPropertyChanged(nameof(PreviewLabelFontPoint));
        }
    }

    public int CommentFontPoint
    {
        get => _commentFontPoint;
        set
        {
            if (!Set(ref _commentFontPoint, Math.Clamp(value, 0, 48))) return;
            OnPropertyChanged(nameof(PreviewCommentFontPoint));
        }
    }

    /// <summary>序号格式（printf 风格，如 "%s."）。配置键为 style/label_format。</summary>
    public string LabelTextFormat
    {
        get => _labelTextFormat;
        set => Set(ref _labelTextFormat, value);
    }

    public string MarkText
    {
        get => _markText;
        set => Set(ref _markText, value);
    }

    public WeaselLayoutType LayoutType
    {
        get => _layoutType;
        set => Set(ref _layoutType, value);
    }

    public WeaselPreeditType PreeditType
    {
        get => _preeditType;
        set => Set(ref _preeditType, value);
    }

    public WeaselAntiAliasMode AntiAliasMode
    {
        get => _antiAliasMode;
        set => Set(ref _antiAliasMode, value);
    }

    public WeaselHoverType HoverType
    {
        get => _hoverType;
        set => Set(ref _hoverType, value);
    }

    /// <summary>候选行间距（style/layout/linespacing）。</summary>
    public int Linespacing
    {
        get => _linespacing;
        set => Set(ref _linespacing, Math.Clamp(Math.Abs(value), 0, 24));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void RefreshTexts()
    {
        // 状态栏是「赋值那一刻拼好的 string」，靠 ViewModelBase 记下的 key 重建。
        StatusText = Restatus();
    }

    // ── 加载 ──────────────────────────────────────────────────
    //
    // ⚠️ 关键不变量：`_custom`（CustomYamlFile）必须在任何「读用户 patch」步骤之前
    // 完成初始化。原版在 line 273 引用 `_custom.Patch`，却到 line 294 才赋值，导致
    // ctor → LoadAll → line 273 抛 NullReferenceException、整窗崩在
    // `MainWindow..ctor → AppearanceViewModel..ctor → LoadAll` 这条链上。
    // 同时 v0.1.14 崩溃对话框走 L10n，L10n 全死时连真正的栈都看不到，只剩裸键。
    //
    // 修复：「先 custom、后 catalog、最后 style」线性流；`_custom = new CustomYamlFile(path)`
    // 放在方法第一句，且 ctor 自带 Load()，第二次 Load() 删除。
    public void LoadAll()
    {
        SchemeNames.Clear();

        // 0) 用户自定义 patch —— 总是先初始化。后续 catalog / style 都依赖它。
        var customPath = Path.Combine(_userDirectory, "weasel.custom.yaml");
        _custom = new CustomYamlFile(customPath);   // ctor 内自带 Load()

        // 1) 内置配色目录：优先共享数据的 weasel.yaml（随用户本机版本自动更新）
        ColorSchemeCatalog catalog = ColorSchemeCatalog.Empty;
        string? source = null;
        var sharedWeasel = string.IsNullOrWhiteSpace(Environment.SharedDataDirectory)
            ? null
            : Path.Combine(Environment.SharedDataDirectory, "weasel.yaml");
        if (sharedWeasel is not null && File.Exists(sharedWeasel))
        {
            try
            {
                catalog = ColorSchemeCatalog.Parse(File.ReadAllText(sharedWeasel));
                source = sharedWeasel;
            }
            catch { /* 落到下一候选 */ }
        }

        if (catalog.Names.Count == 0)
        {
            var userWeasel = Path.Combine(_userDirectory, "weasel.yaml");
            if (File.Exists(userWeasel))
            {
                try
                {
                    catalog = ColorSchemeCatalog.Parse(File.ReadAllText(userWeasel));
                    source = userWeasel;
                }
                catch { /* 忽略 */ }
            }
        }

        // 3) 把用户自定义配色并入目录。
        //    没这一步的话，「自定义配色」页建出来的方案虽然已经写进 YAML，
        //    下拉框（只解析 weasel.yaml）里却看不到它 —— 建了也选不上。
        catalog = catalog.Appending(PresetColorSchemes.Extract(_custom.Patch));

        foreach (var n in catalog.Names) SchemeNames.Add(n);
        _catalog = catalog;
        _catalogLoaded = catalog.Names.Count > 0;

        // 2) 当前生效值 = 共享 weasel.yaml（出厂）+ 用户 patch 合并后的**派生结果**。
        //    ⚠️ 这里不能只读 weasel.custom.yaml 的原值：小狼毫渲染用的是派生值，
        //    二者会不一致（例：font_point 没配过时配置里是空，实际生效是 12）。
        //    面板若显示原值，用户会以为「没生效」。
        RimeConfigView baseView = RimeConfigView.Empty;
        if (sharedWeasel is not null && File.Exists(sharedWeasel))
        {
            try { baseView = RimeConfigView.FromYaml(File.ReadAllText(sharedWeasel)); }
            catch { /* 解析失败则退化为出厂空视图 */ }
        }

        // _custom 已在方法开头初始化并 Load（见上「⚠️ 关键不变量」注释）。
        // 任何后续对 _custom.Patch 的引用都是安全的。
        var merged = RimeConfigView.MergePatch(baseView, _custom.Patch);
        var style = WeaselStyleResolver.ResolveGlobal(merged);

        _fontFace = style.FontFace;
        _fontPoint = style.FontPoint;
        _labelFontPoint = style.LabelFontPoint;
        _commentFontPoint = style.CommentFontPoint;
        _labelTextFormat = style.LabelTextFormat;
        _markText = style.MarkText;
        _inlinePreedit = style.InlinePreedit;
        _layoutType = style.LayoutType;
        _preeditType = style.PreeditType;
        _antiAliasMode = style.AntiAliasMode;
        _hoverType = style.HoverType;
        _linespacing = style.Linespacing;

        OnPropertyChanged(nameof(FontFace));
        OnPropertyChanged(nameof(FontPoint));
        OnPropertyChanged(nameof(LabelFontPoint));
        OnPropertyChanged(nameof(CommentFontPoint));
        OnPropertyChanged(nameof(LabelTextFormat));
        OnPropertyChanged(nameof(MarkText));
        OnPropertyChanged(nameof(InlinePreedit));
        OnPropertyChanged(nameof(LayoutType));
        OnPropertyChanged(nameof(PreeditType));
        OnPropertyChanged(nameof(AntiAliasMode));
        OnPropertyChanged(nameof(HoverType));
        OnPropertyChanged(nameof(Linespacing));

        // color_scheme 属于颜色层，不在 WeaselStyle 里，单独从合并视图取
        var scheme = merged.Lookup("style/color_scheme") as string ?? "aqua";

        // 先建色卡网格（依赖 _catalog），再据生效方案选中对应卡
        BuildSchemeCards(scheme);

        // 选中生效方案对应卡；目录里没这个名字，仍保留（可能是自定义方案）
        if (!_catalogLoaded || _catalog.Contains(scheme))
        {
            _selectedCard = SchemeCards.FirstOrDefault(c => c.Name == scheme) ?? SchemeCards.FirstOrDefault();
        }
        else if (SchemeCards.Count > 0)
        {
            _selectedCard = SchemeCards[0];
        }

        OnPropertyChanged(nameof(SelectedCard));
        OnPropertyChanged(nameof(SelectedScheme));

        StatusText = _catalogLoaded
            ? StatusFromKey("Appearance.Status.LoadedSchemes", SchemeNames.Count)
            : StatusFromKey("Appearance.Status.NoCatalog");

        MarkLoaded();
    }

    /// <summary>据当前目录构建 3 列色卡网格的数据（所见即所得，走完整回退链）。</summary>
    private void BuildSchemeCards(string effectiveScheme)
    {
        SchemeCards.Clear();
        foreach (var name in _catalog.Names)
        {
            var resolved = _catalog.Resolve(name);
            if (resolved is null) continue;
            SchemeCards.Add(new SchemeCardItem(
                name,
                isActive: name == effectiveScheme,
                ToBrush(resolved.BackColor),
                ToBrush(resolved.BorderColor),
                ToBrush(resolved.TextColor),
                ToBrush(resolved.CandidateTextColor),
                ToBrush(resolved.HilitedCandidateBackColor),
                ToBrush(resolved.HilitedCandidateTextColor),
                ToBrush(resolved.LabelTextColor),
                ToBrush(resolved.CommentTextColor)));
        }
    }

    private ColorSchemeCatalog _catalog = ColorSchemeCatalog.Empty;

    /// <summary>用户的 weasel.custom.yaml，供「应用」时复用（保持已加载状态）。</summary>
    private CustomYamlFile? _custom;

    /// <summary>ABGR 字面量 → WPF 画笔。RimeColor 内部已处理字节序与 alpha。</summary>
    private static SolidColorBrush ToBrush(uint abgr)
    {
        var c = RimeColor.FromAbgr(abgr);
        byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0d, 1d) * 255d);
        var color = Color.FromArgb(B(c.Alpha), B(c.Red), B(c.Green), B(c.Blue));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ── 枚举 → 配置串 ──────────────────────────────────────────────────
    // 一律显式映射，不用 ToLowerInvariant()：PreviewAll 会变成 "previewall"，
    // 而小狼毫认的是 "preview_all" —— 这类错误编译期发现不了。

    private static string ToConfigName(WeaselPreeditType v) => v switch
    {
        WeaselPreeditType.Preview => "preview",
        WeaselPreeditType.PreviewAll => "preview_all",
        _ => "composition",
    };

    private static string ToConfigName(WeaselAntiAliasMode v) => v switch
    {
        WeaselAntiAliasMode.ForceDword => "force_dword",
        WeaselAntiAliasMode.ClearType => "cleartype",
        WeaselAntiAliasMode.Grayscale => "grayscale",
        WeaselAntiAliasMode.Aliased => "aliased",
        _ => "default",
    };

    private static string ToConfigName(WeaselHoverType v) => v switch
    {
        WeaselHoverType.SemiHilite => "semi_hilite",
        WeaselHoverType.Hilite => "hilite",
        _ => "none",
    };

    /// <summary>
    /// 布局类型一律写 style/layout/type —— 它在 5 步覆盖链中优先级最高，
    /// 是唯一能无歧义表达全部 5 种布局的键（写 horizontal/vertical_text
    /// 这类 bool 键会遇到「写 false 取消不掉」的问题）。
    /// </summary>
    private static string LayoutTypeToConfig(WeaselLayoutType v) => v switch
    {
        WeaselLayoutType.Horizontal => "horizontal",
        WeaselLayoutType.VerticalText => "vertical_text",
        WeaselLayoutType.VerticalFullscreen => "vertical+fullscreen",
        WeaselLayoutType.HorizontalFullscreen => "horizontal+fullscreen",
        _ => "vertical",
    };

    // ── 应用 ──────────────────────────────────────────────────
    public async Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Appearance.Status.Writing");
        try
        {
            Directory.CreateDirectory(_userDirectory);
            var path = Path.Combine(_userDirectory, "weasel.custom.yaml");
            // ⚠️ 必须用「磁盘最新态」重新读取，不能用 LoadAll 缓存的 _custom 旧快照做 Save()：
            // weasel.custom.yaml 同时被本页（style/*）、行为页（show_notifications 等）、
            // 配色页（preset_color_schemes/*）三个面板写。若用旧快照整文件重写，
            // 会把其它面板已写入的键覆盖掉 —— 表现为「改了 A 功能、B 功能配置丢失」。
            // 每次 apply 都重新读盘最稳（行为页、配色页本就如此，这里对齐）。
            var custom = new CustomYamlFile(path);
            if (custom.State == CustomYamlLoadState.Absent) custom.Load();

            if (!custom.IsWritable)
            {
                StatusText = StatusFromKey("Appearance.Status.ParseFailed", custom.LoadError);
                return;
            }

            if (SelectedScheme is not null) custom.Set("style/color_scheme", SelectedScheme);
            custom.Set("style/font_face", FontFace);
            custom.Set("style/font_point", FontPoint);
            custom.Set("style/label_font_point", LabelFontPoint);
            custom.Set("style/comment_font_point", CommentFontPoint);

            // ⚠️ 键名是 label_format，不是字段名 label_text_format
            custom.Set("style/label_format", LabelTextFormat);
            custom.Set("style/mark_text", MarkText);

            custom.Set("style/preedit_type", ToConfigName(PreeditType));
            custom.Set("style/antialias_mode", ToConfigName(AntiAliasMode));
            custom.Set("style/hover_type", ToConfigName(HoverType));
            custom.Set("style/inline_preedit", InlinePreedit);

            // 布局类型写 style/layout/type —— 它在 5 步覆盖链中优先级最高，
            // 是唯一能无歧义表达全部 5 种布局的键。
            custom.Set("style/layout/type", LayoutTypeToConfig(LayoutType));
            custom.Set("style/layout/linespacing", Linespacing);

            custom.Save();

            // ⚠️ 写盘后回读校验：确认 style/color_scheme 真的落到磁盘。
            // 否则「点了应用、候选窗没变、以为没保存」类的静默失败会变成明确报错，
            // 而不是让用户反复怀疑面板坏了。回读紧接 Save 之后、部署之前，
            // 此时 Weasel 尚未触碰该文件，不会因锁文件产生误报。
            var verify = new CustomYamlFile(path);
            if (!verify.IsWritable ||
                (SelectedScheme is not null && verify.StringForPath("style/color_scheme") != SelectedScheme))
            {
                StatusText = StatusFromKey("Appearance.Status.WriteFailed", "style/color_scheme 未落盘");
                return;
            }

            StatusText = StatusFromKey("Appearance.Status.Written", path);

            MarkLoaded();
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Appearance.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeployAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Appearance.Status.Deploying");
        try
        {
            var result = await Task.Run(() => ProbeService.ProbeDeployer(Environment.DeployerPath));
            StatusText = result.Status == ProbeStatus.Ok
                ? StatusFromKey("Appearance.Status.DeployOk")
                : StatusFromKey("Appearance.Status.DeployResult", result.Summary);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Appearance.Status.DeployException", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task ReloadAsync()
    {
        LoadAll();
        return Task.CompletedTask;
    }
}
