using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView()
    {
        InitializeComponent();
    }

    public InspectorView(WeaselEnvironment environment) : this()
    {
        var vm = new InspectorViewModel(environment);
        DataContext = vm;

        // 与其他页一致：把目录枚举与文件读取推迟到页面真正进可视化树。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public InspectorViewModel? ViewModel => DataContext as InspectorViewModel;
}
