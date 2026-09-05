//
//  BehaviorViewModel.cs — 行为页
//
//  ── 这一页的字段为什么跟 macOS 鼠须管面板的 BehaviorPage 不一样 ──────────
//  两个平台的前端各有自己的一套「前端专有设置」，照抄会写出上游根本不认的键：
//
//   · macOS 的 BehaviorPage 里有 keyboard_layout（ABC / USExtended…）与
//     show_notifications_when —— 那是 squirrel.yaml 的键，小狼毫没有对应项。
//
//   · 小狼毫这边对应的专有项在 weasel.yaml 的顶层与 style 段里：
//     show_notifications / show_notifications_time / global_ascii /
//     app_options，以及 style 下的 paging_on_scroll / click_to_capture /
//     ascii_tip_follow_cursor / display_tray_icon / vertical_auto_reverse。
//     这些键名是 2026-09-02 从上游 output/data/weasel.yaml 逐条核对的，
//     改动前请重新核对上游，不要凭印象增删。
//
//   · 两边共有的只有 Rime 通用部分：ascii_composer/good_old_caps_lock 与
//     ascii_composer/switch_key/*（写 default.custom.yaml）。
//
//  ── ⚠️ 不碰的两个键（会被别的页覆盖）─────────────────────────────────
//   style/inline_preedit   → 外观页已写（AppearanceViewModel.ApplyAsync）
//   key_binder/bindings    → 输入页整体重写（InputViewModel.BuildBindings）
//   行为页若也写这两个键，两个页面会互相覆盖，用户改哪边都像「没生效」。
//   候选窗按键的高级编辑器要上的话，得先把输入页的预设式开关合并进来。
//
//  ── 写盘分两个文件、走两条路径（不是偷懒，是两个文件的性质不同）─────────
//   weasel.custom.yaml  → CustomYamlFile.Set() + Save()        （与外观页一致）
//   default.custom.yaml → PatchSet + ApplyLineEdits()          （与输入页一致）
//   default.custom.yaml 是 Rime 的共享配置文件，用户常在里面手写别的条目，
//   整文件重序列化会连注释一起丢；ApplyLineEdits 是逐行手术式改，只动目标键。
//

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.App.ViewModels;

/// <summary>
/// 下拉项：id 是写进配置的值，Name 是本地化显示名。
/// 实现 INPC 只为一个目的 —— 切语言时更新 Name 而不用重建集合
/// （重建集合会让 ComboBox 丢掉当前选中项）。
/// </summary>
public sealed class NamedOption : INotifyPropertyChanged
{
    private string _name;

    public NamedOption(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class BehaviorViewModel : ViewModelBase, ILanguageAware, IPanelActions
{
    /// <summary>
    /// ascii_composer/switch_key 的可选动作，取自 librime 的 SwitcherCommand。
    /// 空串 = 这一项不写盘（保持输入方案自带行为）。
    /// </summary>
    private static readonly string[] SwitchActionIds =
        { "", "inline_ascii", "commit_code", "commit_text", "clear", "noop" };

    private readonly WeaselEnvironment _environment;
    private bool _isBusy;
    private bool _hasLoaded;
    private string _statusText = "";

    // ── ascii_composer（default.custom.yaml）──────────────────────────
    private bool _goodOldCapsLock;
    private string _capsLockAction = "";
    private string _shiftLeftAction = "";
    private string _shiftRightAction = "";
    private string _controlLeftAction = "";
    private string _controlRightAction = "";

    // ── 小狼毫专有（weasel.custom.yaml）───────────────────────────────
    private bool _showNotifications = true;
    private int _notificationTime = 1200;
    private bool _globalAscii;
    private bool _pagingOnScroll;
    private bool _clickToCapture;
    private bool _asciiTipFollowCursor;
    private bool _displayTrayIcon;
    private bool _verticalAutoReverse;

    public BehaviorViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        ApplyCommand = new RelayCommand(ApplyAsync, () => !IsBusy);
        ReloadCommand = new DelegateCommand(Load);

        SwitchActionOptions = SwitchActionIds
            .Select(id => new NamedOption(id, L10n.Instance.T(ActionNameKey(id))))
            .ToList();

        _statusText = StatusFromKey("Behavior.Status.Ready");
    }

    public ICommand ApplyCommand { get; }
    public ICommand ReloadCommand { get; }

    /// <summary>切换动作下拉项。集合只建一次，切语言时改每项的 Name。</summary>
    public List<NamedOption> SwitchActionOptions { get; }

    // ── ascii_composer ───────────────────────────────────────────────

    public bool GoodOldCapsLock
    {
        get => _goodOldCapsLock;
        set => Set(ref _goodOldCapsLock, value);
    }

    public string CapsLockAction
    {
        get => _capsLockAction;
        set => Set(ref _capsLockAction, value);
    }

    public string ShiftLeftAction
    {
        get => _shiftLeftAction;
        set => Set(ref _shiftLeftAction, value);
    }

    public string ShiftRightAction
    {
        get => _shiftRightAction;
        set => Set(ref _shiftRightAction, value);
    }

    public string ControlLeftAction
    {
        get => _controlLeftAction;
        set => Set(ref _controlLeftAction, value);
    }

    public string ControlRightAction
    {
        get => _controlRightAction;
        set => Set(ref _controlRightAction, value);
    }

    // ── 小狼毫专有 ───────────────────────────────────────────────────

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => Set(ref _showNotifications, value);
    }

