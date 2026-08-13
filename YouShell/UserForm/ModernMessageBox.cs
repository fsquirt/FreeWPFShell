using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YouShell.UserForm
{
    // WinUI 3 没有 WPF 的 MessageBoxButton/MessageBoxImage/MessageBoxResult，这里补齐同名枚举，
    // 以最小化迁移时的调用点改动。
    public enum MessageBoxButton { OK, YesNo, OKCancel }

    public enum MessageBoxImage { None, Error, Question, Warning, Information }

    public enum MessageBoxResult { None, OK, Cancel, Yes, No }

    /// <summary>
    /// 现代化消息框：等价于 WPF 的 ModernMessageBox，底层用 WinUI 3 的 ContentDialog。
    /// 注意：WinUI 3 对话框是异步的，因此 ShowAsync 返回 Task；确认类调用请 await。
    /// </summary>
    public static class ModernMessageBox
    {
        /// <summary>解析对话框的 XamlRoot（主窗口根元素）。</summary>
        private static XamlRoot? GetRoot() => App.MainWindow?.Content?.XamlRoot;

        /// <summary>供 ContentDialog 子类在代码里创建后共享的 XamlRoot。</summary>
        public static XamlRoot? Root => App.MainWindow?.Content?.XamlRoot;

        public static async Task<MessageBoxResult> ShowAsync(
            string message,
            string title = "提示",
            MessageBoxButton button = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                DefaultButton = ContentDialogButton.Primary,
            };

            switch (button)
            {
                case MessageBoxButton.YesNo:
                    dialog.PrimaryButtonText = "是";
                    dialog.CloseButtonText = "否";
                    break;
                case MessageBoxButton.OKCancel:
                    dialog.PrimaryButtonText = "确定";
                    dialog.CloseButtonText = "取消";
                    break;
                default:
                    dialog.PrimaryButtonText = "确定";
                    break;
            }

            var root = GetRoot();
            if (root != null) dialog.XamlRoot = root;

            var result = await dialog.ShowAsync();
            return button switch
            {
                MessageBoxButton.YesNo => result == ContentDialogResult.Primary
                    ? MessageBoxResult.Yes : MessageBoxResult.No,
                MessageBoxButton.OKCancel => result == ContentDialogResult.Primary
                    ? MessageBoxResult.OK : MessageBoxResult.Cancel,
                _ => MessageBoxResult.OK,
            };
        }
    }
}
