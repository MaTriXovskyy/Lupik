using System.Windows;
using System.Windows.Media;

namespace Lupik.Views;

/// <summary>Where a row sits in the archive tree, for drawing its guide lines.</summary>
/// <param name="Depth">0 = top level (no lines).</param>
/// <param name="Lines">For each outer level (depth − 1 of them): does a line pass through (more items follow in that folder)?</param>
/// <param name="IsLast">Last item in its folder: the line ends here (└) instead of going on (├).</param>
public sealed record TreePosition(int Depth, bool[] Lines, bool IsLast);

/// <summary>
/// Tree guide lines left of a row (like Explorer's navigation pane or VS Code): a line runs down from each folder
/// along everything inside it, with an elbow into each item. Draws past its own top/bottom so the lines join
/// across the row padding.
/// </summary>
public class TreeGuides : FrameworkElement
{
    public const double LevelWidth = 18;

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(TreePosition), typeof(TreeGuides),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public TreePosition? Position
    {
        get => (TreePosition?)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>How far above/below the row the lines reach (the list item's vertical padding).</summary>
    public double Overhang { get; set; } = 5;

    private static Pen? _linePen;
    private static int _lineVersion;

    /// <summary>The guide lines' pen, in the current theme.</summary>
    private static Pen LinePen
    {
        get
        {
            if (_linePen != null && _lineVersion == Core.Palette.Version) return _linePen;
            _lineVersion = Core.Palette.Version;
            _linePen = new Pen(Core.Palette.Brush(0x4A433B), 1);
            _linePen.Freeze();
            return _linePen;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new((Position?.Depth ?? 0) * LevelWidth, 0);

    protected override void OnRender(DrawingContext dc)
    {
        var pos = Position;
        if (pos == null || pos.Depth == 0) return;

        double top = -Overhang, bottom = ActualHeight + Overhang, mid = ActualHeight / 2;
        // Crisp 1 px lines: centre them on a pixel
        double X(int level) => Math.Floor(level * LevelWidth + LevelWidth / 2) + 0.5;

        for (int level = 0; level < pos.Depth - 1; level++)
            if (pos.Lines[level]) dc.DrawLine(LinePen, new Point(X(level), top), new Point(X(level), bottom));

        // The elbow into this item: ├ (more follow) or └ (last one)
        double x = X(pos.Depth - 1);
        dc.DrawLine(LinePen, new Point(x, top), new Point(x, pos.IsLast ? mid : bottom));
        dc.DrawLine(LinePen, new Point(x, mid), new Point(pos.Depth * LevelWidth - 2, mid));
    }
}
