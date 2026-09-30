using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// Picks Lupik's accent: ready-made colors, or any RGB color from a saturation/brightness field with a hue bar,
/// or typed as hex / R G B. <see cref="ColorChanged"/> fires while dragging (final = false) and once the choice
/// is made (final = true: mouse released, field committed, swatch clicked).
/// </summary>
public partial class AccentPicker : UserControl
{
    /// <summary>Ready-made accents (name key, color); the first one is Lupik's gold.</summary>
    private static readonly (string Key, string Hex)[] PresetColors =
    {
        ("gold", "#E3B341"), ("amber", "#EE9A3A"), ("coral", "#F07A5E"), ("rose", "#EC6F93"), ("orchid", "#C77DDB"),
        ("lavender", "#9B8CF2"), ("sky", "#5AA9F0"), ("teal", "#3CC4B4"), ("mint", "#6FCF8E"), ("lime", "#B5D335"),
        ("silver", "#B8B2A8"),
    };

    public event Action<Color, bool>? ColorChanged;

    // HSV of the current color; the hue is kept even when gray/black (so the field doesn't jump to red)
    private double _h, _s, _v;
    private Color _color;
    private bool _updating;

    public AccentPicker()
    {
        InitializeComponent();
        foreach (var (key, hex) in PresetColors)
        {
            var color = Accent.Parse(hex)!.Value;
            var swatch = new RadioButton
            {
                Style = (Style)Resources["Swatch"],
                GroupName = "AccentPresets",
                Background = new SolidColorBrush(color),
                Foreground = new SolidColorBrush(Accent.Luminance(color) > 0.3 ? Color.FromRgb(0x1A, 0x14, 0x07) : Colors.White),
                Tag = color,
            };
            swatch.SetBinding(ToolTipProperty, new System.Windows.Data.Binding($"[settings.accent.{key}]") { Source = Loc.Instance });
            swatch.Click += (_, _) => Choose(color, final: true);
            Presets.Items.Add(swatch);
        }
        Loc.Instance.LanguageChanged += RefreshName;
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= RefreshName;
    }

    /// <summary>Shows <paramref name="color"/> without raising <see cref="ColorChanged"/>.</summary>
    public void SetColor(Color color)
    {
        var (h, s, v) = Accent.ToHsv(color);
        if (s > 0 && v > 0) _h = h;
        _s = s; _v = v; _color = color;
        Show();
    }

    private void Choose(Color color, bool final)
    {
        SetColor(color);
        ColorChanged?.Invoke(color, final);
    }

    private void ChooseHsv(bool final)
    {
        _color = Accent.FromHsv(_h, _s, _v);
        Show();
        ColorChanged?.Invoke(_color, final);
    }

    // --- Showing the state

    private void Show()
    {
        _updating = true;
        HueFill.Fill = new SolidColorBrush(Accent.FromHsv(_h, 1, 1));
        PlaceThumbs();
        HueThumb.Fill = new SolidColorBrush(Accent.FromHsv(_h, 1, 1));
        FieldThumb.Fill = new SolidColorBrush(_color);

        string hex = Accent.ToHex(_color)[1..];
        if (!HexBox.IsKeyboardFocused) HexBox.Text = hex;
        if (!RBox.IsKeyboardFocused) RBox.Text = _color.R.ToString(CultureInfo.InvariantCulture);
        if (!GBox.IsKeyboardFocused) GBox.Text = _color.G.ToString(CultureInfo.InvariantCulture);
        if (!BBox.IsKeyboardFocused) BBox.Text = _color.B.ToString(CultureInfo.InvariantCulture);

        foreach (RadioButton swatch in Presets.Items) swatch.IsChecked = (Color)swatch.Tag == _color;
        RefreshName();
        _updating = false;
    }

