namespace XiaomiSMBViewer.Models;

/// <summary>
/// Wszystkie segmenty jednej kamery z jednego dnia, posortowane po czasie startu.
/// Zapewnia mapowanie: sekunda dnia -> (segment, offset w segmencie).
/// </summary>
public sealed class DayIndex
{
    public required string CameraId { get; init; }
    public required DateOnly Date { get; init; }
    public required IReadOnlyList<VideoSegment> Segments { get; init; }

    public static DayIndex Empty(string cameraId, DateOnly date) =>
        new() { CameraId = cameraId, Date = date, Segments = Array.Empty<VideoSegment>() };

    public bool IsEmpty => Segments.Count == 0;

    public double FirstSecond => IsEmpty ? 0 : Segments[0].StartOfDay;

    public double LastSecond => IsEmpty ? 0 : Segments[^1].EndOfDay;

    public double RecordedSeconds => Segments.Sum(s => s.DurationSeconds);

    /// <summary>Segment zawierajacy dana sekunde dnia, albo null jesli to luka.</summary>
    public VideoSegment? SegmentAt(double dayseconds)
    {
        int idx = IndexAtOrBefore(dayseconds);
        if (idx < 0) return null;
        var seg = Segments[idx];
        return seg.Contains(dayseconds) ? seg : null;
    }

    /// <summary>Pierwszy segment zaczynajacy sie o/po podanej sekundzie.</summary>
    public VideoSegment? SegmentAtOrAfter(double dayseconds)
    {
        var hit = SegmentAt(dayseconds);
        if (hit != null) return hit;
        foreach (var s in Segments)
            if (s.StartOfDay >= dayseconds) return s;
        return null;
    }

    public VideoSegment? SegmentBefore(double dayseconds)
    {
        VideoSegment? best = null;
        foreach (var s in Segments)
        {
            if (s.StartOfDay < dayseconds - 0.5) best = s;
            else break;
        }
        return best;
    }

    public VideoSegment? Next(VideoSegment current)
    {
        int i = IndexOf(current);
        return i >= 0 && i + 1 < Segments.Count ? Segments[i + 1] : null;
    }

    public VideoSegment? Previous(VideoSegment current)
    {
        int i = IndexOf(current);
        return i > 0 ? Segments[i - 1] : null;
    }

    public int IndexOf(VideoSegment segment)
    {
        for (int i = 0; i < Segments.Count; i++)
            if (ReferenceEquals(Segments[i], segment) ||
                string.Equals(Segments[i].Path, segment.Path, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>Segmenty przecinajace zakres [from, to) w sekundach dnia.</summary>
    public IEnumerable<VideoSegment> SegmentsInRange(double from, double to)
    {
        foreach (var s in Segments)
        {
            if (s.EndOfDay <= from) continue;
            if (s.StartOfDay >= to) break;
            yield return s;
        }
    }

    /// <summary>Ciagle bloki nagran (segmenty stykajace sie z tolerancja) - do rysowania timeline'u.</summary>
    public List<(double Start, double End)> Blocks(double gapToleranceSeconds = 2.0)
    {
        var result = new List<(double, double)>();
        if (IsEmpty) return result;

        double blockStart = Segments[0].StartOfDay;
        double blockEnd = Segments[0].EndOfDay;

        for (int i = 1; i < Segments.Count; i++)
        {
            var s = Segments[i];
            if (s.StartOfDay - blockEnd <= gapToleranceSeconds)
            {
                blockEnd = Math.Max(blockEnd, s.EndOfDay);
            }
            else
            {
                result.Add((blockStart, blockEnd));
                blockStart = s.StartOfDay;
                blockEnd = s.EndOfDay;
            }
        }
        result.Add((blockStart, blockEnd));
        return result;
    }

    private int IndexAtOrBefore(double dayseconds)
    {
        int lo = 0, hi = Segments.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Segments[mid].StartOfDay <= dayseconds) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }
}
