using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Terminal.Wpf;

namespace FreeWPFShell.Services
{
    internal static class TerminalShaderBridge
    {
        private static readonly FieldInfo? ContainerField =
            typeof(TerminalControl).GetField("termContainer", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly MethodInfo? SetParamsMethod =
            typeof(TerminalContainer).GetMethod("SetPixelShaderParams", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void SetImageBrightnessReduce(TerminalControl terminal, int reducePercent)
        {
            try
            {
                var container = ContainerField?.GetValue(terminal);
                if (container == null || SetParamsMethod == null) return;

                float dim = Math.Clamp(reducePercent, 0, 100) / 100f;

                SetParamsMethod.Invoke(
                    container,
                    new object[] { (float)terminal.PixelShaderImageStretchMode, -dim, 0f, 0f });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TerminalShaderBridge] 设置背景图亮度失败: {ex.Message}");
            }
        }
    }
}
