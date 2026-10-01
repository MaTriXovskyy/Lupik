using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik;

/// <summary>
/// Edit mode (E): text and code, CSV cells, PDF pages, quick picture fixes. The preview normally never takes the
/// focus; in edit mode it does (typing needs a real keyboard focus), and gives it back to Explorer afterwards.
/// Saving writes the new version next to the file, sends the original to the Recycle Bin and puts the new one in place.
/// </summary>
public partial class MainWindow
{
    /// <summary>Edit mode is on: the keyboard hook leaves every key to the window (read by the hook thread).</summary>
    public volatile bool EditMode;

    /// <summary>The current preview can be edited: E is routed (read by the hook thread).</summary>
    public volatile bool EditableInPreview;

    private Views.IEditable? _editing;
    private string _editingPath = "";

    private Views.IEditable? CurrentEditable()
    {
        foreach (UIElement viewer in new UIElement[] { CodeViewerControl, CsvViewerControl, PdfViewerControl, ImageViewerControl })
            if (viewer.Visibility == Visibility.Visible && viewer is Views.IEditable e) return e;
        return null;
    }

    private void UpdateEditable()
    {
        EditableInPreview = CurrentEditable() != null && File.Exists(_currentFilePath) && !EditMode;
        EditButton.Visibility = CurrentEditable() != null && !EditMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEditClicked(object sender, RoutedEventArgs e) => EnterEdit();
    private void OnEditSaveClicked(object sender, RoutedEventArgs e) => _ = SaveEditAsync();
    private void OnEditDoneClicked(object sender, RoutedEventArgs e) => ExitEdit();

    private void EnterEdit()
    {
        if (EditMode) return;
        var editable = CurrentEditable();
        if (editable == null || !File.Exists(_currentFilePath)) return;
        if (editable.WhyNotEditable() is string reason)
        {
            ShowNotice(reason);
            return;
        }
        CloseSearch();

        _editing = editable;
        _editingPath = _currentFilePath;
        EditMode = true;
        EditableInPreview = false;
        editable.DirtyChanged += OnEditDirtyChanged;
        editable.BeginEdit();

        HeaderTools.Visibility = Visibility.Collapsed;
        EditBar.Visibility = Visibility.Visible;
        EditButton.Visibility = Visibility.Collapsed;
        OnEditDirtyChanged();
        TakeFocus();
        App.Log($"[MainWindow] Edit mode: '{_editingPath}'");
    }

    /// <summary>
    /// Leaves edit mode; with unsaved changes asks first (save / discard / stay). False if the user chose to stay.
    /// </summary>
    private bool ExitEdit()
    {
        if (!EditMode || _editing == null) return true;
        if (_editing.IsDirty)
        {
            var answer = Views.UnsavedChangesDialog.Ask(this, _editingPath);
            if (answer == Views.UnsavedChoice.Cancel) return false;
            if (answer == Views.UnsavedChoice.Save)
            {
                // Save first; leave only if it worked
                _ = SaveEditAsync(exitAfter: true);
                return false;
            }
        }
        FinishEdit(discarded: _editing.IsDirty);
        return true;
    }

    private void FinishEdit(bool discarded)
    {
        var editable = _editing;
        if (editable == null) return;
        editable.DirtyChanged -= OnEditDirtyChanged;
        editable.EndEdit();
        _editing = null;
        EditMode = false;

        EditBar.Visibility = Visibility.Collapsed;
        HeaderTools.Visibility = Visibility.Visible;
        GiveFocusBack();
        UpdateEditable();
        App.Log($"[MainWindow] Edit mode off{(discarded ? " (changes discarded)" : "")}");
        // Thrown-away changes: show the file as it is on disk
        if (discarded && File.Exists(_editingPath)) _ = ShowFile(_editingPath);
    }

    private bool _saving;

    private async Task SaveEditAsync(bool exitAfter = false)
    {
        var editable = _editing;
        if (editable == null || _saving) return;
        _saving = true;
        EditSaveButton.IsEnabled = false;
        string path = _editingPath;
        try
        {
            await SafeReplace.ReplaceAsync(path, editable.SaveToAsync, editable.ReleaseFile);
            await editable.ReloadAsync(path);
            ShowNotice(Loc.T("edit.saved", Path.GetFileName(path)), success: true);
            if (exitAfter) FinishEdit(discarded: false);
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Saving the edit failed: {ex}");
            Views.MessageCard.Show(this, Loc.T("edit.saveError", ex.Message));
            try { await editable.ReloadAsync(path); } catch { /* the viewer keeps what it has */ }
        }
        finally
        {
            _saving = false;
            EditSaveButton.IsEnabled = true;
            OnEditDirtyChanged();
        }
    }

    private void OnEditDirtyChanged()
    {
        bool dirty = _editing?.IsDirty == true;
        EditDirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        EditSaveButton.Opacity = dirty ? 1 : 0.55;
    }

    /// <summary>Keys while editing (the window has the focus now): the viewer first, then Ctrl+S and Esc.</summary>
    private bool HandleEditModeKey(Key key, ModifierKeys mods)
    {
        if (_editing == null) return false;
        // Full screen works while editing too, just not while typing (then F is a letter)
        if (key == Key.F && mods == 0 && !IsTypingInEditor()) { ToggleFullScreen(); return true; }
        if (_editing.HandleEditKey(key, mods)) return true;
        if (key == Key.S && mods == ModifierKeys.Control) { _ = SaveEditAsync(); return true; }
        if (key == Key.Escape && mods == 0) { ExitEdit(); return true; }
        return false;
    }

    /// <summary>
    /// Would F type a letter? In a text field or the code editor, and in a table with a selected cell (typing there
    /// starts editing the cell, like in Excel).
    /// </summary>
    private bool IsTypingInEditor() =>
        Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or ICSharpCode.AvalonEdit.Editing.TextArea
        || (CsvViewerControl.Visibility == Visibility.Visible && CsvViewerControl.HasSelection);

    // ---------- Focus ----------

    private const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Makes the preview a normal, activatable window and brings it to the front with the keyboard focus.</summary>
    private void TakeFocus()
    {
        SetWindowLong(Hwnd, GWL_EXSTYLE, GetWindowLong(Hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        Core.Foreground.Take(Hwnd);
        Activate();
    }

    /// <summary>Back to how the preview always works: no activation, Explorer in front with the focus.</summary>
    private void GiveFocusBack()
    {
        if (_pinned) return; // a pinned window is a normal window: it keeps the focus
        SetWindowLong(Hwnd, GWL_EXSTYLE, GetWindowLong(Hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        if (SourceWindow != IntPtr.Zero) NativeMethods.SetForegroundWindow(SourceWindow);
    }

    /// <summary>A short message in the title bar area (reusing the toast on the save icon when it's not edit mode).</summary>
    private async void ShowNotice(string message, bool success = false)
    {
        NoticeText.Text = message;
        NoticeText.Foreground = success
            ? new System.Windows.Media.SolidColorBrush(Core.Palette.Color(0xA6E3A1))
            : (System.Windows.Media.Brush)FindResource("GoldHover");
        NoticeText.Visibility = Visibility.Visible;
        string shown = message;
        await Task.Delay(3500);
        if (NoticeText.Text == shown) NoticeText.Visibility = Visibility.Collapsed;
    }

    // ---------- Rename (F2) ----------

    private void RenameCurrent()
    {
        string path = _currentFilePath;
        if (!PathExists(path) || (EditMode && _editing?.IsDirty == true)) return;
        bool isFolder = Directory.Exists(path);
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        string dir = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar))!;
        // Like Explorer: the name is selected, the extension isn't
        int stem = isFolder || !name.Contains('.') ? name.Length : name.LastIndexOf('.');

        string? newName = Views.InputDialog.Ask(this, Loc.T("rename.title"), null, name, Loc.T("rename.ok"), "pencil",
            text => ValidateName(text, name, dir), stem);
        if (!EditMode && SourceWindow != IntPtr.Zero) NativeMethods.SetForegroundWindow(SourceWindow); // the keys go back to Explorer
        if (newName == null || newName == name) return;

        string target = Path.Combine(dir, newName.Trim());
        try
        {
            // Viewers holding the file open let go of it first
            MediaViewerControl.Stop();
            SystemPreviewControl.Close();
            if (PdfViewerControl.Visibility == Visibility.Visible && !EditMode) PdfViewerControl.Release();
            if (isFolder) Directory.Move(path, target);
            else File.Move(path, target);
            App.Log($"[MainWindow] Renamed '{path}' -> '{target}'");
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Rename failed: {ex.Message}");
            Views.MessageCard.Show(this, Loc.T("rename.error", ex.Message));
            _ = ShowFile(path);
            return;
        }

        _selectionSet = _selectionSet.Select(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? target : p).ToArray();
        if (EditMode) _editingPath = target;
        _ = ShowFile(target);
    }

    private static string? ValidateName(string text, string current, string dir)
    {
        string name = text.Trim();
        if (name.Length == 0) return Loc.T("rename.empty");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Loc.T("rename.invalid");
        if (name is "." or "..") return Loc.T("rename.invalid");
        if (!string.Equals(name, current, StringComparison.OrdinalIgnoreCase) && Path.Exists(Path.Combine(dir, name)))
            return Loc.T("rename.exists");
        return null;
    }
}
