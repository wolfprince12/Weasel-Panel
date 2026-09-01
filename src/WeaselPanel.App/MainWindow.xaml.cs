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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Rime"));
            MessageBox.Show("环境探测失败：\n" + ex.Message, "小狼毫控制面板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _environment = environment;
        _diagnosticsView = new DiagnosticsView(new DiagnosticsViewModel());
        _appearanceView = new AppearanceView(new AppearanceViewModel(environment));
        _schemaView = new SchemaView(new SchemaViewModel(environment));
        _aboutView = new AboutView(environment);

        ContentHost.Content = _diagnosticsView;
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContentHost.Content = Nav.SelectedIndex switch
        {
            1 => _appearanceView,
            2 => _schemaView,
            3 => _aboutView,
            _ => _diagnosticsView,
        };
    }
}
