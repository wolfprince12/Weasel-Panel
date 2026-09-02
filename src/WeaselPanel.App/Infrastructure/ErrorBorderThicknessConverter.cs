using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WeaselPanel.App.Infrastructure;

/// <summary>bool → 边框粗细。非法值/错误时为 2、否则为 1，配合 BorderBrush 标红。
/// 与 <see cref="InverseBoolToVisibilityConverter"/> 同处基础设施层。</summary>
public sealed class ErrorBorderThicknessConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new Thickness(2) : new Thickness(1);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}
