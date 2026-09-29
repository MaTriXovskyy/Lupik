using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Lupik.Localization;

namespace Lupik.Core;

/// <summary>A key with its modifiers (e.g. Ctrl+Space), as chosen in Settings.</summary>
public sealed class KeyCombo
{
    public int Vk { get; set; }
    public ModifierKeys Mods { get; set; }

    public KeyCombo() { }
    public KeyCombo(int vk, ModifierKeys mods) { Vk = vk; Mods = mods; }

    public static KeyCombo DefaultPreview => new(0x20, ModifierKeys.None);

    /// <summary>Exactly this key with exactly these modifiers held.</summary>
    public bool Matches(uint vk, ModifierKeys mods) => vk == Vk && mods == Mods;

    public override string ToString() => Display();

    /// <summary>"Ctrl+Alt+Space", in the app's language.</summary>
    public string Display()
    {
        var parts = new List<string>();
        if ((Mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Mods & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(Vk));
        return string.Join("+", parts);
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public static string KeyName(int vk)
    {
        if (vk == 0x20) return Loc.T("key.space");
        var key = KeyInterop.KeyFromVirtualKey(vk);
        switch (key)
        {
            case Key.Return: return "Enter";
            case Key.Escape: return "Esc";
            case Key.Back: return "Backspace";
            case Key.PageUp: return "PgUp";
            case Key.PageDown: return "PgDn";
            case Key.Left: return "←";
            case Key.Right: return "→";
            case Key.Up: return "↑";
            case Key.Down: return "↓";
            case Key.Capital: return "Caps Lock";
            case Key.Snapshot: return "Print Screen";
            case >= Key.D0 and <= Key.D9: return ((char)('0' + (key - Key.D0))).ToString();
            case >= Key.NumPad0 and <= Key.NumPad9: return "Num " + (key - Key.NumPad0);
            case Key.Multiply: return "Num *";
            case Key.Add: return "Num +";
            case Key.Subtract: return "Num −";
            case Key.Divide: return "Num /";
            case Key.Decimal: return "Num .";
        }
        // Punctuation: the character printed on the key in the current keyboard layout
        uint ch = MapVirtualKey((uint)vk, 2) & 0x7FFFFFFF;
        if (ch > 32) return char.ToUpperInvariant((char)ch).ToString();
        return key.ToString();
    }
}
