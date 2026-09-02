using System;
using System.Globalization;
using System.Windows.Data;

namespace WeaselPanel.App.Infrastructure;

/// <summary>
/// 「这个值有内容吗」→ bool。
///
/// XAML 的 DataTrigger 只能比「等于某个值」，没有「不等于 null」的写法。
/// 侧栏的分组标题需要「Tag 有值才显示」，直接写 <c>Value="{x:Null}"</c> 只能表达
/// 相反的一半，两条 Trigger 打架时优先级还不好控。转成 bool 之后条件就只有一条。
///
/// 空字符串与纯空白也算「没内容」—— 语言包里某个组标题键漏了译文时返回空串，
/// 不能因此在侧栏留一行看不见但占高度的空白。
/// </summary>
public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
