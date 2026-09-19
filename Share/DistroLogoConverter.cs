using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreeWPFShell.Share
{

    public class DistroLogoConverter : IValueConverter
    {
        private static readonly Dictionary<string, ImageSource?> s_cache = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var distro = (value as string)?.Trim();
            return Load(string.IsNullOrEmpty(distro) ? "linux" : distro);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static ImageSource? Load(string name)
        {
            if (s_cache.TryGetValue(name, out var cached)) return cached;
            var img = LoadPng(name) ?? LoadPng("linux");
            s_cache[name] = img;
            return img;
        }

        private static ImageSource? LoadPng(string name)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri($"pack://application:,,,/LinuxLogo/{name}.png");
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }
    }
}
