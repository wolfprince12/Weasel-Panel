using System.Windows.Controls;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class ColorSchemesView : UserControl
{
    public ColorSchemesView()
    {
        InitializeComponent();
    }

    public ColorSchemesView(WeaselEnvironment environment) : this()
    {
        var vm = new ColorSchemesViewModel(environment);
        DataContext = vm;

        // 与其它页一致：文件 I/O 推迟到页面真正进可视化树。
        // 构造期就读盘会让首屏卡顿 —— 十几个页面同时 new 出来时，
        // 用户没点开的那几页也在读 user_color_schemes.json 与 weasel.custom.yaml。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public ColorSchemesViewModel? ViewModel => DataContext as ColorSchemesViewModel;
}
