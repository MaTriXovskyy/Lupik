using Lupik.Core;

namespace Lupik;

public partial class MainWindow
{
    internal void ShowDialogSelection(DialogSelection selection)
    {
        if (_saving || selection.Paths.Count == 0) return;
        string path = selection.Paths[0];
        if (!PathExists(path)) return;
        bool sameSelection = selection.Paths.Count > 1
            ? _selectionSet.SequenceEqual(selection.Paths, StringComparer.OrdinalIgnoreCase)
            : _selectionSet.Count == 0 && string.Equals(_explorerSelection, path, StringComparison.OrdinalIgnoreCase);
        if (IsVisible && SourceWindow == selection.Window && sameSelection)
        {
            HideWindow();
            return;
        }
        // Capture the dialog before activation: closing Lupik returns focus to this picker.
        SourceWindow = selection.Window;
        _explorerSelection = path;
        _selectionSet = selection.Paths.Count > 1 ? selection.Paths : Array.Empty<string>();
        ResetFolderHistory();
        _focusOnShow = true;
        ShowSelection(path);
    }
}
