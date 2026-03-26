using System.Windows;
using System.Windows.Input;
using MicaWPF.Controls;

namespace FreeWPFShell.UserForm
{
    public partial class RenameDialog : MicaWindow
    {
        public string NewName { get; private set; }

        public RenameDialog(string oldName)
        {
            InitializeComponent();
            TxtNewName.Text = oldName;
            TxtNewName.SelectAll();
            TxtNewName.Focus();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtNewName.Text))
            {
                ModernMessageBox.Show("新文件名不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            NewName = TxtNewName.Text;
            DialogResult = true;
        }
    }
}
