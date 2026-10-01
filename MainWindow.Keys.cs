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
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Editing: the keys are for the editor, except the few edit mode ones
        if (EditMode) { if (HandleEditModeKey(key, KeyState.Modifiers)) e.Handled = true; return; }
        // A pinned window's search bar: editing keys here, the characters arrive as text input
        if (_pinned && SearchActive)
        {
            var m = KeyState.Modifiers;
            if (key is Key.Escape or Key.Enter or Key.F3 or Key.Up or Key.Down or Key.Back || (key == Key.V && m == ModifierKeys.Control))
            {
                HandleSearchKey(KeyInterop.VirtualKeyFromKey(key), m, null);
                e.Handled = true;
            }
            return;
        }
        if (HandleKey(key, KeyState.Modifiers)) e.Handled = true;
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
        // The shortcuts card: F1 toggles it, any other key closes it first (Esc does nothing else)
        if (key == Key.F1 && mods == 0) { ToggleHelp(); return true; }
        if (HelpOpen) { CloseHelp(); if (key == Key.Escape) return true; }
        bool imageShown = ImageViewerControl.Visibility == Visibility.Visible;
        bool csvShown = CsvViewerControl.Visibility == Visibility.Visible;
        bool mediaShown = MediaViewerControl.Visibility == Visibility.Visible;

        // A PDF: digits type a page number (Enter jumps there); while typing, Backspace / Esc edit it
        if (PdfViewerControl.Visibility == Visibility.Visible && mods == ModifierKeys.None && PdfViewerControl.HandlePageEntryKey(key))
            return true;

        if (imageShown && ImageViewerControl.IsReadingText)
        {
            // Text mode: Enter copies everything, Esc / T leave; the rest (arrows, zoom) works as usual
            if (key is Key.Enter or Key.Return || (key == Key.A && mods == ModifierKeys.Control)) { ImageViewerControl.CopyAllText(); return true; }
            if ((key == Key.Escape || key == Key.T) && mods == 0) { ImageViewerControl.EndTextMode(); return true; }
        }

        if (imageShown && ImageViewerControl.IsCropping)
        {
            // Crop mode owns the keyboard: Enter saves the crop, Esc leaves; nothing else (no file switching)
            if (key is Key.Enter or Key.Return) SaveCrop();
            else if (key == Key.Escape) ImageViewerControl.CancelCrop();
            else ImageViewerControl.CropModifiersChanged(); // Shift / Alt pressed mid-drag
            return true;
        }

        if (key == Key.E && mods == 0 && MarkdownViewerControl.Visibility == Visibility.Visible)
        {
            _ = EditMarkdownSourceAsync();
            return true;
        }
        if (key == Key.M && mods == 0 && IsMarkdownShown)
        {
            SetMarkdownRendered(MarkdownViewerControl.Visibility != Visibility.Visible);
            return true;
        }

        if (key == Key.Escape && OpenWithMenu.IsOpen)
        {
            CloseOpenWithMenu();
            handled = true;
        }
        else if (key == Key.E && mods == 0 && CurrentEditable() != null)
        {
            EnterEdit();
            handled = true;
        }
        else if (key == Key.F2 && mods == 0 && PathExists(_currentFilePath) && CompareViewerControl.Visibility != Visibility.Visible && DiffViewerControl.Visibility != Visibility.Visible)
        {
            RenameCurrent();
            handled = true;
        }
        else if ((key == Key.F && mods == ModifierKeys.Control) || (key == Key.F3 && !SearchActive))
        {
            OpenSearch(); // Ctrl+F / F3 in code, tables, Word, PDF and diffs
            handled = true;
        }
        else if (imageShown && key == Key.K && mods == 0)
        {
            ImageViewerControl.BeginCrop();
            handled = true;
        }
        else if (key == Key.Delete && mods == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            if (ExplorerSelectionMoved()) KeyboardHook.ReplayToSystem(0x2E); // meant for the files picked in Explorer
            else DeleteCurrent();
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
        else if (imageShown && key == Key.S && mods == 0)
        {
            ImageViewerControl.ToggleFilmstrip();
            handled = true;
        }
        else if (imageShown && key == Key.T && mods == 0)
        {
            ImageViewerControl.ToggleTextMode();
            handled = true;
        }
        else if (imageShown && key == Key.B && mods == 0)
        {
            ShowNotice(ImageViewerControl.CycleBackground());
            handled = true;
        }
        else if (key == Key.P && mods == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            TogglePin();
            handled = true;
        }
        else if (imageShown && key == Key.I && mods == 0)
        {
            ImageViewerControl.ToggleInfo();
            handled = true;
        }
        else if (mods == 0 && key == Key.C && CanToggleCompare())
        {
            ToggleCompare(); // 2 images or 2 text files selected: side by side ⇄ single
            handled = true;
        }
        else if (DiffViewerControl.Visibility == Visibility.Visible && mods == 0 && key is Key.Up or Key.Down)
        {
            DiffViewerControl.StepChange(key == Key.Up ? -1 : 1); // in a diff ↑/↓ jump between changes
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
        else if (ArchiveViewerControl.Visibility == Visibility.Visible && mods == ModifierKeys.Control && key is Key.A or Key.C)
        {
            if (key == Key.A) ArchiveViewerControl.SelectAll(); else ArchiveViewerControl.CopySelection();
            handled = true;
        }
        else if (ArchiveViewerControl.Visibility == Visibility.Visible && key == Key.Escape && ArchiveViewerControl.HasSelection)
        {
            ArchiveViewerControl.ClearSelection(); // first Esc clears the selection, the next one closes
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
            ToggleOpenWith();
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
        else if ((key == Key.Up || key == Key.Down) && MarkdownViewerControl.Visibility == Visibility.Visible)
        {
            MarkdownViewerControl.ScrollBy(key == Key.Up ? -60 : 60);
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
        bool show = ImageViewerControl.Visibility != Visibility.Visible;
        FooterRow.Height = new GridLength(show ? 30 : 0);
        FooterBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Full screen (F): the window fills the whole screen above the taskbar, and keeps its title bar, file actions,
    /// footer and key hints, like the window does. (Covering the taskbar would need Lupik to be the active window,
    /// and the preview never takes the focus from Explorer.) Arrow keys keep switching files in full screen.
    /// </summary>
    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            // The work area of the monitor the window is on
            var center = new System.Drawing.Point((int)((Left + Width / 2) * VisualTreeHelper.GetDpi(this).DpiScaleX),
                                                  (int)((Top + Height / 2) * VisualTreeHelper.GetDpi(this).DpiScaleY));
            var screen = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            _boundsBeforeFullScreen = new Rect(Left, Top, Width, Height);
            SetBounds(new Rect(screen.Left / dpi.DpiScaleX, screen.Top / dpi.DpiScaleY, screen.Width / dpi.DpiScaleX, screen.Height / dpi.DpiScaleY), exact: true);
            _isFullScreen = true; // after SetBounds: while full screen, file switches don't resize the window
            return;
        }

        _isFullScreen = false;
        // A pinned window goes back to exactly where it was; the preview to the size the file normally gets
        if (_pinned) SetBounds(_boundsBeforeFullScreen, exact: true);
        else RestoreNormalBounds();
    }

    private Rect _boundsBeforeFullScreen;

    /// <summary>Back from full screen: the size the file would normally get.</summary>
    private void RestoreNormalBounds()
    {
        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            SetBounds(ImageViewerControl.Visibility == Visibility.Visible
                ? ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight)
                : ComputeDefaultBounds(Path.GetExtension(_currentFilePath).ToLowerInvariant()));
        }
    }
}
