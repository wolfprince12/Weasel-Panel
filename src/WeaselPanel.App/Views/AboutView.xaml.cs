using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using WeaselPanel.App.Services;
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
        var sb = new StringBuilder();
        sb.AppendLine("程序目录：" + (environment.ProgramDirectory ?? "（未找到）"));
        sb.AppendLine("共享数据：" + (environment.SharedDataDirectory ?? "（未找到）"));
        sb.AppendLine("用户目录：" + environment.UserDirectory);
        sb.AppendLine("部署器：" + (environment.DeployerPath ?? "（未找到）"));
        DataContext = new { EnvironmentSummary = sb.ToString().TrimEnd() };
    }

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
