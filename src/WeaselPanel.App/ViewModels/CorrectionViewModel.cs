//
//  CorrectionViewModel.cs — 紫毫纠错页
//
//  ── 这一页管什么 ──────────────────────────────────────────────────────
//  只管三个字段：CorrectionEnabled / CorrectionInjectionPosition /
//  CorrectionCandidateCount。它们落在两处：
//    · 开关 → rime_ice.custom.yaml 的 engine/filters 里那条
//             lua_filter@*amethyst_corrector，以及 speller/algebra 的纠错 derive
//    · 位置与数量 → 用户目录下 correction_position.txt / correction_count.txt
//                   （yaml 装不下它们，lua 每次 init 时读这两个文件）
//
//  ── 为什么和「雾凇拼音」页分开 ─────────────────────────────────────────
//  两页写的是同一份 rime_ice.custom.yaml。若都暴露纠错开关，用户在 A 页开、
//  在 B 页应用，B 页那份陈旧快照会把它关掉，界面还显示着「开」——
//  典型的 UI 与磁盘脱钩。所以纠错只此一处可改，雾凇页只读地反映它
//  （被纠错强制启用的模糊音规则显示为勾选且禁用）。
//
//  ── Apply 前必须重读磁盘 ──────────────────────────────────────────────
//  本页的 RimeIceConfig 实例是进页面时读的。用户可能中途去雾凇页应用了别的
//  改动，此时本页快照已陈旧，直接 WritePatch 会把那些改动**回滚**。
//  所以 Apply 的第一步是重新 new 一份（= 重读磁盘），只把三个纠错字段覆盖上去，
//  确保本页永远「只改自己那三个键」。
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WeaselPanel.App.ViewModels;

/// <summary>纠错规则表里的一行（只读展示，用户不能单独关某一条）。</summary>
public sealed class CorrectionRuleRow
{
    public CorrectionRuleRow(string label, string rule)
    {
        Label = label;
        Rule = rule;
    }

    /// <summary>人类可读描述，如 "zh/ch/sh → z/c/s"。规则本身与界面语言无关，不需要翻译。</summary>
    public string Label { get; }

    /// <summary>写进 speller/algebra 的原文。</summary>
    public string Rule { get; }
}

/// <summary>
/// librime-lua（即「带 Lua 的 rime.dll」）的检测结果。
/// 面板只做检测与指引，绝不自动改写系统输入法的 rime.dll —— 版本不匹配
/// 会让整个小狼毫失效，且无法在 macOS 上真机验证 Windows 部署。见 #59。
/// </summary>
public enum LuaEngineState
{
    /// <summary>小狼毫未安装，无法检测。</summary>
    NotInstalled,

    /// <summary>已安装小狼毫，但在其安装目录未找到 Lua 运行时特征文件。</summary>
    Absent,

    /// <summary>安装目录中存在 Lua 运行时特征文件（如 lua54.dll）。</summary>
    Present,
}

public sealed class CorrectionViewModel : ViewModelBase, ILanguageAware, IPanelActions
{
    private readonly WeaselEnvironment _environment;
    private RimeIceConfig _config;

    private bool _isBusy;
    private string _statusText = "";
    private string _baseline = "";
    private bool _loaded;

    // ── librime-lua（Lua 运行时）─────────────────────────────────────
    // 纠错脚本（amethyst_corrector.lua）由面板自动部署到用户目录，
    // 但运行它依赖 librime-lua 提供的 Lua 运行时（即「带 Lua 的 rime.dll」）。
    // 面板**不**自动改写系统输入法的 rime.dll：版本不匹配会让整个输入法失效，
    // 且无法在 macOS 上真机验证 Windows 部署。故只做检测 + 指引（见 #59）。
    private LuaEngineState _luaState;

    private bool _enabled;
    private CorrectionInjectionPosition _position = CorrectionInjectionPosition.AfterFirst;
    private int _candidateCount = 1;

