using System.Diagnostics;
using System.IO;
using System.Text;

namespace XiaomiSMBViewer.Services;

/// <summary>Lokalizacja i uruchamianie ffmpeg/ffprobe (miniatury + eksport).</summary>
public static class FfmpegTools
{
    private static string? _ffmpeg;
    private static string? _ffprobe;

    public static string? FfmpegPath => _ffmpeg ??= Locate("ffmpeg.exe");
    public static string? FfprobePath => _ffprobe ??= Locate("ffprobe.exe");

    public static bool Available => FfmpegPath != null && FfprobePath != null;

    public static void OverrideDirectory(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var ffmpeg = Path.Combine(dir, "ffmpeg.exe");
        var ffprobe = Path.Combine(dir, "ffprobe.exe");
        if (File.Exists(ffmpeg)) _ffmpeg = ffmpeg;
        if (File.Exists(ffprobe)) _ffprobe = ffprobe;
    }

    private static string? Locate(string exe)
    {
        var local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* nieprawidlowy wpis w PATH */ }
        }
        return null;
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string exePath, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return (proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>Surowe bajty na stdout (miniatura jako JPEG).</summary>
    public static async Task<byte[]?> RunCaptureBinaryAsync(string exePath, string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var drainErr = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await Task.WhenAll(copy, drainErr).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }

        return ms.Length > 0 ? ms.ToArray() : null;
    }
}
