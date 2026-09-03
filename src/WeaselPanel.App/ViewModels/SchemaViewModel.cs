//  Weasel Panel — 方案（输入方案）管理 ViewModel。
//
//  数据来源：SchemaCatalog（Core 层扫描结果）。
//  数据去向：default.custom.yaml 的 patch/schema_list。
//
//  写盘走 CustomYamlFile.ApplyLineEdits + PatchSet → schema_list 作为整体块替换，
//  保留用户在同文件里手写的其它键与注释。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.App.ViewModels;

/// <summary>方案列表里的一行（用于双向绑定 ObservableCollection&lt;SchemaRow&gt;）。</summary>
/// <remarks>
/// 实现 INotifyPropertyChanged 只为两件事：
///   1. <see cref="IsDefault"/> —— 列表首位即默认方案，上下移动时要能实时挪动「默认」标记，
///      重建整个列表会丢选中态，代价更大。
///   2. 语言切换时由 <see cref="RaiseLanguageChanged"/> 重新发出派生文本的通知。
///      OriginLabel / Subtitle / 孤儿条目的显示名都是「取值那一刻拼好的字符串」，
///      WPF 不会替我们重取。
/// </remarks>
public sealed class SchemaRow : INotifyPropertyChanged
{
    public required string SchemaId { get; init; }

    /// <summary>方案自己的名字。孤儿条目（磁盘上找不到 schema.yaml）为 null，
    /// 显示时由 <see cref="DisplayName"/> 用 id + 「（未安装）」兜底。</summary>
    public string? Name { get; init; }

    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public required bool IsBuiltIn { get; init; }

    /// <summary>若被启用，但在磁盘上找不到对应 schema.yaml，标 true（孤儿条目）。</summary>
    public bool IsOrphan { get; init; }

    private bool _isDefault;

    /// <summary>是否是默认方案（即「已启用」列表的第一项）。</summary>
    public bool IsDefault
    {
        get => _isDefault;
        set
        {
            if (_isDefault == value) return;
            _isDefault = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName => Name ?? SchemaId + L10n.Instance.T("Schema.NotInstalled");

    public string OriginLabel => L10n.Instance.T(IsBuiltIn ? "Schema.OriginBuiltIn" : "Schema.OriginUser");

    public string Subtitle =>
        !string.IsNullOrWhiteSpace(Description) ? Description! :
        !string.IsNullOrWhiteSpace(Author) ? Author! :
        IsOrphan ? L10n.Instance.T("Schema.OrphanDesc") :
        SchemaId;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>语言切换后：这三处文本都是在取值时才拼的，通知一次让绑定重取。</summary>
    public void RaiseLanguageChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(OriginLabel));
        OnPropertyChanged(nameof(Subtitle));
    }
}

public sealed class SchemaViewModel : ViewModelBase, ILanguageAware, IPanelActions
{
    private readonly WeaselEnvironment _environment;
    private SchemaCatalog _catalog = SchemaCatalog.Empty;
    private string _baseline = "";

    /// <summary>用户目录里「当前启用」的方案列表（按用户视角排序，可编辑）。</summary>
    public ObservableCollection<SchemaRow> ActiveSchemas { get; } = new();

    /// <summary>未启用但已安装的方案（候选）。</summary>
    public ObservableCollection<SchemaRow> AvailableSchemas { get; } = new();

