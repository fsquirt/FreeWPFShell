using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreeWPFShell.Views
{
    /// <summary>
    /// 发行版标识（/etc/os-release 的 ID=）→ 首页卡片 logo。
    /// 匹配规则：精确匹配文件名 → 发行版 ID 包含文件名（almalinux→alma）或反之（opensuse-leap→opensuse）→ 兜底 linux。
    /// </summary>
    public class DistroLogoConverter : IValueConverter
    {
        private static readonly HashSet<string> s_available = new()
        {
            "alma", "alpine", "amazon", "android", "apple", "arch", "centos", "debian",
            "fedora", "freebsd", "gentoo", "kali", "linux", "manjaro", "mint", "nixos",
            "opensuse", "raspberry", "redhat", "rocky", "ubuntu"
        };

        // os-release 中常见的发行版 ID 与 logo 文件名的别名映射
        private static readonly Dictionary<string, string> s_aliases = new()
        {
            ["rhel"] = "redhat",
            ["sles"] = "opensuse",
        };

        private static readonly Dictionary<string, ImageSource?> s_cache = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var distro = (value as string)?.Trim().ToLowerInvariant();
            var name = ResolveLogoName(distro);
            return LoadLogo(name);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static string ResolveLogoName(string? distro)
        {
            if (string.IsNullOrEmpty(distro)) return "linux";
            if (s_available.Contains(distro)) return distro;
            if (s_aliases.TryGetValue(distro, out var alias) && s_available.Contains(alias)) return alias;

            // 子串匹配（不分先后，取第一个命中的）
            foreach (var name in s_available)
            {
                if (name == "linux") continue; // 兜底文件不参与子串匹配
                if (distro.Contains(name) || name.Contains(distro)) return name;
            }
            return "linux";
        }

        private static ImageSource? LoadLogo(string name)
        {
            if (s_cache.TryGetValue(name, out var cached)) return cached;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri($"pack://application:,,,/LinuxLogo/{name}.png");
                img.EndInit();
                img.Freeze();
                s_cache[name] = img;
                return img;
            }
            catch
            {
                s_cache[name] = null;
                return null;
            }
        }
    }
}
