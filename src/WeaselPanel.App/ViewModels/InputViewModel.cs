//
//  InputViewModel.cs
//  WeaselPanel.App
//
//  「按键与输入」页：候选数、翻页键、中英切换键、附加开关。
//  → default.custom.yaml 的 patch：menu/page_size、key_binder/bindings、
//    ascii_composer/switch_key。写盘后需部署生效（与方案页同一套约定）。
//
//  ── 两条必须说清的语义 ────────────────────────────────────────────────
//  1. **key_binder/bindings 是列表整体替换，不是深度合并**。
//     本页因此只在用户明确点「应用」时写一次，且一次写全（勾选了哪些就写哪些）；
//     未点应用时一个字节都不动，避免把用户原有的绑定悄悄抹掉。
//  2. **载入读的是「出厂 default.yaml + 用户 patch 合并后的值」**，
//     与外观页同源：面板显示配置原值（没配就是空）会让用户以为「没生效」。
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.App.ViewModels;

//
//  ⚠️ 本页不再碰 ascii_composer/switch_key —— 中英切换键已整体移到「行为」页。
//
//  原因：switch_key 是**整块替换**的字典。本页原来的四个预设
//  （None / 左右Shift / CapsLock / 左右Control）写下去，会把行为页逐键设置的
//  Caps_Lock=commit_code、Shift_L=inline_ascii 之类整块冲掉；反过来行为页也会
//  把本页的预设冲掉。用户在两个页面来回改，表现就是「改了没生效」。
//
//  macOS 鼠须管面板里这一项本来就只挂在 BehaviorPage 一处（SettingsStore 里
//  再无第二个写入点），而行为页的 5 键 × 6 动作是本页预设的严格超集，
//  所以归行为页独占，本页只留一行指向提示（Input.Switch.MovedToBehavior）。
//

public sealed class InputViewModel : ViewModelBase
{
    private readonly WeaselEnvironment _environment;
    private bool _isBusy;
    private string _statusText = "";
    private bool _hasLoaded;

    private int _pageSize = 5;
    private bool _pagingMinusEqual = true;
    private bool _pagingCommaPeriod;
    private bool _pagingBrackets;
    private bool _pagingUpDown;
    private bool _toggleFullShape;
    private bool _togglePunctuation;