    private SchemaRow? _selectedActive;
    public SchemaRow? SelectedActive
    {
        get => _selectedActive;
        set
        {
            if (Set(ref _selectedActive, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private SchemaRow? _selectedAvailable;
    public SchemaRow? SelectedAvailable
    {
        get => _selectedAvailable;
        set
        {
            if (Set(ref _selectedAvailable, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    private bool _hasLoaded;
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => Set(ref _hasLoaded, value);
    }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SetDefaultCommand { get; }
    public ICommand ApplyCommand { get; }

    public SchemaViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        // DelegateCommand 用 _canExecute 闭包重算；list 变动后需手动 InvalidateRequerySuggested。
        AddCommand = new DelegateCommand(Add, () => SelectedAvailable is not null);
        RemoveCommand = new DelegateCommand(Remove, () => SelectedActive is not null);
        MoveUpCommand = new DelegateCommand(() => Move(-1),
            () => SelectedActive is not null && ActiveSchemas.IndexOf(SelectedActive) > 0);
        MoveDownCommand = new DelegateCommand(() => Move(+1),
            () => SelectedActive is not null && ActiveSchemas.IndexOf(SelectedActive) < ActiveSchemas.Count - 1);
        // 已在首位（或压根没选）时「设为默认」没有意义，直接禁用，省得用户点了没反应
        SetDefaultCommand = new DelegateCommand(SetDefault,
            () => SelectedActive is not null && ActiveSchemas.IndexOf(SelectedActive) > 0);
        ApplyCommand = new RelayCommand(ApplyAsync, () => ActiveSchemas.Count > 0);

        // ObservableCollection 不带 setter 触发的 CanExecute 通知，改写 ActiveSchemas 后统一刷一次。
        // 顺带驱动两张列表的空状态提示 —— XAML 没法直接判「集合为空」。
        ActiveSchemas.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowNoActive));
            CommandManager.InvalidateRequerySuggested();
        };
        AvailableSchemas.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowNoAvailable));
            CommandManager.InvalidateRequerySuggested();
        };
    }

    /// <summary>左栏（已启用）为空时为真 —— 通常是还没扫到方案，或全被移走了。</summary>
    public bool ShowNoActive => ActiveSchemas.Count == 0;

    /// <summary>右栏（可启用）为空时为真 —— 所有已安装方案都已在左栏。</summary>
    public bool ShowNoAvailable => AvailableSchemas.Count == 0;

    // ── 加载 ────────────────────────────────────────────────────────────────

