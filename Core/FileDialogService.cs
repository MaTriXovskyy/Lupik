using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Lupik.Core;

internal sealed record DialogSelection(IntPtr Window, IReadOnlyList<string> Paths);

/// <summary>Reads standard Windows file pickers without accepting the dialog or changing its selection.</summary>
internal static class FileDialogService
{
    private static int _reading;
    private sealed record DialogContext(IntPtr View, SelectionPattern[] Lists, AutomationElement? Address, ValuePattern? FileName);
    private static readonly object ContextLock = new();
    private static IntPtr _contextWindow;
    private static Task<DialogContext>? _contextTask;
    private static IntPtr _foregroundHook;
    private static IntPtr _focusHook, _showHook;
    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr window, int obj, int child, uint thread, uint time);
    private static readonly WinEventProc ForegroundChanged = (_, _, window, _, _, _, _) => Warm(GetAncestor(window, GA_ROOT));
    private static readonly WinEventProc DialogShown = (_, _, window, obj, child, _, _) =>
    {
        if (obj == 0 && child == 0 && ClassName(window) == "#32770") Warm(window);
    };
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    private const uint GA_ROOT = 2;
    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    internal static void Start()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        _foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, ForegroundChanged, 0, 0, 0);
        _focusHook = SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, ForegroundChanged, 0, 0, 0);
        _showHook = SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, DialogShown, 0, 0, 0);
        Warm(NativeMethods.GetForegroundWindow());
    }

    internal static void Stop()
    {
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        if (_focusHook != IntPtr.Zero) UnhookWinEvent(_focusHook);
        if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
        _foregroundHook = IntPtr.Zero;
        _focusHook = _showHook = IntPtr.Zero;
        lock (ContextLock) { _contextWindow = IntPtr.Zero; _contextTask = null; }
    }

    private static void Warm(IntPtr window)
    {
        // Discover controls as soon as the picker becomes active, before the user presses Space.
        // Selection and address values are still read fresh on each key press.
        if (Settings.Current.PreviewKey == null || ShellView(window) == IntPtr.Zero) return;
        lock (ContextLock)
            if (_contextWindow == window && _contextTask is { IsFaulted: false }) return;
        var task = Context(window);
        _ = task.ContinueWith(t => App.Log($"[FileDialog] Warm-up unavailable: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static Task<DialogContext> Context(IntPtr window)
    {
        lock (ContextLock)
        {
            if (_contextWindow == window && _contextTask is { IsFaulted: false } existing) return existing;
            _contextWindow = window;
            return _contextTask = Task.Run(() => BuildContext(window));
        }
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(128);
        NativeMethods.GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    internal static IntPtr ShellView(IntPtr window)
    {
        if (window == IntPtr.Zero || ClassName(window) != "#32770") return IntPtr.Zero;
        IntPtr view = IntPtr.Zero;
        EnumChildWindows(window, (child, _) =>
        {
            if (ClassName(child) != "SHELLDLL_DefView") return true;
            view = child;
            return false;
        }, IntPtr.Zero);
        return view;
    }

    // Only native, cheap checks belong in the low-level keyboard hook. Never call UIA from that thread.
    internal static bool CanPreview(IntPtr window)
    {
        IntPtr view = ShellView(window);
        if (view == IntPtr.Zero) return false;
        uint thread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var info = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref info) || info.hwndFocus == IntPtr.Zero) return false;
        for (IntPtr focus = info.hwndFocus; focus != IntPtr.Zero && focus != window; focus = GetParent(focus))
        {
            string name = ClassName(focus);
            if (name.Contains("Edit", StringComparison.OrdinalIgnoreCase) || name.Contains("Search", StringComparison.OrdinalIgnoreCase)) return false;
            if (focus == view) return true;
        }
        return false;
    }

    internal static void Request(IntPtr window, Action<DialogSelection> ready)
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return;
        // Task pool threads are MTA. A slow/unresponsive accessibility provider must never block the hook or UI.
        _ = Task.Run(async () =>
        {
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var paths = await ReadSelectionAsync(window).ConfigureAwait(false);
                if (paths.Count == 0) return;
                if (started.Elapsed > TimeSpan.FromSeconds(2) || !IsCurrent(window))
                {
                    App.Log("[FileDialog] Selection discarded: picker lost focus or the read took too long");
                    return;
                }
                ready(new DialogSelection(window, paths));
            }
            catch (Exception ex) { App.Log($"[FileDialog] Selection unavailable: {ex.Message}"); }
            finally { Volatile.Write(ref _reading, 0); }
        });
    }

    internal static bool IsCurrent(IntPtr window)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        return (GetAncestor(foreground, GA_ROOT) == window || foreground == window) && CanPreview(window);
    }

    internal static IReadOnlyList<string> ReadSelection(IntPtr window) => ReadSelectionAsync(window).GetAwaiter().GetResult();

    private static async Task<IReadOnlyList<string>> ReadSelectionAsync(IntPtr window)
    {
        IntPtr view = ShellView(window);
        if (view == IntPtr.Zero) return Array.Empty<string>();
        var context = await Context(window).ConfigureAwait(false);
        if (context.View != view)
        {
            lock (ContextLock) _contextTask = null;
            context = await Context(window).ConfigureAwait(false);
        }
        try { return ReadSelection(window, context); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            lock (ContextLock) _contextTask = null;
            return ReadSelection(window, await Context(window).ConfigureAwait(false));
        }
    }

    private static DialogContext BuildContext(IntPtr window)
    {
        IntPtr view = ShellView(window);
        if (view == IntPtr.Zero) throw new InvalidOperationException("Not a Windows shell file picker");
        var root = AutomationElement.FromHandle(window);
        var pane = AutomationElement.FromHandle(view);
        var lists = pane.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.IsSelectionPatternAvailableProperty, true),
            new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid))));
        var patterns = new List<SelectionPattern>();
        foreach (AutomationElement list in lists)
        {
            if (list.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)) patterns.Add((SelectionPattern)pattern);
        }
        if (patterns.Count == 0) throw new InvalidOperationException("File list is not ready yet");
        AutomationElement? address = null;
        foreach (AutomationElement bar in root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar)))
        {
            string name = bar.Current.Name;
            if (FolderFromAddress(name) != null || FolderFromAddress(Value(bar)) != null) { address = bar; break; }
            // The picker may first open at Home / This PC, then navigate to a filesystem folder.
            if (address == null && bar.Current.ClassName == "ToolbarWindow32" && name.Contains(':')) address = bar;
        }
        ValuePattern? fileName = null;
        foreach (AutomationElement field in root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1148")))
        {
            if (field.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) { fileName = (ValuePattern)pattern; break; }
        }
        if (address == null && fileName == null) throw new InvalidOperationException("Picker controls are not ready yet");
        App.Log($"[FileDialog] Controls prepared: lists={patterns.Count}, address={address != null}, filename={fileName != null}");
        return new DialogContext(view, patterns.ToArray(), address, fileName);
    }

    private static IReadOnlyList<string> ReadSelection(IntPtr window, DialogContext context)
    {
        foreach (var pattern in context.Lists)
        {
            var selected = pattern.Current.GetSelection();
            if (selected.Length == 0) continue;

            // DirectUI can also put focus on an inline rename box or a header. Do not take their Space key.
            var focused = AutomationElement.FocusedElement;
            if (focused == null || focused.Current.ControlType is var type &&
                (type == ControlType.Edit || type == ControlType.Button || type == ControlType.ComboBox || type == ControlType.HeaderItem))
                return Array.Empty<string>();

            string? folder = context.Address is { } address ? FolderFromAddress(address.Current.Name) ?? FolderFromAddress(Value(address)) : null;
            string[] typedNames = ReadFileNames(context.FileName);
            App.Log($"[FileDialog] Selected={selected.Length}, filesystem folder={folder != null}, filename entries={typedNames.Length}");
            var paths = new List<string>();
            foreach (var item in selected)
            {
                string name = item.Current.Name;
                string? path = ExistingPath(Value(item));
                path ??= ExistingPath(name);
                path ??= ResolveItem(folder, name, typedNames);
                // An ambiguous or virtual item must not turn into some other Explorer window's selection.
                if (path == null) return Array.Empty<string>();
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);
            }
            App.Log($"[FileDialog] Read {paths.Count} selected item(s) from 0x{window:X}");
            return paths;
        }
        return Array.Empty<string>();
    }

    private static string Value(AutomationElement element) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? ((ValuePattern)value).Current.Value : "";

    private static string? ExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        return File.Exists(path) || Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? FolderFromAddress(string text)
    {
        // The breadcrumb toolbar exposes the full filesystem address even when the displayed breadcrumbs
        // contain only folder names. Ignore the localized "Address:" / "Adres:" prefix.
        var match = Regex.Match(text, @"(?:[a-zA-Z]:\\|\\\\)[^\r\n]*");
        return match.Success && ExistingPath(match.Value.Trim()) is string path && Directory.Exists(path) ? path : null;
    }

    private static string[] ReadFileNames(ValuePattern? field)
    {
        // 1148 is the common dialog's filename edit/combobox automation ID, independent of display language.
        string value = field?.Current.Value.Trim() ?? "";
        if (value.Length == 0) return Array.Empty<string>();
        var quoted = Regex.Matches(value, "\"([^\"]+)\"");
        return quoted.Count > 0 ? quoted.Select(m => m.Groups[1].Value).ToArray() : new[] { value };
    }

    internal static string? ResolveItem(string? folder, string displayName, IReadOnlyList<string> typedNames)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        bool MatchesName(string path) => string.Equals(Path.GetFileName(path), displayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(path), displayName, StringComparison.OrdinalIgnoreCase);
        // The filename field keeps actual extensions when Explorer hides known extensions in the list.
        var exact = typedNames.Select(name => Path.IsPathFullyQualified(name) ? ExistingPath(name) :
                folder != null ? ExistingPath(Path.Combine(folder, name)) : null)
            .Where(path => path != null && MatchesName(path) && (folder == null || string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? ""), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;
        if (folder == null || !Directory.Exists(folder)) return null;
        // A hidden extension is safe to resolve only when the visible name is unique in this folder.
        var candidates = Directory.EnumerateFileSystemEntries(folder).Where(MatchesName).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
