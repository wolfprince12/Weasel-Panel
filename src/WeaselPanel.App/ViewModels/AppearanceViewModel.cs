using System.Collections.ObjectModel;
using System.Windows.Media;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>
/// 外观页。预览用的颜色一律走 Core 的 <see cref="ColorSchemeResolver"/>，
/// 即套用上游完整回退链与 alpha 混合 —— 保证「面板所见」=「候选窗所得」。
/// </summary>
public sealed class AppearanceViewModel : ViewModelBase
{
    private readonly string _userDirectory;
    private string? _selectedScheme;
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
    private bool _isBusy;
    private string _statusText = "就绪";
    private bool _catalogLoaded;

    public AppearanceViewModel(WeaselEnvironment environment)
    {
        _userDirectory = environment.UserDirectory;
        Environment = environment;

        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy && SelectedScheme is not null);
        ReloadCommand = new DelegateCommand(LoadAll);
        DeployCommand = new RelayCommand(DeployAsync, () => !IsBusy && environment.DeployerPath is not null);

        LoadAll();
    }

    public WeaselEnvironment Environment { get; }
    public ObservableCollection<string> SchemeNames { get; } = new();

    public RelayCommand ApplyCommand { get; }
    public DelegateCommand ReloadCommand { get; }
    public RelayCommand DeployCommand { get; }

    public string? SelectedScheme
    {
        get => _selectedScheme;
        set
        {
            if (Set(ref _selectedScheme, value)) RefreshPreview();
        }
    }

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
    /// 预览用的注释字号。上游 label/comment 字号为 0 时表示「未单独设置」，
    /// 实际渲染跟随主字号，故预览也必须回退，否则会显示成 0（不可见）。
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
        set
        {
            if (!Set(ref _labelTextFormat, value)) return;
            OnPropertyChanged(nameof(PreviewLabel1));
            OnPropertyChanged(nameof(PreviewLabel2));
            OnPropertyChanged(nameof(PreviewLabel3));
        }
    }

    /// <summary>高亮候选前的标记符。空串在小狼毫中等价于 "*"。</summary>
    public string MarkText
    {
        get => _markText;
        set
        {
            if (Set(ref _markText, value)) OnPropertyChanged(nameof(PreviewMarkText));
        }
    }

    /// <summary>预览用的标记符：空串按上游语义兜底为 "*"。</summary>
    public string PreviewMarkText => _markText.Length == 0 ? "*" : _markText;

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

    /// <summary>
    /// 预览里的序号文本。把 printf 风格的 "%s" 替换为实际序号。
    /// 上游用 swprintf_s 套用该格式（RimeWithWeasel.cpp:857）。
    /// </summary>
    public string PreviewLabel(string ordinal) =>
        _labelTextFormat.Contains("%s", StringComparison.Ordinal)
            ? _labelTextFormat.Replace("%s", ordinal)
            : _labelTextFormat;

    // 预览用的三个序号（WPF 不能绑定带参数的方法，故展开为属性）
    public string PreviewLabel1 => PreviewLabel("1");
    public string PreviewLabel2 => PreviewLabel("2");
    public string PreviewLabel3 => PreviewLabel("3");

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

    // ── 预览画笔 ──────────────────────────────────────────────
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

    public string CatalogSource { get; private set; } = "（未加载）";

    // ── 加载 ──────────────────────────────────────────────────
    public void LoadAll()
    {
        SchemeNames.Clear();

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

        foreach (var n in catalog.Names) SchemeNames.Add(n);
        _catalog = catalog;
        CatalogSource = source ?? "（未找到 weasel.yaml，配色目录为空）";
        OnPropertyChanged(nameof(CatalogSource));
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

        var customPath = Path.Combine(_userDirectory, "weasel.custom.yaml");
        _custom = new CustomYamlFile(customPath);
        if (File.Exists(customPath))
        {
            try { _custom.Load(); } catch { /* 解析失败则按出厂值处理 */ }
        }

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

        OnPropertyChanged(nameof(FontFace));
        OnPropertyChanged(nameof(FontPoint));
        OnPropertyChanged(nameof(LabelFontPoint));
        OnPropertyChanged(nameof(CommentFontPoint));
        OnPropertyChanged(nameof(LabelTextFormat));
        OnPropertyChanged(nameof(MarkText));
        OnPropertyChanged(nameof(PreviewMarkText));
        OnPropertyChanged(nameof(InlinePreedit));
        OnPropertyChanged(nameof(LayoutType));
        OnPropertyChanged(nameof(PreeditType));
        OnPropertyChanged(nameof(AntiAliasMode));
        OnPropertyChanged(nameof(HoverType));

        // color_scheme 属于颜色层，不在 WeaselStyle 里，单独从合并视图取
        var scheme = merged.Lookup("style/color_scheme") as string ?? "aqua";

        // 先选中再刷新预览；若目录里没这个名字，仍保留（可能是自定义方案）
        if (!_catalogLoaded || _catalog.Contains(scheme))
        {
            _selectedScheme = scheme;
            OnPropertyChanged(nameof(SelectedScheme));
        }
        else if (SchemeNames.Count > 0)
        {
            _selectedScheme = SchemeNames[0];
            OnPropertyChanged(nameof(SelectedScheme));
        }

        RefreshPreview();

        StatusText = _catalogLoaded
            ? $"已加载 {SchemeNames.Count} 套内置配色"
            : "未能加载内置配色目录 —— 请到「诊断」页检查共享数据目录";
    }

    private ColorSchemeCatalog _catalog = ColorSchemeCatalog.Empty;

    /// <summary>用户的 weasel.custom.yaml，供「应用」时复用（保持已加载状态）。</summary>
    private CustomYamlFile? _custom;

    private void RefreshPreview()
    {
        var resolved = SelectedScheme is null ? null : _catalog.Resolve(SelectedScheme);
        if (resolved is null)
        {
            // 目录里没有（自定义方案）→ 退回空白预览，不要崩溃
            return;
        }

        BackBrush = ToBrush(resolved.BackColor);
        TextBrush = ToBrush(resolved.TextColor);
        CandidateTextBrush = ToBrush(resolved.CandidateTextColor);
        HilitedTextBrush = ToBrush(resolved.HilitedCandidateTextColor);
        HilitedBackBrush = ToBrush(resolved.HilitedCandidateBackColor);
        LabelBrush = ToBrush(resolved.LabelTextColor);
        HilitedLabelBrush = ToBrush(resolved.HilitedLabelTextColor);
        CommentBrush = ToBrush(resolved.CommentTextColor);
        BorderBrush = ToBrush(resolved.BorderColor);
    }

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
    private async Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = "正在写入配置……";
        try
        {
            Directory.CreateDirectory(_userDirectory);
            var path = Path.Combine(_userDirectory, "weasel.custom.yaml");
            var custom = _custom ?? new CustomYamlFile(path);
            if (custom.State == CustomYamlLoadState.Absent) custom.Load();

            if (!custom.IsWritable)
            {
                StatusText = "配置解析失败，已拒绝写入（避免损坏用户文件）：" + custom.LoadError;
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

            custom.Save();
            StatusText = "已写入 " + path + "（需执行部署后生效）";
        }
        catch (Exception ex)
        {
            StatusText = "写入失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeployAsync()
    {
        IsBusy = true;
        StatusText = "正在部署……";
        try
        {
            var result = await Task.Run(() => ProbeService.ProbeDeployer(Environment.DeployerPath));
            StatusText = result.Status == ProbeStatus.Ok
                ? "部署完成，切换输入法即可看到新外观"
                : "部署返回：" + result.Summary;
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
}