    /// <summary>通知显示时长（毫秒）。上游 weasel.yaml 出厂值是 1200。</summary>
    public int NotificationTime
    {
        get => _notificationTime;
        set => Set(ref _notificationTime, Math.Clamp(value, 0, 10000));
    }

    public bool GlobalAscii
    {
        get => _globalAscii;
        set => Set(ref _globalAscii, value);
    }

    public bool PagingOnScroll
    {
        get => _pagingOnScroll;
        set => Set(ref _pagingOnScroll, value);
    }

    public bool ClickToCapture
    {
        get => _clickToCapture;
        set => Set(ref _clickToCapture, value);
    }

    public bool AsciiTipFollowCursor
    {
        get => _asciiTipFollowCursor;
        set => Set(ref _asciiTipFollowCursor, value);
    }

    public bool DisplayTrayIcon
    {
        get => _displayTrayIcon;
        set => Set(ref _displayTrayIcon, value);
    }

    public bool VerticalAutoReverse
    {
        get => _verticalAutoReverse;
        set => Set(ref _verticalAutoReverse, value);
    }

    // ⚠️ IsBusy / StatusText 是纯 UI 状态，绝不走 Set<T>（会把 HasUnsavedChanges 翻脏）。
    // 否则 ApplyAsync 末尾 finally { IsBusy = false; } 会在 MarkLoaded() 清零后再次翻脏，
    // 导致「应用并重新部署」后该面板永远显示「有未保存改动」。
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => Set(ref _hasLoaded, value);
    }

    // ── 读取 ─────────────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(_environment.UserDirectory);
            LoadWeasel();
            LoadRime();
            HasLoaded = true;
            StatusText = StatusFromKey("Behavior.Status.Loaded");
            RaiseAllChanged();
            // ⚠️ Load() 末尾必须 MarkLoaded()：上方 HasLoaded=true 和 StatusText=...
            // 都走 Set<T>，已经把 HasUnsavedChanges 翻成 true；Load 完应回到干净态。
            // 故意不在 catch 分支调 —— 失败时内存态半新半旧 ≠ 磁盘，留 dirty 让用户能 Reload。
            MarkLoaded();
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Behavior.Status.LoadFailed", ex.Message);
        }
    }

    /// <summary>
    /// 读 weasel.yaml（出厂）+ weasel.custom.yaml（用户补丁）合并后的值。
    /// 只读 custom 会把「用户没改过、但出厂值不是 false」的项读成 false。
    /// </summary>
    private void LoadWeasel()
    {
        var merged = MergeWithFactory("weasel.yaml", "weasel.custom.yaml");

        _showNotifications = !merged.TryGetBool("show_notifications", out var sn) || sn;
        _notificationTime = merged.TryGetInt("show_notifications_time", out var st)
            ? Math.Clamp(st, 0, 10000)
            : 1200;

        _globalAscii = merged.TryGetBool("global_ascii", out var ga) && ga;
        _pagingOnScroll = merged.TryGetBool("style/paging_on_scroll", out var ps) && ps;
        _clickToCapture = merged.TryGetBool("style/click_to_capture", out var cc) && cc;
        _asciiTipFollowCursor = merged.TryGetBool("style/ascii_tip_follow_cursor", out var af) && af;
        _displayTrayIcon = merged.TryGetBool("style/display_tray_icon", out var dt) && dt;
        _verticalAutoReverse = merged.TryGetBool("style/vertical_auto_reverse", out var vr) && vr;
    }

    private void LoadRime()
    {
        var merged = MergeWithFactory("default.yaml", "default.custom.yaml");

        // 读不到键时按出厂默认 true（上游 rime-prelude/default.yaml 与 macOS
        // 鼠须管面板的 SettingsStore 都是 true）。写成 `x && x` 会让「键缺失」
        // 被读成 false，与出厂行为相反。
        _goodOldCapsLock =
            !merged.TryGetBool("ascii_composer/good_old_caps_lock", out var gc) || gc;
        _capsLockAction = SwitchKeyOf(merged, "Caps_Lock");
        _shiftLeftAction = SwitchKeyOf(merged, "Shift_L");
        _shiftRightAction = SwitchKeyOf(merged, "Shift_R");
        _controlLeftAction = SwitchKeyOf(merged, "Control_L");
        _controlRightAction = SwitchKeyOf(merged, "Control_R");
    }

    /// <summary>出厂文件 + 用户 patch 合并视图。出厂文件缺失或解析失败时退化为只有 patch。</summary>
    private RimeConfigView MergeWithFactory(string factoryName, string customName)
    {
        var baseView = RimeConfigView.Empty;

        var shared = _environment.SharedDataDirectory;
        if (!string.IsNullOrWhiteSpace(shared))
        {
            var factoryPath = Path.Combine(shared, factoryName);
            if (File.Exists(factoryPath))
            {
                try { baseView = RimeConfigView.FromYaml(File.ReadAllText(factoryPath)); }
                catch
                {
                    // 出厂文件读不出来不能让整页打不开 —— 退化为「只有用户 patch」，
                    // 未设置的项走代码内默认，比抛异常有用得多。
                }
            }
        }

        var customPath = Path.Combine(_environment.UserDirectory, customName);
        var custom = new CustomYamlFile(customPath);
        if (File.Exists(customPath)) custom.Load();

        return RimeConfigView.MergePatch(baseView, custom.Patch);
    }

    /// <summary>
    /// 取某个切换键的动作。不在已知动作表里的一律当「未设置」，
    /// 免得把用户手写的怪值原样显示在下拉里、一点「应用」又被写回去。
    /// </summary>
    private static string SwitchKeyOf(RimeConfigView view, string key) =>
        view.TryGetString("ascii_composer/switch_key/" + key, out var value)
        && SwitchActionIds.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value
            : "";

    private void RaiseAllChanged()
    {
        OnPropertyChanged(nameof(GoodOldCapsLock));
        OnPropertyChanged(nameof(CapsLockAction));
        OnPropertyChanged(nameof(ShiftLeftAction));
        OnPropertyChanged(nameof(ShiftRightAction));
        OnPropertyChanged(nameof(ControlLeftAction));
        OnPropertyChanged(nameof(ControlRightAction));
        OnPropertyChanged(nameof(ShowNotifications));
        OnPropertyChanged(nameof(NotificationTime));
        OnPropertyChanged(nameof(GlobalAscii));
        OnPropertyChanged(nameof(PagingOnScroll));
        OnPropertyChanged(nameof(ClickToCapture));
        OnPropertyChanged(nameof(AsciiTipFollowCursor));
        OnPropertyChanged(nameof(DisplayTrayIcon));
        OnPropertyChanged(nameof(VerticalAutoReverse));
    }

    // ── 应用 ─────────────────────────────────────────────────────────

    public Task ReloadAsync() { Load(); return Task.CompletedTask; }

    public async Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Behavior.Status.Writing");
        try
        {
            Directory.CreateDirectory(_environment.UserDirectory);

            // ⚠️ 必须检查每个文件的返回值：本页一次「应用」要写两个文件，
            // 其中一个解析失败时若继续往下走，最后那句 "Written" 会把失败
            // 状态盖掉 —— 用户看到「已写入」，实际一个字节都没落盘。
            if (!ApplyWeasel()) return;
            if (!ApplyRime()) return;

            StatusText = StatusFromKey("Behavior.Status.Written");
            MarkLoaded();
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Behavior.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <returns>false = 文件解析失败、已拒绝写入（调用方须就此停下）。</returns>
    private bool ApplyWeasel()
    {
        var path = Path.Combine(_environment.UserDirectory, "weasel.custom.yaml");
        var custom = new CustomYamlFile(path);
        if (custom.State == CustomYamlLoadState.Absent) custom.Load();

        if (!custom.IsWritable)
        {
            StatusText = StatusFromKey("Behavior.Status.ParseFailed", custom.LoadError);
            return false;
        }

        custom.Set("show_notifications", ShowNotifications);
        custom.Set("show_notifications_time", NotificationTime);
        custom.Set("global_ascii", GlobalAscii);
        custom.Set("style/paging_on_scroll", PagingOnScroll);
        custom.Set("style/click_to_capture", ClickToCapture);
        custom.Set("style/ascii_tip_follow_cursor", AsciiTipFollowCursor);
        custom.Set("style/display_tray_icon", DisplayTrayIcon);
        custom.Set("style/vertical_auto_reverse", VerticalAutoReverse);

        custom.Save();
        return true;
    }

    /// <returns>false = 文件解析失败、已拒绝写入（调用方须就此停下）。</returns>
    private bool ApplyRime()
    {
        var path = Path.Combine(_environment.UserDirectory, "default.custom.yaml");
        var custom = new CustomYamlFile(path);
        if (custom.State == CustomYamlLoadState.Absent) custom.Load();

        if (!custom.IsWritable)
        {
            StatusText = StatusFromKey("Behavior.Status.ParseFailed", custom.LoadError);
            return false;
        }

        var set = new PatchSet();
        set.Set("ascii_composer/good_old_caps_lock", PatchValue.Of(GoodOldCapsLock));

        var switchKeys = new Dictionary<string, object?>(StringComparer.Ordinal);
        void AddSwitch(string key, string action)
        {
            if (!string.IsNullOrEmpty(action)) switchKeys[key] = action;
        }

        AddSwitch("Caps_Lock", CapsLockAction);
        AddSwitch("Shift_L", ShiftLeftAction);
        AddSwitch("Shift_R", ShiftRightAction);
        AddSwitch("Control_L", ControlLeftAction);
        AddSwitch("Control_R", ControlRightAction);

        // 全空就整键删掉，而不是写个空字典 —— 空字典会让 Rime 认为
        // 「用户显式声明了没有切换键」，把方案自带的切换键也一并屏蔽。
        if (switchKeys.Count > 0) set.Set("ascii_composer/switch_key", PatchValue.Dictionary(switchKeys));
        else set.Remove("ascii_composer/switch_key");

        custom.ApplyLineEdits(set);
        return true;
    }

    // ── 本地化 ───────────────────────────────────────────────────────

    private static string ActionNameKey(string id) =>
        string.IsNullOrEmpty(id) ? "Behavior.SwitchAction.Unset" : "Behavior.SwitchAction." + id;

    public void RefreshTexts()
    {
        StatusText = Restatus();
        foreach (var option in SwitchActionOptions)
        {
            option.Name = L10n.Instance.T(ActionNameKey(option.Id));
        }
    }
}
