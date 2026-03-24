using Microsoft.Terminal.Wpf;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FreeWPFShell.View
{
    /// <summary>
    /// TerminalForm.xaml 的交互逻辑
    /// </summary>
    public partial class TerminalForm : Window
    {
        private ConPtyConnection _connection;

        public TerminalForm()
        {
            InitializeComponent();
            Terminal.Loaded += Terminal_Loaded;
        }

        private void Terminal_Loaded(object sender, RoutedEventArgs e)
        {
            var theme = new TerminalTheme
            {
                DefaultBackground = 0x0c0c0c,
                DefaultForeground = 0xcccccc,
                DefaultSelectionBackground = 0xcccccc,
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable = new uint[]
                {
                    0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                    0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                    0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                    0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
                },
            };

            _connection = new ConPtyConnection();
            var conn = new ConPtyConnection("cmd.exe", 120, 30);

            Terminal.Connection = conn;
            Terminal.Connection = _connection;
            Terminal.SetTheme(theme, "Cascadia Code", 12);
            Terminal.Focus();
        }

        protected override void OnClosed(EventArgs e)
        {
            _connection?.Close();
            base.OnClosed(e);
        }

    }
}