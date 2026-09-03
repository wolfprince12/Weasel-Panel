using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WeaselPanel.App.Infrastructure;

/// <summary>
/// bool(是否选中) → 边框厚度。选中 2.5 / 未选 1，仿鼠须管 SchemeSwatch。
/// </summary>
public sealed class SelectedBorderThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => value is true ? new Thickness(2.5) : new Thickness(1);

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => null;
}
