using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik;

/// <summary>
/// Pinning (P or the pin button): the file moves to a window of its own that stays where it is, above other windows,
/// and no longer follows Explorer. The preview itself closes and keeps working as before, so another file can be
/// opened next to the pinned one. A pinned window is a normal window: click it to use its keys, P toggles
/// "always on top", Esc or Space closes it.
/// </summary>
public partial class MainWindow
{
    // Field initializers run before the constructor, so the window knows it's pinned before its handle exists
    [ThreadStatic] private static bool _creatingPinned;
    private readonly bool _pinned = _creatingPinned;

    /// <summary>A pinned copy (not the Explorer preview).</summary>
    public bool Pinned => _pinned;

    private void TogglePin()
    {
        if (Pinned)
        {
            Topmost = !Topmost;
            UpdatePinButton();
            ShowNotice(Loc.T(Topmost ? "pin.onTop" : "pin.notOnTop"));
            return;
        }
        if (!PathExists(_currentFilePath) || CompareViewerControl.Visibility == Visibility.Visible || DiffViewerControl.Visibility == Visibility.Visible) return;
        if (EditMode && !ExitEdit()) return;

        string path = _currentFilePath;
        var bounds = new Rect(Left, Top, Width, Height);
        var pinned = CreatePinned();
        pinned.Left = bounds.Left; pinned.Top = bounds.Top; pinned.Width = bounds.Width; pinned.Height = bounds.Height;
        HideWindow();
        pinned.Topmost = true;
        pinned.Show();
        pinned.UpdatePinButton();
        _ = pinned.ShowFile(path);
        App.Log($"[MainWindow] Pinned '{path}'");
    }

    private static MainWindow CreatePinned()
    {
        _creatingPinned = true;
        try { return new MainWindow(); }
        finally { _creatingPinned = false; }
    }

    private void UpdatePinButton()
    {
        bool pinned = _pinned;
        PinIcon.Kind = pinned && !Topmost ? "pin-off" : "pin";
        if (pinned && Topmost) PinButton.SetResourceReference(ForegroundProperty, "Gold");
        else PinButton.ClearValue(ForegroundProperty);
        PinButton.ToolTip = Loc.T(!pinned ? "pin.tip" : Topmost ? "pin.onTopTip" : "pin.notOnTopTip");
    }

    private void OnPinClicked(object sender, RoutedEventArgs e) => TogglePin();

    /// <summary>Typing into the search bar of a pinned window (it gets its keys the normal way, not from the hook).</summary>
    private void OnWindowTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_pinned || !SearchActive || string.IsNullOrEmpty(e.Text)) return;
        HandleSearchKey(0, ModifierKeys.None, e.Text);
        e.Handled = true;
    }
}
