using System.Windows;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik;

public partial class MainWindow
{
    internal async Task ShowScreenshotAsync(string path, IntPtr origin, bool copied)
    {
        // Never replace an editor with unsaved work or an in-progress save.
        MainWindow target = _saving || EditMode ? CreatePinned() : this;
        if (target != this) { target.Topmost = Settings.Current.PinnedOnTop; target.UpdatePinButton(); }
        target.ResetFolderHistory();
        target._focusOnShow = true;
        await target.ShowFile(path);
        target.SourceWindow = origin;
        if (target.IsVisible)
        {
            target.TakeFocus();
            target.ShowNotice(Loc.T(copied ? "capture.copied" : "capture.copyFailed"), success: copied);
        }
    }
}
