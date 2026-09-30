using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Lupik.Views;

/// <summary>A preview that can be edited in edit mode (E): text, CSV, PDF pages, pictures.</summary>
public interface IEditable
{
    /// <summary>Null if this file can be edited now, otherwise why not (shown to the user).</summary>
    string? WhyNotEditable();

    void BeginEdit();

    /// <summary>Leaves edit mode. Unsaved changes are thrown away (the caller asked first).</summary>
    void EndEdit();

    bool IsDirty { get; }

    event Action? DirtyChanged;

    /// <summary>Writes the edited file to <paramref name="path"/> (a temporary file next to the original).</summary>
    Task SaveToAsync(string path);

    /// <summary>Lets go of the file (e.g. the open PDF) so it can be replaced.</summary>
    void ReleaseFile();

    /// <summary>The saved file is in place: show it again, still in edit mode.</summary>
    Task ReloadAsync(string path);

    /// <summary>A key in edit mode, before the window's own (Ctrl+S, Esc). True if the viewer used it.</summary>
    bool HandleEditKey(Key key, ModifierKeys mods);
}
