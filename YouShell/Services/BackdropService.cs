using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace YouShell.Services
{
    /// <summary>
    /// 窗口背景材质服务。等价于 WPF 中基于 MicaWPF 的 BackdropService：
    /// 依据设置项（None/Mica/Acrylic/Tabbed）为窗口挂载 WinUI 3 的 SystemBackdrop。
    /// </summary>
    public static class BackdropService
    {
        /// <summary>依据设置字符串创建对应的背景材质。不支持的类型返回 null（无背景）。</summary>
        public static SystemBackdrop? Create(string type) => type switch
        {
            "Mica" => new MicaBackdrop(),
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "Tabbed" => new MicaBackdrop(), // WinUI 3 内置无 Tabbed Mica，暂映射到 Mica
            _ => null,
        };

        /// <summary>把背景材质应用到指定窗口（主窗口及新建的次级窗口）。</summary>
        public static void Apply(Window window, string type)
        {
            try { window.SystemBackdrop = Create(type); }
            catch { /* 背景材质失败不致命 */ }
        }
    }
}
