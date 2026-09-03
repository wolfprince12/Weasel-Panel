//
//  AppOptionsViewModel.cs — 应用选项页（按程序设定输入法进入时的默认状态）
//
//  ── 这一页管的是哪个键 ────────────────────────────────────────────────
//  weasel.custom.yaml 的 patch/app_options/<exe 文件名>/<选项名>。
//  键是 **exe 文件名**（不是程序名、不是窗口标题）：小狼毫由客户端进程的
//  GetModuleFileName(NULL) 取文件名发过来，再 to_lower() 后查表，
//  所以 cmd.exe 与 CMD.EXE 是同一条 —— 列表里若出现两行就是 bug。
//
//  ── 只暴露三个开关，不是偷懒 ──────────────────────────────────────────
//  上游 _LoadAppOptions() 会把 app_options/<exe> 下**所有**键当 bool 读出来
//  set_option()，所以写任何名字都不会报错 —— 但只有 ascii_mode / vim_mode /
//  inline_preedit 真正被消费。多给几个开关只会让人以为生效了。
//  （macOS 鼠须管的 no_inline / inline 在 rime/weasel 全仓不存在，没照抄。）
//
//  ── 为什么保存后要回读 ────────────────────────────────────────────────
//  写盘规则是「与出厂相同的值一律不写，已有的删掉」。于是用户把 cmd.exe 的
//  「默认英文」拨回出厂的 true 时，磁盘上那条键会被**删掉**而不是写成 true。
//  不回读的话界面仍显示「已修改」，用户刷新一次又变回「出厂」，像见鬼。
//  回读一次，界面与磁盘立刻一致。
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>
/// 三态下拉的一档。泛型是必需的 —— ComboBox 的 SelectedValue 直接绑到
/// <typeparamref name="T"/>，用字符串再转一次只会多一处出错点。
/// </summary>
public sealed class ValueOption<T> : INotifyPropertyChanged
{
    private string _name;

    public ValueOption(T id, string name)
    {
        Id = id;
        _name = name;
    }

    public T Id { get; }

    /// <summary>显示名。语言包里的值，切语言时由 ViewModel 就地改写。</summary>
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

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 界面上的一行。包一层而不是直接把 <see cref="AppOptionEntry"/> 丢给 DataGrid，
/// 是因为它的开关是普通自动属性 —— 勾一下不通知外层，「保存」按钮就永远点不亮。
/// </summary>
public sealed class AppOptionRow : INotifyPropertyChanged
{
    public const int InlineFollow = 0;
    public const int InlineOn = 1;
    public const int InlineOff = 2;

    private readonly AppOptionEntry _entry;
    private readonly Action _onChanged;

    public AppOptionRow(AppOptionEntry entry, Action onChanged, IReadOnlyList<ValueOption<int>> inlineOptions)
    {
        _entry = entry;
        _onChanged = onChanged;
        InlineOptions = inlineOptions;
    }

    /// <summary>底层数据。保存时原样交回 <see cref="AppOptionsFile.Save"/>。</summary>
    public AppOptionEntry Entry => _entry;

    /// <summary>
    /// exe 文件名。列表里是只读的 —— 就地改名等于「删掉旧键加一条新键」，
    /// 而旧键若是出厂条目，它删不掉，改名后会出现两行指着同一个程序。
    /// 要改就删掉重加，语义清楚。
    /// </summary>
    public string ExeKey => _entry.ExeKey;

