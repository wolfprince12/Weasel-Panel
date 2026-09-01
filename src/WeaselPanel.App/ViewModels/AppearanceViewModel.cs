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
        set => Set(ref _fontPoint, Math.Clamp(value, 8, 48));
    }

    public bool InlinePreedit
    {
        get => _inlinePreedit;
        set => Set(ref _inlinePreedit, value);
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

        // 2) 当前生效值：用户目录下的 weasel.custom.yaml
        var customPath = Path.Combine(_userDirectory, "weasel.custom.yaml");
        var custom = new CustomYamlFile(customPath);
        if (File.Exists(customPath))
        {
            try { custom.Load(); } catch { /* 解析失败则按出厂值处理 */ }
        }

        var scheme = custom.StringForPath("style/color_scheme") ?? "aqua";
        _fontFace = custom.StringForPath("style/font_face") ?? "Microsoft YaHei";
        _fontPoint = custom.IntForPath("style/font_point") ?? 14;
        _inlinePreedit = custom.BoolForPath("style/inline_preedit") ?? false;
        OnPropertyChanged(nameof(FontFace));
        OnPropertyChanged(nameof(FontPoint));
        OnPropertyChanged(nameof(InlinePreedit));

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

    // ── 应用 ──────────────────────────────────────────────────
    private async Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = "正在写入配置……";
        try
        {
            Directory.CreateDirectory(_userDirectory);
            var path = Path.Combine(_userDirectory, "weasel.custom.yaml");
            var custom = new CustomYamlFile(path);
            if (File.Exists(path)) custom.Load();

            if (!custom.IsWritable)
            {
                StatusText = "配置解析失败，已拒绝写入（避免损坏用户文件）：" + custom.LoadError;
                return;
            }

            if (SelectedScheme is not null) custom.Set("style/color_scheme", SelectedScheme);
            custom.Set("style/font_face", FontFace);
            custom.Set("style/font_point", FontPoint);
            custom.Set("style/inline_preedit", InlinePreedit);

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
