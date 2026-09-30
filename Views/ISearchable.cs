using System.Threading;
using System.Threading.Tasks;

namespace Lupik.Views;

/// <summary>A preview the search bar (Ctrl+F) can search in. Matching is case-insensitive.</summary>
public interface ISearchable
{
    /// <summary>Finds and highlights every match; returns how many there are.</summary>
    Task<int> SearchAsync(string query, CancellationToken token);

    /// <summary>Makes match <paramref name="index"/> (0-based) the current one and scrolls it into view.</summary>
    void ShowMatch(int index);

    /// <summary>Removes the highlights.</summary>
    void ClearSearch();
}
