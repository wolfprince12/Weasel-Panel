using System.Windows;
using System.Windows.Threading;

namespace WeaselPanel.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI 线程未捕获异常 → 显示对话框而不是整个进程崩。
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                "发生未处理的异常：\n\n" + args.Exception,
                "小狼毫控制面板 — 预览版",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            // 设为 true 让进程继续；设为 false 会让 WPF 把进程杀掉。
            // 预览版阶段优先让用户看到崩溃后还能继续试别的页面，所以 Handled=true。
            args.Handled = true;
        };
    }
}
