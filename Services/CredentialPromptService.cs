using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace FreeWPFShell.Services
{

    public static class CredentialPromptService
    {

        public static async Task<bool> RequestAuthenticationAsync(string prompt)
        {
            try
            {
                var availability = await UserConsentVerifier.CheckAvailabilityAsync();
                if (availability == UserConsentVerifierAvailability.Available)
                {
                    var result = await UserConsentVerifier.RequestVerificationAsync(prompt);
                    if (result == UserConsentVerificationResult.Verified) return true;
                    if (result == UserConsentVerificationResult.Canceled) return false;
                }
            }
            catch { }

            return await Task.Run(() =>
            {
                int authError = 0;
                while (true)
                {
                    var uiInfo = new CREDUI_INFO
                    {
                        cbSize = Marshal.SizeOf(typeof(CREDUI_INFO)),
                        hwndParent = GetConsoleWindow(),
                        pszMessageText = prompt
                    };
                    uint authPackage = 0; IntPtr outBuffer; uint outSize; bool save = false;
                    uint result = CredUIPromptForWindowsCredentials(
                        ref uiInfo, authError, ref authPackage,
                        IntPtr.Zero, 0, out outBuffer, out outSize, ref save, 0x1);
                    if (result == 1223) return false; 
                    if (result == 0)
                    {
                        if (outBuffer != IntPtr.Zero) Marshal.FreeCoTaskMem(outBuffer);
                        return true;
                    }
                    authError = (int)result;
                }
            });
        }

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern IntPtr GetConsoleWindow();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDUI_INFO
        {
            public int cbSize;
            public IntPtr hwndParent;
            public string pszMessageText;
            public string pszCaptionText;
            public IntPtr hbmBanner;
        }

        [DllImport("credui.dll", CharSet = CharSet.Unicode)]
        private static extern uint CredUIPromptForWindowsCredentials(
            ref CREDUI_INFO pUiInfo, int authError, ref uint pulAuthPackage,
            IntPtr pvInAuthBuffer, uint ulInAuthBufferSize,
            out IntPtr ppvOutAuthBuffer, out uint pulOutAuthBufferSize,
            ref bool pfSave, int flags);
    }
}
