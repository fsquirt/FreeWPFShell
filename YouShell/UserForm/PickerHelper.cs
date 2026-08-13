using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace YouShell.UserForm
{
    /// <summary>文件选择器辅助：等价于 WPF 的 Microsoft.Win32.OpenFileDialog 简化用法。</summary>
    public static class PickerHelper
    {
        /// <summary>弹出单个文件选择框，返回选中文件路径；用户取消时返回 null。</summary>
        public static async Task<string?> PickSingleFileAsync(params string[] extensions)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            // WinUI 3 的 FileOpenPicker 必须绑定到拥有它的窗口 HWND，否则抛出 E_ILLEGAL_METHOD_CALL
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow!);
            InitializeWithWindow.Initialize(picker, hwnd);
            foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
    }
}
