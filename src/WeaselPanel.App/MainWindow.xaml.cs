using System.Windows;
using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.App.Views;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App;

public partial class MainWindow : Window
{
    private readonly WeaselEnvironment _environment;
    private readonly DiagnosticsView _diagnosticsView;
    private readonly AppearanceView _appearanceView;
    private readonly SchemaView _schemaView;
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
            MessageBox.Show("环境探测失败：\n" + ex.Message, "小狼毫控制面板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _environment = environment;
        _diagnosticsView = new DiagnosticsView(new DiagnosticsViewModel());
        _appearanceView = new AppearanceView(new AppearanceViewModel(environment));
        _schemaView = new SchemaView(new SchemaViewModel(environment));
        _aboutView = new AboutView(environment);

        // ⚠️ 不要在 XAML 里写 ListBoxItem.IsSelected="True"，会在 InitializeComponent
        // 阶段触发 SelectionChanged，此时 4 个 view 字段还没就绪 → NRE。
        // 也不要 Nav.SelectedIndex = 0 来"显式首次切换"，那同样会触发回调。
        // 直接在 ctor 末尾赋 ContentHost.Content 给默认页，不经过事件 —— 用户
        // 启动即看「环境诊断」，后续点击 ListBoxItem 才进入正常的 SelectionChanged 路径。
        ContentHost.Content = _diagnosticsView;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 首屏成功呈现。日志里出现 "main_window_ready" = 窗口已能正常显示；
        // 若崩溃但日志里没有这行，说明崩在 InitializeComponent / ctor 阶段。
        App.LogStartupReady();

        // 标题栏带上版本号与构建时间 —— 让用户一眼确认自己跑的是哪一版，
        // 不再出现「以为测的是新版、其实是旧 exe」的误判。
        var v = App.ExecutableVersion;
        Title = $"小狼毫控制面板 — v{v.Major}.{v.Minor}.{v.Build} " +
                $"(构建 {App.ExecutableBuildTime:MM-dd HH:mm})";
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 兜底：如果将来发生 ctor 阶段回调（极端改造），所有 4 个 view 字段都非空就放行，
        // 否则 return —— 永远不让 Nav_SelectionChanged 因为字段未就绪而 NRE。
        if (_diagnosticsView is null || _appearanceView is null
            || _schemaView is null || _aboutView is null) return;

        ContentHost.Content = Nav.SelectedIndex switch
        {
            1 => _appearanceView,
            2 => _schemaView,
            3 => _aboutView,
            _ => _diagnosticsView,
        };
    }
}