    public CorrectionViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        _config = new RimeIceConfig(environment);

        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy && IsDirty && IsInstalled);
        ReloadCommand = new DelegateCommand(Load);
        InstallLuaCommand = new RelayCommand(
            InstallLuaAsync,
            () => !IsBusy && _luaState == LuaEngineState.Absent && _environment.ProgramDirectory is not null);

        PositionOptions = new ObservableCollection<ValueOption<CorrectionInjectionPosition>>
        {
            new(CorrectionInjectionPosition.AfterFirst, ""),
            new(CorrectionInjectionPosition.Top, ""),
        };

        CountOptions = new ObservableCollection<ValueOption<int>>
        {
            new(1, ""),
            new(2, ""),
            new(3, ""),
        };

        Rules = new ObservableCollection<CorrectionRuleRow>(
            RimeIceConfig.CorrectionRules.Select(r => new CorrectionRuleRow(r.Label, r.Rule)));

        StatusText = StatusFromKey("Correction.Status.Ready");
    }

    // ── 集合 ──────────────────────────────────────────────────────────
    public ObservableCollection<ValueOption<CorrectionInjectionPosition>> PositionOptions { get; }
    public ObservableCollection<ValueOption<int>> CountOptions { get; }
    public ObservableCollection<CorrectionRuleRow> Rules { get; }

    public RelayCommand ApplyCommand { get; }
    public DelegateCommand ReloadCommand { get; }

    /// <summary>引导式安装 librime-lua：仅在未检测到 Lua 运行时、且小狼毫已安装时可用。</summary>
    public RelayCommand InstallLuaCommand { get; }

    // ── 状态 ──────────────────────────────────────────────────────────

    /// <summary>没装 rime_ice.schema.yaml 就没有可挂载的目标，整页置灰。</summary>
    public bool IsInstalled => _config.IsInstalled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            // 位置/数量两个下拉在关闭时无意义，跟着灰掉
            OnPropertyChanged(nameof(DetailEnabled));
            MarkChanged();
        }
    }

    /// <summary>位置与数量只在纠错开启时可调。</summary>
    public bool DetailEnabled => _enabled;

    public CorrectionInjectionPosition Position
    {
        get => _position;
        set { if (Set(ref _position, value)) MarkChanged(); }
    }

    public int CandidateCount
    {
        get => _candidateCount;
        set { if (Set(ref _candidateCount, value)) MarkChanged(); }
    }

    /// <summary>纠错开启后被强制启用的模糊音规则条数（界面用来解释「为什么雾凇页那些勾去不掉」）。</summary>
    public string ForcedFuzzyText { get; private set; } = "";

    /// <summary>规则总条数文案。</summary>
    public string RuleCountText { get; private set; } = "";

    // ── 引擎资源状态 ──────────────────────────────────────────────────

    /// <summary>引擎资源是否已随 exe 一起嵌入（构建时漏声明就会是 false）。</summary>
    public bool EngineEmbedded => CorrectionAssets.IsEmbedded;

    /// <summary>lua 脚本在 Rime 用户目录下的目标路径。</summary>
    public string DeployedLuaPath =>
        Path.Combine(_environment.UserDirectory, "lua", "amethyst_corrector.lua");

    /// <summary>正向词表在 Rime 用户目录下的目标路径。</summary>
    public string DeployedDictPath =>
        Path.Combine(_environment.UserDirectory, "correction_pinyin.txt");

    /// <summary>用户可自备的词表路径（存在时优先于出厂词表，由 lua 决定）。</summary>
    public string UserDictPath =>
        Path.Combine(_environment.UserDirectory, "correction_pinyin_user.txt");

    /// <summary>已部署到磁盘（两个文件都在）。关闭纠错后词表会被清掉，这里会转 false。</summary>
    public bool EngineDeployed => File.Exists(DeployedLuaPath) && File.Exists(DeployedDictPath);

    public string EngineStateText { get; private set; } = "";

    // ── librime-lua 状态（检测 + 指引，不写系统目录）──────────────────

    /// <summary>librime-lua 官方 release 页 URL。
    /// XAML 端必须 <c>{x:Static vm:CorrectionViewModel.LuaDownloadUrl}</c> 喂给 Hyperlink.NavigateUri，
    /// 不能 {Binding}：Hyperlink.NavigateUri 默认走 TwoWay（某些 WPF 版本下 TwoWay / OneWay
    /// 行为不一致），实例 computed 属性（`public Uri Foo => ...`）没 setter，
    /// 启动时会抛 InvalidOperationException（"无法对只读属性 Foo 进行 TwoWay 绑定"）。
    /// 修法两步：① 改 VM 端 `public static readonly Uri Foo = new(...)`（Uri 不是 const 编译期常量）；
    /// ② XAML 端改 `NavigateUri="{x:Static vm:Class.Foo}"`。任意一步缺失 v0.2.5 真机启动即崩。
    /// 详见 docs/RELEASE_v0.2.5.md 第⑤类盲区。</summary>
    public static readonly Uri LuaDownloadUrl = new("https://github.com/hchunhui/librime-lua/releases/");

    /// <summary>librime 官方 Release 页（公开，含 lua / octagram / charcode 三插件的 dist/lib/rime.dll）。
    /// 社区现优先从这里取「带 Lua 的 rime.dll」——hchunhui/librime-lua 的预编译包已转到需登录的
    /// GitHub Actions artifacts。安装引导流程要的就是这里的 dist/lib/rime.dll，故作为推荐源。</summary>
    public static readonly Uri LuaDownloadUrlRime = new("https://github.com/rime/librime/releases/");

    /// <summary>判定 Lua 运行时是否在场的文件名特征（动态链接版 librime-lua 会随 rime.dll 一起带来这些独立 dll）。</summary>
    private static readonly string[] LuaRuntimeMarkers =
    {
        "lua54.dll", "lua5.4.dll", "lua.dll", "librime-lua.dll",
    };

    /// <summary>
    /// 「把 Lua 静态编进 rime.dll」的旧构建（如 hchunhui/librime-lua 的 lua-dev-build-17）
    /// 不附带独立的 lua54.dll，Lua 组件名直接编进了 rime.dll 的二进制字符串里。
    /// 这些组件名只有 Lua-enabled 的 rime.dll 才会出现，用作「无独立 dll 也能识别」的判据。
    /// </summary>
    private static readonly string[] LuaRimeDllSignatures =
    {
        "lua_translator", "lua_filter", "lua_segmentor", "lua_processor",
    };

    public LuaEngineState LuaState
    {
        get => _luaState;
        private set
        {
            if (value == _luaState) return;
            _luaState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LuaStatusReady));
            OnPropertyChanged(nameof(LuaStatusBrush));
            OnPropertyChanged(nameof(ShowLuaInstall));
            InstallLuaCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>是否已具备可用的 Lua 运行时（只有 Present 才能正常开启纠错）。</summary>
    public bool LuaStatusReady => _luaState == LuaEngineState.Present;

    /// <summary>仅在「已装小狼毫但未检测到 Lua 运行时」时显示「安装 Lua 引擎」按钮。</summary>
    public bool ShowLuaInstall => _luaState == LuaEngineState.Absent;

    /// <summary>状态文案（随语言切换重建）。</summary>
    public string LuaStatusText { get; private set; } = "";

    /// <summary>状态配色：就绪=绿(SuccessBrush)，缺引擎=橙(WarningBrush)，未安装=灰(TextSecondaryBrush)。</summary>
    public Brush LuaStatusBrush
    {
        get
        {
            var key = _luaState switch
            {
                LuaEngineState.Present => "SuccessBrush",
                LuaEngineState.Absent => "WarningBrush",
                _ => "TextSecondaryBrush",
            };
            return (Brush)(Application.Current?.FindResource(key)
                            ?? new SolidColorBrush(System.Windows.Media.Colors.Gray));
        }
    }

    /// <summary>小狼毫安装目录，用户需把「带 Lua 的 rime.dll」覆盖到这里。</summary>
    public string LuaInstallDir => _environment.ProgramDirectory ?? "";

    /// <summary>librime-lua 预编译下载页（GitHub Releases）。</summary>
    /// <remarks>XAML 已用 {x:Static vm:CorrectionViewModel.LuaDownloadUrl}，
    /// 本类内可在 C# 端直接引用同一静态字段（无需实例别名）。</remarks>

    /// <summary>分步安装指引（随语言切换重建，最多 5 步）。</summary>
    public ObservableCollection<string> LuaGuideLines { get; } = new();

    // ── 忙 / 状态条 ───────────────────────────────────────────────────
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) ApplyCommand.RaiseCanExecuteChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        // 走 Set() 而不是裸赋值 —— 雾凇页曾栽在这里：状态条永远停在初始文案，
        // 用户点了应用看不到任何反馈，误以为按钮没响应。
        private set => Set(ref _statusText, value);
    }

    public string FilePath => _config.IceCustomPath;

    // ── 脏值 ──────────────────────────────────────────────────────────
    // ⚠️ 未 Load 前不报脏（见 SchemaViewModel 同款守卫说明），否则启动即误报。
    public new bool IsDirty => _loaded && Signature() != _baseline;

    private string Signature() => string.Join("#",
        _enabled ? 1 : 0,
        // 关闭状态下位置/数量是惰性的（写出来也会被 RemoveCorrectionAssets 立刻删掉），
        // 纳入签名会让「关着纠错改一下数量」把应用按钮点亮 → 点了报成功但磁盘没变，
        // 是比不改更糟的假成功。Core 的 IsDirty 也是这个口径，两边必须一致。
        _enabled ? ((int)_position).ToString(CultureInfo.InvariantCulture) : "-",
        _enabled ? _candidateCount.ToString(CultureInfo.InvariantCulture) : "-");

    private void MarkChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        ApplyCommand.RaiseCanExecuteChanged();
    }

    // ── 载入 ──────────────────────────────────────────────────────────
    public void Load()
    {
        _config = new RimeIceConfig(_environment);

        _enabled = _config.CorrectionEnabled;
        _position = _config.CorrectionInjectionPosition;
        _candidateCount = Math.Clamp(_config.CorrectionCandidateCount, 1, 3);

        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(DetailEnabled));
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(CandidateCount));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(EngineDeployed));
        OnPropertyChanged(nameof(EngineEmbedded));
        OnPropertyChanged(nameof(DeployedLuaPath));
        OnPropertyChanged(nameof(DeployedDictPath));
        OnPropertyChanged(nameof(UserDictPath));
        OnPropertyChanged(nameof(LuaInstallDir));

        // 检测 librime-lua（只读探测，绝不写系统目录）。
        LuaState = DetectLuaEngine();

        RefreshTexts();
        _baseline = Signature();
        _loaded = true;
        OnPropertyChanged(nameof(IsDirty));
        ApplyCommand.RaiseCanExecuteChanged();

        StatusText = IsInstalled
            ? StatusFromKey("Correction.Status.Ready")
            : StatusFromKey("Correction.Status.NotInstalled");
    }

    // ── 写盘 ──────────────────────────────────────────────────────────
    public Task ReloadAsync() { Load(); return Task.CompletedTask; }

    public async Task ApplyAsync()
    {
        if (!IsInstalled)
        {
            StatusText = StatusFromKey("Correction.Status.NotInstalled");
            return;
        }

        IsBusy = true;
        StatusText = StatusFromKey("Correction.Status.Writing");
        try
        {
            // ⚠️ 重读磁盘再改：本页快照可能已被雾凇页的应用操作甩在后面，
            // 直接写会把用户在那边刚落盘的改动回滚掉。
            _config = new RimeIceConfig(_environment);
            _config.CorrectionEnabled = _enabled;
            _config.CorrectionInjectionPosition = _position;
            _config.CorrectionCandidateCount = Math.Clamp(_candidateCount, 1, 3);

            // 开启时才需要引擎资源。解压失败不静默 —— 没有 lua 和词表，
            // filters 里那条 lua_filter@*amethyst_corrector 会让编译报错，
            // 后果是候选框直接消失，比不开纠错糟糕得多，所以这里硬失败。
            string? assetRoot = null;
            if (_enabled)
            {
                assetRoot = await Task.Run(() => CorrectionAssets.EnsureExtracted(_environment.UserDirectory));
                if (assetRoot is null)
                {
                    StatusText = StatusFromKey("Correction.Status.AssetFailed",
                        CorrectionAssets.LastError ?? "unknown");
                    return;
                }
            }

            _config.WritePatch(assetRoot);

            _baseline = Signature();
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(EngineDeployed));
            RefreshEngineState();
            StatusText = StatusFromKey(_enabled
                ? "Correction.Status.Saved"
                : "Correction.Status.SavedOff");
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Correction.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    // ── 语言切换 ──────────────────────────────────────────────────────
    public void RefreshTexts()
    {
        foreach (var o in PositionOptions)
            o.Name = L10n.Instance.T(o.Id == CorrectionInjectionPosition.Top
                ? "Correction.Position.Top"
                : "Correction.Position.AfterFirst");

        foreach (var o in CountOptions)
            o.Name = L10n.Instance.T("Correction.Count.N", o.Id);

        RuleCountText = L10n.Instance.T("Correction.Rules.Count", Rules.Count);
        ForcedFuzzyText = L10n.Instance.T("Correction.ForcedFuzzy", ForcedFuzzyCount());
        RefreshEngineState();
        RefreshLuaTexts();

        OnPropertyChanged(nameof(PositionOptions));
        OnPropertyChanged(nameof(CountOptions));
        OnPropertyChanged(nameof(RuleCountText));
        OnPropertyChanged(nameof(ForcedFuzzyText));

        if (HasStatusKey) StatusText = Restatus();
    }

    private void RefreshEngineState()
    {
        EngineStateText = L10n.Instance.T(
            !EngineEmbedded ? "Correction.Engine.Missing"
            : EngineDeployed ? "Correction.Engine.Deployed"
            : "Correction.Engine.NotDeployed");
        OnPropertyChanged(nameof(EngineStateText));
        OnPropertyChanged(nameof(EngineDeployed));
    }

    /// <summary>
    /// 只读探测 librime-lua 是否在场（两条路径，覆盖动态与静态两种链接方式）：
    ///   ① 动态链接版：安装目录（含 Win32 子目录）存在独立的 Lua 运行时 dll
    ///      （lua54.dll / lua5.4.dll / lua.dll / librime-lua.dll）—— librime-lua 替换
    ///      rime.dll 时一并带来，是最直观的信号。
    ///   ② 静态链接版：rime.dll 自身把 Lua 编进去了（如 hchunhui/librime-lua 旧构建），
    ///      无独立 dll。此时扫 rime.dll 二进制里的 Lua 组件名字符串
    ///      （lua_translator / lua_filter / lua_segmentor / lua_processor）来判定。
    /// 小狼毫未安装 → NotInstalled；两条路径都查不到 → Absent。
    /// ⚠️ 不在此做任何写操作。
    /// </summary>
    private LuaEngineState DetectLuaEngine()
    {
        if (_environment.ProgramDirectory is null) return LuaEngineState.NotInstalled;

        // 1) 动态链接版：ProgramDirectory / Win32 下能找到独立的 Lua 运行时 dll
        var candidates = new[]
        {
            _environment.ProgramDirectory,
            Path.Combine(_environment.ProgramDirectory, "Win32"),
        };
        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            if (LuaRuntimeMarkers.Any(m => File.Exists(Path.Combine(dir, m))))
                return LuaEngineState.Present;
        }

        // 2) 静态链接版：rime.dll 自身带 Lua（无独立 dll）。扫 rime.dll 二进制里的
        //    Lua 组件名字符串来判断。hchunhui/librime-lua 旧构建即此情况——
        //    装完若只按「有没有 lua54.dll」判定会误报「重新检测失败」。
        var rimeDll = FindInstalledRimeDll(_environment.ProgramDirectory);
        if (rimeDll is not null && RimeDllHasLua(rimeDll))
            return LuaEngineState.Present;

        return LuaEngineState.Absent;
    }

    /// <summary>在程序目录（含子目录）递归找 rime.dll。</summary>
    private static string? FindInstalledRimeDll(string programDir)
    {
        try
        {
            return Directory.EnumerateFiles(programDir, "rime.dll", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>读 rime.dll 二进制，按 Latin1 译出字符串后找 Lua 组件名签名。
    /// 非 ASCII 字节译成 '?' 不影响这几个纯 ASCII 签名。</summary>
    private static bool RimeDllHasLua(string rimeDllPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(rimeDllPath);
            var text = Encoding.Latin1.GetString(bytes);
            return LuaRimeDllSignatures.Any(sig => text.Contains(sig));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 引导式安装 librime-lua：确认框 → 选文件 → 后台提权覆盖 → 重部署 → 重检测。
    /// 危险动作（覆盖 Program Files 下的 rime.dll）全自动，但会先备份并停服务，
    /// 且版本由用户下载的那一份决定——面板只负责「安全地把文件放到位」。
    /// </summary>
    private async Task InstallLuaAsync()
    {
        // 1. 二次确认：覆盖 rime.dll 版本错配会让整个输入法失效、候选框消失。
        var confirm = MessageBox.Show(
            L10n.Instance.T("Correction.Lua.ConfirmBody"),
            L10n.Instance.T("Correction.Lua.ConfirmTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        // 2. 选文件：用户已手动从官网下好 rime.dll / .zip（.7z 请先解压）。
        var dialog = new OpenFileDialog
        {
            Title = L10n.Instance.T("Correction.Lua.PickFile"),
            Filter = "rime.dll|*.dll|Zip 压缩包|*.zip|全部文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            StatusText = StatusFromKey("Correction.Lua.Err.NoFile");
            return;
        }

        var picked = dialog.FileName;
        var ext = Path.GetExtension(picked).ToLowerInvariant();

        IsBusy = true;
        StatusText = StatusFromKey("Correction.Lua.Installing");
        try
        {
            // 3. 解析出源 rime.dll（以及同包的 lua54.dll）。
            string srcDll;
            string? srcLua = null;
            if (ext == ".dll")
            {
                srcDll = picked;
                var candLua = Path.Combine(Path.GetDirectoryName(picked)!, "lua54.dll");
                if (File.Exists(candLua)) srcLua = candLua;
            }
            else if (ext == ".zip")
            {
                var (dll, lua) = await LuaInstaller.ExtractZipForRimeDllAsync(picked).ConfigureAwait(false);
                if (dll is null)
                {
                    StatusText = StatusFromKey("Correction.Lua.Err.BadFile");
                    return;
                }
                srcDll = dll;
                srcLua = lua;
            }
            else if (ext == ".7z")
            {
                StatusText = StatusFromKey("Correction.Lua.Err.SevenZip");
                return;
            }
            else
            {
                StatusText = StatusFromKey("Correction.Lua.Err.BadFile");
                return;
            }

            // 4. 提权覆盖（备份 + 停服务内置在临时 bat 里，用户只见一次 UAC）。
            var ok = await LuaInstaller.OverwriteWithElevationAsync(
                _environment.ProgramDirectory!, srcDll, srcLua).ConfigureAwait(false);
            if (!ok)
            {
                // 区分架构错（PE Machine 不匹配）与普通提权错 —— 前者必须给详细诊断
                // + 手动恢复指引，否则用户只会重试然后再炸 0xC000007B。
                if (LuaInstaller.LastError is { } err && err.StartsWith("源架构="))
                {
                    // err 形如「源架构=X；已装 rime.dll 架构=Y；锚点架构=Z；destDir=W」
                    var parts = err.Split('；', 4); // 中文全角分号作分隔
                    StatusText = StatusFromKey(
                        "Correction.Lua.Err.ArchMismatch",
                        ValueAfter(parts, 0, "源架构="),
                        ValueAfter(parts, 1, "已装 rime.dll 架构="),
                        ValueAfter(parts, 2, "锚点架构="),
                        ValueAfter(parts, 3, "destDir="));
                }
                else
                {
                    StatusText = StatusFromKey("Correction.Lua.Err.Overwrite");
                }
                return;
            }

            // 5. 重部署（让 librime 重新加载），退出码负才视为失败（已运行=1 不算）。
            var deployCode = await WeaselDeployer.RunAsync(_environment, "/deploy").ConfigureAwait(false);

            // 6. 重检测：覆盖后若特征文件就位，即为成功。
            LuaState = DetectLuaEngine();
            if (LuaState == LuaEngineState.Present)
            {
                var msg = StatusFromKey("Correction.Lua.Installed");
                if (deployCode < 0) msg += " " + StatusFromKey("Correction.Lua.Err.Redeploy");
                StatusText = msg;
                RefreshLuaTexts();
            }
            else
            {
                StatusText = StatusFromKey("Correction.Lua.Err.Detect");
            }
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Correction.Lua.InstallFailed") + " " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>从 LastError 解析出的 4 段格式串里抠出某一段，去掉前缀。
    /// 例：<c>ValueAfter(["源架构=x64","锚点架构=x64"], 0, "源架构=")</c> → "x64"。</summary>
    private static string ValueAfter(string[] parts, int idx, string prefix)
    {
        if (idx >= parts.Length) return "";
        var p = parts[idx];
        return p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p;
    }

    /// <summary>随语言切换重建 Lua 状态文案与分步指引。</summary>
    private void RefreshLuaTexts()
    {
        LuaStatusText = L10n.Instance.T(_luaState switch
        {
            LuaEngineState.Present => "Correction.Lua.Status.Present",
            LuaEngineState.Absent => "Correction.Lua.Status.Absent",
            _ => "Correction.Lua.Status.NotInstalled",
        });

        LuaGuideLines.Clear();
        for (var i = 1; i <= 5; i++)
            LuaGuideLines.Add(L10n.Instance.T($"Correction.Lua.Guide.{i}"));

        OnPropertyChanged(nameof(LuaStatusText));
        OnPropertyChanged(nameof(LuaStatusBrush));
        OnPropertyChanged(nameof(LuaGuideLines));
        OnPropertyChanged(nameof(LuaInstallDir));
        OnPropertyChanged(nameof(LuaState));
    }

    /// <summary>
    /// 纠错规则里有多少条与模糊音规则表重合 —— 那些就是开启纠错后在雾凇页
    /// 被强制勾上、且点不动的项。数字算出来告诉用户，比让他自己去数好。
    /// </summary>
    private static int ForcedFuzzyCount() =>
        RimeIceConfig.FuzzyRules.Count(f => RimeIceConfig.CorrectionRuleSet.Contains(f.Rule));
}
