using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WeaselPanel.App.Infrastructure;

/// <summary>
/// bool(是否选中) → 边框画刷。选中用强调色（accent），未选用极淡的主色描边，
/// 仿鼠须管 SchemeSwatch 的「选中 2.5 粗描边 / 未选 0.1 细描边」。
/// </summary>
public sealed class SelectedBorderBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type? targetType, object? parameter, CultureInfo? culture)
    {
        var key = value is true ? "AccentBrush" : "PrimaryFaintBrush";
        return Application.Current.TryFindResource(key) as Brush
               ?? new SolidColorBrush(System.Windows.Media.Colors.Transparent);
    }

    public object? ConvertBack(object? value, Type? targetType, object? parameter, CultureInfo? culture)
        => null;
}
