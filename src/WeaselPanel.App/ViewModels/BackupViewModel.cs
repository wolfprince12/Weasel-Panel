//
//  BackupViewModel.cs
//  WeaselPanel.App
//
//  备份与恢复页（时间机器）。
//  Core 的 BackupManager 已具备完整能力（创建/列出/恢复/删除/单文件 diff/双栏 diff），
//  本类型只负责把它接到界面上，并补上 Core **故意不做**的两件事：
//
//  1. **恢复前自动留一次快照**（见 RestoreAsync 的说明）。
//     `BackupManager.RestoreBackup` 是纯粹的覆盖写入，**不做任何事前备份** ——
//     一旦恢复错版本，用户当前配置就永久没了。这层安全网只能在 UI 侧加。
//
//  2. **破坏性操作的二次确认**。恢复与删除都会真实改动用户数据，
//     必须让用户看清「将覆盖什么」再点头，不能一个按钮下去就没了。
//

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.Core.Backup;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.ViewModels;

public sealed class BackupViewModel : ViewModelBase
{
    private readonly WeaselEnvironment _environment;
    private readonly BackupManager _manager;

    public BackupViewModel(WeaselEnvironment environment)
    {
        _environment = environment;
        _manager = new BackupManager(environment.UserDirectory);

        CreateCommand = new RelayCommand(CreateAsync);
        RestoreAllCommand = new RelayCommand(RestoreAllAsync, () => SelectedBackup is not null);
        RestoreFileCommand = new RelayCommand(RestoreFileAsync, () => SelectedBackup is not null && SelectedFile is not null);
        DeleteCommand = new DelegateCommand(Delete, () => SelectedBackup is not null);
        RefreshCommand = new DelegateCommand(Load);

        Load();
    }

    // ── 命令 ────────────────────────────────────────────────

