using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using XiaomiSMBViewer.Models;

namespace XiaomiSMBViewer.Services;

/// <summary>
/// Czyta strukture: {root}\{cameraId}\{yyyyMMddHH}\{MMmSSs_epoch}.mp4
/// Wszystkie operacje IO ida po SMB, wiec sa async i anulowalne.
/// </summary>
public static class LibraryScanner
{
    private static readonly Regex HourDirRx = new(@"^(\d{4})(\d{2})(\d{2})(\d{2})$", RegexOptions.Compiled);

    // 00M37S_1786986037.mp4  (podstawowy format Xiaomi)
    private static readonly Regex FileRx = new(@"^(\d{2})M(\d{2})S(?:_(\d+))?\.mp4$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 003700.mp4 / 0037.mp4  (warianty spotykane na innych firmware)
    private static readonly Regex FileAltRx = new(@"^(\d{2})(\d{2})(\d{2})?\.mp4$", RegexOptions.Compiled);

    public static Task<List<string>> GetCamerasAsync(string root, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var list = new List<string>();
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }, ct);

    /// <summary>Dni, dla ktorych istnieje jakikolwiek katalog godzinowy. Malejaco (najnowsze pierwsze).</summary>
    public static Task<List<DateOnly>> GetDaysAsync(string root, string cameraId, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var days = new SortedSet<DateOnly>();
            var camDir = Path.Combine(root, cameraId);
            if (!Directory.Exists(camDir)) return new List<DateOnly>();

            foreach (var dir in Directory.EnumerateDirectories(camDir))
            {
                ct.ThrowIfCancellationRequested();
                var m = HourDirRx.Match(Path.GetFileName(dir));
                if (!m.Success) continue;
                if (TryParseHourDir(m, out var date, out _)) days.Add(date);
            }
            return days.Reverse().ToList();
        }, ct);

    /// <summary>
    /// Indeks jednego dnia. Czasy trwania zostaja domyslne (60 s) - uscisla je DurationProber.
    /// </summary>
    public static Task<DayIndex> ScanDayAsync(string root, string cameraId, DateOnly day, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var camDir = Path.Combine(root, cameraId);
            var segments = new List<VideoSegment>();
            if (!Directory.Exists(camDir)) return DayIndex.Empty(cameraId, day);

            for (int hour = 0; hour < 24; hour++)
            {
                ct.ThrowIfCancellationRequested();
                var hourDir = Path.Combine(camDir, $"{day:yyyyMMdd}{hour:00}");
                if (!Directory.Exists(hourDir)) continue;

                foreach (var file in Directory.EnumerateFiles(hourDir, "*.mp4"))
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = Path.GetFileName(file);
                    if (!TryParseMinuteSecond(fileName, out int mm, out int ss)) continue;

                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { /* plik moze zniknac w trakcie */ }
                    if (size == 0) continue;

                    segments.Add(new VideoSegment
                    {
                        Path = file,
                        FileName = fileName,
                        StartLocal = day.ToDateTime(new TimeOnly(hour, mm, ss)),
                        SizeBytes = size,
                    });
                }
            }

            segments.Sort((a, b) => a.StartLocal.CompareTo(b.StartLocal));
            ClampOverlaps(segments);

            return new DayIndex { CameraId = cameraId, Date = day, Segments = segments };
        }, ct);

    /// <summary>
    /// Zanim poznamy realne dlugosci, przycinamy domyslne 60 s tak, by segmenty na siebie nie nachodzily.
    /// </summary>
    private static void ClampOverlaps(List<VideoSegment> segments)
    {
        for (int i = 0; i < segments.Count - 1; i++)
        {
            var gap = (segments[i + 1].StartLocal - segments[i].StartLocal).TotalSeconds;
            if (gap > 0 && gap < segments[i].DurationSeconds)
                segments[i].DurationSeconds = gap;
        }
    }

    private static bool TryParseHourDir(Match m, out DateOnly date, out int hour)
    {
        date = default; hour = 0;
        var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var d = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        hour = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
        if (mo is < 1 or > 12 || d < 1 || hour > 23) return false;
        if (d > DateTime.DaysInMonth(y, mo)) return false;
        date = new DateOnly(y, mo, d);
        return true;
    }

    private static bool TryParseMinuteSecond(string fileName, out int minute, out int second)
    {
        minute = second = 0;
        var m = FileRx.Match(fileName);
        if (m.Success)
        {
            minute = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            second = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return minute < 60 && second < 60;
        }

        var alt = FileAltRx.Match(fileName);
        if (alt.Success)
        {
            minute = int.Parse(alt.Groups[1].Value, CultureInfo.InvariantCulture);
            second = int.Parse(alt.Groups[2].Value, CultureInfo.InvariantCulture);
            return minute < 60 && second < 60;
        }

        return false;
    }
}
