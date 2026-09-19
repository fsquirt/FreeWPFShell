using FreeWPFShell.Services;
using MicaWPF.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace FreeWPFShell.UserForm
{
    public partial class ModernMessageBox : MicaWindow
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

        public int OptionIndex { get; private set; }

        public ModernMessageBox(string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            InitializeComponent();

            var settingsRepo = new Repositories.SettingsRepository();
            BackdropService.ApplyToAllWindows(settingsRepo.Load().BackdropType);

            TxtMessage.Text = message;

            if (button == MessageBoxButton.YesNo)
            {
                BtnYes.Visibility = Visibility.Visible;
                BtnNo.Visibility = Visibility.Visible;
                BtnOk.Visibility = Visibility.Collapsed;
            }
            else 
            {
                BtnOk.Visibility = Visibility.Visible;
                BtnYes.Visibility = Visibility.Collapsed;
                BtnNo.Visibility = Visibility.Collapsed;
            }

            this.Title = title;


            if (image != MessageBoxImage.None)
            {
                TxtIcon.Visibility = Visibility.Visible;
                switch (image)
                {
                    case MessageBoxImage.Error:
                        TxtIcon.Kind = PackIconRemixIconKind.CloseCircleFill;
                        TxtIcon.Foreground = Brushes.Red;
                        break;
                    case MessageBoxImage.Question:
                        TxtIcon.Kind = PackIconRemixIconKind.QuestionFill;
                        TxtIcon.Foreground = Brushes.SkyBlue;
                        break;
                    case MessageBoxImage.Warning:
                        TxtIcon.Kind = PackIconRemixIconKind.ErrorWarningFill;
                        TxtIcon.Foreground = Brushes.Orange;
                        break;
                    case MessageBoxImage.Information:
                        TxtIcon.Kind = PackIconRemixIconKind.InformationFill;
                        TxtIcon.Foreground = Brushes.DeepSkyBlue;
                        break;
                    default:
                        TxtIcon.Kind = PackIconRemixIconKind.InformationFill;
                        TxtIcon.Foreground = Brushes.DeepSkyBlue;
                        break;
                }
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

        private void BtnOpt_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag && int.TryParse(tag, out int index))
                OptionIndex = index;

            DialogResult = true;
        }

        public static int ShowOptions(string message, string title, string option1, string option2, string option3, MessageBoxImage image = MessageBoxImage.Question)
        {
            return App.Current.Dispatcher.Invoke(() =>
            {
                var msgBox = new ModernMessageBox(message, title, MessageBoxButton.OK, image);

                msgBox.BtnOpt1.Content = option1;
                msgBox.BtnOpt2.Content = option2;
                msgBox.BtnOpt3.Content = option3;
                msgBox.PnlButtons.Visibility = Visibility.Collapsed;
                msgBox.PnlOptions.Visibility = Visibility.Visible;

                var mainWin = Application.Current.MainWindow;
                if (mainWin != null && mainWin.IsVisible)
                {
                    try { msgBox.Owner = mainWin; } catch { }
                }

                msgBox.ShowDialog();
                return msgBox.OptionIndex;
            });
        }

        public static MessageBoxResult Show(string message, string title = "提示", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            return App.Current.Dispatcher.Invoke(() =>
            {
                var msgBox = new ModernMessageBox(message, title, button, image);
                

                var mainWin = Application.Current.MainWindow;
                if (mainWin != null && mainWin.IsVisible)
                {
                    try { msgBox.Owner = mainWin; } catch { }
                }
                
                msgBox.ShowDialog();
                return msgBox.Result;
            });
        }
    }
}
