using System.Globalization;
using System.IO;
using System.Text;
using XiaomiSMBViewer.Models;

namespace XiaomiSMBViewer.Services;

/// <summary>
/// Sklejenie zaznaczonego zakresu w jeden plik MP4 przez demuxer concat.
/// Domyslnie bez rekompresji (-c copy) - szybko, ale krawedzie ciecia
/// trafiaja w najblizsza klatke kluczowa.
/// </summary>
public static class ExportService
{
    public static async Task<string> ExportRangeAsync(
        DayIndex index,
        double fromDaySeconds,
        double toDaySeconds,
        string outputPath,
        bool reencode,
        IProgress<string>? log,
        CancellationToken ct)
    {
        if (!FfmpegTools.Available)
            throw new InvalidOperationException("Nie znaleziono ffmpeg.exe w PATH ani obok aplikacji.");

        var parts = index.SegmentsInRange(fromDaySeconds, toDaySeconds).ToList();
        if (parts.Count == 0)
            throw new InvalidOperationException("W zaznaczonym zakresie nie ma zadnych nagran.");

        var listPath = Path.Combine(Path.GetTempPath(), $"xsv_concat_{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();

        foreach (var seg in parts)
        {
            double inPoint = Math.Max(0, fromDaySeconds - seg.StartOfDay);
            double outPoint = Math.Min(seg.DurationSeconds, toDaySeconds - seg.StartOfDay);
            if (outPoint - inPoint <= 0.05) continue;

            sb.Append("file '").Append(seg.Path.Replace("'", @"'\''")).Append("'\n");
            if (inPoint > 0.01) sb.Append("inpoint ").Append(Fmt(inPoint)).Append('\n');
            if (outPoint < seg.DurationSeconds - 0.01) sb.Append("outpoint ").Append(Fmt(outPoint)).Append('\n');
        }

        await File.WriteAllTextAsync(listPath, sb.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);

        try
        {
            var codecArgs = reencode
                ? "-c:v libx264 -preset veryfast -crf 22 -c:a aac -b:a 96k"
                : "-c copy";

            var args = $"-hide_banner -loglevel error -y -f concat -safe 0 -i \"{listPath}\" " +
                       $"-fflags +genpts {codecArgs} \"{outputPath}\"";

            log?.Report($"ffmpeg {args}");
            var (code, _, stderr) = await FfmpegTools.RunAsync(FfmpegTools.FfmpegPath!, args, ct).ConfigureAwait(false);

            if (code != 0)
                throw new InvalidOperationException($"ffmpeg zakonczyl sie kodem {code}:\n{stderr}");

            return outputPath;
        }
        finally
        {
            try { File.Delete(listPath); } catch { }
        }
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
