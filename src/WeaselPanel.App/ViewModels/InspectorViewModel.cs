//
//  InspectorViewModel.cs — 配置查看器（只读）
//
//  ── 这一页回答的问题 ──────────────────────────────────────────────────
//  「我改了半天，小狼毫到底读到的是什么？」
//  面板的各个编辑页都是「写」，没有一处能回答「读到的到底是什么」。
//  用户报 bug 时被问「你的 weasel.yaml 里写了什么」，往往答不上来 ——
//  因为生效值是「基础文件 + custom 补丁」合并后的结果，肉眼看文件看不出来。
//
//  ── Rime 的配置查找与覆盖链（据上游行为，勿凭直觉改）───────────────────
//   1) 同名文件：**用户目录整份覆盖共享目录**，不是逐键合并。
//      即 %AppData%\Rime\weasel.yaml 存在时，共享目录那份完全不参与。
//   2) 补丁：<name>.custom.yaml 里的 patch: 节点，按 Rime 语义并入 <name>.yaml。
//      映射深度合并（只改 style/font_point 不会抹掉 style/layout），
//      列表整体替换（schema_list 写了就整份覆盖）。
//   3) 补丁文件只在**用户目录**里找。共享目录里的 .custom.yaml 不生效。
//
//  所以「生效值」= 合并( 用户目录<name>.yaml ?? 共享目录<name>.yaml ,
//                        用户目录<name>.custom.yaml 的 patch 节点 )
//
//  ── 为什么只读 ────────────────────────────────────────────────────────
//  在这一页改值等于绕过各编辑页的落盘规则（去重、写前备份、与出厂默认值
//  比对），两条写路径并存必然打架。此页只负责「看清」，要改请去对应编辑页。
//

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;
using WeaselPanel.Core.Yaml;

namespace WeaselPanel.App.ViewModels;

/// <summary>下拉框里的一项配置文件。</summary>
public sealed class ConfigFileOption
{
    public required string BaseName { get; init; }
    public required string Name { get; init; }

    public override string ToString() => Name;
}

/// <summary>合并后的一个配置项。</summary>
public sealed class ConfigEntryRow
{
    public required string Path { get; init; }
    public required string Value { get; init; }

    /// <summary>「基础文件」或「补丁」。已是本地化后的文本 ——
    /// 这一列要跟着语言切换变，所以不能在构造时写死。</summary>
    public required string OriginText { get; init; }

    /// <summary>被 .custom.yaml 覆盖过的项。UI 上给一点主色，让「我改过哪些」
    /// 一眼可见 —— 排查配置问题时最想先看到的正是这些。</summary>
    public required bool IsOverridden { get; init; }
}

public sealed class InspectorViewModel : ViewModelBase, ILanguageAware
{
    /// <summary>
    /// 这两个文件是 Rime 自己维护的运行状态（安装 id、同步时间戳、上次部署记录），
    /// 不是用户配置，列出来只会干扰判断。
    /// </summary>
    private static readonly string[] ManagedStateFiles =
    {
        "installation.yaml",
        "user.yaml",
    };

    /// <summary>排在最前面的常用配置。其余按文件名排序。</summary>
    private static readonly string[] PreferredFiles =
    {
        "weasel",
        "default",
    };

    private readonly WeaselEnvironment _environment;
    private string _fileName = "";
    private string _filter = "";
    private string _basePathText = "";
    private string _patchPathText = "";
    private bool _isBusy;

    public InspectorViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        ReloadCommand = new RelayCommand(ReloadAsync, () => !_isBusy);
        CopyCommand = new DelegateCommand(CopyAll, () => Entries.Count > 0);
        OpenDirCommand = new DelegateCommand(OpenContainingFolder);
        ClearFilterCommand = new DelegateCommand(() => Filter = "");

