using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Security.Credentials.UI;

namespace YouShell.Services
{
    /// <summary>
    /// 系统凭据验证服务（单一职责）。
    /// 集中封装 Windows Hello 生物识别验证与 CredUI 凭据提示框逻辑，
    /// 供 HostRepository / KeyRepository 复用，消除重复的 P/Invoke 与验证代码。
    /// </summary>
    public static class CredentialPromptService
    {
        /// <summary>
        /// 请求用户验证身份（优先 Windows Hello，回退到 Windows 凭据提示框）。
        /// </summary>
        /// <param name="prompt">显示给用户的验证提示文本。</param>
        /// <returns>true=验证通过；false=用户取消或验证失败。</returns>
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
                    if (result == 1223) return false; // 用户取消
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
