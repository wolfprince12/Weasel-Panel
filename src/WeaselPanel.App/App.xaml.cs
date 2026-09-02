using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>程序集版本。MainWindow 标题栏显示它，让用户（和我）一眼确认跑的是哪一版。</summary>
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

    /// <summary>
    /// 进程可执行文件的 sha256 前 12 位。每次构建唯一，用来肉眼确认「我打开的是这版 exe」。
    /// 不用全哈希是因为 64 位的指纹对人类太长了，前 12 位碰撞概率 2^48 ≈ 没问题。
    /// </summary>
    public static string ExecutableHashPrefix
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "nohash";
                using var sha = SHA256.Create();
                using var fs = File.OpenRead(path);
                var bytes = sha.ComputeHash(fs);
                var sb = new StringBuilder(12);
                for (var i = 0; i < 6; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
            catch { return "hashfail"; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  崩溃对话框硬编码常量
    // ─────────────────────────────────────────────────────────────────────
    // 这些常量完全独立于 L10n、语言包、EmbeddedResource、内联常量字典、HumanizeKey
    // 任何一处。即整条本地化链路全断（哪怕 ResourceManager、LangResources.g.cs、L10n
    // 实例本身都抛 NullRef），OnStartup 的崩溃对话框也能正确显示。
    //
    // 为什么这么极端？v0.1.15 / v0.1.16 两次修复都假定「Safe() 兜底 + HumanizeKey
    // 兜底」够用，但 VM 上仍看到裸键 "App.Name"/"App.CrashBody" —— 唯一能解释这
    // 现象的是「VM 跑的根本不是新版 exe」。修复思路从「让兜底更厚」转向
    // 「让兜底无懈可击 + 让用户无法打开错版」两条并行。
    // ─────────────────────────────────────────────────────────────────────
    private const string HARDCODED_APP_NAME       = "小狼毫控制面板";
    private const string HARDCODED_CRASH_TITLE    = "小狼毫控制面板 — 启动失败";
    private const string HARDCODED_CRASH_BODY_PRE = "程序启动失败：\n\n";

    protected override void OnStartup(StartupEventArgs e)
    {
        // 第一件事：立刻写一行启动哨兵，让日志能定位是哪次构建崩了。
        // 这行必须写在任何可能抛异常的代码之前。
        Log($"=== startup {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
            $"v{ExecutableVersion} build_at={ExecutableBuildTime:yyyy-MM-dd HH:mm:ss} " +
            $"hash={ExecutableHashPrefix} ===");

        try
        {
            RunStartup(e);
        }
        catch (Exception ex)
        {
            // 启动早期（L10n/设置还没就绪）就崩：不能依赖 L10n 弹窗（它可能自己也废了），
            // 完全用硬编码中文弹窗，保证用户至少看懂「启动失败 + 日志在哪」。
            Log("!!! OnStartup 顶层异常：" + ex);
            try
            {
                MessageBox.Show(
                    HARDCODED_CRASH_BODY_PRE + ex.GetType().Name + ": " + ex.Message +
                    "\n\n日志已写入：\n" + LogFilePath,
                    HARDCODED_CRASH_TITLE,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch { /* 连 MessageBox 都弹不出来时，至少日志已经落盘 */ }
            Shutdown(1);
        }
    }

    private void RunStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
                // 完全不依赖 L10n / Safe / HumanizeKey —— 直接硬编码中文，
                // 把 v0.1.15 / v0.1.16 「L10n 全死时 Safe 兜底也不够」这种坑彻底堵死。
                MessageBox.Show(
                    "发生未处理的异常：\n\n" + args.Exception +
                    "\n\n日志已写入：\n" + LogFilePath,
                    HARDCODED_APP_NAME + " — 错误",
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
