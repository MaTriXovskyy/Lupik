using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Lupik.Core;

/// <summary>Owns only Win+Shift+S, including repeats and the matching release.</summary>
internal static class ScreenshotShortcut
{
    private static bool _held;

    internal static bool Process(uint key, bool down, ModifierKeys modifiers, bool enabled, Action capture)
    {
        if (key != 0x53) return false;
        if (!down) { bool consumed = _held; _held = false; return consumed; }
        if (_held) return true;
        if (!enabled || modifiers != (ModifierKeys.Windows | ModifierKeys.Shift)) return false;
        _held = true;
        capture();
        return true;
    }

    internal static void Reset() => _held = false;

    // S does not reach Explorer. Mark the Windows-key chord as used so releasing Win
    // cannot open Start. An unassigned key has no text or application command of its own.
    internal static void MaskWindowsMenu()
    {
        var inputs = new[]
        {
            new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Vk = 0x07 } } },
            new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Vk = 0x07, Flags = 2 } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
