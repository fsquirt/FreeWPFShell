using System.Windows;
using MicaWPF.Controls;
using MicaWPF.Core.Extensions;

namespace FreeWPFShell.Services
{
    public static class BackdropService
    {
        public static void ApplyToAllWindows(string type)
        {
            try
            {
                var backdrop = type switch
                {
                    "Mica" => MicaWPF.Core.Enums.BackdropType.Mica,
                    "Acrylic" => MicaWPF.Core.Enums.BackdropType.Acrylic,
                    "Tabbed" => MicaWPF.Core.Enums.BackdropType.Tabbed,
                    _ => MicaWPF.Core.Enums.BackdropType.None
                };

                foreach (Window w in Application.Current.Windows)
                {
                    if (w is MicaWindow mw)
                        w.EnableBackdrop(backdrop);
                }
            }
            catch { }
        }
    }
}
