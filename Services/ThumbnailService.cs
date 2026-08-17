using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using XiaomiSMBViewer.Models;

namespace XiaomiSMBViewer.Services;

/// <summary>
/// Miniatury pod kursorem na timeline. Klatki sa kubelkowane co N sekund,
/// zeby ruch myszy nie generowal setek wywolan ffmpeg. Cache: pamiec + dysk.
/// </summary>
public sealed class ThumbnailService
{
    private const int BucketSeconds = 2;
    private const int Width = 320;

    private readonly string _diskCacheDir;
    private readonly ConcurrentDictionary<string, BitmapSource> _memory = new();
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inflight = new();
    private readonly SemaphoreSlim _gate = new(2);

    public ThumbnailService()
    {
        _diskCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaomiSMBViewer", "thumbs");
        Directory.CreateDirectory(_diskCacheDir);
    }

    public Task<BitmapSource?> GetAsync(VideoSegment segment, double offsetSeconds, CancellationToken ct)
    {
        if (!FfmpegTools.Available) return Task.FromResult<BitmapSource?>(null);

        double bucket = Math.Floor(offsetSeconds / BucketSeconds) * BucketSeconds;
        var key = Key(segment.Path, bucket);

        if (_memory.TryGetValue(key, out var cached)) return Task.FromResult<BitmapSource?>(cached);

        return _inflight.GetOrAdd(key, _ => LoadAsync(key, segment, bucket, ct));
    }

    private async Task<BitmapSource?> LoadAsync(string key, VideoSegment segment, double offset, CancellationToken ct)
    {
        try
        {
            var diskPath = Path.Combine(_diskCacheDir, key + ".jpg");
            byte[]? bytes = null;

            if (File.Exists(diskPath))
            {
                try { bytes = await File.ReadAllBytesAsync(diskPath, ct).ConfigureAwait(false); } catch { }
            }

            if (bytes == null || bytes.Length == 0)
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var args = $"-hide_banner -loglevel error -ss {offset.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                               $"-i \"{segment.Path}\" -frames:v 1 -vf scale={Width}:-2 -q:v 6 -f mjpeg -";
                    bytes = await FfmpegTools.RunCaptureBinaryAsync(FfmpegTools.FfmpegPath!, args, ct).ConfigureAwait(false);
                }
                finally { _gate.Release(); }

                if (bytes is { Length: > 0 })
                {
                    try { await File.WriteAllBytesAsync(diskPath, bytes, ct).ConfigureAwait(false); } catch { }
                }
            }

            if (bytes is not { Length: > 0 }) return null;

            var bmp = Decode(bytes);
            if (bmp != null) _memory[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string Key(string path, double bucket)
    {
        var raw = $"{path.ToLowerInvariant()}|{bucket:0}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }
}
