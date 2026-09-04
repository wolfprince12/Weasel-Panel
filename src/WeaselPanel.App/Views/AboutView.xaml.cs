using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using WeaselPanel.App.Services;
using WeaselPanel.App.ViewModels;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        LoadYaozhiQR();
    }

    public AboutView(WeaselEnvironment environment) : this()
    {
        DataContext = new AboutViewModel(environment);
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public AboutViewModel? ViewModel => DataContext as AboutViewModel;

    /// <summary>
    /// 加载爻知云二维码（对齐鼠须管关于页 yaozhiCard：右半直接展示推广图）。
    /// 缺失不致命 —— 推广区仍显示文字，不影响其余功能。
    /// </summary>
    private void LoadYaozhiQR()
    {
        try
        {
            var img = EmbeddedAssets.TryLoadYaozhiQR();
            if (img is not null) YaozhiQRImage.Source = img;
        }
        catch
        {
            // 二维码缺失不致命
        }
    }

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
