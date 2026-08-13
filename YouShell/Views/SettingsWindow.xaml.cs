using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using YouShell.Repositories;
using YouShell.Services;

namespace YouShell.Views
{
    /// <summary>
    /// 设置对话框（WinUI 3 ContentDialog）。保存到 SettingsRepository。
    /// </summary>
    public sealed partial class SettingsWindow : ContentDialog
    {
        private readonly SettingsRepository _settingsRepo = new();

        public SettingsWindow()
        {
            InitializeComponent();
            var settings = _settingsRepo.Load();
            TogVault.IsOn = settings.UseWindowsHello;
            TogLinuxMonitor.IsOn = settings.UseLinuxMonitor;
            TogInjectLocale.IsOn = settings.InjectChineseLocale;
            TxtTerminalBg.Text = settings.TerminalBackground;
            TogImageBg.IsOn = settings.UseImageBackground;
            TxtImagePath.Text = settings.ImageBackgroundPath ?? "";
            TxtTraceTimeout.Text = settings.TracerouteTimeout.ToString();
            TxtTraceMaxHops.Text = settings.TracerouteMaxHops.ToString();
            TxtTerminalFont.Text = settings.TerminalFont;
            TxtTerminalFontSize.Text = settings.TerminalFontSize.ToString();
            CmbBackdrop.SelectedIndex = BackdropToIndex(settings.BackdropType);
            CmbStretch.SelectedIndex = settings.ImageStretchMode;
        }

        private async void BtnSelectImage_Click(object sender, RoutedEventArgs e)
        {
            string? path = await UserForm.PickerHelper.PickSingleFileAsync(".jpg", ".png", ".bmp", ".jpeg");
            if (!string.IsNullOrEmpty(path)) TxtImagePath.Text = path;
        }

        private void CmbBackdrop_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 切换即生效，匹配 WPF 版的实时预览行为
            var settings = _settingsRepo.Load();
            settings.BackdropType = BackdropFromIndex();
            _settingsRepo.Save(settings);
            if (App.MainWindow != null)
                BackdropService.Apply(App.MainWindow, settings.BackdropType);
        }

        private void SettingsWindow_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var settings = _settingsRepo.Load();
            settings.UseWindowsHello = TogVault.IsOn;
            settings.UseLinuxMonitor = TogLinuxMonitor.IsOn;
            settings.InjectChineseLocale = TogInjectLocale.IsOn;
            settings.TerminalBackground = TxtTerminalBg.Text.Trim();
            settings.UseImageBackground = TogImageBg.IsOn;
            settings.ImageBackgroundPath = TxtImagePath.Text;
            settings.TerminalFont = TxtTerminalFont.Text.Trim();
            if (int.TryParse(TxtTerminalFontSize.Text, out int fs)) settings.TerminalFontSize = fs;
            settings.ImageStretchMode = CmbStretch.SelectedIndex;
            if (int.TryParse(TxtTraceTimeout.Text, out int timeout)) settings.TracerouteTimeout = timeout;
            if (int.TryParse(TxtTraceMaxHops.Text, out int hops)) settings.TracerouteMaxHops = hops;
            settings.BackdropType = BackdropFromIndex();
            _settingsRepo.Save(settings);
        }

        private string BackdropFromIndex() => CmbBackdrop.SelectedIndex switch
        {
            0 => "None",
            1 => "Mica",
            2 => "Acrylic",
            3 => "Tabbed",
            _ => "Mica",
        };

        private static int BackdropToIndex(string type) => type switch
        {
            "None" => 0,
            "Acrylic" => 2,
            "Tabbed" => 3,
            _ => 1, // Mica 默认
        };
    }
}
