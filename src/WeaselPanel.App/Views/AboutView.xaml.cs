using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using WeaselPanel.Core.Platform;

namespace WeaselPanel.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
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
