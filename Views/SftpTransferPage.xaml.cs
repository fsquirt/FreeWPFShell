using System.Windows;
using System.Windows.Controls;
using FreeWPFShell.Models;
using FreeWPFShell.Services;

namespace FreeWPFShell.Views
{
    public partial class SftpTransferPage : UserControl
    {
        public SshSessionService Session { get; }

        public SftpTransferPage(SshSessionService session)
        {
            InitializeComponent();
            Session = session;
            DataContext = session.Transfers;
            TransferGrid.ItemsSource = session.Transfers.Tasks;
        }

        private void BtnPauseAll_Click(object sender, RoutedEventArgs e) => Session.Transfers.PauseAll();

        private void BtnResumeAll_Click(object sender, RoutedEventArgs e) => Session.Transfers.ResumeAll();

        private void BtnCancelAll_Click(object sender, RoutedEventArgs e)
        {
            if (Session.Transfers.Tasks.Count == 0) return;
            if (UserForm.ModernMessageBox.Show("确定要取消所有传输任务吗？", "取消传输",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Session.Transfers.CancelAll();
        }

        private void TaskPause_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Pause();

        private void TaskResume_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Resume();

        private void TaskCancel_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Cancel();

        private static SftpTransferTask? TaskOf(object sender)
            => (sender as FrameworkElement)?.DataContext as SftpTransferTask;
    }
}
