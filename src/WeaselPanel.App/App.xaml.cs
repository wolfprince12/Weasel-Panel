using System.Windows;

namespace WeaselPanel.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：预览版阶段把崩溃信息展示出来，便于用户回报
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "发生未处理的异常：\n\n" + args.Exception,
                "小狼毫控制面板 — 预览版",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
