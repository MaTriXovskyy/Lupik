using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lupik.Core;

public class KeyboardHook : IDisposable
{
    private static IntPtr _hookId = IntPtr.Zero;
    private static readonly NativeMethods.LowLevelKeyboardProc _staticProc = HookCallback;
    public static event Action? PreviewKeyPressed;
    public static MainWindow? MainWindowRef { get; set; }

    private uint _hookThreadId;
    private Thread? _hookThread;
    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    private const uint GA_ROOT = 2;
    private static uint _heldPreviewKey;
    private static bool _previewPressConsumed;

    public void Start()
    {
        if (_hookThread != null) return;
        FileDialogService.Start();

        var readyEvent = new ManualResetEventSlim(false);

        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();

            IntPtr hMod = IntPtr.Zero;
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule != null)
                {
                    hMod = NativeMethods.GetModuleHandle(curModule.ModuleName);
                }
            }
            catch
            {
                hMod = IntPtr.Zero;
            }

            if (hMod == IntPtr.Zero)
            {
                hMod = NativeMethods.GetModuleHandle(null);
            }

            _hookId = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _staticProc,
                hMod,
                0);

            int err = Marshal.GetLastWin32Error();
            App.Log($"[KeyboardHook] Dedicated hook thread started (TID: {_hookThreadId}, hMod: 0x{hMod:X}). HookID: 0x{_hookId:X}, err: {err}");
            readyEvent.Set();

            // Dedicated Win32 Message Pump for Low-Level Hook
            NativeMethods.MSG msg;
            while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_hookId != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            App.Log("[KeyboardHook] Hook thread message pump terminated.");
        })
        {
            IsBackground = true,
            Name = "Lupik_KeyboardHook_Thread"
        };

        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        readyEvent.Wait(2000);
    }

    public void Stop()
    {
        FileDialogService.Stop();
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(1000);
            _hookThread = null;
            _hookThreadId = 0;
            App.Log("[KeyboardHook] Low-level hook stopped.");
        }
    }

    /// <summary>
    /// The only key Lupik takes from other windows: the preview key (Space unless changed in Settings), in Explorer
    /// on the Desktop or in a standard Windows file picker's file list. Everything else, in every app, passes through
    /// untouched: once the preview is open it is the active window and gets its keys the normal way.
    /// </summary>
    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP)
                HandlePreviewPress(kb.vkCode, false, OnPreviewKey);
            var previewKey = Settings.Current.PreviewKey;
            if ((wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN) &&
                previewKey != null && previewKey.Matches(kb.vkCode, Core.KeyState.Modifiers))
            {
                if (HandlePreviewPress(kb.vkCode, true, OnPreviewKey)) return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static bool HandlePreviewPress(uint key, bool down, Func<bool> onPress)
    {
        if (!down)
        {
            if (_heldPreviewKey == key) { _heldPreviewKey = 0; _previewPressConsumed = false; }
            return false;
        }
        if (_heldPreviewKey != key)
        {
            _heldPreviewKey = key;
            _previewPressConsumed = onPress();
        }
        return _previewPressConsumed;
    }

    /// <summary>The preview key was pressed: true if it was for Lupik (Explorer or the Desktop, not typing a name).</summary>
    private static bool OnPreviewKey()
    {
        IntPtr fgWnd = NativeMethods.GetForegroundWindow();
        IntPtr rootWnd = GetAncestor(fgWnd, GA_ROOT);
        if (rootWnd == IntPtr.Zero) rootWnd = fgWnd;

        var preview = MainWindowRef;
        if (preview != null && preview.IsShown && rootWnd == preview.Hwnd && !preview.EditMode && !preview.SearchActive)
        {
            // A physical press/release, rather than WPF's stale repeat state after a cross-process focus change.
            preview.Dispatcher.InvokeAsync(() => preview.HideWindow(), System.Windows.Threading.DispatcherPriority.Input);
            return true;
        }

        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(rootWnd, sb, sb.Capacity);
        if (sb.ToString() == "#32770")
        {
            // A shell file list, rather than every classic dialog. Text fields and buttons keep their keys.
            bool canPreview = FileDialogService.CanPreview(rootWnd);
            App.Log($"[FileDialog] Preview key in dialog 0x{rootWnd:X}, file-list focus={canPreview}");
            if (!canPreview) return false;
            FileDialogService.Request(rootWnd, selection =>
            {
                var target = MainWindowRef;
                target?.Dispatcher.InvokeAsync(() =>
                {
                    if (FileDialogService.IsCurrent(selection.Window)) target.ShowDialogSelection(selection);
                });
            });
            return true;
        }
        bool isExplorerOrDesktop = sb.ToString() is "CabinetWClass" or "ExplorerWClass" or "Progman" or "WorkerW";
        if (!isExplorerOrDesktop || IsUserEditingText(fgWnd)) return false;

        var window = MainWindowRef;
        if (window != null && window.IsShown)
        {
            // Preview open, user picked another file in Explorer -> swap (or close if it's the same file)
            App.Log("[KeyboardHook] Preview open -> swapping to current selection.");
            window.Dispatcher.InvokeAsync(() => window.ShowSelectedOrHide());
        }
        else
        {
            App.Log("[KeyboardHook] Preview key in Explorer/Desktop -> opening.");
            Task.Run(() => PreviewKeyPressed?.Invoke());
        }
        return true;
    }

    private static bool IsUserEditingText(IntPtr fgWnd)
    {
        try
        {
            uint threadId = NativeMethods.GetWindowThreadProcessId(fgWnd, out _);
            if (threadId == 0) return false;

            var gui = new NativeMethods.GUITHREADINFO();
            gui.cbSize = Marshal.SizeOf(gui);

            if (NativeMethods.GetGUIThreadInfo(threadId, ref gui))
            {
                if (gui.hwndFocus != IntPtr.Zero)
                {
                    var sb = new StringBuilder(256);
                    NativeMethods.GetClassName(gui.hwndFocus, sb, sb.Capacity);
                    string focusClass = sb.ToString();

                    if ((focusClass.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         focusClass.IndexOf("Search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         focusClass.IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0) &&
                          gui.hwndCaret != IntPtr.Zero)
                    {
                        App.Log($"[KeyboardHook] User editing detected in focusClass='{focusClass}' with caret.");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[KeyboardHook] Error in IsUserEditingText: {ex.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
