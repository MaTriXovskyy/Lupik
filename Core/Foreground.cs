using System;
using System.Runtime.InteropServices;

namespace Lupik.Core;

/// <summary>
/// Bringing one of Lupik's windows to the front with the keyboard focus. Windows only lets the app that got the
/// last input take the foreground; Lupik's keys come through a hook, so a harmless Alt tap counts as "our" input
/// first (the usual way around it).
/// </summary>
public static class Foreground
{
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static void Take(IntPtr hwnd)
    {
        if (NativeMethods.GetForegroundWindow() == hwnd || NativeMethods.SetForegroundWindow(hwnd)) return;
        const byte VK_MENU = 0x12;
        const uint KEYEVENTF_KEYUP = 0x2;
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        NativeMethods.SetForegroundWindow(hwnd);
    }
}
