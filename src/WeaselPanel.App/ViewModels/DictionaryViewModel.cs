//
//  DictionaryViewModel.cs — 词典管理页（用户自定义词组）
//
//  ── 这一页管的是哪个文件 ────────────────────────────────────────────────
//  用户目录里的 custom_phrase.txt（全拼）/ custom_phrase_double.txt（双拼）。
//  格式是 Rime 的 tabledb 纯文本：词<Tab>码<Tab>权重，权重可省。
//
//  ⚠️ 一个必须让用户知道的坑：光有这个文件**不一定生效**。
//  Rime 只有在方案的 translator/dictionary 里挂了 custom_phrase 这个词典名时
//  才会去加载它。雾凇拼音（rime-ice）出厂就挂了；小狼毫自带的
//  明月拼音 / 注音 等方案默认没挂，用户得自己在
//  <方案>.custom.yaml 里加：
//      patch:
//        translator/dictionary: custom_phrase     # 或拼进原有词典名
//  这一页不代用户改方案文件 —— 那属于方案页的职责，且改错会让整个方案编译失败，
//  直接变成「打不出中文」。所以这里只给提示，不偷偷写。
//
//  ── 为什么用 DataGrid 而不是自绘行 ──────────────────────────────────────
//  custom_phrase.txt 动辄几百上千行，ItemsControl 不虚拟化会一次性建出几千个
//  TextBox，加载要卡好几秒。DataGrid 默认带行虚拟化，滚动只渲染可见行。
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Platform;
using WeaselPanel.Core.Rime;

namespace WeaselPanel.App.ViewModels;

/// <summary>
/// 界面上的一行词组。包一层而不是直接把 PhraseLine 丢给 DataGrid，
/// 是因为 PhraseLine 没有实现 INPC —— 单元格里改字不会通知外层，
/// 「保存」按钮就永远点不亮。
/// </summary>
public sealed class DictionaryEntry : INotifyPropertyChanged
{
    private readonly PhraseLine _line;
    private readonly Action _onChanged;

    public DictionaryEntry(PhraseLine line, Action onChanged)
    {
        _line = line;
        _onChanged = onChanged;
    }

    public Guid Id => _line.Id;

