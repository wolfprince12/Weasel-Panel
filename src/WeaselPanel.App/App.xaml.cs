using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WeaselPanel.App.Localization;
using WeaselPanel.Core.Config;

namespace WeaselPanel.App;

public partial class App : Application
{
    /// <summary>启动日志文件路径：%TEMP%\WeaselPanel\startup.log</summary>
    public static string LogFilePath =>
        Path.Combine(Path.GetTempPath(), "WeaselPanel", "startup.log");

    /// <summary>
    /// 程序集版本。MainWindow 标题栏显示它，让用户（和我）一眼确认跑的是哪一版 ——
    /// 之前连续两轮修复都因为「不知道 VM 上跑的是哪个 exe」而误判过。
    /// </summary>
    public static Version ExecutableVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>exe 文件的最后修改时间，比版本号更能区分同一天内的多次构建。</summary>
    public static DateTime ExecutableBuildTime
    {
        get
        {
            try { return File.GetLastWriteTime(Environment.ProcessPath ?? ""); }
            catch { return DateTime.MinValue; }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log($"=== startup {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
            $"v{ExecutableVersion} exe_mtime={ExecutableBuildTime:yyyy-MM-dd HH:mm:ss} ===");
        Log($"exe_path = {Environment.ProcessPath}");
        Log($"os = {Environment.OSVersion} arch = {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

        // 界面语言：用户在设置里选过就用选的，否则跟随系统（zh-CN → 简体中文）。
        // 必须在任何窗口创建之前定好，否则首屏会先闪一下英文再变中文。
        var settings = PanelSettings.Load();
        L10n.Instance.SetLanguage(settings.Language);
        Log($"ui_culture = {System.Globalization.CultureInfo.CurrentUICulture.Name} " +
            $"selected = {settings.Language ?? "(auto)"} resolved = {L10n.Instance.Language}");

        // UI 线程未捕获异常 → 写日志 + 显示对话框，而不是让整个进程崩掉。
        DispatcherUnhandledException += (_, args) =>
        {
            Log("!!! DispatcherUnhandledException: " + args.Exception);
            try
            {
                MessageBox.Show(
                    L10n.Instance.T("App.CrashBody", args.Exception, LogFilePath),
                    L10n.Instance.T("App.Name"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* 连 MessageBox 都弹不出来时（例如资源初始化失败），至少日志已经落盘 */ }

            // 设为 true 让进程继续；设为 false 会让 WPF 把进程杀掉。
            // 预览版阶段优先让用户看到崩溃后还能继续试别的页面，所以 Handled=true。
            args.Handled = true;
        };

        // 非 UI 线程（如后台探测 Task）的未捕获异常。这类异常 Dispatcher 抓不到，
        // 默认行为是直接杀进程（连对话框都不弹），所以必须单独兜一层并记录。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("!!! AppDomain.UnhandledException: " + args.ExceptionObject);
        };

        // 进程退出时补一行，便于判断「崩了」还是「正常关」。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log("--- exit ---");
    }

    /// <summary>首屏成功呈现后由 MainWindow 调用。日志里有这行 = 窗口已能正常显示。</summary>
    public static void LogStartupReady() => Log(">>> main_window_ready (首屏已呈现)");

    /// <summary>
    /// 追加一行日志。所有异常路径都可能走到这里，因此自身绝不能抛异常 ——
    /// 日志失败不能把「崩溃」变成「崩溃 + 二次崩溃」。
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFilePath, message + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 日志写不进去就算了，不能影响主流程
        }
    }
}
