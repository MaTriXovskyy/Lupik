using System.IO;
using System.Windows;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik;

public partial class MainWindow
{
    internal async Task ShowScreenshotAsync(string path, IntPtr origin, bool copied)
    {
        // Never replace an editor with unsaved work or an in-progress save.
        MainWindow target = _saving || EditMode ? CreatePinned() : this;
        if (target != this) { target.Topmost = Settings.Current.PinnedOnTop; target.UpdatePinButton(); }
        target.ResetFolderHistory();
        target._focusOnShow = true;
        await target.ShowFile(path);
        target.SourceWindow = origin;
        if (target.IsVisible)
        {
            target.TakeFocus();
            target.ShowNotice(Loc.T(copied ? "capture.copied" : "capture.copyFailed"), success: copied);
        }
    }

    // --- Editing a screenshot: changes apply to the capture itself, one Ctrl+Z takes them back ---

    /// <summary>
    /// The capture as it was before the last applied change, and the edit mode state then (null for a crop).
    /// Kept only while this capture is shown: another file, closing the preview or quitting drops it.
    /// </summary>
    private sealed record ScreenshotUndo(string Path, byte[] Before, object? EditState);
    private ScreenshotUndo? _screenshotUndo;

    private bool CanUndoScreenshot(string path) =>
        _screenshotUndo is { } undo && string.Equals(undo.Path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lets go of the undo copy (it holds a whole picture in memory).</summary>
    private void ForgetScreenshotUndo(string? keepFor = null)
    {
        if (_screenshotUndo != null && (keepFor == null || !CanUndoScreenshot(keepFor))) _screenshotUndo = null;
    }

    /// <summary>Writes the edits straight over the capture (no Recycle Bin copy: it's a temporary file).</summary>
    private async Task ApplyToScreenshotAsync(string path, Views.IEditable editable)
    {
        byte[] before = await File.ReadAllBytesAsync(path);
        object? state = editable is Views.ImageViewer image ? image.SnapshotEdit() : null;
        await ReplaceScreenshotAsync(path, editable.SaveToAsync);
        _screenshotUndo = new ScreenshotUndo(path, before, state);
    }

    /// <summary>The new version is written next to the capture first, so a failed write leaves it as it was.</summary>
    private static async Task ReplaceScreenshotAsync(string path, Func<string, Task> write)
    {
        string temp = path + ".lupik.png"; // *.png: cleaned with the session if anything goes wrong
        try
        {
            await write(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    /// <summary>Ctrl+Z after applying: the capture as before, and (after an edit) edit mode with every mark editable.</summary>
    private async Task UndoScreenshotAsync()
    {
        if (_screenshotUndo is not { } undo || _saving) return;
        _screenshotUndo = null; // one step back only
        try
        {
            await File.WriteAllBytesAsync(undo.Path, undo.Before);
            if (EditMode && _editing == ImageViewerControl)
            {
                await ImageViewerControl.ReloadAsync(undo.Path);
            }
            else
            {
                await ShowFile(undo.Path);
                if (undo.EditState != null && ImageViewerControl.Visibility == Visibility.Visible) EnterEdit();
            }
            if (undo.EditState != null && EditMode) ImageViewerControl.RestoreEdit(undo.EditState);
            await CopyScreenshotAsync(undo.Path);
            ShowNotice(Loc.T("capture.undone"), success: true);
            App.Log($"[MainWindow] Screenshot changes undone: '{undo.Path}'");
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Screenshot undo failed: {ex}");
            ShowNotice(Loc.T("edit.saveError", ex.Message));
        }
    }

    /// <summary>The capture as it is now goes to the clipboard (with its PNG, so transparency survives).</summary>
    private static async Task CopyScreenshotAsync(string path)
    {
        try
        {
            byte[] png = await File.ReadAllBytesAsync(path);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(png);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            var data = ScreenshotFreeform.ClipboardData(bitmap, png);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetDataObject(data, copy: true); return; }
                catch (System.Runtime.InteropServices.ExternalException) { await Task.Delay(30); }
            }
        }
        catch (Exception ex) { App.Log($"[MainWindow] Copying the screenshot failed: {ex.Message}"); }
    }
}
