using System.IO;
using System.Text.Json;
using XiaomiSMBViewer.Models;

namespace XiaomiSMBViewer.Services;

/// <summary>
/// Cache realnych dlugosci segmentow, zeby nie odpytywac SMB ffprobe'em przy kazdym otwarciu dnia.
/// Klucz: nazwa pliku (unikalna w obrebie dnia), wartosc: dlugosc w sekundach.
/// </summary>
public sealed class IndexCache
{
    private readonly string _root;

    public IndexCache()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaomiSMBViewer", "index");
        Directory.CreateDirectory(_root);
    }

    private string FileFor(string cameraId, DateOnly day)
    {
        var dir = Path.Combine(_root, Sanitize(cameraId));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{day:yyyyMMdd}.json");
    }

    public Dictionary<string, double> Load(string cameraId, DateOnly day)
    {
        try
        {
            var path = FileFor(cameraId, day);
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void Save(string cameraId, DateOnly day, IReadOnlyList<VideoSegment> segments)
    {
        try
        {
            var map = new Dictionary<string, double>(segments.Count);
            foreach (var s in segments)
                if (s.DurationProbed) map[s.FileName] = Math.Round(s.DurationSeconds, 3);

            if (map.Count == 0) return;
            File.WriteAllText(FileFor(cameraId, day), JsonSerializer.Serialize(map));
        }
        catch
        {
            // cache jest opcjonalny - brak zapisu nie moze wywrocic aplikacji
        }
    }

    /// <summary>Nanosi zapamietane dlugosci na swiezo zeskanowany indeks. Zwraca liczbe trafien.</summary>
    public int Apply(DayIndex index)
    {
        var map = Load(index.CameraId, index.Date);
        if (map.Count == 0) return 0;

        int hits = 0;
        foreach (var s in index.Segments)
        {
            if (map.TryGetValue(s.FileName, out var dur) && dur > 0)
            {
                s.DurationSeconds = dur;
                s.DurationProbed = true;
                hits++;
            }
        }
        return hits;
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
