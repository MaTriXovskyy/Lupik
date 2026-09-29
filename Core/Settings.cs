using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Lupik.Core;

/// <summary>User preferences, stored in %AppData%\Lupik\settings.json.</summary>
public class Settings
{
    /// <summary>Opens the preview of the file selected in Explorer/Desktop (and closes it). Null = off.</summary>
    public KeyCombo? PreviewKey { get; set; } = KeyCombo.DefaultPreview;

    /// <summary>Opens/closes the preview from anywhere. Null = off.</summary>
    public KeyCombo? GlobalHotkey { get; set; } = KeyCombo.DefaultHotkey;

    /// <summary>Close the preview when another app comes to the front.</summary>
    public bool CloseOnFocusLoss { get; set; }

    /// <summary>Size of the preview window, 0.6–1 of the default.</summary>
    public double WindowScale { get; set; } = 1.0;

    /// <summary>Audio and video start playing as soon as they open.</summary>
    public bool AutoplayMedia { get; set; } = true;

    /// <summary>Look for a new version on GitHub (at start and every few hours).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Printer used last time (preselected in the print dialog, like Chrome).</summary>
    public string? LastPrinter { get; set; }

    /// <summary>"auto" = the Windows display language (if Lupik has it), or a language code like "de".</summary>
    public string Language { get; set; } = "auto";

    // Older settings files: migrated in Load
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? SpaceInExplorer { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Hotkey { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lupik", "settings.json");

    public static Settings Current { get; private set; } = Load();

    public static event Action? Changed;

    private static Settings Load()
    {
        try
        {
            // The app used to be called QuickPeek: carry its settings over once
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickPeek", "settings.json");
            if (!File.Exists(FilePath) && File.Exists(old))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(old, FilePath);
            }

            if (File.Exists(FilePath))
                return Migrate(JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings());
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Could not read settings, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    private static Settings Migrate(Settings s)
    {
        if (s.SpaceInExplorer == false) s.PreviewKey = null;
        if (s.Hotkey is { } old) // 0 = Ctrl+Space, 1 = Ctrl+Alt+Space, 2 = off
            s.GlobalHotkey = old switch
            {
                1 => new KeyCombo(0x20, ModifierKeys.Control | ModifierKeys.Alt),
                2 => null,
                _ => KeyCombo.DefaultHotkey,
            };
        s.SpaceInExplorer = null;
        s.Hotkey = null;
        s.WindowScale = Math.Clamp(s.WindowScale, 0.6, 1.0);
        return s;
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
