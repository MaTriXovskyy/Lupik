using Microsoft.Win32;

namespace Lupik.Core;

/// <summary>"Start with Windows": an entry in HKCU\...\Run that starts Lupik silently in the tray.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                return key?.GetValue("Lupik") != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>False if the registry refused the change.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return false;
            if (!enabled) key.DeleteValue("Lupik", false);
            else if (Environment.ProcessPath is { } exe) key.SetValue("Lupik", $"\"{exe}\" --tray"); // --tray: start silently in the tray
            else return false;
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Autostart] Could not change: {ex.Message}");
            return false;
        }
    }

    /// <summary>The entry points at the exe that set it; after a move or an update, point it at this one.</summary>
    public static void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            // Entry left by the old name (QuickPeek)
            bool old = key.GetValue("QuickPeek") != null;
            if (old) key.DeleteValue("QuickPeek", false);
            if ((old || key.GetValue("Lupik") != null) && Environment.ProcessPath is { } exe)
                key.SetValue("Lupik", $"\"{exe}\" --tray");
        }
        catch (Exception ex)
        {
            App.Log($"[Autostart] Refresh failed: {ex.Message}");
        }
    }
}