    public InputViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy);
        ReloadCommand = new DelegateCommand(Load);
        StatusText = L10n.Instance.T("Input.Status.Ready");
    }

    public ICommand ApplyCommand { get; }
    public ICommand ReloadCommand { get; }

    // ── 绑定属性 ────────────────────────────────────────────────

    public int PageSize
    {
        get => _pageSize;
        set => Set(ref _pageSize, Math.Clamp(value, 3, 10));
    }

    public bool PagingMinusEqual
    {
        get => _pagingMinusEqual;
        set => Set(ref _pagingMinusEqual, value);
    }

    public bool PagingCommaPeriod
    {
        get => _pagingCommaPeriod;
        set => Set(ref _pagingCommaPeriod, value);
    }

    public bool PagingBrackets
    {
        get => _pagingBrackets;
        set => Set(ref _pagingBrackets, value);
    }

    public bool PagingUpDown
    {
        get => _pagingUpDown;
        set => Set(ref _pagingUpDown, value);
    }

    public bool ToggleFullShape
    {
        get => _toggleFullShape;
        set => Set(ref _toggleFullShape, value);
    }

    public bool TogglePunctuation
    {
        get => _togglePunctuation;
        set => Set(ref _togglePunctuation, value);
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

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => Set(ref _hasLoaded, value);
    }

    /// <summary>面板上展示的绑定预览（让用户写盘前看清将要落什么）。</summary>
    public ObservableCollection<string> PreviewLines { get; } = new();

    // ── 载入 ────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            var baseView = RimeConfigView.Empty;
            var sharedDefault = string.IsNullOrWhiteSpace(_environment.SharedDataDirectory)
                ? null
                : Path.Combine(_environment.SharedDataDirectory, "default.yaml");
            if (sharedDefault is not null && File.Exists(sharedDefault))
            {
                try { baseView = RimeConfigView.FromYaml(File.ReadAllText(sharedDefault)); }
                catch { /* 出厂文件解析失败 → 退化为「键盘位全空」，由 patch 兜底 */ }
            }

            var customPath = Path.Combine(_environment.UserDirectory, "default.custom.yaml");
            var custom = new CustomYamlFile(customPath);
            if (File.Exists(customPath)) custom.Load();

            var merged = RimeConfigView.MergePatch(baseView, custom.Patch);

            if (merged.TryGetInt("menu/page_size", out var size)) _pageSize = Math.Clamp(size, 3, 10);

            var bindings = ReadBindings(merged.Lookup("key_binder/bindings"));
            _pagingMinusEqual = bindings.Contains("minus|Page_Up") || bindings.Contains("equal|Page_Down");
            _pagingCommaPeriod = bindings.Contains("comma|Page_Up") || bindings.Contains("period|Page_Down");
            _pagingBrackets = bindings.Contains("bracketleft|Page_Up") || bindings.Contains("bracketright|Page_Down");
            _pagingUpDown = bindings.Contains("Up|Page_Up") || bindings.Contains("Down|Page_Down");

            _toggleFullShape = bindings.Any(b => b.StartsWith("Shift+space|", StringComparison.OrdinalIgnoreCase));
            _togglePunctuation = bindings.Any(b => b.StartsWith("Control+period|", StringComparison.OrdinalIgnoreCase));

            HasLoaded = true;
            StatusText = File.Exists(customPath)
                ? L10n.Instance.T("Input.Status.Loaded", customPath)
                : L10n.Instance.T("Input.Status.NoConfig");

            RaiseAllChanged();
            RefreshPreview();
        }
        catch (Exception ex)
        {
            StatusText = L10n.Instance.T("Input.Status.LoadFailed",
                Path.Combine(_environment.UserDirectory, "default.custom.yaml"), ex.Message);
        }
    }

    private void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(PageSize));
        OnPropertyChanged(nameof(PagingMinusEqual));
        OnPropertyChanged(nameof(PagingCommaPeriod));
        OnPropertyChanged(nameof(PagingBrackets));
        OnPropertyChanged(nameof(PagingUpDown));
        OnPropertyChanged(nameof(ToggleFullShape));
        OnPropertyChanged(nameof(TogglePunctuation));
    }

    /// <summary>把 key_binder/bindings 摊平成 "accept|send" 集合，便于按存在性判断。</summary>
    private static HashSet<string> ReadBindings(object? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (value is not System.Collections.IEnumerable seq || value is string) return result;

        foreach (var item in seq)
        {
            if (item is not IReadOnlyDictionary<string, object?> map) continue;
            if (!map.TryGetValue("accept", out var acceptObj) || acceptObj is null) continue;

            var accept = acceptObj.ToString() ?? "";
            // 翻页用 send；附加开关用 toggle —— 两者都记下来，键名用 "accept|send-or-toggle"
            var action = "";
            if (map.TryGetValue("send", out var sendObj) && sendObj is not null) action = sendObj.ToString() ?? "";
            else if (map.TryGetValue("toggle", out var toggleObj) && toggleObj is not null) action = toggleObj.ToString() ?? "";

            result.Add(accept + "|" + action);
        }
        return result;
    }

    // ── 预览 ────────────────────────────────────────────────────

    /// <summary>
    /// 按当前勾选生成将要写入的 bindings 列表。
    /// 这一份**同时也是真正写入的内容** —— 预览与写盘同源，
    /// 杜绝「界面显示一套、落盘又是另一套」。
    /// </summary>
    private List<Dictionary<string, object?>> BuildBindings()
    {
        var list = new List<Dictionary<string, object?>>();

        void Page(string accept, string send) =>
            list.Add(new Dictionary<string, object?>
            {
                ["when"] = "paging",
                ["accept"] = accept,
                ["send"] = send,
            });

        if (PagingMinusEqual) { Page("minus", "Page_Up"); Page("equal", "Page_Down"); }
        if (PagingCommaPeriod) { Page("comma", "Page_Up"); Page("period", "Page_Down"); }
        if (PagingBrackets) { Page("bracketleft", "Page_Up"); Page("bracketright", "Page_Down"); }
        if (PagingUpDown) { Page("Up", "Page_Up"); Page("Down", "Page_Down"); }

        if (ToggleFullShape)
            list.Add(new Dictionary<string, object?>
            {
                ["when"] = "always",
                ["accept"] = "Shift+space",
                ["toggle"] = "full_shape",
            });

        if (TogglePunctuation)
            list.Add(new Dictionary<string, object?>
            {
                ["when"] = "always",
                ["accept"] = "Control+period",
                ["toggle"] = "ascii_punct",
            });

        return list;
    }

    private void RefreshPreview()
    {
        PreviewLines.Clear();
        foreach (var b in BuildBindings())
        {
            var accept = b["accept"]?.ToString() ?? "";
            var action = b.TryGetValue("send", out var s) && s is not null
                ? "send: " + s
                : "toggle: " + (b.TryGetValue("toggle", out var t) && t is not null ? t.ToString() : "");
            PreviewLines.Add($"- {{{b["when"]}, {accept} → {action}}}");
        }
        if (PreviewLines.Count == 0) PreviewLines.Add(L10n.Instance.T("Input.PagingKeys.None"));
    }

    // ── 应用 ────────────────────────────────────────────────────

    public Task ReloadAsync() { Load(); return Task.CompletedTask; }

    public async Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = L10n.Instance.T("Input.Status.Writing");
        try
        {
            Directory.CreateDirectory(_environment.UserDirectory);
            var path = Path.Combine(_environment.UserDirectory, "default.custom.yaml");
            var custom = new CustomYamlFile(path);
            if (custom.State == CustomYamlLoadState.Absent) custom.Load();

            if (!custom.IsWritable)
            {
                StatusText = L10n.Instance.T("Input.Status.ParseFailed", custom.LoadError);
                return;
            }

            // 走 ApplyLineEdits（逐行手术式）而不是 Set + Save（整文件重序列化）：
            // 用户可能在同一个 default.custom.yaml 里手写过别的条目，
            // 整文件重写会丢掉那些注释与条目。ApplyLineEdits 内部已含写盘 + 回读校验。
            var set = new PatchSet();
            set.Set("menu/page_size", PatchValue.Of(PageSize));

            var bindings = BuildBindings();
            set.Set("key_binder/bindings",
                bindings.Count > 0
                    ? PatchValue.KeyBindings(bindings)
                    : PatchValue.StringList(Array.Empty<string>()));

            custom.ApplyLineEdits(set);
            StatusText = L10n.Instance.T("Input.Status.Written", path);
        }
        catch (Exception ex)
        {
            StatusText = L10n.Instance.T("Input.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>语言切换后刷新所有静态文案（ViewModel 里拼出来的那部分）。</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(StatusText));
        RefreshPreview();
    }
}
