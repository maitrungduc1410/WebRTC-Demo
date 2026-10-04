namespace WebRtcDemo.Core.Group;

public readonly record struct TileRect(double X, double Y, double Width, double Height);

/// <summary>The remote tiles of a group call: as large as possible and roughly camera shaped.</summary>
public static class GroupGrid
{
    public const double TileAspect = 4.0 / 3.0;

    /// <summary>The column count that gives the largest tiles, as GroupCallView.vue picks it.</summary>
    public static (int Columns, int Rows) Shape(int count, double width, double height, double gap)
    {
        count = Math.Max(count, 1);
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        var best = (Columns: 1, Rows: count);
        var bestScore = double.NegativeInfinity;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;
            var cellWidth = (width - gap * (columns - 1)) / columns;
            var cellHeight = (height - gap * (rows - 1)) / rows;
            var score = Math.Min(cellWidth, cellHeight * TileAspect);
            if (score > bestScore)
            {
                bestScore = score;
                best = (columns, rows);
            }
        }
        return best;
    }

    /// <summary>
    /// Tile rectangles inside a <paramref name="width"/> × <paramref name="height"/> area: every
    /// cell the same size, rows filled in order and the last, shorter row centred.
    /// </summary>
    public static IReadOnlyList<TileRect> Layout(int count, double width, double height, double gap)
    {
        if (count <= 0) return [];
        var (columns, rows) = Shape(count, width, height, gap);
        var cellWidth = Math.Max((width - gap * (columns - 1)) / columns, 0);
        var cellHeight = Math.Max((height - gap * (rows - 1)) / rows, 0);
        var tiles = new TileRect[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            var inRow = row == rows - 1 ? count - row * columns : columns;
            var rowWidth = inRow * cellWidth + (inRow - 1) * gap;
            var left = (width - rowWidth) / 2;
            tiles[i] = new TileRect(left + column * (cellWidth + gap), row * (cellHeight + gap), cellWidth, cellHeight);
        }
        return tiles;
    }
}

/// <summary>
/// A tile's fit: fill (crop) by default, fit (letterbox) while that participant presents, and a
/// double-click override that lasts until they start or stop presenting.
/// </summary>
public struct TileFit
{
    private bool? _override;
    private bool _presenting;

    public readonly bool Fit => _override ?? _presenting;

    /// <returns>True when <see cref="Fit"/> changed.</returns>
    public bool SetPresenting(bool presenting)
    {
        if (presenting == _presenting) return false;
        var before = Fit;
        _presenting = presenting;
        _override = null;
        return before != Fit;
    }

    public void Toggle() => _override = !Fit;
}

/// <summary>
/// The loudest participant above a threshold, held for a while so the ring does not flicker
/// between words (web: 0.03 and 1.2 s).
/// </summary>
public sealed class ActiveSpeaker(double threshold = ActiveSpeaker.DefaultThreshold, TimeSpan? hold = null)
{
    public const double DefaultThreshold = 0.03;
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(1200);

    private readonly TimeSpan _hold = hold ?? DefaultHold;
    private DateTimeOffset _heardAt;

    public string? Id { get; private set; }

    /// <param name="levels">Participant id → audio level, only for participants whose mic is on.</param>
    /// <returns>The active speaker now, or null.</returns>
    public string? Update(IReadOnlyDictionary<string, double> levels, DateTimeOffset now)
    {
        string? loudest = null;
        var max = threshold;
        foreach (var (id, level) in levels)
        {
            if (level > max)
            {
                max = level;
                loudest = id;
            }
        }
        if (loudest != null)
        {
            Id = loudest;
            _heardAt = now;
        }
        else if (Id != null && (now - _heardAt >= _hold || !levels.ContainsKey(Id)))
        {
            Id = null;
        }
        return Id;
    }

    public void Reset() => Id = null;
}