    public bool AsciiMode
    {
        get => _entry.AsciiMode;
        set
        {
            if (_entry.AsciiMode == value) return;
            _entry.AsciiMode = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public bool VimMode
    {
        get => _entry.VimMode;
        set
        {
            if (_entry.VimMode == value) return;
            _entry.VimMode = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>
    /// 三态开关用 int 而不是 <c>bool?</c>：XAML 里把 ComboBox 的 SelectedValue
    /// 绑到可空布尔要再写一个转换器，绑到 int 直接就能比。
    /// </summary>
    public int InlineMode
    {
        get => _entry.InlinePreedit switch
        {
            true => InlineOn,
            false => InlineOff,
            _ => InlineFollow,
        };
        set
        {
            bool? next = value switch
            {
                InlineOn => true,
                InlineOff => false,
                _ => null,
            };
            if (_entry.InlinePreedit == next) return;
            _entry.InlinePreedit = next;
            OnPropertyChanged();
            _onChanged();
        }
    }

    /// <summary>三态选项表。由 ViewModel 持有同一份实例，各行只是引用 ——
    /// 切语言时只改那一份，所有行的下拉一起变。</summary>
    public IReadOnlyList<ValueOption<int>> InlineOptions { get; }

    /// <summary>出厂 weasel.yaml 里就有这一条（如 cmd.exe / conhost.exe）。</summary>
    public bool IsBuiltIn => _entry.IsBuiltIn;

    public bool IsCustomized => _entry.IsCustomized;

    /// <summary>「共 N 条」这类拼好的串不会随语言自动变，切语言时由 VM 重建。</summary>
    public string OriginText => L10n.Instance.T(
        _entry.IsBuiltIn ? "AppOptions.OriginBuiltIn" : "AppOptions.OriginUser");

    public void RefreshTexts() => OnPropertyChanged(nameof(OriginText));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AppOptionsViewModel : ViewModelBase, ILanguageAware, IPanelActions
{
    /// <summary>三态下拉三档的语言包键，下标即 <see cref="AppOptionRow"/> 的三个常量。</summary>
    private static readonly string[] InlineKeys =
    {
        "AppOptions.Inline.Follow",
        "AppOptions.Inline.On",
        "AppOptions.Inline.Off",
    };

    private readonly WeaselEnvironment _environment;
    private readonly Dictionary<string, string> _presetLabelKeys = new(StringComparer.Ordinal);

    private AppOptionsFile? _file;
    private bool _isBusy;
    private bool _hasLoaded;
    private string _statusText = "";
    private string _filter = "";
    private string _newExeKey = "";
    private AppOptionRow? _selected;

    /// <summary>载入（或保存回读）那一刻的快照。脏值按内容比，拨过去再拨回来不算改动。</summary>
    private List<(string ExeKey, bool Ascii, bool Vim, bool? Inline)> _baseline = new();

    public AppOptionsViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        BuildInlineOptions();
        BuildPresets();

        // 过滤走 ICollectionView，不重建集合 —— 重建会丢掉当前选中行与滚动位置，
        // 用户每敲一个字光标就跳回列表顶部。
        RowView = CollectionViewSource.GetDefaultView(Rows);
        RowView.Filter = o => o is AppOptionRow r && MatchesFilter(r);

        AddCommand = new DelegateCommand(Add, () => CanAdd);
        RemoveCommand = new DelegateCommand(Remove, () => SelectedRow is { IsBuiltIn: false });
        ClearFilterCommand = new DelegateCommand(() => Filter = "", () => Filter.Length > 0);
        SaveCommand = new RelayCommand(SaveAsync, () => _file is not null && IsDirty && !_isBusy);
        ReloadCommand = new RelayCommand(ReloadAsync, () => !_isBusy);
        ResetAllCommand = new DelegateCommand(ResetAll, () => _file is not null && !_isBusy);
    }

    // ── 数据 ────────────────────────────────────────────────────────────

    public ObservableCollection<AppOptionRow> Rows { get; } = new();
    public ObservableCollection<ValueOption<string>> Presets { get; } = new();
    public ObservableCollection<ValueOption<int>> InlineOptions { get; } = new();
    public ICollectionView RowView { get; }

    public AppOptionRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            ((DelegateCommand)RemoveCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>输入框里的 exe 名（待添加）。</summary>
    public string NewExeKey
    {
        get => _newExeKey;
        set
        {
            if (!Set(ref _newExeKey, value ?? "")) return;
            ((DelegateCommand)AddCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 预设下拉的落点。用 OneWayToSource 而不是双向：下拉只是「快速填充输入框」
    /// 的入口，不是独立状态 —— 双向绑定的话，用户在输入框里打了列表里没有的名字，
    /// ComboBox 会立刻把选中值清成 null 并反向回写，把刚打的字吞掉。
    /// </summary>
    public string? PresetPick
    {
        get => null;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            NewExeKey = value;
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            RowView.Refresh();
            OnPropertyChanged(nameof(ShowNoRows));
            OnPropertyChanged(nameof(ShowFilteredEmpty));
            ((DelegateCommand)ClearFilterCommand).RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsNotBusy));
            RefreshDerived();
        }
    }

    public bool IsNotBusy => !IsBusy;

    /// <summary>
    /// 按内容比，不靠「改过就是脏」的标记 —— 拨过去再拨回来不该亮保存按钮。
    /// 条目数只有个位数，逐条比的成本可以忽略。
    /// </summary>
    public new bool IsDirty
    {
        get
        {
            if (!_hasLoaded) return false;
            if (Rows.Count != _baseline.Count) return true;
            for (var i = 0; i < Rows.Count; i++)
                if (Snapshot(Rows[i]) != _baseline[i]) return true;
            return false;
        }
    }

    public bool HasCustomizations => Rows.Any(r => r.IsCustomized);

    public int RowCount => Rows.Count;

    public string RowCountText => L10n.Instance.T("AppOptions.RowCountFormat", RowCount);

    public string FilePath => _file?.Custom.FilePath ?? "";

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>一条都没有。</summary>
    public bool ShowNoRows => Rows.Count == 0;

    /// <summary>有条目，但被搜索框全过滤掉了。跟「一条都没有」是两回事。</summary>
    public bool ShowFilteredEmpty => Rows.Count > 0 && RowView.IsEmpty;

    private bool CanAdd
    {
        get
        {
            var key = _newExeKey.Trim();
            if (key.Length == 0) return false;
            return !Rows.Any(r => r.ExeKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ── 命令 ────────────────────────────────────────────────────────────

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ResetAllCommand { get; }

    // ── 加载 ────────────────────────────────────────────────────────────

    public void Load()
    {
        if (_hasLoaded) return;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("AppOptions.Status.Loading");
        try
        {
            var file = await Task.Run(() => AppOptionsFile.Load(_environment));
            _file = file;
            RebuildRows(file.Entries());
            _hasLoaded = true;
            StatusText = StatusFromKey("AppOptions.Status.Loaded", Rows.Count);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("AppOptions.Status.LoadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshDerived();
        }
    }

    public async Task ReloadAsync()
    {
        if (IsDirty && !Confirm(L10n.Instance.T("AppOptions.DiscardTitle"),
                                L10n.Instance.T("AppOptions.DiscardBody"))) return;

        _hasLoaded = false;
        await LoadAsync();
    }

    // ── 编辑 ────────────────────────────────────────────────────────────

    private void Add()
    {
        var key = _newExeKey.Trim();
        if (key.Length == 0) return;
        if (Rows.Any(r => r.ExeKey.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = StatusFromKey("AppOptions.Status.Duplicate", key);
            return;
        }

        var row = new AppOptionRow(
            new AppOptionEntry
            {
                ExeKey = key,
                // 默认英文是绝大多数人加条目的理由，别让人加了还得再勾一下
                AsciiMode = AppOptionsFile.DefaultAsciiModeWhenAdded,
                VimMode = false,
                InlinePreedit = null,
            },
            OnRowChanged,
            InlineOptions);

        Rows.Add(row);
        SelectedRow = row;
        NewExeKey = "";
        StatusText = StatusFromKey("AppOptions.Status.Added", key);
        RefreshDerived();
    }

    /// <summary>
    /// 移除一行。出厂条目不让删 —— 它在 weasel.yaml 里，删了下次读盘还会回来，
    /// 那才是真见鬼。要改它就改开关，要清掉本面板写过的键用「恢复默认」。
    /// </summary>
    private void Remove()
    {
        if (SelectedRow is null || SelectedRow.IsBuiltIn) return;

        var key = SelectedRow.ExeKey;
        Rows.Remove(SelectedRow);
        SelectedRow = null;

        StatusText = StatusFromKey("AppOptions.Status.Removed", key);
        RefreshDerived();
    }

    private async Task SaveAsync()
    {
        if (_file is null) return;

        IsBusy = true;
        StatusText = StatusFromKey("AppOptions.Status.Saving");
        try
        {
            var rows = Rows.ToList();
            var selectKey = SelectedRow?.ExeKey;
            await Task.Run(() => _file.Save(rows.Select(r => r.Entry)));

            // 回读：列表的「来源」列、键的大小写归一、以及「与出厂相同就删键」
            // 抹掉的那几行，只有重新读盘才看得到。
            var file = await Task.Run(() => AppOptionsFile.Load(_environment));
            _file = file;
            RebuildRows(file.Entries(), selectKey);

            StatusText = StatusFromKey("AppOptions.Status.Saved", Rows.Count);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("AppOptions.Status.SaveFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshDerived();
        }
    }

    /// <summary>全局部署协调器入口：与本页「保存」共用同一段代码。</summary>
    public Task ApplyAsync() => SaveAsync();

    /// <summary>清掉本面板在 weasel.custom.yaml 里写过的全部 app_options 键。
    /// 用户在同一节点下手写的其它键原样保留（Core 只认三个托管选项名）。</summary>
    private void ResetAll()
    {
        if (_file is null) return;
        if (!Confirm(L10n.Instance.T("AppOptions.ResetAllTitle"),
                     L10n.Instance.T("AppOptions.ResetAllBody"))) return;

        try
        {
            _file.ClearManaged();
            var file = AppOptionsFile.Load(_environment);
            _file = file;
            RebuildRows(file.Entries());
            StatusText = StatusFromKey("AppOptions.Status.Reset");
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("AppOptions.Status.SaveFailed", ex.Message);
        }
        finally
        {
            RefreshDerived();
        }
    }

    // ── 内部 ────────────────────────────────────────────────────────────

    private void RebuildRows(IEnumerable<AppOptionEntry> entries, string? selectKey = null)
    {
        Rows.Clear();
        foreach (var e in entries)
            Rows.Add(new AppOptionRow(e, OnRowChanged, InlineOptions));

        _baseline = Rows.Select(Snapshot).ToList();

        SelectedRow = selectKey is null
            ? null
            : Rows.FirstOrDefault(r => r.ExeKey.Equals(selectKey, StringComparison.OrdinalIgnoreCase));
    }

    private static (string ExeKey, bool Ascii, bool Vim, bool? Inline) Snapshot(AppOptionRow row) =>
        (row.ExeKey, row.AsciiMode, row.VimMode, row.Entry.InlinePreedit);

    private void OnRowChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(RowCount));
        OnPropertyChanged(nameof(RowCountText));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(HasCustomizations));
        OnPropertyChanged(nameof(ShowNoRows));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ReloadCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)AddCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)RemoveCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)ResetAllCommand).RaiseCanExecuteChanged();
    }

    private bool MatchesFilter(AppOptionRow row)
    {
        if (_filter.Length == 0) return true;
        var f = _filter.Trim();
        return row.ExeKey.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Confirm(string title, string body) =>
        MessageBox.Show(body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
        == MessageBoxResult.Yes;

    // ── 本地化 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 三态下拉的三档。第一次建实例，之后只改名 —— 重建集合会让所有行的
    /// ComboBox 先变空再回填，闪一下。
    /// </summary>
    private void BuildInlineOptions()
    {
        if (InlineOptions.Count == 0)
        {
            InlineOptions.Add(new ValueOption<int>(AppOptionRow.InlineFollow, ""));
            InlineOptions.Add(new ValueOption<int>(AppOptionRow.InlineOn, ""));
            InlineOptions.Add(new ValueOption<int>(AppOptionRow.InlineOff, ""));
        }

        for (var i = 0; i < InlineKeys.Length; i++)
            InlineOptions[i].Name = L10n.Instance.T(InlineKeys[i]);
    }

    /// <summary>预设下拉。exe 名是专有名词，只有「命令提示符」这类描述要本地化，
    /// 所以语言包里的值是带 {0} 占位符的模板。</summary>
    private void BuildPresets()
    {
        if (Presets.Count == 0)
        {
            foreach (var (labelKey, exe) in AppOptionsFile.Presets)
            {
                _presetLabelKeys[exe] = labelKey;
                Presets.Add(new ValueOption<string>(exe, L10n.Instance.T(labelKey, exe)));
            }
            return;
        }

        foreach (var option in Presets)
            if (_presetLabelKeys.TryGetValue(option.Id, out var key))
                option.Name = L10n.Instance.T(key, option.Id);
    }

    public void RefreshTexts()
    {
        StatusText = Restatus();
        BuildInlineOptions();
        BuildPresets();

        // 拼好的串不会随语言自动变，必须重建
        OnPropertyChanged(nameof(RowCountText));
        foreach (var row in Rows) row.RefreshTexts();
    }
}
