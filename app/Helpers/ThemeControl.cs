using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GHelper.Helpers
{
    public static class ThemeControl
    {

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        // SPI_SETSYSTEMDARKMODE with SPIF_UPDATEINIFILE | SPIF_SENDCHANGE — what Settings sends for the Windows mode
        private const uint SPI_SETSYSTEMDARKMODE = 0x005F;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        // WM_SETTINGCHANGE "ImmersiveColorSet" broadcast — what Settings sends so apps re-read the theme
        private const uint WM_SETTINGCHANGE = 0x001A;
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, uint pvParam, uint fWinIni);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        public static bool IsDark()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                object? value = key?.GetValue("AppsUseLightTheme");
                return value is null || (int)value <= 0;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Can't read theme registry: " + ex.Message);
                return false;
            }
        }

        // Serializes write -> SPI so two rapid toggles can't interleave and leave the
        // registry and the actual system theme out of sync. The broadcast is intentionally
        // outside the lock — it's notification-only, so a rapid second toggle must not
        // queue behind the first toggle's system-wide broadcast.
        private static readonly object themeLock = new();

        // Apps re-read the theme only when they receive the ImmersiveColorSet broadcast.
        // During a rapid burst an app can miss the final broadcast (it's busy applying an
        // earlier one), so re-broadcast a moment after the LAST toggle — the timer is
        // reset on every toggle, so it fires only once the burst has settled.
        private static readonly System.Threading.Timer followUpTimer = new(
            _ => BroadcastThemeChange(), null, Timeout.Infinite, Timeout.Infinite);

        public static void SetDark(bool dark)
        {
            lock (themeLock)
            {
                int value = dark ? 0 : 1;

                try
                {
                    using RegistryKey key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
                    key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
                    key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Can't set theme registry: " + ex.Message);
                }

                // Re-theme GHelper itself right away — the registry is already written, so the
                // visible window flips now instead of waiting for the broadcast round-trip. When
                // the broadcast arrives later, InitTheme sees no change and skips the heavy work.
                try
                {
                    // The form can be disposed mid-shutdown between the check and the call
                    Program.settingsForm?.BeginInvoke(() =>
                    {
                        Program.settingsForm.InitTheme();
                        Program.settingsForm.VisualiseIcon(true);
                    });
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Can't re-theme main form on switch: " + ex.Message);
                }

                // Toggle Windows mode the same way the Settings app does
                SystemParametersInfo(SPI_SETSYSTEMDARKMODE, (uint)value, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }

            // Broadcast so apps re-read the theme; outside the lock so a rapid second
            // toggle doesn't wait behind the first one's broadcast. The 1s follow-up
            // re-broadcast covers apps that were too busy to apply it in time.
            BroadcastThemeChange();

            followUpTimer.Change(1000, Timeout.Infinite);
        }

        private static void BroadcastThemeChange()
        {
            // WM_SETTINGCHANGE (0x001A) broadcast to all windows, SMTO_ABORTIFHUNG (0x0002)
            // Short timeout: the call waits on every top-level window in the system, so one slow
            // app must not stall the switch. Windows that were too busy to apply it in time get
            // a second chance from the 1s follow-up re-broadcast.
            UIntPtr result;
            SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, UIntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 500, out result);
        }

    }
}
