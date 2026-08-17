using System.Globalization;
using XiaomiSMBViewer.Models;

namespace XiaomiSMBViewer.Services;

/// <summary>
/// Uscisla dlugosci segmentow ffprobe'em. Odpala sie w tle po zaladowaniu dnia,
/// rownolegle (SMB lubi kilka strumieni naraz), z raportowaniem postepu.
/// </summary>
public sealed class DurationProber(IndexCache cache)
{
    private readonly IndexCache _cache = cache;

    public async Task ProbeAsync(
        DayIndex index,
        IProgress<double>? progress,
        Action? onBatchApplied,
        CancellationToken ct)
    {
        if (!FfmpegTools.Available) return;

        var pending = index.Segments.Where(s => !s.DurationProbed).ToList();
        if (pending.Count == 0) return;

        int done = 0;
        int sinceSave = 0;
        var gate = new SemaphoreSlim(6);

        var tasks = pending.Select(async segment =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var dur = await ProbeOneAsync(segment.Path, ct).ConfigureAwait(false);
                if (dur is > 0)
                {
                    segment.DurationSeconds = dur.Value;
                    segment.DurationProbed = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* pojedynczy plik moze byc uszkodzony - zostaje przy 60 s */ }
            finally
            {
                gate.Release();
                int n = Interlocked.Increment(ref done);
                progress?.Report((double)n / pending.Count);

                if (Interlocked.Increment(ref sinceSave) >= 50)
                {
                    Interlocked.Exchange(ref sinceSave, 0);
                    onBatchApplied?.Invoke();
                }
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cache.Save(index.CameraId, index.Date, index.Segments);
            throw;
        }

        _cache.Save(index.CameraId, index.Date, index.Segments);
        onBatchApplied?.Invoke();
    }

    private static async Task<double?> ProbeOneAsync(string path, CancellationToken ct)
    {
        var args = $"-v error -select_streams v:0 -show_entries format=duration " +
                   $"-of default=noprint_wrappers=1:nokey=1 \"{path}\"";
        var (code, stdout, _) = await FfmpegTools.RunAsync(FfmpegTools.FfprobePath!, args, ct).ConfigureAwait(false);
        if (code != 0) return null;

        var text = stdout.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && d < 3600)
            return d;
        return null;
    }
}
