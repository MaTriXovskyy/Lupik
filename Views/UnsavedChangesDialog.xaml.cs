using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Lupik.Core;

namespace Lupik.Views;

public enum UnsavedChoice { Save, Discard, Cancel }

/// <summary>"Save the changes?" card in Lupik's style (like the delete confirmation): Enter saves, Esc stays.</summary>
public partial class UnsavedChangesDialog : Window
{
    private UnsavedChoice _choice = UnsavedChoice.Cancel;

    private UnsavedChangesDialog(string path)
    {
        InitializeComponent();
        NameText.Text = Path.GetFileName(path);
        NameText.ToolTip = path;
        ShellThumbnails.Request(path, 144, ShellThumbnails.CurrentGeneration, bmp => Dispatcher.BeginInvoke(() => Thumb.Source = bmp));
        Loaded += (_, _) => Animate();
    }

    public static UnsavedChoice Ask(Window owner, string path)
    {
        var dialog = new UnsavedChangesDialog(path) { Owner = owner };
        dialog.ShowDialog();
        return dialog._choice;
    }

    private void Close(UnsavedChoice choice)
    {
        _choice = choice;
        DialogResult = choice != UnsavedChoice.Cancel;
    }

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { Close(UnsavedChoice.Save); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(UnsavedChoice.Cancel); e.Handled = true; }
    }

    private void OnSave(object sender, RoutedEventArgs e) => Close(UnsavedChoice.Save);
    private void OnDiscard(object sender, RoutedEventArgs e) => Close(UnsavedChoice.Discard);
    private void OnCancel(object sender, RoutedEventArgs e) => Close(UnsavedChoice.Cancel);

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
