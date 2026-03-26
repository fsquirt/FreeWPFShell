using System.Windows;
using System.Windows.Input;
using MicaWPF.Controls;

namespace FreeWPFShell.Pages
{
    public partial class ModernMessageBox : MicaWindow
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

        public ModernMessageBox(string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            InitializeComponent();
            TxtMessage.Text = message;

            if (button == MessageBoxButton.YesNo)
            {
                BtnYes.Visibility = Visibility.Visible;
                BtnNo.Visibility = Visibility.Visible;
                BtnOk.Visibility = Visibility.Collapsed;
            }
            else // Default OK
            {
                BtnOk.Visibility = Visibility.Visible;
                BtnYes.Visibility = Visibility.Collapsed;
                BtnNo.Visibility = Visibility.Collapsed;
            }
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Yes;
            DialogResult = true;
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.No;
            DialogResult = false;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.OK;
            DialogResult = true;
        }

        public static MessageBoxResult Show(string message, string title = "提示", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            return App.Current.Dispatcher.Invoke(() =>
            {
                var msgBox = new ModernMessageBox(message, title, button, image);
                msgBox.Owner = Application.Current.MainWindow;
                msgBox.ShowDialog();
                return msgBox.Result;
            });
        }
    }
}
