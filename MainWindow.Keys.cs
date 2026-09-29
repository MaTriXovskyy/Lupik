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

/// <summary>Keyboard: keys routed from the hook (the window never takes focus), full screen.</summary>
public partial class MainWindow
{
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, KeyState.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// A key from the global keyboard hook (virtual-key code). The preview is never the focused window (taking the
    /// focus from Explorer makes Windows wait on it, up to 5 s when it's busy), so its keys arrive this way.
    /// </summary>
    public void HandleHookKey(int vk, ModifierKeys mods)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        // The delete confirmation doesn't take the focus either: Enter / Esc answer it
        if (Views.ConfirmDeleteWindow.Current is { } confirm)
        {
            if (key == Key.Enter) confirm.Answer(true);
            else if (key == Key.Escape) confirm.Answer(false);
            return;
        }
        if (key is Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.System)
        {
            ImageViewerControl.CropModifiersChanged();
            return;
        }
        HandleKey(key, mods);
    }

    /// <summary>All preview shortcuts. Returns true if the key did something.</summary>
    private bool HandleKey(Key key, ModifierKeys mods)
    {
        bool handled = false;
        bool imageShown = ImageViewerControl.Visibility == Visibility.Visible;
        bool csvShown = CsvViewerControl.Visibility == Visibility.Visible;
        bool mediaShown = MediaViewerControl.Visibility == Visibility.Visible;

        // A PDF: digits type a page number (Enter jumps there); while typing, Backspace / Esc edit it
        if (PdfViewerControl.Visibility == Visibility.Visible && mods == ModifierKeys.None && PdfViewerControl.HandlePageEntryKey(key))
            return true;

        if (imageShown && ImageViewerControl.IsCropping)
        {
            // Crop mode owns the keyboard: Enter saves the crop, Esc leaves; nothing else (no file switching)
            if (key is Key.Enter or Key.Return) SaveCrop();
            else if (key == Key.Escape) ImageViewerControl.CancelCrop();
            else ImageViewerControl.CropModifiersChanged(); // Shift / Alt pressed mid-drag
            return true;
        }

        if (imageShown && key == Key.K && mods == 0)
        {
            ImageViewerControl.BeginCrop();
            handled = true;
        }
        else if (key == Key.Delete && mods == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            DeleteCurrent();
            handled = true;
        }
        else if (mediaShown && mods == 0 && key is Key.K or Key.J or Key.L or Key.M)
        {
            // YouTube-style: K play/pause, J/L back/forward, M mute (Space and arrows keep closing/navigating)
            if (key == Key.K) MediaViewerControl.TogglePlay();
            else if (key == Key.J) MediaViewerControl.SeekBy(-5);
            else if (key == Key.L) MediaViewerControl.SeekBy(5);
            else MediaViewerControl.ToggleMute();
            handled = true;
        }
        else if (key == Key.S && mods == ModifierKeys.Control)
        {
            SaveAs();
            handled = true;
        }
        else if (key == Key.P && mods == ModifierKeys.Control)
        {
            OpenPrintDialog();
            handled = true;
        }
        else if (imageShown && key == Key.C && mods == ModifierKeys.Control)
        {
            ImageViewerControl.CopyImageToClipboard();
            handled = true;
        }
        else if (imageShown && key == Key.R && (mods & ~ModifierKeys.Shift) == 0)
        {
            ImageViewerControl.Rotate(mods == ModifierKeys.Shift ? -90 : 90);
            handled = true;
        }
        else if (imageShown && mods == 0 && key is Key.OemPlus or Key.Add)
        {
            ImageViewerControl.ZoomBy(1.25);
            handled = true;
        }
        else if (imageShown && mods == 0 && key is Key.OemMinus or Key.Subtract)
        {
            ImageViewerControl.ZoomBy(1 / 1.25);
            handled = true;
        }
        else if (imageShown && key == Key.I && mods == 0)
        {
            ImageViewerControl.ToggleInfo();
            handled = true;
        }
        else if (mods == 0 && key == Key.C && (CompareViewerControl.Visibility == Visibility.Visible || (imageShown && ComparePair() != null)))
        {
            ToggleCompare(); // 2 images selected: side by side ⇄ single
            handled = true;
        }
        else if (CompareViewerControl.Visibility == Visibility.Visible && CompareViewerControl.HandleKey(key, mods))
        {
            handled = true; // +/−/0, S (mode), X (swap); arrows do nothing here
        }
        else if (imageShown && mods == 0 && key is Key.D0 or Key.NumPad0)
        {
            ImageViewerControl.ResetZoom();
            handled = true;
        }
        else if (csvShown && key == Key.C && mods == ModifierKeys.Control)
        {
            CsvViewerControl.CopySelection();
            handled = true;
        }
        else if (csvShown && key == Key.A && mods == ModifierKeys.Control)
        {
            CsvViewerControl.SelectAll();
            handled = true;
        }
        else if (csvShown && key == Key.Escape && CsvViewerControl.HasSelection)
        {
            CsvViewerControl.ClearSelection(); // first Esc clears the selection, the next one closes
            handled = true;
        }
        else if (mods == ModifierKeys.Alt && (key == Key.Left || key == Key.Right))
        {
            if (key == Key.Left) GoBackToFolder(); else GoForward(); // Alt+←/→ like in Explorer
            handled = true;
        }
        else if ((key == Key.Back || key == Key.Escape) && mods == 0 && _folderHistory.Count > 0 && !_isFullScreen)
        {
            GoBackToFolder(); // opened from a folder preview: back to the folder instead of closing
            handled = true;
        }
        else if (key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen(); // first Esc leaves full screen, the next one closes
            handled = true;
        }
        else if (key == Key.F && mods == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            ToggleFullScreen();
            handled = true;
        }
        else if (key == Key.Enter && mods == 0)
        {
            OpenInDefaultApp();
            handled = true;
        }
        else if (key == Key.Space || key == Key.Escape)
        {
            HideWindow();
            handled = true;
        }
        else if (PdfViewerControl.Visibility == Visibility.Visible && key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            // In a PDF: ↑/↓ scroll, PgUp/PgDn go a page back/forward, Home/End to the first/last page; ←/→ still switch files
            switch (key)
            {
                case Key.Up: PdfViewerControl.ScrollBy(-80); break;
                case Key.Down: PdfViewerControl.ScrollBy(80); break;
                case Key.PageUp: PdfViewerControl.StepPage(-1); break;
                case Key.PageDown: PdfViewerControl.StepPage(1); break;
                case Key.Home: PdfViewerControl.FirstPage(); break;
                case Key.End: PdfViewerControl.LastPage(); break;
            }
            handled = true;
        }
        else if ((key == Key.Up || key == Key.Down) && CodeViewerControl.Visibility == Visibility.Visible)
        {
            // In the code preview ↑/↓ scroll the text; ←/→ still switch files
            CodeViewerControl.ScrollLines(key == Key.Up ? -1 : 1);
            handled = true;
        }
        else if (key == Key.Up || key == Key.Left)
        {
            NavigateAdjacent(-1);
            handled = true;
        }
        else if (key == Key.Down || key == Key.Right)
        {
            NavigateAdjacent(1);
            handled = true;
        }
        return handled;
    }

    /// <summary>Releasing Shift / Alt while dragging the crop frame applies at once; Alt alone must not open a menu.</summary>
    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (!ImageViewerControl.IsCropping) return;
        ImageViewerControl.CropModifiersChanged();
        e.Handled = true;
    }

    private bool _isFullScreen;

    /// <summary>The key-hint footer; the image preview shows the hints in its own footer instead.</summary>
    private void UpdateFooter()
    {
        bool show = !_isFullScreen && ImageViewerControl.Visibility != Visibility.Visible;
        FooterRow.Height = new GridLength(show ? 30 : 0);
        FooterBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Full screen for a slideshow feel: no title bar, window covers the whole monitor (incl. taskbar).
    /// Arrow-key navigation keeps working and stays in full screen.
    /// </summary>
    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        TitleRow.Height = new GridLength(_isFullScreen ? 0 : 38);
        TitleBar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        UpdateFooter();
        Background = _isFullScreen ? System.Windows.Media.Brushes.Black : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x13, 0x12, 0x11));
        // Full screen needs a frameless window (a captioned one maximizes to the work area, leaving the taskbar)
        if (_isFullScreen) WindowStyle = WindowStyle.None;
        WindowState = _isFullScreen ? WindowState.Maximized : WindowState.Normal;
        if (!_isFullScreen) WindowStyle = WindowStyle.SingleBorderWindow;

        if (!_isFullScreen && !string.IsNullOrEmpty(_currentFilePath))
        {
            SetBounds(ImageViewerControl.Visibility == Visibility.Visible
                ? ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight)
                : ComputeDefaultBounds(Path.GetExtension(_currentFilePath).ToLowerInvariant()));
        }
    }
}
