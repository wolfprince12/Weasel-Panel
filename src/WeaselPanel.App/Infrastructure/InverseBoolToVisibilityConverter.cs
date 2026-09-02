using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WeaselPanel.App.Infrastructure;

/// <summary>
/// bool → Visibility，取反版。
/// </summary>
/// <remarks>
/// 项目里几乎每个页面都有「有选中项时显示编辑区，没有时显示一句提示」这种成对判断。
/// 没有这个转换器的话，只能给每个否定分支都在 ViewModel 里补一个 <c>HasNoXxx</c> 属性，
/// 于是 VM 里凭空多出一堆只服务于一个绑定的取反属性。
/// 用它之后 VM 只保留正向语义（HasSelection / IsDirty …），否定留在 XAML 里写。
/// </remarks>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ? false : true;
}
