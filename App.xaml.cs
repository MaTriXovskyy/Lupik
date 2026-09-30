using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Lupik.Core;

using Lupik.Localization;
namespace Lupik;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "Lupik_SingleInstance_App_Mutex";
    private const string WakeEventName = "Lupik_Wake_Event";
    private static EventWaitHandle? _wakeEvent;
    private static CancellationTokenSource? _wakeCts;

    private KeyboardHook? _keyboardHook;
    private MainWindow? _mainWindow;
    private TrayService? _trayService;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log("Unhandled AppDomain Exception: " + e.ExceptionObject);
        };

        DispatcherUnhandledException += (s, e) =>
        {
            Log("Dispatcher Exception: " + e.Exception);
            e.Handled = true;
        };
    }

    private readonly HashSet<System.Windows.Interop.HwndSource> _automationRefused = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Localization.Loc.Instance.SetLanguage(Core.Settings.Current.Language);
        Core.Accent.ApplySaved(); // before any window: every gold brush comes from here
        foreach (var missing in Localization.Loc.MissingKeys()) Log($"[Loc] Missing translation: {missing}");

        // Every window of the app refuses UI Automation queries (see NativeMethods.RefuseAutomation)
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window w && PresentationSource.FromVisual(w) is System.Windows.Interop.HwndSource src && _automationRefused.Add(src))
                src.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
                {
                    Core.NativeMethods.RefuseAutomation(msg, ref handled);
                    return IntPtr.Zero;
                });
        }));

        Log("Lupik starting...");

        // 1. Single instance check with wake signaling
        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            Log("Second instance launched. Signaling primary instance to wake...");
            try
            {
                using var wake = EventWaitHandle.OpenExisting(WakeEventName);
                wake.Set();
                Log("Wake signal sent successfully.");
            }
            catch (Exception ex)
            {
                Log($"Could not signal existing instance: {ex.Message}");
            }
            Shutdown();
            return;
        }

        // Unfinished zip / extraction outputs of a previous run that was killed or crashed
        Task.Run(Core.PendingCleanup.CleanLeftovers);

        try
        {
            // 2. Setup Wake Event Listener for future instances
            _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
            _wakeCts = new CancellationTokenSource();
            var token = _wakeCts.Token;

            Task.Run(() =>
            {
                // Sleeps until either a wake signal or shutdown: no periodic polling while idle
                var handles = new WaitHandle[] { _wakeEvent, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (WaitHandle.WaitAny(handles) == 0)
                        {
                            Log("[App] Wake event triggered from external launch! Toggling window...");
                            Dispatcher.InvokeAsync(() => _mainWindow?.ToggleWindow());
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[App] Wake listener loop error: {ex.Message}");
                        break;
                    }
                }
            }, token);

            // 3. Create MainWindow
            _mainWindow = new MainWindow();
            KeyboardHook.MainWindowRef = _mainWindow;

            // 4. Initialize Keyboard Hook
            _keyboardHook = new KeyboardHook();
            KeyboardHook.PreviewKeyPressed += OnPreviewKeyPressed;
            _keyboardHook.Start();

            // 5. Setup System Tray Icon
            _trayService = new TrayService();
            Autostart.Refresh();
            _trayService.Initialize();
            Updater.ScheduleChecks();
            Task.Run(Views.ArchiveViewer.CleanTemp); // peeks / drags out of archives from last time
            _trayService.DoubleClicked += () =>
            {
                Dispatcher.InvokeAsync(() => _mainWindow.ToggleWindow());
            };
            // Launched by autostart with --tray: stay silent in the tray
            bool startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            if (!startInTray)
            {
                _trayService.ShowBalloonNotification(
                    Loc.T("tray.readyTitle"),
                    Loc.T("tray.readyBody", Loc.T("key.preview")));
            }

            Log($"Lupik initialized successfully and running (startInTray={startInTray}).");

            // 6. Immediately toggle/show window so user sees it right away (unless autostarted)
            if (!startInTray)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    _mainWindow.ToggleWindow();
                });
            }
        }
        catch (Exception ex)
        {
            Log("Initialization error: " + ex);
            MessageBox.Show(Loc.T("app.startError", ex.Message), "Lupik");
            Shutdown();
        }
    }

    private void OnPreviewKeyPressed()
    {
        Log("[App] OnPreviewKeyPressed received from hook. Dispatching to UI thread...");
        Dispatcher.InvokeAsync(() =>
        {
            Log("[App] Calling _mainWindow.ToggleWindow()...");
            _mainWindow?.ToggleWindow();
        });
    }

    /// <summary>
    /// %LocalAppData%\Lupik\logs\Lupik.log: outside the program folder, which an update replaces
    /// (and when running from bin\ the log still lands in one known place).
    /// </summary>
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lupik", "logs", "Lupik.log");

    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        try
        {
            lock (LogLock) // called from the hook thread, thumbnail workers and the UI at once
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                // Keep the log from growing forever: past 2 MB it becomes Lupik.old.log (one generation)
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                    File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), overwrite: true);

                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("Lupik exiting.");
        Core.PendingCleanup.CancelAllAndClean(); // unfinished zip / extraction outputs

        _wakeCts?.Cancel();
        _wakeEvent?.Dispose();

        _keyboardHook?.Dispose();
        _trayService?.Dispose();

        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore
            }
            _mutex.Dispose();
        }

        base.OnExit(e);
    }
}
