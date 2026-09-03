using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class CorrectionView : UserControl
{
    public CorrectionView()
    {
        InitializeComponent();
    }

    public CorrectionView(WeaselEnvironment environment) : this()
    {
        var vm = new CorrectionViewModel(environment);
        DataContext = vm;

        // 与其它页一致：文件 I/O 推迟到页面真正进可视化树。
        Loaded += (_, _) => vm.Load();
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public CorrectionViewModel? ViewModel => DataContext as CorrectionViewModel;

    /// <summary>
    /// 超链接点击：用系统默认程序打开 URL（下载页 / 仓库等）。
    /// 与 AboutView 同款；启动失败时静默吞掉，不当作错误。
    /// </summary>
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch
        {
            // 浏览器启动失败不视为错误
        }
    }
}
