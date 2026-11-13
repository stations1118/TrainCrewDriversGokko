using System.Runtime.InteropServices;

namespace Gokko.Client;

internal static class ProtectionRadioHotKey
{
    internal const int Id = 0x474B;
    internal const int Message = 0x0312;
    private const uint NoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    internal static bool Register(IntPtr window) => RegisterHotKey(window, Id, NoRepeat, (uint)Keys.F5);
    internal static void Unregister(IntPtr window) => UnregisterHotKey(window, Id);
}
