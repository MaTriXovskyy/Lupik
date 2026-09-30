using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>Settings, opened from the tray: language, keys, behaviour, updates, about. Every change applies at once.</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    /// <summary>Which key field is waiting for a key (null = none).</summary>
    private Button? _recording;
    // True until the controls show the saved values. It must be on during InitializeComponent too: the slider's
    // Minimum="60" bumps its value from 0 to 60 there and raises ValueChanged, which used to save 60%.
    private bool _loading = true;

    public static void ShowOrActivate()
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow()
    {
        InitializeComponent();

        BuildLanguageList();
        StartupSwitch.IsChecked = Autostart.IsEnabled;
        FocusLossSwitch.IsChecked = Settings.Current.CloseOnFocusLoss;
        AutoplaySwitch.IsChecked = Settings.Current.AutoplayMedia;
        ScaleSlider.Value = Math.Round(Settings.Current.WindowScale * 100);
        UpdatesSwitch.IsChecked = Settings.Current.CheckForUpdates;
        _loading = false;

        RefreshTexts();
        Loc.Instance.LanguageChanged += RefreshTexts;
        Updater.StatusChanged += RefreshTexts;
        Closed += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= RefreshTexts;
            Updater.StatusChanged -= RefreshTexts;
            StopRecording();
        };
        Loaded += (_, _) => Animate();
    }

    // --- Texts built in code (follow the language)

    private void RefreshTexts()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshTexts); return; }

        PageTitle.Text = Loc.T(NavKeys.IsChecked == true ? "settings.navKeys"
            : NavUpdates.IsChecked == true ? "settings.navUpdates"
            : NavAbout.IsChecked == true ? "settings.navAbout" : "settings.navGeneral");

        string version = Loc.T("settings.version", Updater.CurrentVersion);
        VersionFooter.Text = version;
        AboutVersionText.Text = version;
        UpdatesVersionText.Text = "Lupik " + Updater.CurrentVersion;
        UpdateStatusText.Text = Updater.StatusText();
        CheckNowButton.IsEnabled = !Updater.IsBusy;
        ScaleText.Text = $"{ScaleSlider.Value:0}%";
        AutoLanguageHint.Text = Loc.T("settings.languageAutoHint", SystemLanguageName());

        if (_recording != PreviewKeyField) PreviewKeyField.Content = Settings.Current.PreviewKey?.Display() ?? Loc.T("settings.off");
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (PageGeneral == null) return; // during InitializeComponent
        StopRecording();
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageKeys.Visibility = NavKeys.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = NavUpdates.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshTexts();
    }

    // --- Language

    private TextBlock AutoLanguageHint = new();

    private static string SystemLanguageName()
    {
        string code = Loc.Resolve("auto");
        return Loc.Languages.First(l => l.Code == code).Name;
    }

    private void BuildLanguageList()
    {
        // "Automatic": a globe, and which language that means on this PC
        var autoIcon = new LucideIcon { Kind = "globe", Size = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        autoIcon.SetResourceReference(TextElement.ForegroundProperty, "Gold");
        var autoTexts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var autoTitle = new TextBlock { FontSize = 13.5 };
        autoTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        autoTitle.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("[settings.languageAuto]") { Source = Loc.Instance });
        AutoLanguageHint = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        AutoLanguageHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        autoTexts.Children.Add(autoTitle);
        autoTexts.Children.Add(AutoLanguageHint);
        var auto = new StackPanel { Orientation = Orientation.Horizontal, Children = { autoIcon, autoTexts } };
        LanguageList.Items.Add(new ListBoxItem { Content = auto, Tag = "auto" });

        foreach (var (code, name) in Loc.Languages)
        {
            var flag = new FlagIcon { Code = code, Width = 24, Height = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { flag, label }, MinHeight = 32 };
            LanguageList.Items.Add(new ListBoxItem { Content = content, Tag = code });
        }

        string current = Settings.Current.Language;
        LanguageList.SelectedItem = LanguageList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == current)
                                    ?? LanguageList.Items[0];
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageList.SelectedItem is not ListBoxItem { Tag: string code }) return;
        Settings.Update(s => s.Language = code);
        Loc.Instance.SetLanguage(code);
    }

    // --- Behaviour

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        bool want = StartupSwitch.IsChecked == true;
        if (!Autostart.Set(want))
        {
            StartupSwitch.IsChecked = Autostart.IsEnabled;
            MessageCard.Show(this, Loc.T("settings.startupError"));
        }
    }

    private void OnFocusLossClick(object sender, RoutedEventArgs e) =>
        Settings.Update(s => s.CloseOnFocusLoss = FocusLossSwitch.IsChecked == true);

    private void OnAutoplayClick(object sender, RoutedEventArgs e) =>
        Settings.Update(s => s.AutoplayMedia = AutoplaySwitch.IsChecked == true);

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleText != null) ScaleText.Text = $"{e.NewValue:0}%";
        if (_loading) return;
        Settings.Update(s => s.WindowScale = e.NewValue / 100.0);
    }

    // --- Keys

    private void OnRecordPreviewKey(object sender, RoutedEventArgs e) => StartRecording(PreviewKeyField);

    private void StartRecording(Button field)
    {
        StopRecording();
        _recording = field;
        field.Content = Loc.T("settings.pressKeys");
        field.BorderBrush = (Brush)FindResource("Gold");
        ShowKeyMessage(Loc.T("settings.recordHint"), error: false);
    }

    private void StopRecording()
    {
        if (_recording == null) return;
        _recording.BorderBrush = (Brush)FindResource("LineStrong");
        _recording = null;
        RefreshTexts();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == null)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (KeyCombo.IsModifierKey(key)) return; // wait for the actual key
        var mods = KeyState.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None) { StopRecording(); return; }

        var combo = new KeyCombo(KeyInterop.VirtualKeyFromKey(key), mods);
        var field = _recording;

        Settings.Update(s => s.PreviewKey = combo);
        _recording = null;
        field.BorderBrush = (Brush)FindResource("LineStrong");
        ShowKeyMessage(Loc.T("settings.recordHint"), error: false);
        RefreshTexts();
    }

    private void ShowKeyMessage(string text, bool error)
    {
        KeyMessage.Text = text;
        KeyMessage.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "TextMuted");
    }

    private void OnDisablePreviewKey(object sender, RoutedEventArgs e)
    {
        StopRecording();
        Settings.Update(s => s.PreviewKey = null);
    }

    private void OnRestoreKeys(object sender, RoutedEventArgs e)
    {
        StopRecording();
        Settings.Update(s => s.PreviewKey = KeyCombo.DefaultPreview);
        ShowKeyMessage(Loc.T("settings.recordHint"), error: false);
    }

    // --- Updates

    private void OnUpdatesClick(object sender, RoutedEventArgs e)
    {
        Settings.Update(s => s.CheckForUpdates = UpdatesSwitch.IsChecked == true);
        Updater.ScheduleChecks();
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e) => await Updater.CheckAsync(interactive: true, owner: this);

    // --- About

    private void OnGitHub(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Updater.RepoUrl) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log($"[Settings] Could not open the browser: {ex.Message}"); }
    }

    // --- Window

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
    }
}
