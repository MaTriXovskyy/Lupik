using System.Runtime.InteropServices;
using System.Windows.Input;

namespace QuickPeek.Core;

/// <summary>
/// Modifier keys read from the system. The preview window never takes the keyboard focus (so WPF's
/// Keyboard.Modifiers isn't kept up to date for it); this asks Windows directly.
/// </summary>
public static class KeyState
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static ModifierKeys Modifiers
    {
        get
        {
            var m = ModifierKeys.None;
            if (Down(0x10)) m |= ModifierKeys.Shift;
            if (Down(0x11)) m |= ModifierKeys.Control;
            if (Down(0x12)) m |= ModifierKeys.Alt;
            if (Down(0x5B) || Down(0x5C)) m |= ModifierKeys.Windows;
            return m;
        }
    }
}
