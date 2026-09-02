using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class MaintenanceView : UserControl
{
    public MaintenanceView()
    {
        InitializeComponent();
    }

    public MaintenanceView(WeaselEnvironment environment) : this()
    {
        var vm = new MaintenanceViewModel(environment);
        DataContext = vm;

        // 首次进页才扫日志目录。构造期读盘会让十页一起 new 时首屏卡顿，
        // 而且用户根本没点开的页面也在做无谓的 I/O。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public MaintenanceViewModel? ViewModel => DataContext as MaintenanceViewModel;
}
