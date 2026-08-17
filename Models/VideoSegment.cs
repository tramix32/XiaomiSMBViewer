namespace XiaomiSMBViewer.Models;

/// <summary>
/// Jeden minutowy plik nagrania. Czas startu bierzemy z nazwy folderu (YYYYMMDDHH)
/// i nazwy pliku (MMmSSs) - to jest czas lokalny kamery, odporny na strefy/DST.
/// </summary>
public sealed class VideoSegment
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required DateTime StartLocal { get; init; }

    /// <summary>Domyslnie 60 s, uscislane w tle przez ffprobe.</summary>
    public double DurationSeconds { get; set; } = 60.0;

    public bool DurationProbed { get; set; }

    public long SizeBytes { get; set; }

    public DateTime EndLocal => StartLocal.AddSeconds(DurationSeconds);

    /// <summary>Sekunda od polnocy dnia, do ktorego segment nalezy.</summary>
    public double StartOfDay => StartLocal.TimeOfDay.TotalSeconds;

    public double EndOfDay => StartOfDay + DurationSeconds;

    public bool Contains(double dayseconds) => dayseconds >= StartOfDay && dayseconds < EndOfDay;
}
