using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class RimeIceView : UserControl
{
    public RimeIceView()
    {
        InitializeComponent();
    }

    public RimeIceView(WeaselEnvironment environment) : this()
    {
        var vm = new RimeIceViewModel(environment);
        DataContext = vm;

        // 与其它页一致：文件 I/O 推迟到页面真正进可视化树。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public RimeIceViewModel? ViewModel => DataContext as RimeIceViewModel;
}
