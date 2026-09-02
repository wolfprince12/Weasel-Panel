using System.Windows;
using System.Windows.Controls;
using WeaselPanel.App.ViewModels;

namespace WeaselPanel.App.Views;

public partial class BehaviorView : UserControl
{
    private bool _loaded;

    public BehaviorView()
    {
        InitializeComponent();
    }

    public BehaviorView(BehaviorViewModel viewModel) : this()
    {
        DataContext = viewModel;
        // 与 InputView / SchemaView 同一套路：首次进入可视化树时才读盘。
        // 行为页要读 4 个 YAML（两个出厂 + 两个 patch），启动时跟着别的页一起
        // 扫磁盘会拖慢首屏；用户没点进来就压根不该读。
        Loaded += OnLoaded;
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public BehaviorViewModel? ViewModel => DataContext as BehaviorViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (DataContext is BehaviorViewModel vm && !vm.HasLoaded) vm.Load();
    }
}
