using System;
using System.IO;
using System.Text.Json;

namespace QuickPeek.Core;

public enum GlobalHotkey
{
    CtrlSpace,
    CtrlAltSpace,
    None,
}

/// <summary>User preferences, stored in %AppData%\QuickPeek\settings.json.</summary>
public class Settings
{
    /// <summary>Space on a selected file in Explorer/Desktop opens the preview.</summary>
    public bool SpaceInExplorer { get; set; } = true;

    public GlobalHotkey Hotkey { get; set; } = GlobalHotkey.CtrlSpace;

    /// <summary>Printer used last time (preselected in the print dialog, like Chrome).</summary>
    public string? LastPrinter { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPeek", "settings.json");

    public static Settings Current { get; private set; } = Load();

    public static event Action? Changed;

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Could not read settings, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public static void Update(Action<Settings> change)
    {
        change(Current);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Could not save settings: {ex.Message}");
        }
        Changed?.Invoke();
    }
}
