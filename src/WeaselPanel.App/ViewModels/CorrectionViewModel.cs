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
using System.Threading.Tasks;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.Services;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

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

public sealed class CorrectionViewModel : ViewModelBase, ILanguageAware
{
    private readonly WeaselEnvironment _environment;
    private RimeIceConfig _config;

    private bool _isBusy;
    private string _statusText = "";
    private string _baseline = "";

    private bool _enabled;
    private CorrectionInjectionPosition _position = CorrectionInjectionPosition.AfterFirst;
    private int _candidateCount = 1;

    public CorrectionViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        _config = new RimeIceConfig(environment);

        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy && IsDirty && IsInstalled);
        ReloadCommand = new DelegateCommand(Load);

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
    public bool IsDirty => Signature() != _baseline;

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

        RefreshTexts();
        _baseline = Signature();
        OnPropertyChanged(nameof(IsDirty));
        ApplyCommand.RaiseCanExecuteChanged();

        StatusText = IsInstalled
            ? StatusFromKey("Correction.Status.Ready")
            : StatusFromKey("Correction.Status.NotInstalled");
    }

    // ── 写盘 ──────────────────────────────────────────────────────────
    private async Task ApplyAsync()
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
    /// 纠错规则里有多少条与模糊音规则表重合 —— 那些就是开启纠错后在雾凇页
    /// 被强制勾上、且点不动的项。数字算出来告诉用户，比让他自己去数好。
    /// </summary>
    private static int ForcedFuzzyCount() =>
        RimeIceConfig.FuzzyRules.Count(f => RimeIceConfig.CorrectionRuleSet.Contains(f.Rule));
}
