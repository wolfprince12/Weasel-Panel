using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WeaselPanel.App.Infrastructure;
using WeaselPanel.App.Localization;
using WeaselPanel.App.ViewModels;
using WeaselPanel.App.Views;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App;

public partial class MainWindow : Window
{
    private readonly WeaselEnvironment _environment;
    private readonly DiagnosticsView _diagnosticsView;
    private readonly AppearanceView _appearanceView;
    private readonly ColorSchemesView _colorSchemesView;
    private readonly SchemaView _schemaView;
    private readonly RimeIceView _rimeIceView;
    private readonly InputView _inputView;
    private readonly BehaviorView _behaviorView;
    private readonly AppOptionsView _appOptionsView;
    private readonly DictionaryView _dictionaryView;
    private readonly MaintenanceView _maintenanceView;
    private readonly InspectorView _inspectorView;
    private readonly BackupView _backupView;
    private readonly AboutView _aboutView;

    public MainWindow()
    {
        InitializeComponent();

        WeaselEnvironment environment;
        try
        {
            environment = WeaselPaths.Detect();
        }
        catch (Exception ex)
        {
            // 探测本身失败不应导致窗口打不开 —— 退化为空环境，由诊断页呈现错误
            environment = WeaselEnvironment.WithUserDirectory(
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Rime"));
            MessageBox.Show(L10n.Instance.T("App.DetectFailed", ex.Message),
                L10n.Instance.T("App.Name"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _environment = environment;
        _diagnosticsView = new DiagnosticsView(new DiagnosticsViewModel());
        _appearanceView = new AppearanceView(new AppearanceViewModel(environment));
        _colorSchemesView = new ColorSchemesView(environment);
        _schemaView = new SchemaView(new SchemaViewModel(environment));
        _rimeIceView = new RimeIceView(environment);
        _inputView = new InputView(new InputViewModel(environment));
        _behaviorView = new BehaviorView(new BehaviorViewModel(environment));
        _appOptionsView = new AppOptionsView(environment);
        _dictionaryView = new DictionaryView(environment);
        _maintenanceView = new MaintenanceView(environment);
        _inspectorView = new InspectorView(environment);
        _backupView = new BackupView(new BackupViewModel(environment));
        _aboutView = new AboutView(environment);

        // ⚠️ 不要在 XAML 里写 ListBoxItem.IsSelected="True"，会在 InitializeComponent
        // 阶段触发 SelectionChanged，此时各 view 字段还没就绪 → NRE。
        // 直接在 ctor 末尾赋 ContentHost.Content 给默认页，不经过事件 —— 用户
        // 启动即看「环境诊断」，后续点击 ListBoxItem 才进入正常的 SelectionChanged 路径。
        ContentHost.Content = _diagnosticsView;

        // 语言可能在「关于」页被切换，标题与侧栏版本号要跟着变。
        L10n.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 首屏成功呈现。日志里出现 "main_window_ready" = 窗口已能正常显示；
        // 若崩溃但日志里没有这行，说明崩在 InitializeComponent / ctor 阶段。
        App.LogStartupReady();
        App.Log($">>> language = {L10n.Instance.Language}");

        ApplyLocalizedText();
    }

    /// <summary>标题栏 + 侧栏版本号。语言切换时也会重跑（见 OnLanguageChanged）。</summary>
    private void ApplyLocalizedText()
    {
        var v = App.ExecutableVersion;
        var version = $"{L10n.Instance.T("App.VersionPrefix")}{v.Major}.{v.Minor}.{v.Build}";
        var build = $"{L10n.Instance.T("App.BuildPrefix")} {App.ExecutableBuildTime:MM-dd HH:mm}";

        Title = $"{L10n.Instance.T("App.Name")} — {version} ({build})";
        VersionLabel.Text = $"{version} {L10n.Instance.T("App.Preview")}";
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyLocalizedText();
        RefreshViewTexts();
    }

    /// <summary>
    /// 把语言变更派发给各页的 ViewModel。
    /// XAML 里的 <c>{l10n:L Key}</c> 会自己刷新，但 ViewModel 里「赋值那一刻拼好的
    /// 字符串」不会 —— 那些由各自的 <see cref="ILanguageAware.RefreshTexts"/> 重建。
    /// 诊断页/外观页/方案页/按键页/行为页/词典页/维护页/配置查看器页/备份页都实现了该
    /// 接口；关于页没有需要重建的文本。
    /// </summary>
    private void RefreshViewTexts()
    {
        UserControl[] views =
        {
            _diagnosticsView, _appearanceView, _colorSchemesView, _schemaView,
            _rimeIceView, _inputView, _behaviorView, _appOptionsView, _dictionaryView,
            _maintenanceView, _inspectorView,
            _backupView, _aboutView,
        };

        foreach (var view in views)
        {
            if (view?.DataContext is ILanguageAware aware) aware.RefreshTexts();
        }
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 兜底：如果将来发生 ctor 阶段回调（极端改造），所有 view 字段都非空就放行，
        // 否则 return —— 永远不让 Nav_SelectionChanged 因为字段未就绪而 NRE。
        if (_diagnosticsView is null || _appearanceView is null
            || _colorSchemesView is null
            || _schemaView is null || _rimeIceView is null
            || _inputView is null
            || _behaviorView is null || _appOptionsView is null
            || _dictionaryView is null
            || _maintenanceView is null || _inspectorView is null
            || _backupView is null || _aboutView is null) return;

        ContentHost.Content = ViewAtIndex(Nav.SelectedIndex);
    }

    /// <summary>
    /// 导航顺序表。
    /// ⚠️ 顺序必须与 MainWindow.xaml 里 ListBoxItem 的顺序严格一致。
    /// </summary>
    /// <remarks>
    /// 原来是 <c>SelectedIndex switch { 1 => …, 10 => … }</c>。那样每加一页都要把
    /// 后面所有 case 的索引手动 +1；漏一个就会「点『维护』跳出『词典』」，
    /// 而且既不报错也不崩，只能靠肉眼发现。改成顺序数组后，加页只要在
    /// XAML 和这里各插一行，索引由数组位置自己算出来，没有数字可漏。
    /// </remarks>
    private UserControl ViewAtIndex(int index)
    {
        // ⚠️ 边界用 views.Length，不要写死数字。之前是 `< 13`，加一页就得记着把
        // 13 改成 14 —— 忘了改的后果是「最后一页点不开，静默退回诊断页」，不报错。
        var views = NavViews;
        return index >= 0 && index < views.Length ? views[index] : _diagnosticsView;
    }

    private UserControl[] NavViews =>
    [
        _diagnosticsView,   // 0 环境诊断
        _appearanceView,    // 1 外观
        _colorSchemesView,  // 2 自定义配色
        _schemaView,        // 3 输入方案
        _rimeIceView,       // 4 雾凇拼音
        _inputView,         // 5 按键与输入
        _behaviorView,      // 6 行为
        _appOptionsView,    // 7 应用选项
        _dictionaryView,    // 8 词典
        _maintenanceView,   // 9 维护
        _inspectorView,     // 10 配置查看器
        _backupView,        // 11 备份与恢复
        _aboutView,         // 12 关于
    ];
}
