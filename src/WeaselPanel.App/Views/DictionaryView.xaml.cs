using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class DictionaryView : UserControl
{
    public DictionaryView()
    {
        InitializeComponent();
    }

    public DictionaryView(WeaselEnvironment environment) : this()
    {
        var vm = new DictionaryViewModel(environment);
        DataContext = vm;

        // 跟输入页/行为页一样，把文件 I/O 推迟到页面真正进可视化树 ——
        // 构造期就读盘会让首屏卡顿，而且 10 个页面同时 new 出来时，
        // 用户没点开的那几页也在读文件。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public DictionaryViewModel? ViewModel => DataContext as DictionaryViewModel;
}
