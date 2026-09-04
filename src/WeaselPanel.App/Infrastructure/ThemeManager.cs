using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WeaselPanel.App.Infrastructure;

/// <summary>
/// 明暗主题管理器：侦测 Windows 浅/深主题（AppsUseLightTheme 注册表项），
/// 在启动时与运行时（用户在系统里切主题）实时改写 App.Resources 里语义画刷的 Color。
///
/// 设计取舍（为什么是「改写画刷实例的 Color」，而不是换 ResourceDictionary）：
/// 1. 全部页面与样式都用 {StaticResource X} 引用语义画刷；StaticResource 在元素解析时
///    就已逮死画刷实例，换字典不会让已解析的引用刷新， DynamicResource 才是为换字典而生的。
/// 2. 改写共享画刷实例的 Color 属性能让所有引用**即时**生效，且**不重建窗口** ——
///    本程序的脏标记 / 全局部署都是内存态，重建窗口会丢掉用户未保存的编辑。
/// 3. App.Resources 顶层条目里的 SolidColorBrush 默认未被 Freeze，可安全改写 Color。
///
/// 只切换「随主题变」的那一组语义画刷；主色梯度与状态色跨主题保持不变，不列入。
/// </summary>
public static class ThemeManager
{
    // 需随主题切换的语义画刷：key → (浅色 Color, 深色 Color)。
    private static readonly Dictionary<string, (Color Light, Color Dark)> ThemeBrushes = new()
    {
        ["TextPrimaryBrush"]    = (Color.FromRgb(0x16, 0x19, 0x1D), Color.FromRgb(0xF2, 0xF4, 0xF7)),
        ["TextLabelBrush"]      = (Color.FromRgb(0x45, 0x4C, 0x54), Color.FromRgb(0xC5, 0xCB, 0xD2)),
        ["TextSecondaryBrush"]  = (Color.FromRgb(0x5F, 0x68, 0x71), Color.FromRgb(0x9C, 0xA4, 0xAD)),
        ["TextDisabledBrush"]   = (Color.FromRgb(0x9A, 0xA2, 0xAB), Color.FromRgb(0x6B, 0x72, 0x7B)),

        ["WindowBgBrush"]       = (Color.FromRgb(0xF5, 0xF7, 0xF9), Color.FromRgb(0x1F, 0x22, 0x27)),
        ["SidebarBgBrush"]      = (Color.FromRgb(0xF3, 0xF5, 0xF7), Color.FromRgb(0x2E, 0x32, 0x3A)),
        ["CardBgBrush"]         = (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x26, 0x29, 0x2F)),
        ["BgSubtleBrush"]       = (Color.FromRgb(0xF3, 0xF5, 0xF7), Color.FromRgb(0x2E, 0x32, 0x3A)),
        ["BgHoverBrush"]        = (Color.FromRgb(0xF3, 0xF5, 0xF7), Color.FromRgb(0x31, 0x36, 0x40)),
        ["RowAltBrush"]         = (Color.FromRgb(0xFA, 0xFB, 0xFC), Color.FromRgb(0x22, 0x25, 0x2B)),
        ["PressedSurfaceBrush"] = (Color.FromRgb(0xE5, 0xE9, 0xED), Color.FromRgb(0x1B, 0x1E, 0x23)),

        ["BorderBrushKey"]      = (Color.FromRgb(0xE5, 0xE9, 0xED), Color.FromRgb(0x3A, 0x3F, 0x47)),
        ["BorderBrushStrong"]   = (Color.FromRgb(0xD2, 0xD8, 0xDE), Color.FromRgb(0x4A, 0x50, 0x5A)),
    };

    private static bool _currentDark;
    private static bool _subscribed;

    /// <summary>在 App.OnStartup 中、创建任何窗口之前调用。</summary>
    public static void Init()
    {
        _currentDark = IsSystemDark();
        Apply(_currentDark);

        if (!_subscribed)
        {
            _subscribed = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // 主题/颜色变化走 General 类。
        if (e.Category != UserPreferenceCategory.General) return;
        var dark = IsSystemDark();
        if (dark == _currentDark) return;

        // 该事件可能不在 UI 线程触发，确保回 UI 线程改写画刷（画刷是 DependencyObject）。
        if (Application.Current.Dispatcher.CheckAccess())
            Apply(dark);
        else
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Apply(dark)));
    }

    private static void Apply(bool dark)
    {
        _currentDark = dark;
        var res = Application.Current.Resources;
        foreach (var kv in ThemeBrushes)
        {
            if (res[kv.Key] is not SolidColorBrush brush)
            {
                // 资源缺失（理论上不会发生）：打个日志不致命，不影响其余画刷。
                App.Log($"ThemeManager: 语义画刷缺失，跳过 {kv.Key}");
                continue;
            }
            if (brush.IsFrozen)
            {
                // App.Resources 顶层画刷默认不冻结；万一冻结，改写会抛异常。
                // 这里只记录，避免把整个启动流程带崩（最坏结果是该画刷不随主题切换）。
                App.Log($"ThemeManager: 画刷 {kv.Key} 已冻结，无法随主题切换");
                continue;
            }
            brush.Color = dark ? kv.Value.Dark : kv.Value.Light;
        }
    }

    /// <summary>读注册表 AppsUseLightTheme：1=浅色，0 或缺失=深色。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            var v = Registry.GetValue(key, "AppsUseLightTheme", 1);
            if (v is int i) return i == 0;
        }
        catch
        {
            // 读不到就按浅色，最安全。
        }
        return false;
    }
}