        EntryView = CollectionViewSource.GetDefaultView(Entries);
        EntryView.Filter = o => o is ConfigEntryRow row && MatchesFilter(row);
    }

    // ── 数据 ────────────────────────────────────────────────────────────

    public ObservableCollection<ConfigFileOption> FileOptions { get; } = new();

    public ObservableCollection<ConfigEntryRow> Entries { get; } = new();

    /// <summary>
    /// 过滤走 ICollectionView 而不是重建集合。重建会把选中行和滚动位置一起丢掉，
    /// 用户每敲一个搜索字符光标就跳回顶部，体验上等于不能用。
    /// </summary>
    public ICollectionView EntryView { get; }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (!Set(ref _fileName, value ?? "")) return;
            LoadSelected();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value ?? "")) return;
            EntryView.Refresh();
            OnPropertyChanged(nameof(ShowFilteredEmpty));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ((RelayCommand)ReloadCommand).RaiseCanExecuteChanged();
        }
    }

    public string BasePathText
    {
        get => _basePathText;
        private set => Set(ref _basePathText, value);
    }

    public string PatchPathText
    {
        get => _patchPathText;
        private set => Set(ref _patchPathText, value);
    }

    /// <summary>有没有对应的 custom 补丁文件。没有时 UI 把那一行压掉，
    /// 不留一个写着「未找到」的空行。</summary>
    public bool HasPatch { get; private set; }

    public string EntryCountText =>
        L10n.Instance.T("Inspector.EntryCountFormat", Entries.Count);

    public string OverrideCountText =>
        L10n.Instance.T("Inspector.OverrideCountFormat", OverrideCount);

    public int OverrideCount { get; private set; }

    public bool ShowNoEntries => Entries.Count == 0;
    public bool ShowFilteredEmpty => Entries.Count > 0 && EntryView.IsEmpty;

    private string _statusText = "";

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // ── 命令 ────────────────────────────────────────────────────────────

    public ICommand ReloadCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenDirCommand { get; }
    public ICommand ClearFilterCommand { get; }

    // ── 加载 ────────────────────────────────────────────────────────────

    private bool _hasLoaded;

    public void Load()
    {
        if (_hasLoaded) return;
        _hasLoaded = true;
        Reload();
    }

    /// <summary>首次进页时同步扫一次（文件枚举很快，不值得为它上一次异步）。</summary>
    private void Reload() => ApplyFileList(CollectFileNames());

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            // 只有「枚举目录」这一步上后台线程；集合与视图操作一律留在 UI 线程，
            // 否则改 ObservableCollection 会直接抛「调用线程无法访问此集合」。
            var names = await Task.Run(CollectFileNames);
            ApplyFileList(names);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFileList(string[] names)
    {
        FileOptions.Clear();
        foreach (var name in names)
        {
            FileOptions.Add(new ConfigFileOption { BaseName = name, Name = name + ".yaml" });
        }

        // 保留用户上一次选的文件；它若已不在列表里（文件被删了）就回到首选。
        if (!names.Contains(_fileName, StringComparer.OrdinalIgnoreCase))
        {
            _fileName = names.FirstOrDefault(n => n == "weasel") ?? names.FirstOrDefault() ?? "";
            OnPropertyChanged(nameof(FileName));
        }

        LoadSelected();
    }

    /// <summary>扫描用户目录与共享目录里的配置文件基础名。</summary>
    private string[] CollectFileNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectFrom(_environment.UserDirectory, set);
        if (_environment.SharedDataDirectory is { } shared) CollectFrom(shared, set);

        var ordered = new List<string>();
        foreach (var preferred in PreferredFiles)
        {
            if (set.Contains(preferred)) ordered.Add(preferred);
        }
        foreach (var name in set.Where(n => !PreferredFiles.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(name);
        }
        return ordered.ToArray();

        static void CollectFrom(string? directory, HashSet<string> sink)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);

                // *.custom.yaml 不是独立配置，是补丁，按基础名归到它对应的文件下。
                if (fileName.EndsWith(".custom.yaml", StringComparison.OrdinalIgnoreCase)) continue;
                if (ManagedStateFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;

                var baseName = Path.GetFileNameWithoutExtension(fileName);
                if (baseName.Length > 0) sink.Add(baseName);
            }
        }
    }

    private void LoadSelected()
    {
        if (_fileName.Length == 0)
        {
            Entries.Clear();
            BasePathText = "";
            PatchPathText = "";
            HasPatch = false;
            OnPropertyChanged(nameof(HasPatch));
            return;
        }

        var notFound = L10n.Instance.T("Common.NotFound");

        // 用户目录同名文件整份覆盖共享目录那份（不是逐键合并）。
        var baseSources = _environment.ConfigSources(_fileName + ".yaml");
        var basePath = baseSources.Length > 0 ? baseSources[0] : null;

        // 补丁只在用户目录里找。
        var patchPath = Path.Combine(_environment.UserDirectory, _fileName + ".custom.yaml");
        var patchExists = File.Exists(patchPath);

        BasePathText = basePath ?? notFound;
        PatchPathText = patchExists ? patchPath : notFound;
        HasPatch = patchExists;
        OnPropertyChanged(nameof(HasPatch));

        var baseRoot = ReadRoot(basePath);
        var patchMap = ReadPatchMap(patchExists ? patchPath : null);

        var merged = RimeConfigView.MergePatch(RimeConfigView.FromTree(baseRoot), patchMap);

        // 补丁触及的路径集合。patch 里的键本身就是配置路径（可写成扁平的
        // "style/font_point"，也可写成嵌套映射），所以按路径展开即得。
        var patchPaths = new HashSet<string>(StringComparer.Ordinal);
        FlattenPaths(patchMap, "", patchPaths);

        var baseText = L10n.Instance.T("Inspector.Origin.Base");
        var patchText = L10n.Instance.T("Inspector.Origin.Patch");

        var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
        Flatten(merged.Root, "", flat);

        var overrides = 0;
        Entries.Clear();
        foreach (var kv in flat.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var overridden = patchPaths.Contains(kv.Key);
            if (overridden) overrides++;

            Entries.Add(new ConfigEntryRow
            {
                Path = kv.Key,
                Value = FormatValue(kv.Value),
                OriginText = overridden ? patchText : baseText,
                IsOverridden = overridden,
            });
        }

        OverrideCount = overrides;

        StatusText = basePath is null
            ? StatusFromKey("Inspector.Status.NoBaseFile", _fileName + ".yaml")
            : StatusFromKey("Inspector.Status.Loaded", Path.GetFileName(basePath), flat.Count);

        EntryView.Refresh();
        OnPropertyChanged(nameof(ShowNoEntries));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
        OnPropertyChanged(nameof(EntryCountText));
        OnPropertyChanged(nameof(OverrideCount));
        OnPropertyChanged(nameof(OverrideCountText));
        ((DelegateCommand)CopyCommand).RaiseCanExecuteChanged();
    }

    // ── 读取与展开 ──────────────────────────────────────────────────────

    private static Dictionary<string, object?> ReadRoot(string? path)
    {
        if (path is null || !File.Exists(path)) return new(StringComparer.Ordinal);
        try
        {
            var text = File.ReadAllText(path);
            if (YamlLoader.Load(text) is { Success: true, Root: not null } result)
            {
                return result.Root;
            }
        }
        catch (IOException) { /* 读不到就当空配置 */ }
        catch (UnauthorizedAccessException) { /* 同上 */ }
        return new(StringComparer.Ordinal);
    }

    /// <summary>取 custom 文件里的 patch 子树。没有就返回空字典。</summary>
    private static Dictionary<string, object?>? ReadPatchMap(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var root = ReadRoot(path);
            return root.TryGetValue("patch", out var node) && node is Dictionary<string, object?> map
                ? map
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>把嵌套树摊平成「路径 → 值」。映射继续下钻，其余（含空映射、列表）作为叶子。</summary>
    private static void Flatten(
        IReadOnlyDictionary<string, object?> map, string prefix, Dictionary<string, object?> sink)
    {
        foreach (var kv in map)
        {
            var path = prefix.Length == 0 ? kv.Key : prefix + "/" + kv.Key;

            if (kv.Value is Dictionary<string, object?> child && child.Count > 0)
            {
                Flatten(child, path, sink);
            }
            else
            {
                sink[path] = kv.Value;
            }
        }
    }

    /// <summary>收集补丁触及的所有路径。与 Flatten 同构，但只要路径不要值。</summary>
    private static void FlattenPaths(
        IReadOnlyDictionary<string, object?>? map, string prefix, HashSet<string> sink)
    {
        if (map is null) return;

        foreach (var kv in map)
        {
            var path = prefix.Length == 0 ? kv.Key : prefix + "/" + kv.Key;

            if (kv.Value is Dictionary<string, object?> child && child.Count > 0)
            {
                FlattenPaths(child, path, sink);
            }
            else
            {
                sink.Add(path);
            }
        }
    }

    /// <summary>
    /// 值的显示文本。刻意不做 YAML 序列化 —— 这一列是给人看的，
    /// 列表写成 <c>[a, b, c]</c> 比还原成块式 YAML 好读得多。
    /// </summary>
    private static string FormatValue(object? value)
    {
        var text = value switch
        {
            null => "~",
            bool b => b ? "true" : "false",
            string s => s.Length == 0 ? "''" : s,
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            System.Collections.IEnumerable seq => FormatList(seq),
            _ => value.ToString() ?? "",
        };

        // 单个值可能很长（比如一整条 key_binder 绑定列表），截断免得把行撑爆
        return text.Length > 220 ? text[..220] + "…" : text;
    }

    private static string FormatList(System.Collections.IEnumerable sequence)
    {
        const int maxItems = 12;

        var sb = new StringBuilder("[");
        var count = 0;
        foreach (var item in sequence)
        {
            if (count >= maxItems)
            {
                sb.Append(", …");
                break;
            }
            if (count > 0) sb.Append(", ");
            sb.Append(FormatScalar(item));
            count++;
        }
        return sb.Append(']').ToString();
    }

    private static string FormatScalar(object? item) => item switch
    {
        null => "~",
        bool b => b ? "true" : "false",
        string s => s,
        Dictionary<string, object?> map =>
            "{" + string.Join(", ", map.Select(kv => kv.Key + ": " + FormatScalar(kv.Value))) + "}",
        _ => item.ToString() ?? "",
    };

    private bool MatchesFilter(ConfigEntryRow row)
    {
        if (_filter.Length == 0) return true;
        return row.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase)
               || row.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase);
    }

    // ── 动作 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 复制全部生效值。用户报 bug 时被要求贴配置，手抄必然出错 ——
    /// 这里直接给出「合并后」的结果，比让他贴两个文件有用得多。
    /// </summary>
    private void CopyAll()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# " + L10n.Instance.T("App.Name"));
        sb.AppendLine(L10n.Instance.T("Inspector.Copy.BaseLine", BasePathText));
        if (HasPatch) sb.AppendLine(L10n.Instance.T("Inspector.Copy.PatchLine", PatchPathText));
        sb.AppendLine();

        foreach (var row in Entries)
        {
            sb.Append(row.Path).Append(": ").Append(row.Value);
            if (row.IsOverridden) sb.Append("   # ").Append(row.OriginText);
            sb.AppendLine();
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = StatusFromKey("Inspector.Status.Copied", Entries.Count);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被别的进程占用时会抛这个，不算错误，给用户一句提示即可
            StatusText = StatusFromKey("Inspector.Status.ClipboardBusy");
        }
    }

    private void OpenContainingFolder()
    {
        var target = HasPatch ? PatchPathText : BasePathText;
        if (!File.Exists(target)) return;

        try
        {
            // explorer /select,<file> —— 直接定位到文件，比打开目录再让用户自己找好
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception) { /* 打不开就算了 */ }
    }

    // ── 本地化 ──────────────────────────────────────────────────────────

    public void RefreshTexts()
    {
        StatusText = Restatus();
        // 「基础 / 补丁」两列文字是拼好的字符串，切语言时整表重建最省事
        // （行数最多几百，重建代价可忽略；不重建的话就会出现半中半英）。
        LoadSelected();
    }
}
