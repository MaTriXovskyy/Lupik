using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickPeek.Core;

namespace QuickPeek;

public partial class App : Application
{
    private static Mutex? _mutex;
    private const string MutexName = "QuickPeek_SingleInstance_App_Mutex";
    private const string WakeEventName = "QuickPeek_Wake_Event";
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

        Log("QuickPeek starting...");

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
            KeyboardHook.SpacePressed += OnSpacePressed;
            _keyboardHook.Start();

            // 5. Setup System Tray Icon
            _trayService = new TrayService(_mainWindow);
            _trayService.Initialize();
            _trayService.DoubleClicked += () =>
            {
                Dispatcher.InvokeAsync(() => _mainWindow.ToggleWindow());
            };
            // Launched by autostart with --tray: stay silent in the tray
            bool startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));

            if (!startInTray)
            {
                _trayService.ShowBalloonNotification(
                    "QuickPeek jest gotowy!",
                    "Wciśnij SPACJĘ na dowolnym pliku w Eksploratorze lub na Pulpicie.");
            }

            Log($"QuickPeek initialized successfully and running (startInTray={startInTray}).");

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
            MessageBox.Show($"Błąd uruchamiania QuickPeek:\n{ex.Message}", "QuickPeek");
            Shutdown();
        }
    }

    private void OnSpacePressed()
    {
        Log("[App] OnSpacePressed received from hook. Dispatching to UI thread...");
        Dispatcher.InvokeAsync(() =>
        {
            Log("[App] Calling _mainWindow.ToggleWindow()...");
            _mainWindow?.ToggleWindow();
        });
    }

    public static void Log(string message)
    {
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QuickPeek.log");

            // Keep the log from growing forever: past 2 MB it becomes QuickPeek.old.log (one generation)
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
                File.Move(logPath, Path.ChangeExtension(logPath, ".old.log"), overwrite: true);

            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("QuickPeek exiting.");
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