    private void RefreshName()
    {
        int preset = Array.FindIndex(PresetColors, p => Accent.Parse(p.Hex) == _color);
        ColorName.Text = preset >= 0 ? Loc.T($"settings.accent.{PresetColors[preset].Key}") : Loc.T("settings.accentCustom");
        ColorHex.Text = Accent.ToHex(_color);
        var readable = Accent.Readable(_color);
        LiftNote.Visibility = readable == _color ? Visibility.Collapsed : Visibility.Visible;
        LiftText.Text = Loc.T(Core.Palette.IsLight ? "settings.accentDarkened" : "settings.accentLifted", Accent.ToHex(readable));
    }

    private void PlaceThumbs()
    {
        double w = Field.ActualWidth, h = Field.ActualHeight;
        Canvas.SetLeft(FieldThumb, _s * w - FieldThumb.Width / 2);
        Canvas.SetTop(FieldThumb, (1 - _v) * h - FieldThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _h / 360 * HueBar.ActualWidth - HueThumb.Width / 2);
    }

    private void OnFieldSized(object sender, SizeChangedEventArgs e)
    {
        FieldColors.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
        PlaceThumbs();
    }

    // --- Dragging in the field and on the hue bar

    private void OnFieldDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();
        Field.CaptureMouse();
        FieldAt(e.GetPosition(Field), final: false);
    }

    private void OnFieldMove(object sender, MouseEventArgs e)
    {
        if (Field.IsMouseCaptured) FieldAt(e.GetPosition(Field), final: false);
    }

    private void OnFieldUp(object sender, MouseButtonEventArgs e)
    {
        if (!Field.IsMouseCaptured) return;
        FieldAt(e.GetPosition(Field), final: true);
        Field.ReleaseMouseCapture();
    }

    private void FieldAt(Point p, bool final)
    {
        _s = Math.Clamp(p.X / Math.Max(1, Field.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, Field.ActualHeight), 0, 1);
        ChooseHsv(final);
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();
        HueBar.CaptureMouse();
        HueAt(e.GetPosition(HueBar), final: false);
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (HueBar.IsMouseCaptured) HueAt(e.GetPosition(HueBar), final: false);
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        if (!HueBar.IsMouseCaptured) return;
        HueAt(e.GetPosition(HueBar), final: true);
        HueBar.ReleaseMouseCapture();
    }

    private void HueAt(Point p, bool final)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueBar.ActualWidth), 0, 1) * 359.999;
        // A gray has no hue to show: picking one makes it a color
        if (_s < 0.05) _s = 0.7;
        if (_v < 0.05) _v = 0.9;
        ChooseHsv(final);
    }

    /// <summary>Capture lost mid-drag (Alt+Tab, another window): keep what was reached.</summary>
    private void OnLostCapture(object sender, MouseEventArgs e) => ColorChanged?.Invoke(_color, true);

    // --- Typing

    private void OnFieldKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { Commit((TextBox)sender); e.Handled = true; }
    }

    private void OnHexCommit(object sender, KeyboardFocusChangedEventArgs e) => Commit(HexBox);
    private void OnRgbCommit(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    private void Commit(TextBox box)
    {
        if (_updating) return;
        Color? color = box == HexBox
            ? Accent.Parse(HexBox.Text)
            : Channel(RBox) is byte r && Channel(GBox) is byte g && Channel(BBox) is byte b ? Color.FromRgb(r, g, b) : null;
        if (color is Color c && c != _color) Choose(c, final: true);
        else Show(); // invalid or unchanged: put the real value back
    }

    private static byte? Channel(TextBox box) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? (byte)Math.Clamp(v, 0, 255) : null;

    /// <summary>The wheel over R, G or B nudges it (Shift: by 10).</summary>
    private void OnRgbWheel(object sender, MouseWheelEventArgs e)
    {
        var box = (TextBox)sender;
        int step = (e.Delta > 0 ? 1 : -1) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1);
        int value = Math.Clamp((Channel(box) ?? 0) + step, 0, 255);
        box.Text = value.ToString(CultureInfo.InvariantCulture);
        var c = _color;
        Choose(box == RBox ? Color.FromRgb((byte)value, c.G, c.B) : box == GBox ? Color.FromRgb(c.R, (byte)value, c.B) : Color.FromRgb(c.R, c.G, (byte)value), final: true);
        e.Handled = true;
    }
}
