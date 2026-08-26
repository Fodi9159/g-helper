using System;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace GHelper.Helpers
{
    // Fork: Bluetooth on/off via the WinRT radios API (same as the quick-settings tile).
    public static class BluetoothHelper
    {
        public static async Task<bool> Toggle()
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                Radio bt = null;
                foreach (var r in radios)
                    if (r.Kind == RadioKind.Bluetooth) { bt = r; break; }

                if (bt is null)
                {
                    Logger.WriteLine("Bluetooth: no radio found");
                    Program.toast.RunToast("Bluetooth not found", ToastIcon.MicrophoneMute);
                    return false;
                }

                bool turnOn = bt.State != RadioState.On;
                await bt.SetStateAsync(turnOn ? RadioState.On : RadioState.Off);
                Logger.WriteLine("Bluetooth toggled " + (turnOn ? "On" : "Off"));
                Program.toast.RunToast("Bluetooth " + (turnOn ? Properties.Strings.On : Properties.Strings.Off), ToastIcon.FnLock);
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Bluetooth toggle failed: " + ex.Message);
                Program.toast.RunToast("Bluetooth toggle failed", ToastIcon.MicrophoneMute);
                return false;
            }
        }
    }
}
