using System.Windows;
using System.Windows.Controls;

namespace WeaselPanel.App;

/// <summary>
/// 侧栏导航项的图标字形（Segoe MDL2 Assets 码位，如 "&amp;#xE790;"）。
/// 挂在 <see cref="ListBoxItem"/> 上，由 <c>NavItem</c> 模板读取并渲染到标签左侧。
/// 图标用 Windows 内置的 Segoe MDL2 Assets 字体，不依赖外部图片，单文件发布下也不会丢。
/// </summary>
public static class NavItemHelper
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon", typeof(string), typeof(NavItemHelper), new PropertyMetadata(default(string)));

    public static void SetIcon(DependencyObject element, string value) => element.SetValue(IconProperty, value);
    public static string GetIcon(DependencyObject element) => (string)element.GetValue(IconProperty);
}
