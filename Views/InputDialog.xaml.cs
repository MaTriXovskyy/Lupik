using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Lupik.Views;

/// <summary>
/// A one-line question in Lupik's style: renaming a file (F2), the pages to extract from a PDF, a note on a picture.
/// Unlike the preview it takes the keyboard focus (it's for typing), and gives it back when closed.
/// </summary>
public partial class InputDialog : Window
{
    private readonly Func<string, string?>? _validate;

    private InputDialog(string heading, string? hint, string text, string okText, string icon, Func<string, string?>? validate, int selectLength)
    {
        InitializeComponent();
        _validate = validate;
        HeadingText.Text = heading;
        if (hint != null) { HintText.Text = hint; HintText.Visibility = Visibility.Visible; }
        HeadingIcon.Kind = icon;
        OkText.Text = okText;
        Input.Text = text;
        Loaded += (_, _) =>
        {
            Lupik.Core.Foreground.Take(new WindowInteropHelper(this).Handle);
            Activate();
            Input.Focus();
            Input.Select(0, selectLength < 0 ? text.Length : selectLength);
        };
        Validate();
    }

    /// <summary>Asks; null if cancelled. <paramref name="validate"/> returns an error message, or null when fine.</summary>
    public static string? Ask(Window owner, string heading, string? hint, string text, string okText, string icon = "pencil",
        Func<string, string?>? validate = null, int selectLength = -1)
    {
        var dialog = new InputDialog(heading, hint, text, okText, icon, validate, selectLength) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Input.Text : null;
    }

    private bool Validate()
    {
        string? error = _validate?.Invoke(Input.Text);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = error == null;
        OkButton.Opacity = error == null ? 1 : 0.5;
        return error == null;
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Validate();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { if (Validate()) DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { if (Validate()) DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox) DragMove();
    }
}