    public ICommand CreateCommand { get; }
    public ICommand RestoreAllCommand { get; }
    public ICommand RestoreFileCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }

    // ── 数据 ────────────────────────────────────────────────

    public ObservableCollection<BackupInfo> Backups { get; } = new();
    public ObservableCollection<string> BackupFiles { get; } = new();
    public ObservableCollection<SideBySideLine> DiffLines { get; } = new();

    private BackupInfo? _selectedBackup;
    public BackupInfo? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (!Set(ref _selectedBackup, value)) return;
            LoadFilesForSelection();
            RaiseCanExecuteChanged();
        }
    }

    private string? _selectedFile;
    public string? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Set(ref _selectedFile, value)) return;
            LoadDiffForSelection();
            OnPropertyChanged(nameof(ShowNoDiffNotice));
            RaiseCanExecuteChanged();
        }
    }

    private string _newLabel = "";
    /// <summary>新建备份时的可选标签；留空即「自动备份」。</summary>
    public string NewLabel
    {
        get => _newLabel;
        set => Set(ref _newLabel, value);
    }

    private bool _diffIdentical;
    /// <summary>当前对比的文件两版是否完全一致（一致时界面提示「无差异」）。</summary>
    public bool DiffIdentical
    {
        get => _diffIdentical;
        set
        {
            if (!Set(ref _diffIdentical, value)) return;
            // 未选中任何文件时不该显示「两版完全一致」—— 那时根本没有可比的东西，
            // 显示它会让人误以为已经对比过。这个判断放 ViewModel 里，
            // 比在 XAML 里用 MultiDataTrigger 反向比较 null 可靠得多。
            OnPropertyChanged(nameof(ShowNoDiffNotice));
        }
    }

    /// <summary>是否显示「两版完全一致」提示：已选中文件，且对比结果为无差异。</summary>
    public bool ShowNoDiffNotice => _diffIdentical && _selectedFile is not null;

    private string _status = "就绪";
    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string BackupsDirectory => _manager.BackupsDirectory;

    // ── 载入 ────────────────────────────────────────────────

    public void Load()
    {
        try
        {
            var list = _manager.ListBackups();
            Backups.Clear();
            foreach (var b in list) Backups.Add(b);

            Status = list.Count == 0
                ? "暂无备份。建议先创建一次，之后改配置就有退路了。"
                : $"共 {list.Count} 个备份　·　存放于 {BackupsDirectory}";
        }
        catch (Exception ex)
        {
            Status = "读取备份失败：" + ex.Message;
        }
    }

    private void LoadFilesForSelection()
    {
        BackupFiles.Clear();
        DiffLines.Clear();
        _selectedFile = null;
        OnPropertyChanged(nameof(SelectedFile));

        if (_selectedBackup is null) return;

        try
        {
            foreach (var f in _manager.ListBackupFiles(_selectedBackup.DirName))
                BackupFiles.Add(f);
        }
        catch (Exception ex)
        {
            Status = "读取备份内容失败：" + ex.Message;
        }
    }

    private void LoadDiffForSelection()
    {
        DiffLines.Clear();
        if (_selectedBackup is null || _selectedFile is null)
        {
            DiffIdentical = true;
            return;
        }

        try
        {
            var (lines, identical) = _manager.CompareBackupSideBySide(_selectedBackup.DirName, _selectedFile);
            DiffIdentical = identical;
            foreach (var l in lines) DiffLines.Add(l);

            Status = identical
                ? $"「{_selectedFile}」与该备份完全一致"
                : $"「{_selectedFile}」与备份存在差异（左＝备份版，右＝当前版）";
        }
        catch (Exception ex)
        {
            DiffIdentical = true;
            Status = "对比失败：" + ex.Message;
        }
    }

    // ── 操作 ────────────────────────────────────────────────

    private Task CreateAsync()
    {
        try
        {
            var label = string.IsNullOrWhiteSpace(NewLabel) ? null : NewLabel.Trim();
            var info = _manager.CreateBackup(label);

            NewLabel = "";
            Load();

            // 新建的备份排在最前（时间倒序），直接选中它，省得用户再点一次
            SelectedBackup = Backups.FirstOrDefault(b => b.DirName == info.DirName);

            Status = $"已创建备份：{info.LabelText}　{info.CreatedText}　{info.FileCount} 个文件 / {info.SizeText}";
        }
        catch (Exception ex)
        {
            MessageBox.Show("创建备份失败：\n" + ex.Message, "小狼毫控制面板",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Status = "创建备份失败：" + ex.Message;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 整量恢复。
    /// ⚠️ **恢复前必须先给当前状态留一次快照** —— 这是 Core 不提供、但绝不能省的安全网。
    /// `BackupManager.RestoreBackup` 是无条件覆盖：一旦恢复错版本，用户当前配置就永久丢失，
    /// 而「备份页把用户的数据弄丢了」对一个备份工具来说是最不可接受的失败。
    /// 留了快照，恢复就变成可撤销的操作。
    /// </summary>
    private Task RestoreAllAsync()
    {
        if (_selectedBackup is null) return Task.CompletedTask;

        var target = _selectedBackup;
        var answer = MessageBox.Show(
            $"即将把用户目录下的配置**全部**恢复到：\n\n" +
            $"　{target.LabelText}　{target.CreatedText}\n" +
            $"　{target.FileCount} 个文件 / {target.SizeText}\n\n" +
            $"当前的配置将被覆盖。\n" +
            $"恢复前会自动为当前状态创建一个备份（标签「恢复前自动备份」），可以随时撤销本次操作。\n\n" +
            $"确定要恢复吗？",
            "恢复备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return Task.CompletedTask;

        try
        {
            _manager.CreateBackup("恢复前自动备份");
            _manager.RestoreBackup(target.DirName);

            Load();
            Status = "已恢复。请到「外观」页点「部署」，改动才会在输入法中生效。";
            MessageBox.Show(
                "恢复完成。\n\n请到「外观」页点「部署」，改动才会在输入法中生效。",
                "恢复备份", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("恢复失败：\n" + ex.Message, "小狼毫控制面板",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Status = "恢复失败：" + ex.Message;
        }
        return Task.CompletedTask;
    }

    /// <summary>只恢复选中的单个文件。同样先留快照（理由同 RestoreAllAsync）。</summary>
    private Task RestoreFileAsync()
    {
        if (_selectedBackup is null || _selectedFile is null) return Task.CompletedTask;

        var target = _selectedBackup;
        var file = _selectedFile;

        var answer = MessageBox.Show(
            $"即将把文件「{file}」恢复到备份版本：\n\n" +
            $"　{target.LabelText}　{target.CreatedText}\n\n" +
            $"恢复前会自动为当前状态创建一个备份，可以随时撤销。\n\n" +
            $"确定吗？",
            "恢复单个文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return Task.CompletedTask;

        try
        {
            _manager.CreateBackup("恢复前自动备份");
            _manager.RestoreBackup(target.DirName, new[] { file });

            LoadDiffForSelection();
            Status = $"已恢复「{file}」。请点「部署」使改动生效。";
        }
        catch (Exception ex)
        {
            MessageBox.Show("恢复失败：\n" + ex.Message, "小狼毫控制面板",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Status = "恢复失败：" + ex.Message;
        }
        return Task.CompletedTask;
    }

    private void Delete()
    {
        if (_selectedBackup is null) return;

        var target = _selectedBackup;
        var answer = MessageBox.Show(
            $"确定删除这个备份吗？\n\n　{target.LabelText}　{target.CreatedText}\n" +
            $"　{target.FileCount} 个文件\n\n删除后无法找回。",
            "删除备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _manager.DeleteBackup(target.DirName);
            _selectedBackup = null;
            OnPropertyChanged(nameof(SelectedBackup));
            Load();
            Status = "已删除备份";
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败：\n" + ex.Message, "小狼毫控制面板",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Status = "删除失败：" + ex.Message;
        }
    }

    private void RaiseCanExecuteChanged()
    {
        ((RelayCommand)RestoreAllCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RestoreFileCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)DeleteCommand).RaiseCanExecuteChanged();
    }
}
