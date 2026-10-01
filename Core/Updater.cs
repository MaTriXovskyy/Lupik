using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Lupik.Localization;
using Lupik.Views;
using Velopack;
using Velopack.Sources;

namespace Lupik.Core;

/// <summary>
/// Updates from GitHub Releases (Velopack). Checks at start and every 6 hours when enabled in Settings,
/// asks before installing, then restarts into the new version. Does nothing when Lupik runs outside
/// an installation (e.g. from bin\ while developing).
/// </summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/MaTriXovskyy/Lupik";

    private static readonly UpdateManager? Manager = Create();
    private static DispatcherTimer? _timer;
    private static UpdateInfo? _available;
    private static string? _declined; // version the user said "later" to (not asked again automatically)
    private static string _status = "";

    public static bool IsBusy { get; private set; }

    /// <summary>A newer version was found (the tray offers it).</summary>
    public static string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();

    public static event Action? StatusChanged;

    public static string CurrentVersion =>
        Manager?.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    private static UpdateManager? Create()
    {
        try
        {
            return new UpdateManager(new GithubSource(RepoUrl, null, false));
        }
        catch (Exception ex)
        {
            App.Log($"[Updater] Not available: {ex.Message}");
            return null;
        }
    }

    private static bool IsInstalled => Manager?.IsInstalled == true;

    public static string StatusText() =>
        !IsInstalled ? Loc.T("update.notInstalled")
        : _status != "" ? _status
        : _available != null ? Loc.T("update.available", AvailableVersion)
        : "";

    private static void SetStatus(string text)
    {
        _status = text;
        StatusChanged?.Invoke();
    }

    /// <summary>First check shortly after start, then every 6 hours (only when enabled).</summary>
    public static void ScheduleChecks()
    {
        _timer?.Stop();
        _timer = null;
        if (!IsInstalled || !Settings.Current.CheckForUpdates) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += async (_, _) =>
        {
            _timer!.Interval = TimeSpan.FromHours(6);
            await CheckAsync(interactive: false, owner: null);
        };
        _timer.Start();
    }

    /// <summary>
    /// Looks for a new version. Interactive = the user pressed "Check now" (always asks; reports "up to date");
    /// automatic checks ask once per version.
    /// </summary>
    public static async Task CheckAsync(bool interactive, Window? owner)
    {
        if (Manager == null || !IsInstalled || IsBusy) { StatusChanged?.Invoke(); return; }
        IsBusy = true;
        SetStatus(Loc.T("update.checking"));
        try
        {
            var info = await Manager.CheckForUpdatesAsync();
            _available = info;
            if (info == null)
            {
                App.Log("[Updater] Up to date");
                SetStatus(Loc.T("update.upToDate"));
                return;
            }

            string version = info.TargetFullRelease.Version.ToString();
            App.Log($"[Updater] Version {version} available");
            SetStatus("");
            if (!interactive && _declined == version) return;
            IsBusy = false;
            await OfferAsync(owner);
        }
        catch (Exception ex)
        {
            App.Log($"[Updater] Check failed: {ex.Message}");
            SetStatus(Loc.T("update.failed", ex.Message));
        }
        finally
        {
            IsBusy = false;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>"Version X is ready, update?" and, on yes, download + restart. Also used by the tray item.</summary>
    public static async Task OfferAsync(Window? owner)
    {
        if (Manager == null || _available == null || IsBusy) return;
        var info = _available;
        string version = info.TargetFullRelease.Version.ToString();

        if (!UpdateDialog.Ask(owner, version, CurrentVersion))
        {
            _declined = version;
            return;
        }

        IsBusy = true;
        // Lupik's own progress card; Velopack's plain dialog is switched off (silent) below
        var progress = new UpdateProgressWindow(version);
        progress.Show();
        try
        {
            SetStatus(Loc.T("update.downloading", 0));
            await Manager.DownloadUpdatesAsync(info, p => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                SetStatus(Loc.T("update.downloading", p));
                progress.SetProgress(p);
            }));
            App.Log($"[Updater] Downloaded {version}, restarting");
            progress.SetRestarting();
            await Task.Delay(900); // long enough to read "restarting", short enough not to wait for nothing
            // The updater waits for Lupik to exit, swaps the files without any window, and starts the new version
            // in the tray
            Manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: true, restartArgs: new[] { "--tray" });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.Log($"[Updater] Update failed: {ex.Message}");
            progress.Close();
            SetStatus(Loc.T("update.failed", ex.Message));
            IsBusy = false;
            MessageCard.Show(owner, Loc.T("update.failed", ex.Message));
        }
    }
}
