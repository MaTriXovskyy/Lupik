using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// The cursor while files are dragged out of Lupik: a big arrow with a card next to it (the file's icon, its name or
/// "3 files", and a gold "+" badge), instead of Windows' small default that's easy to lose on a busy screen. A dimmed
/// version shows where nothing can be dropped. Shown via GiveFeedback, which keeps firing over other apps too.
/// </summary>
public sealed class DragCursor : IDisposable
{
    private readonly Cursor _copy, _none;
    private readonly List<SafeHandle> _handles = new();

    private DragCursor(ImageSource? icon, string label, double scale)
    {
        _copy = Make(icon, label, scale, droppable: true);
        _none = Make(icon, label, scale, droppable: false);
    }

    /// <summary>
    /// Hooks the cursor up to <paramref name="source"/> for one drag; dispose it once DoDragDrop returns.
    /// <paramref name="label"/>: what is being dragged ("photo.jpg", "2 files, 1 folder").
    /// </summary>
    public static DragCursor Attach(UIElement source, ImageSource? icon, string label)
    {
        double scale = VisualTreeHelper.GetDpi(source).DpiScaleX;
        var cursor = new DragCursor(icon, label, scale) { _source = source };
        source.GiveFeedback += cursor.OnGiveFeedback;
        return cursor;
    }

    private UIElement? _source;

    private void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = false;
        Mouse.SetCursor(e.Effects == DragDropEffects.None ? _none : _copy);
        e.Handled = true;
    }

    public void Dispose()
    {
        if (_source != null) _source.GiveFeedback -= OnGiveFeedback;
        Mouse.SetCursor(null);
        foreach (var h in _handles) h.Dispose();
        _handles.Clear();
    }

    // --- Drawing

    private Cursor Make(ImageSource? icon, string label, double scale, bool droppable) =>
        ToCursor(Draw(icon, label, scale, droppable), hotX: (int)Math.Round(1 * scale), hotY: (int)Math.Round(1 * scale));

    private static BitmapSource Draw(ImageSource? icon, string label, double scale, bool droppable)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (!droppable) dc.PushOpacity(0.55);

            // The card, below and to the right of the arrow tip
            var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 13,
                Palette.Brush(0xFBF8F2), scale) { MaxTextWidth = 220, Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
            double cardX = 18, cardY = 22, pad = 10, iconSize = 24;
            var card = new Rect(cardX, cardY, pad + iconSize + 8 + Math.Min(220, text.Width) + pad + 6, pad * 2 + iconSize);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), null, new Rect(card.X + 1, card.Y + 3, card.Width, card.Height), 10, 10);
            dc.DrawRoundedRectangle(Palette.Brush(0x1B1917), new Pen((Brush)Application.Current.FindResource("Gold"), 1.5), card, 10, 10);
            if (icon != null) dc.DrawImage(icon, new Rect(card.X + pad, card.Y + pad, iconSize, iconSize));
            dc.DrawText(text, new Point(card.X + pad + iconSize + 8, card.Y + (card.Height - text.Height) / 2));

            // "+" badge (copy), or a crossed circle where it can't be dropped
            var badge = new Point(card.Right - 2, card.Top + 2);
            if (droppable)
            {
                dc.DrawEllipse((Brush)Application.Current.FindResource("Gold"), new Pen(Palette.Brush(0x1B1917), 2), badge, 9, 9);
                var on = new Pen((Brush)Application.Current.FindResource("OnGold"), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(on, new Point(badge.X - 4, badge.Y), new Point(badge.X + 4, badge.Y));
                dc.DrawLine(on, new Point(badge.X, badge.Y - 4), new Point(badge.X, badge.Y + 4));
            }
            else
            {
                var red = new Pen(Palette.Brush(0xE5534B), 2);
                dc.DrawEllipse(Palette.Brush(0x1B1917), red, badge, 8, 8);
                dc.DrawLine(red, new Point(badge.X - 5.5, badge.Y + 5.5), new Point(badge.X + 5.5, badge.Y - 5.5));
            }
            if (!droppable) dc.Pop();

            // The arrow itself, large and outlined so it reads on light and dark
            var arrow = Geometry.Parse("M1,1 L1,25 L7,19 L11,28 L15,26 L11,17 L19,17 Z");
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.5) { LineJoin = PenLineJoin.Round }, arrow);
        }

        var bounds = visual.ContentBounds;
        int w = (int)Math.Ceiling((bounds.Right + 4) * scale), h = (int)Math.Ceiling((bounds.Bottom + 4) * scale);
        var bmp = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        return bmp;
    }

    /// <summary>A 32-bit bitmap as a Windows cursor with the hotspot at the arrow's tip.</summary>
    private Cursor ToCursor(BitmapSource bmp, int hotX, int hotY)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bmp.CopyPixels(pixels, stride, 0);

        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)Marshal.SizeOf<BITMAPV5HEADER>(), bV5Width = w, bV5Height = -h, bV5Planes = 1, bV5BitCount = 32,
            bV5Compression = 3, bV5RedMask = 0x00FF0000, bV5GreenMask = 0x0000FF00, bV5BlueMask = 0x000000FF, bV5AlphaMask = 0xFF000000,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        // An all-zero mask: the alpha channel of the color bitmap does the shape
        var maskBits = new byte[(w + 15) / 16 * 2 * h];
        var pinned = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
        IntPtr mask = CreateBitmap(w, h, 1, 1, pinned.AddrOfPinnedObject());
        pinned.Free();
        var info = new ICONINFO { fIcon = false, xHotspot = hotX, yHotspot = hotY, hbmMask = mask, hbmColor = color };
        IntPtr hCursor = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);

        var handle = new CursorHandle(hCursor);
        _handles.Add(handle);
        return CursorInteropHelper.Create(handle);
    }

    private sealed class CursorHandle : SafeHandle
    {
        public CursorHandle(IntPtr h) : base(IntPtr.Zero, true) => SetHandle(h);
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => DestroyCursor(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint bV5Size; public int bV5Width, bV5Height; public ushort bV5Planes, bV5BitCount; public uint bV5Compression, bV5SizeImage;
        public int bV5XPelsPerMeter, bV5YPelsPerMeter; public uint bV5ClrUsed, bV5ClrImportant, bV5RedMask, bV5GreenMask, bV5BlueMask, bV5AlphaMask, bV5CSType;
        public int ex1, ey1, ez1, ex2, ey2, ez2, ex3, ey3, ez3;
        public uint bV5GammaRed, bV5GammaGreen, bV5GammaBlue, bV5Intent, bV5ProfileData, bV5ProfileSize, bV5Reserved;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bitCount, IntPtr bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr cursor);
}
