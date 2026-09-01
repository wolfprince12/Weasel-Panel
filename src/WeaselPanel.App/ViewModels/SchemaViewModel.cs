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
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.App.ViewModels;

/// <summary>方案列表里的一行（用于双向绑定 ObservableCollection&lt;SchemaRow&gt;）。</summary>
public sealed class SchemaRow
{
    public required string SchemaId { get; init; }
    public required string DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Description { get; init; }
    public required bool IsBuiltIn { get; init; }
    /// <summary>若被启用，但在磁盘上找不到对应 schema.yaml，标 true（孤儿条目）。</summary>
    public bool IsOrphan { get; init; }

    public string OriginLabel => IsBuiltIn ? "内置" : "用户";

    public string Subtitle =>
        !string.IsNullOrWhiteSpace(Description) ? Description! :
        !string.IsNullOrWhiteSpace(Author) ? Author! :
        IsOrphan ? "（未在磁盘找到对应方案文件）" :
        SchemaId;
}

public sealed class SchemaViewModel : ViewModelBase
{
    private readonly WeaselEnvironment _environment;
    private SchemaCatalog _catalog = SchemaCatalog.Empty;

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

    private string _statusText = "就绪";
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
        ApplyCommand = new RelayCommand(ApplyAsync, () => ActiveSchemas.Count > 0);

        // ObservableCollection 不带 setter 触发的 CanExecute 通知，改写 ActiveSchemas 后统一刷一次。
        ActiveSchemas.CollectionChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
        AvailableSchemas.CollectionChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
    }

    // ── 加载 ────────────────────────────────────────────────────────────────

    public void Load()
    {
        IsBusy = true;
        StatusText = "正在扫描方案……";
        try
        {
            _catalog = SchemaCatalog.Build(_environment.UserDirectory, _environment.SharedDataDirectory);
            RebuildLists();
            HasLoaded = true;
            StatusText = _catalog.All.Count == 0
                ? "未发现任何方案（请确认小狼毫已安装，且共享数据目录存在 *.schema.yaml）"
                : $"已扫描 {_catalog.All.Count} 个方案，{ActiveSchemas.Count} 个已启用";
        }
        catch (Exception ex)
        {
            StatusText = "扫描失败：" + ex.Message;
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
                ActiveSchemas.Add(new SchemaRow
                {
                    SchemaId = id,
                    DisplayName = id + "（未安装）",
                    IsBuiltIn = false,
                    IsOrphan = true,
                });
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
            AvailableSchemas.Add(new SchemaRow
            {
                SchemaId = id,
                DisplayName = id + "（未安装）",
                IsBuiltIn = false,
                IsOrphan = true,
            });
        }
    }

    private static SchemaRow MakeRow(InputSchema s, bool isOrphan) => new()
    {
        SchemaId = s.SchemaId,
        DisplayName = s.Name,
        Version = s.Version,
        Author = s.Author,
        Description = s.Description,
        IsBuiltIn = s.IsBuiltIn,
        IsOrphan = isOrphan,
    };

    // ── 列表操作 ────────────────────────────────────────────────────────────

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
        StatusText = $"已加入 {src.DisplayName}（尚未写入磁盘，请点「应用」）";
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
        StatusText = $"已移除 {src.DisplayName}（尚未写入磁盘，请点「应用」）";
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
    }

    // ── 应用 ────────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task ApplyAsync()
    {
        IsBusy = true;
        StatusText = "正在写入 default.custom.yaml……";
        try
        {
            Directory.CreateDirectory(_environment.UserDirectory);
            var path = Path.Combine(_environment.UserDirectory, "default.custom.yaml");
            var custom = new CustomYamlFile(path);
            if (custom.State == CustomYamlLoadState.Absent) custom.Load();   // 首次：再次确认

            if (!custom.IsWritable)
            {
                StatusText = "配置文件解析失败，已拒绝写入（避免损坏）：" + custom.LoadError;
                return;
            }

            var ids = ActiveSchemas.Select(r => r.SchemaId).ToList();
            var set = new PatchSet();
            set.Set("schema_list", PatchValue.SchemaList(ids));
            custom.ApplyLineEdits(set);

            StatusText = $"已写入 {path}（{ids.Count} 个方案已写盘，需执行部署后生效）";
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
}