    public void Load()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Schema.Status.Scanning");
        try
        {
            _catalog = SchemaCatalog.Build(_environment.UserDirectory, _environment.SharedDataDirectory);
            RebuildLists();
            HasLoaded = true;
            StatusText = _catalog.All.Count == 0
                ? StatusFromKey("Schema.Status.None")
                : StatusFromKey("Schema.Status.Scanned", _catalog.All.Count, ActiveSchemas.Count);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Schema.Status.ScanFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildLists()
    {
        ActiveSchemas.Clear();
        AvailableSchemas.Clear();

        var orphan = new HashSet<string>(_catalog.OrphanIds, StringComparer.Ordinal);
        var active = new HashSet<string>(_catalog.EffectiveActiveIds, StringComparer.Ordinal);

        foreach (var id in _catalog.EffectiveActiveIds)
        {
            if (_catalog.All.TryGetValue(id, out var s))
            {
                ActiveSchemas.Add(MakeRow(s, isOrphan: false));
            }
            else
            {
                ActiveSchemas.Add(MakeOrphanRow(id));
            }
        }

        foreach (var s in _catalog.AvailableToAdd)
        {
            AvailableSchemas.Add(MakeRow(s, isOrphan: false));
        }

        // 即便用户没启用，也把 orphan 列在「可用」里以便他能看到
        foreach (var id in orphan)
        {
            if (_catalog.All.ContainsKey(id)) continue;
            AvailableSchemas.Add(MakeOrphanRow(id));
        }

        SyncDefault();
    }

    /// <summary>默认方案是哪一行：ActiveSchemas 的首位即默认，靠 IsDefault 在界面上标出来。</summary>
    private void SyncDefault()
    {
        for (var i = 0; i < ActiveSchemas.Count; i++)
            ActiveSchemas[i].IsDefault = i == 0;

        OnPropertyChanged(nameof(DefaultSummary));
    }

    /// <summary>「默认方案：X」那一行。没有启用任何方案时显示「（未启用任何方案）」。</summary>
    public string DefaultSummary => ActiveSchemas.Count == 0
        ? L10n.Instance.T("Schema.DefaultNone")
        : L10n.Instance.T("Schema.DefaultSummary",
            L10n.Instance.T("Schema.DefaultLabel"), ActiveSchemas[0].DisplayName);

    private static SchemaRow MakeRow(InputSchema s, bool isOrphan) => new()
    {
        SchemaId = s.SchemaId,
        Name = s.Name,
        Version = s.Version,
        Author = s.Author,
        Description = s.Description,
        IsBuiltIn = s.IsBuiltIn,
        IsOrphan = isOrphan,
    };

    /// <summary>孤儿条目：配置里启用了，磁盘上却没有对应的 schema.yaml。</summary>
    private static SchemaRow MakeOrphanRow(string id) => new()
    {
        SchemaId = id,
        Name = null,
        IsBuiltIn = false,
        IsOrphan = true,
    };

    // ── 列表操作 ────────────────────────────────────────────────────────────

    /// <summary>把选中的方案挪到首位 —— Rime 的 schema_list 顺序即切换顺序，首位就是默认。</summary>
    public void SetDefault()
    {
        var src = SelectedActive;
        if (src is null) return;

        var idx = ActiveSchemas.IndexOf(src);
        if (idx <= 0) return;

        ActiveSchemas.Move(idx, 0);
        SelectedActive = src;
        SyncDefault();
        StatusText = StatusFromKey("Schema.Status.DefaultSet", src.DisplayName);
    }

    private void Add()
    {
        var src = SelectedAvailable;
        if (src is null) return;

        // 防重：同 id 已存在则不再加（理论上 UI 已过滤，但 paranoid 兜底）
        foreach (var row in ActiveSchemas)
            if (row.SchemaId == src.SchemaId) return;

        ActiveSchemas.Add(src);
        AvailableSchemas.Remove(src);
        SelectedActive = src;
        SelectedAvailable = null;
        SyncDefault();
        StatusText = StatusFromKey("Schema.Status.Added", src.DisplayName);
    }

    private void Remove()
    {
        var src = SelectedActive;
        if (src is null) return;

        // 孤儿条目（即便是被禁用的）只在 Active 列表里出现一次；删掉即可
        if (!src.IsOrphan)
        {
            AvailableSchemas.Add(src);
        }
        ActiveSchemas.Remove(src);
        SelectedActive = null;
        SyncDefault();
        StatusText = StatusFromKey("Schema.Status.Removed", src.DisplayName);
    }

    private void Move(int delta)
    {
        var src = SelectedActive;
        if (src is null) return;

        var idx = ActiveSchemas.IndexOf(src);
        var target = idx + delta;
        if (target < 0 || target >= ActiveSchemas.Count) return;

        ActiveSchemas.Move(idx, target);
        SelectedActive = src;  // 选中态要保持，否则 MoveUp 后选中会跑到空

        // 顺序变了，默认方案（首位）可能跟着变
        SyncDefault();
    }

    /// <summary>
    /// 语言切换后重建本页文本。
    /// 行内的 OriginLabel / Subtitle / 孤儿条目的显示名交给各自的 RaiseLanguageChanged
    /// 通知重取，不重建列表 —— 重建会丢选中态，而选中态在方案页是有意义的上下文。
    /// </summary>
    public void RefreshTexts()
    {
        StatusText = Restatus();

        if (!HasLoaded)
        {
            // 还没扫过盘（用户没点开这一页）—— 别为了切语言去做一次磁盘扫描
            return;
        }

        foreach (var row in ActiveSchemas) row.RaiseLanguageChanged();
        foreach (var row in AvailableSchemas) row.RaiseLanguageChanged();

        OnPropertyChanged(nameof(DefaultSummary));
    }
    // ── 应用 ────────────────────────────────────────────────────────────────

    public new bool IsDirty => Signature() != _baseline;

    private string Signature() => string.Join(",", ActiveSchemas.Select(r => r.SchemaId));

    public Task ReloadAsync() { Load(); return Task.CompletedTask; }

    public async System.Threading.Tasks.Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Schema.Status.Writing");
        try
        {
            Directory.CreateDirectory(_environment.UserDirectory);
            var path = Path.Combine(_environment.UserDirectory, "default.custom.yaml");
            var custom = new CustomYamlFile(path);
            if (custom.State == CustomYamlLoadState.Absent) custom.Load();   // 首次：再次确认

            if (!custom.IsWritable)
            {
                StatusText = StatusFromKey("Schema.Status.ParseFailed", custom.LoadError);
                return;
            }

            var ids = ActiveSchemas.Select(r => r.SchemaId).ToList();
            var set = new PatchSet();
            set.Set("schema_list", PatchValue.SchemaList(ids));
            custom.ApplyLineEdits(set);

            StatusText = StatusFromKey("Schema.Status.Written", path, ids.Count);

            _baseline = Signature();
            OnPropertyChanged(nameof(IsDirty));
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Schema.Status.WriteFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}