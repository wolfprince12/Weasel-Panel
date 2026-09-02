using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class PackageManagerView : UserControl
{
    public PackageManagerView()
    {
        InitializeComponent();
    }

    public PackageManagerView(WeaselEnvironment environment) : this()
    {
        var vm = new PackageManagerViewModel(environment);
        DataContext = vm;

        // 和其它页一样把读盘推迟到进可视化树 —— 这一页要读清单目录，
        // 构造期就扫盘会让首屏多等一次文件系统往返。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public PackageManagerViewModel? ViewModel => DataContext as PackageManagerViewModel;
}