    public string Word
    {
        get => _line.Word;
        set
        {
            if (_line.Word == value) return;
            _line.Word = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public string Code
    {
        get => _line.Code;
        set
        {
            if (_line.Code == value) return;
            _line.Code = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public string Weight
    {
        get => _line.Weight;
        set
        {
            if (_line.Weight == value) return;
            _line.Weight = value;
            OnPropertyChanged();
            _onChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DictionaryViewModel : ViewModelBase, ILanguageAware
{
    /// <summary>可选的用户词典文件。db_name 必须与文件名一致（见 CustomPhraseFile.DefaultHeader）。</summary>
    private static readonly string[] FileNames =
    [
        "custom_phrase.txt",
        "custom_phrase_double.txt",
    ];

    private readonly WeaselEnvironment _environment;
    private CustomPhraseFile? _file;
    private bool _isBusy;
    private bool _hasLoaded;
    private string _statusText = "";
    private string _filter = "";
    private string _fileName = FileNames[0];
    private DictionaryEntry? _selected;

    public DictionaryViewModel(WeaselEnvironment environment)
    {
        _environment = environment;

        foreach (var n in FileNames)
            FileOptions.Add(new NamedOption(n, LocalizedFileName(n)));

        // 过滤走 ICollectionView，不重建集合 —— 重建会丢掉当前选中行与滚动位置，
        // 用户每敲一个字光标就跳回列表顶部，没法用。
        EntryView = CollectionViewSource.GetDefaultView(Entries);
        EntryView.Filter = o => o is DictionaryEntry e && MatchesFilter(e);

        AddCommand = new DelegateCommand(Add);
        RemoveCommand = new DelegateCommand(Remove, () => SelectedEntry is not null);
        ClearFilterCommand = new DelegateCommand(() => Filter = "", () => Filter.Length > 0);
        SaveCommand = new RelayCommand(SaveAsync, () => _file is not null && _file.IsDirty && !_isBusy);
        ReloadCommand = new RelayCommand(ReloadAsync, () => !_isBusy);
    }

    // ── 数据 ────────────────────────────────────────────────────────────

    public ObservableCollection<DictionaryEntry> Entries { get; } = new();
    public List<NamedOption> FileOptions { get; } = new();
    public ICollectionView EntryView { get; }

    /// <summary>
    /// 绑定到 DataGrid 的 SelectedItem 用的属性。不用 Selected 是因为
    /// CollectionView 的 CurrentItem 与列表选中项是两套机制，混用会错位；
    /// 这里让 DataGrid 直接写回 ViewModel 的 SelectedEntry。
    /// </summary>
    public DictionaryEntry? SelectedEntry
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) RaiseAll();
        }
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (!Set(ref _fileName, value)) return;
            // 换文件等于放弃当前未保存改动。不偷偷丢，也不弹窗打断 ——
            // 由 Load() 之后的状态文本说明，用户在保存按钮上能看到还能不能点。
            _ = LoadAsync();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            EntryView.Refresh();
            OnPropertyChanged(nameof(ShowNoEntries));
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
            RaiseAll();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public new bool IsDirty => _file?.IsDirty ?? false;

    public int EntryCount => _file?.EntryCount ?? 0;

    /// <summary>
    /// 「共 N 条」这类带数字的文案得在 ViewModel 里拼 —— XAML 的 TextBlock
    /// 没法给本地化串传参数。语言切换时在 RefreshTexts 里重建。
    /// </summary>
    public string EntryCountText =>
        L10n.Instance.T("Dictionary.EntryCountFormat", EntryCount);

    public string FilePath => _file?.FilePath ?? "";

    public bool FileExists => _file?.Exists ?? false;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>一条都没有（文件空或刚建）。</summary>
    public bool ShowNoEntries => Entries.Count == 0;

    /// <summary>有条目，但被搜索框全过滤掉了。跟「一条都没有」是两回事，
    /// 提示语要分开，否则用户会以为自己的词组丢了。</summary>
    public bool ShowFilteredEmpty => Entries.Count > 0 && EntryView.IsEmpty;

    // ── 命令 ────────────────────────────────────────────────────────────

    public System.Windows.Input.ICommand AddCommand { get; }
    public System.Windows.Input.ICommand RemoveCommand { get; }
    public System.Windows.Input.ICommand ClearFilterCommand { get; }
    public System.Windows.Input.ICommand SaveCommand { get; }
    public System.Windows.Input.ICommand ReloadCommand { get; }

    // ── 加载 ────────────────────────────────────────────────────────────

    public void Load()
    {
        if (_hasLoaded) return;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = StatusFromKey("Dictionary.Status.Loading");
        try
        {
            var path = Path.Combine(_environment.UserDirectory, _fileName);
            var file = await Task.Run(() => new CustomPhraseFile(path));

            _file = file;
            Entries.Clear();
            foreach (var line in file.Lines.Where(l => l.IsEntry))
                Entries.Add(new DictionaryEntry(line, OnEntryChanged));

            _hasLoaded = true;
            StatusText = file.Exists
                ? StatusFromKey("Dictionary.Status.Loaded", file.EntryCount)
                : StatusFromKey("Dictionary.Status.NewFile");
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Dictionary.Status.LoadFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshDerived();
        }
    }

    private async Task ReloadAsync()
    {
        _hasLoaded = false;
        await LoadAsync();
    }

    // ── 编辑 ────────────────────────────────────────────────────────────

    private void Add()
    {
        if (_file is null) return;

        var line = new PhraseLine();
        _file.AddEntry();

        var entry = new DictionaryEntry(line, OnEntryChanged);
        Entries.Add(entry);
        SelectedEntry = entry;

        StatusText = StatusFromKey("Dictionary.Status.Added");
        RefreshDerived();
    }

    private void Remove()
    {
        if (_file is null || SelectedEntry is null) return;

        _file.RemoveEntry(SelectedEntry.Id);
        Entries.Remove(SelectedEntry);
        SelectedEntry = null;

        StatusText = StatusFromKey("Dictionary.Status.Removed");
        RefreshDerived();
    }

    public async Task ApplyAsync() => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_file is null) return;

        IsBusy = true;
        StatusText = StatusFromKey("Dictionary.Status.Saving");
        try
        {
            await Task.Run(() => _file.Save());
            StatusText = StatusFromKey("Dictionary.Status.Saved", _file.EntryCount);
        }
        catch (Exception ex)
        {
            StatusText = StatusFromKey("Dictionary.Status.SaveFailed", ex.Message);
        }
        finally
        {
            IsBusy = false;
            RefreshDerived();
        }
    }

    /// <summary>单元格里改了字：重算脏值并让「保存」按钮重新判断可用性。</summary>
    private void OnEntryChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileExists));
        OnPropertyChanged(nameof(ShowNoEntries));
        OnPropertyChanged(nameof(ShowFilteredEmpty));
        OnPropertyChanged(nameof(EntryCountText));
        ((RelayCommand)SaveCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ReloadCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)RemoveCommand).RaiseCanExecuteChanged();
    }

    private void RaiseAll()
    {
        ((DelegateCommand)RemoveCommand).RaiseCanExecuteChanged();
    }

    private bool MatchesFilter(DictionaryEntry e)
    {
        if (_filter.Length == 0) return true;
        var f = _filter.Trim();
        return e.Word.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Code.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalizedFileName(string name) =>
        name == "custom_phrase.txt"
            ? L10n.Instance.T("Dictionary.File.Full")
            : L10n.Instance.T("Dictionary.File.Double");

    // ── 本地化 ──────────────────────────────────────────────────────────

    public void RefreshTexts()
    {
        StatusText = Restatus();
        // 「共 N 条」是拼好的字符串，不会随语言自动变，必须重建
        OnPropertyChanged(nameof(EntryCountText));
        foreach (var opt in FileOptions)
            opt.Name = LocalizedFileName(opt.Id);
    }
}
