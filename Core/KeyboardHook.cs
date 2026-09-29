using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickPeek.Core;

public class KeyboardHook : IDisposable
{
    private static IntPtr _hookId = IntPtr.Zero;
    private static readonly NativeMethods.LowLevelKeyboardProc _staticProc = HookCallback;
    public static event Action? SpacePressed;
    public static MainWindow? MainWindowRef { get; set; }

    private uint _hookThreadId;
    private Thread? _hookThread;
    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Start()
    {
        if (_hookThread != null) return;

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
            Name = "QuickPeek_KeyboardHook_Thread"
        };

        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        readyEvent.Wait(2000);
    }

    public void Stop()
    {
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread?.Join(1000);
            _hookThread = null;
            _hookThreadId = 0;
            App.Log("[KeyboardHook] Low-level hook stopped.");
        }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private static bool IsOwnDialogInForeground()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || (MainWindowRef != null && fg == MainWindowRef.Hwnd)) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == OwnProcessId;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            // Our own dialogs (e.g. "Save As") get Space/Esc untouched
            if (IsOwnDialogInForeground())
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            if (kb.vkCode == NativeMethods.VK_SPACE)
            {
                bool isCtrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                bool isAlt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
                bool isWin = ((GetAsyncKeyState(0x5B) & 0x8000) != 0) || ((GetAsyncKeyState(0x5C) & 0x8000) != 0);

                if (!isCtrl && !isAlt && !isWin)
                {
                    var window = MainWindowRef;
                    bool previewShown = window != null && window.IsShown;

                    IntPtr fgWnd = NativeMethods.GetForegroundWindow();
                    IntPtr rootWnd = GetAncestor(fgWnd, GA_ROOT);
                    if (rootWnd == IntPtr.Zero) rootWnd = fgWnd;

                    // 1. Preview has focus -> Space closes it
                    if (previewShown && (fgWnd == window!.Hwnd || rootWnd == window.Hwnd))
                    {
                        App.Log("[KeyboardHook] Preview focused -> Space pressed, closing window.");
                        window.Dispatcher.InvokeAsync(() => window.HideWindow());
                        return (IntPtr)1; // Swallow Space
                    }

                    var sb = new StringBuilder(256);
                    NativeMethods.GetClassName(fgWnd, sb, sb.Capacity);
                    string className = sb.ToString();

                    var sbRoot = new StringBuilder(256);
                    NativeMethods.GetClassName(rootWnd, sbRoot, sbRoot.Capacity);
                    string rootClass = sbRoot.ToString();


                    bool isExplorerOrDesktop = className == "CabinetWClass" || className == "ExplorerWClass" ||
                                               rootClass == "CabinetWClass" || rootClass == "ExplorerWClass" ||
                                               className == "Progman" || className == "WorkerW" ||
                                               rootClass == "Progman" || rootClass == "WorkerW" ||
                                               className == "SHELLDLL_DefView" || rootClass == "SHELLDLL_DefView" ||
                                               className == "SysListView32" || rootClass == "SysListView32" ||
                                               className == "DirectUIHWND" ||
                                               className == "#32770" || rootClass == "#32770";

                    if (isExplorerOrDesktop && Settings.Current.SpaceInExplorer)
                    {
                        bool editing = IsUserEditingText(fgWnd);
                        App.Log($"[KeyboardHook] Target is Explorer/Desktop! isEditing={editing}");

                        if (!editing)
                        {
                            if (previewShown)
                            {
                                // 2. Preview open, user picked another file -> swap (or close if same file)
                                App.Log("[KeyboardHook] Preview open -> swapping to current selection.");
                                window!.Dispatcher.InvokeAsync(() => window.ShowSelectedOrHide());
                            }
                            else
                            {
                                // 3. Preview closed -> open it
                                App.Log("[KeyboardHook] Triggering SpacePressed event async...");
                                Task.Run(() => SpacePressed?.Invoke());
                            }
                            return (IntPtr)1; // Swallow space so Explorer doesn't scroll
                        }
                    }
                    // Space in any other app is left alone: the preview stays open (e.g. parked on the taskbar)
                }
            }
            // Esc is handled by the preview window itself when it has focus (full screen, selection, close);
            // in other apps it's left alone, so it doesn't close a preview waiting on the taskbar
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
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
