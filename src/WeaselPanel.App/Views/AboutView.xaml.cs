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
        LoadLogo();
    }

    public AboutView(WeaselEnvironment environment) : this()
    {
        DataContext = new AboutViewModel(environment);
    }

    /// <summary>当前页的 ViewModel。MainWindow 靠它把语言变更派发下来。</summary>
    public AboutViewModel? ViewModel => DataContext as AboutViewModel;

    /// <summary>
    /// 从嵌入式资源加载 logo。失败时显示文字占位，不让 XAML 整块崩。
    /// 为什么不用 pack://application:,,,/?见 EmbeddedAssets.cs 顶部注释。
    /// </summary>
    private void LoadLogo()
    {
        try
        {
            var img = EmbeddedAssets.TryLoadLogo();
            if (img is not null)
            {
                LogoImage.Source = img;
            }
            else
            {
                LogoImage.Visibility = Visibility.Collapsed;
                FallbackText.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            LogoImage.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
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
