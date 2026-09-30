using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik.Core;

using Lupik.Localization;

namespace Lupik;

/// <summary>File actions: save as, crop, print, open, show in folder, delete.</summary>
public partial class MainWindow
{
    private void OnSaveAsClicked(object sender, RoutedEventArgs e) => SaveAs();

    private void OnCropClicked(object sender, RoutedEventArgs e)
    {
        if (ImageViewerControl.Visibility != Visibility.Visible) return;
        if (ImageViewerControl.IsCropping) SaveCrop(); else ImageViewerControl.BeginCrop();
        Keyboard.Focus(this);
    }

    /// <summary>Saves the selected part of the image as a new file (the original is never changed).</summary>
    private async void SaveCrop()
    {
        string source = _currentFilePath;
        if (!File.Exists(source)) return;
        var f = ImageViewerControl.CropFraction;
        int rotation = ImageViewerControl.Rotation;
        ImageViewerControl.CancelCrop();

        string ext = Path.GetExtension(source).ToLowerInvariant();
        // Vector and exotic formats are saved as PNG; common raster formats keep their own
        string targetExt = ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tif" or ".tiff" ? ext : ".png";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("crop.saveTitle"),
            InitialDirectory = Path.GetDirectoryName(source),
            FileName = Path.GetFileNameWithoutExtension(source) + Loc.T("crop.fileSuffix") + targetExt,
            Filter = $"{targetExt.TrimStart('.').ToUpperInvariant()}|*{targetExt}|PNG|*.png|JPEG|*.jpg",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            Views.MessageCard.Show(this, Loc.T("save.sameName"), error: false);
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using var image = Views.ImageViewer.OpenForExport(source);
                image.AutoOrient();
                if (rotation != 0) image.Rotate(rotation);
                // The selection is relative to the image as shown (rotated), in the full-resolution pixels
                int x = (int)Math.Round(f.X * image.Width), y = (int)Math.Round(f.Y * image.Height);
                int w = (int)Math.Round(f.Width * image.Width), h = (int)Math.Round(f.Height * image.Height);
                w = Math.Clamp(w, 1, (int)image.Width - x);
                h = Math.Clamp(h, 1, (int)image.Height - y);
                image.Crop(new ImageMagick.MagickGeometry(x, y, (uint)w, (uint)h));
                image.ResetPage();
                if (Path.GetExtension(target).ToLowerInvariant() is ".jpg" or ".jpeg") image.Quality = 95;
                image.Write(target);
            });
            App.Log($"[MainWindow] Cropped '{source}' to '{target}'");
            ShowToast(Loc.T("crop.saved", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Crop failed: {ex}");
            Views.MessageCard.Show(this, Loc.T("crop.saveError", ex.Message));
        }
    }

    private void OnPrintClicked(object sender, RoutedEventArgs e) => OpenPrintDialog();

    private bool CanPrintCurrent()
    {
        string ext = Path.GetExtension(_currentFilePath).ToLowerInvariant();
        return File.Exists(_currentFilePath) && (ext == ".pdf" || Array.IndexOf(ImageExtensions, ext) >= 0);
    }

    /// <summary>Opens the print dialog for the current PDF or image.</summary>
    private async void OpenPrintDialog()
    {
        if (!CanPrintCurrent()) return;
        string path = _currentFilePath;
        int rotation = ImageViewerControl.Rotation; // read on the UI thread: WPF controls can't be touched from Task.Run
        try
        {
            Core.IPrintSource source = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? await Core.PdfPrintSource.OpenAsync(path)
                : await Task.Run(() => new Core.ImagePrintSource(path, rotation)); // prints as rotated in the preview

            var dialog = new Views.PrintWindow(source) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Could not open print dialog: {ex}");
            Views.MessageCard.Show(this, Loc.T("print.prepareError", ex.Message));
        }
    }

    private void OnShowInFolderClicked(object sender, RoutedEventArgs e) => RunShell("explorer.exe", $"/select,\"{_currentFilePath}\"");
    private void OnOpenDefaultClicked(object sender, RoutedEventArgs e) => OpenInDefaultApp();
    // Windows' own "Open with…" picker, so any installed app (Photoshop, etc.) can be chosen
    private void OnOpenWithClicked(object sender, RoutedEventArgs e) => RunShell("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {_currentFilePath}");

    private void OpenInDefaultApp()
    {
        if (!PathExists(_currentFilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_currentFilePath) { UseShellExecute = true });
            HideWindow();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Open in default app failed: {ex.Message}");
        }
    }

    private void RunShell(string exe, string args)
    {
        if (!PathExists(_currentFilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
            HideWindow();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] {exe} failed: {ex.Message}");
        }
    }

    private static readonly (string Label, string Ext, ImageMagick.MagickFormat Format)[] ExportFormats =
    {
        ("PNG", ".png", ImageMagick.MagickFormat.Png),
        ("JPEG", ".jpg", ImageMagick.MagickFormat.Jpeg),
        ("TIFF", ".tif", ImageMagick.MagickFormat.Tiff),
        ("WebP", ".webp", ImageMagick.MagickFormat.WebP),
        ("BMP", ".bmp", ImageMagick.MagickFormat.Bmp),
    };

    /// <summary>
    /// Like "Export…" in macOS Preview: images can be re-encoded to another format
    /// (handy when an app like Photoshop chokes on the original); other files are saved as a copy.
    /// </summary>
    private async void SaveAs()
    {
        string source = _currentFilePath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source) || !IsImagePath(source)) return;

        string ext = Path.GetExtension(source).ToLowerInvariant();
        bool isImage = Array.IndexOf(ImageExtensions, ext) >= 0;
        string baseName = Path.GetFileNameWithoutExtension(source);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("save.title"),
            InitialDirectory = Path.GetDirectoryName(source),
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (isImage)
        {
            string original = Loc.T("save.original", ext.TrimStart('.').ToUpperInvariant()) + $"|*{ext}";
            dialog.Filter = string.Join("|", ExportFormats.Select(f => $"{f.Label} (*{f.Ext})|*{f.Ext}").Prepend(original));
            // HEIC/HEIF default to JPEG, since most apps can't open them
            bool preferJpeg = ext is ".heic" or ".heif";
            dialog.FilterIndex = preferJpeg ? 3 : 1;
            dialog.FileName = baseName + Loc.T("save.copySuffix") + (preferJpeg ? ".jpg" : ext);
        }
        else
        {
            dialog.Filter = $"{(ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : Loc.T("save.file"))}|*{(ext.Length > 0 ? ext : ".*")}|{Loc.T("save.allFiles")}|*.*";
            dialog.FileName = baseName + Loc.T("save.copySuffix") + ext;
        }

        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            Views.MessageCard.Show(this, Loc.T("save.sameNameOrFolder"), error: false);
            return;
        }

        try
        {
            string targetExt = Path.GetExtension(target).ToLowerInvariant();
            var format = ExportFormats.FirstOrDefault(f => f.Ext == targetExt || (targetExt == ".jpeg" && f.Ext == ".jpg") || (targetExt == ".tiff" && f.Ext == ".tif"));

            // Rotation done in the preview is baked into the saved file, so it needs a re-encode too
            int rotation = isImage ? ImageViewerControl.Rotation : 0;
            bool convert = isImage && ((targetExt != ext && format.Label != null) || rotation != 0);

            if (convert)
            {
                await Task.Run(() =>
                {
                    using var image = Views.ImageViewer.OpenForExport(source);
                    image.AutoOrient();
                    if (rotation != 0) image.Rotate(rotation);
                    if (format.Label != null)
                    {
                        if (format.Format == ImageMagick.MagickFormat.Jpeg) image.Quality = 95;
                        image.Write(target, format.Format);
                    }
                    else
                    {
                        image.Write(target); // same, non-listed format (e.g. JFIF): inferred from the extension
                    }
                });
            }
            else
            {
                await Task.Run(() => File.Copy(source, target, overwrite: true));
            }

            App.Log($"[MainWindow] Saved '{source}' as '{target}'");
            ShowToast(Loc.T("save.saved", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Save As failed: {ex}");
            Views.MessageCard.Show(this, Loc.T("save.error", ex.Message));
        }
    }

    private async void ShowToast(string message)
    {
        // Icon-only button: flash a green check, with the details in the tooltip
        SaveAsIcon.Kind = "check";
        SaveAsButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
        SaveAsButton.ToolTip = message;
        await Task.Delay(2200);
        SaveAsIcon.Kind = "save";
        SaveAsButton.ClearValue(ForegroundProperty);
        SaveAsButton.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("[main.saveAsTip]") { Source = Loc.Instance });
    }

    // Files selected in Explorer when the preview was opened; with 2+ files, arrows cycle only through them
    private IReadOnlyList<string> _selectionSet = Array.Empty<string>();

    // --- Delete: to the Recycle Bin, then on to the next file (like Peek) ---

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => DeleteCurrent();

    private void DeleteCurrent()
    {
        string path = _currentFilePath;
        if (!PathExists(path) || CompareViewerControl.Visibility == Visibility.Visible) return;

        bool isFolder = Directory.Exists(path);
        if (!Views.ConfirmDeleteWindow.Confirm(this, path)) return;

        // Where to go afterwards: decided before the file disappears from Explorer's view
        string? next = GetNeighbor(path, 1);
        if (next == null || string.Equals(next, path, StringComparison.OrdinalIgnoreCase)) next = GetNeighbor(path, -1);
        if (next != null && string.Equals(next, path, StringComparison.OrdinalIgnoreCase)) next = null;

        // Viewers that keep the file open must let go of it first
        MediaViewerControl.Stop();
        SystemPreviewControl.Close();
        if (PdfViewerControl.Visibility == Visibility.Visible) PdfViewerControl.Release();

        try
        {
            if (isFolder)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            App.Log($"[MainWindow] Moved to Recycle Bin: '{path}'");
        }
        catch (OperationCanceledException)
        {
            _ = ShowFile(path); // cancelled in the Windows error dialog: show it again
            return;
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Delete failed: {ex}");
            Views.MessageCard.Show(this, Loc.T("delete.error", ex.Message));
            _ = ShowFile(path);
            return;
        }

        _selectionSet = _selectionSet.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (next != null && PathExists(next)) _ = ShowFile(next);
        else HideWindow();
    }
}
