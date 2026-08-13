using Microsoft.UI.Xaml.Data;

namespace YouShell.Converters
{
    /// <summary>bool IsDirectory → Segoe MDL2 图标字形（文件夹/文件）。</summary>
    public sealed class DirectoryToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? "" : ""; // Folder / Document

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
