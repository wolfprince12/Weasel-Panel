using System.Windows;
using System.Windows.Controls;
using WeaselPanel.App.ViewModels;

namespace WeaselPanel.App.Views;

public partial class InputView : UserControl
{
    private bool _loaded;

    public InputView()
    {
        InitializeComponent();
    }

    public InputView(InputViewModel viewModel) : this()
    {
        DataContext = viewModel;
        // 与 SchemaView 同一套路：首次进入可视化树时才做文件 I/O，
        // 避免启动时 6 个页面一起扫磁盘拖慢首屏。
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (DataContext is InputViewModel vm && !vm.HasLoaded) vm.Load();
    }
}
