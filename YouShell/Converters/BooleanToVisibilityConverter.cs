using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace YouShell.Converters
{
    /// <summary>bool → Visibility 转换器（WinUI 3 未内置，等价于 WPF 的同名转换器）。</summary>
    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
