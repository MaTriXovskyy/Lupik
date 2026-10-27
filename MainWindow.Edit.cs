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
/// Edit mode (E): text and code, CSV cells, PDF pages, quick picture fixes. Opening a preview leaves the
/// source focused; clicking it or entering edit mode gives Lupik normal keyboard focus.
/// Saving writes the new version next to the file, sends the original to the Recycle Bin and puts the new one in place.
/// </summary>
public partial class MainWindow
{
    /// <summary>Edit mode is on: the keyboard hook leaves every key to the window (read by the hook thread).</summary>
    public volatile bool EditMode;

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
        // Markdown shown as a document is edited as its source (E switches to it first)
        bool editable = CurrentEditable() != null || MarkdownViewerControl.Visibility == Visibility.Visible;
        EditButton.Visibility = editable && !EditMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>E on a Markdown document: show its source, then edit that.</summary>
    private async Task EditMarkdownSourceAsync()
    {
        if (Settings.Current.MarkdownRendered) Settings.Update(s => s.MarkdownRendered = false);
        await ShowFile(_currentFilePath);
        if (CurrentEditable() != null) EnterEdit();
    }

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (MarkdownViewerControl.Visibility == Visibility.Visible) _ = EditMarkdownSourceAsync();
        else EnterEdit();
    }
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
        editable.DirtyChanged += OnEditDirtyChanged;
        editable.BeginEdit();

        HeaderTools.Visibility = Visibility.Collapsed;
        EditBar.Visibility = Visibility.Visible;
        EditButton.Visibility = Visibility.Collapsed;
        OnEditDirtyChanged();
        TakeFocus();
        App.Log($"[MainWindow] Edit mode: '{_editingPath}'");
    }

    private bool _askingToExit;

    /// <summary>
    /// Leaves edit mode; with unsaved changes asks first (save / discard / stay).
    /// A deferred close/navigation is resumed only after saving succeeds.
    /// </summary>
    private bool ExitEdit(Action? afterSave = null, bool reloadDiscarded = true)
    {
        if (_saving || _askingToExit) return false;
        if (!EditMode || _editing == null) return true;
        if (_editing.IsDirty)
        {
            Views.UnsavedChoice answer;
            _askingToExit = true;
            try { answer = Views.UnsavedChangesDialog.Ask(this, _editingPath); }
            finally { _askingToExit = false; }
            if (answer == Views.UnsavedChoice.Cancel) return false;
            if (answer == Views.UnsavedChoice.Save)
            {
                // Save first; leave only if it worked
                _ = SaveEditAsync(exitAfter: true, afterSave: afterSave);
                return false;
            }
        }
        FinishEdit(discarded: _editing.IsDirty, reloadDiscarded: reloadDiscarded);
        return true;
    }

    private void FinishEdit(bool discarded, bool reloadDiscarded = true)
    {
        var editable = _editing;
        if (editable == null) return;
        editable.DirtyChanged -= OnEditDirtyChanged;
        editable.EndEdit();
        _editing = null;
        EditMode = false;

        EditBar.Visibility = Visibility.Collapsed;
        HeaderTools.Visibility = Visibility.Visible;
        UpdateEditable();
        App.Log($"[MainWindow] Edit mode off{(discarded ? " (changes discarded)" : "")}");
        // Thrown-away changes: show the file as it is on disk
        if (discarded && reloadDiscarded && File.Exists(_editingPath)) _ = ShowFile(_editingPath);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_pinned)
        {
            // Alt+F4 and taskbar Close follow the same save/discard/cancel path as our X.
            e.Cancel = true;
            HideWindow();
            return;
        }
        if (!ExitEdit(afterSave: Close, reloadDiscarded: false)) e.Cancel = true;
    }

    private bool _saving;

    private async Task SaveEditAsync(bool exitAfter = false, Action? afterSave = null)
    {
        var editable = _editing;
        if (editable == null || _saving) return;
        _saving = true;
        // All editors remain stable until their snapshot has been committed and reloaded.
        // Window closing and navigation also check _saving, so they cannot release this editor.
        var editorControl = editable as UIElement;
        bool wasEnabled = editorControl?.IsEnabled ?? true;
        if (editorControl != null) editorControl.IsEnabled = false;
        EditBar.IsEnabled = false;
        string path = _editingPath;
        bool saved = false;
        try
        {
            await SafeReplace.ReplaceAsync(path, editable.SaveToAsync, editable.ReleaseFile);
            await editable.ReloadAsync(path);
            ShowNotice(Loc.T("edit.saved", Path.GetFileName(path)), success: true);
            saved = true;
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Saving the edit failed: {ex}");
            // Reloading here would discard CSV/image edits or mark unsaved text as clean.
            if (!SuppressActivationForTests) Views.MessageCard.Show(this, Loc.T("edit.saveError", ex.Message));
        }
        finally
        {
            _saving = false;
            if (editorControl != null) editorControl.IsEnabled = wasEnabled;
            EditBar.IsEnabled = true;
            OnEditDirtyChanged();
        }
        if (saved && exitAfter && !editable.IsDirty)
        {
            FinishEdit(discarded: false);
            // A tiny save may complete inside Window.Closing; defer the next close/navigation
            // until that event has returned and its cancellation has taken effect.
            if (afterSave != null) _ = Dispatcher.InvokeAsync(afterSave);
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
        if (_saving) return true;
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

    /// <summary>Makes the preview a normal, activatable window and brings it to the front with the keyboard focus.</summary>
    private void TakeFocus()
    {
        if (SuppressActivationForTests) return;
        Core.Foreground.Take(Hwnd);
        Activate();
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
