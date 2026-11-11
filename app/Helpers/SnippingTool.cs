using Microsoft.Win32;

namespace GHelper.Helpers
{
    /// <summary>
    /// Opens the Windows Snipping Tool snip overlay (the same UI as Win+Shift+S).
    ///
    /// The overlay is activated through the ms-screenclip: protocol instead of
    /// faking a key press, so it opens no matter which window has the focus.
    /// </summary>
    public static class SnippingTool
    {
        public static void Open()
        {
            // ms-screenclip: was introduced with Snip & Sketch on Windows 10 1809,
            // every supported Windows version has it. If it is missing fall back to
            // the global Win+Shift+S hotkey.
            using (RegistryKey? protocol = Registry.ClassesRoot.OpenSubKey("ms-screenclip"))
            {
                if (protocol is null)
                {
                    KeyboardHook.KeyKeyKeyPress(Keys.LWin, Keys.LShiftKey, Keys.S);
                    return;
                }
            }

            // Started through the shell user, so it keeps working when G-Helper
            // happens to run elevated - packaged apps like Snipping Tool cannot be
            // launched elevated.
            RestrictedProcessHelper.RunAsRestrictedUser("start \"\" \"ms-screenclip:\"");
        }
    }
}
