using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YouShell.Terminal
{
    /// <summary>像素着色器背景图片的拉伸模式（与原生着色器语义一致）。</summary>
    public enum PixelShaderImageStretchMode
    {
        Stretch = 0,
        Fill = 1,
        Fit = 2,
        Tile = 3,
        Center = 4,
        Span = 5,
    }

    /// <summary>
    /// WinUI 3 终端控件。承载 <see cref="HwndTerminalHost"/>，对外暴露与
    /// Microsoft.Terminal.Wpf.TerminalControl 一致的编程接口。
    /// </summary>
    public sealed partial class TerminalControl : UserControl
    {
        private readonly HwndTerminalHost _host = new();

        private PixelShaderImageStretchMode _stretchMode = PixelShaderImageStretchMode.Stretch;

        public TerminalControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public int Rows => _host.Rows;
        public int Columns => _host.Columns;

        public bool AutoResize
        {
            get => _host.AutoResize;
            set => _host.AutoResize = value;
        }

        public ITerminalConnection? Connection
        {
            set => _host.Connection = value;
        }

        public string? PixelShaderPath
        {
            set => _host.SetPixelShaderPath(value);
        }

        public string? PixelShaderImagePath
        {
            set => _host.SetPixelShaderImagePath(value);
        }

        public PixelShaderImageStretchMode PixelShaderImageStretchMode
        {
            get => _stretchMode;
            set
            {
                _stretchMode = value;
                _host.SetPixelShaderParams((float)value, 0f, 0f, 0f);
            }
        }

        public void SetTheme(TerminalTheme theme, string fontFamily, short fontSize)
            => _host.SetTheme(theme, fontFamily, fontSize);

        public void SetPixelShaderParams(float p1, float p2, float p3, float p4)
            => _host.SetPixelShaderParams(p1, p2, p3, p4);

        public string GetSelectedText() => _host.GetSelectedText();

        public void ClearPixelShaderBackground()
        {
            PixelShaderPath = "";
            PixelShaderImagePath = "";
        }

        public void FocusTerminal() => _host.FocusTerminal();

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is not null)
            {
                _host.Attach(App.MainWindow, Placeholder);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 切 Tab 只是卸载视觉树：隐藏并解除绑定，保留终端缓冲与状态。
            _host.Detach();
        }

        /// <summary>关 Tab 时彻底销毁原生终端宿主。</summary>
        public void DisposeHost() => _host.Dispose();
    }
}
