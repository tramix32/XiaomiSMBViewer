using System.Globalization;
using LibVLCSharp.Shared;
using XiaomiSMBViewer.Models;

namespace XiaomiSMBViewer.Playback;

/// <summary>
/// Wirtualna oś czasu jednego dnia zbudowana z minutowych plików.
/// Na zewnatrz aplikacja operuje wylacznie na "sekundzie dnia" (0..86400),
/// engine sam dobiera plik i offset, i przeskakuje przez luki w nagraniach.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly System.Timers.Timer _ticker;
    private readonly object _sync = new();

    private DayIndex? _index;
    private VideoSegment? _current;
    private Media? _currentMedia;
    private double _pendingDaySecond;
    private bool _hasPending;
    private float _rate = 1.0f;
    private int _generation;
    private bool _disposed;

    /// <summary>Sekunda dnia (0..86400) - podnoszone ~10x/s.</summary>
    public event Action<double>? PositionChanged;

    /// <summary>Odtworzono ostatni segment dnia.</summary>
    public event Action? ReachedEndOfDay;

    /// <summary>Zmienil sie aktualnie odtwarzany plik.</summary>
    public event Action<VideoSegment?>? SegmentChanged;

    public event Action<bool>? PlayStateChanged;

    public PlaybackEngine(LibVLC libVlc, bool hardwareDecoding)
    {
        _libVlc = libVlc;
        _player = new MediaPlayer(_libVlc)
        {
            EnableHardwareDecoding = hardwareDecoding,
            EnableMouseInput = false,
            EnableKeyInput = false,
        };

        _player.EndReached += OnEndReached;
        _player.EncounteredError += OnEncounteredError;
        _player.Playing += (_, _) => PlayStateChanged?.Invoke(true);
        _player.Paused += (_, _) => PlayStateChanged?.Invoke(false);
        _player.Stopped += (_, _) => PlayStateChanged?.Invoke(false);

        _ticker = new System.Timers.Timer(100) { AutoReset = true };
        _ticker.Elapsed += (_, _) => Tick();
        _ticker.Start();
    }

    public MediaPlayer Player => _player;

    public VideoSegment? CurrentSegment => _current;

    public bool IsPlaying => _player.IsPlaying;

    public double CurrentDaySecond
    {
        get
        {
            var seg = _current;
            if (seg == null) return _hasPending ? _pendingDaySecond : 0;
            return seg.StartOfDay + Math.Max(0, _player.Time) / 1000.0;
        }
    }

    public float Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, 0.25f, 16f);
            _player.SetRate(_rate);
        }
    }

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 100);
    }

    public bool Muted
    {
        get => _player.Mute;
        set => _player.Mute = value;
    }

    public void SetIndex(DayIndex? index)
    {
        lock (_sync)
        {
            _index = index;
            _generation++;
        }
        StopInternal();
    }

    /// <summary>
    /// Ustawia glowice na dana sekunde dnia. Jesli trafiamy w luke,
    /// przeskakujemy do poczatku najblizszego pozniejszego nagrania.
    /// </summary>
    public void SeekToDaySecond(double daySecond, bool autoPlay = true)
    {
        DayIndex? index;
        lock (_sync) index = _index;
        if (index == null || index.IsEmpty) return;

        daySecond = Math.Clamp(daySecond, 0, 86400);

        var segment = index.SegmentAt(daySecond);
        double offset;

        if (segment != null)
        {
            offset = daySecond - segment.StartOfDay;
        }
        else
        {
            segment = index.SegmentAtOrAfter(daySecond);
            if (segment == null)
            {
                // Za ostatnim nagraniem dnia - zatrzymujemy sie na jego koncu.
                ReachedEndOfDay?.Invoke();
                return;
            }
            offset = 0;
        }

        // Drobny skok w obrebie aktualnie otwartego pliku - taniej niz reopen.
        if (ReferenceEquals(segment, _current) && _player.IsSeekable && _player.Media != null)
        {
            _player.Time = (long)(offset * 1000);
            if (autoPlay && !_player.IsPlaying) _player.Play();
            PositionChanged?.Invoke(segment.StartOfDay + offset);
            return;
        }

        OpenSegment(segment, offset, autoPlay);
    }

    public void TogglePlayPause()
    {
        if (_current == null)
        {
            DayIndex? index;
            lock (_sync) index = _index;
            if (index is { IsEmpty: false }) SeekToDaySecond(index.FirstSecond);
            return;
        }

        if (_player.IsPlaying) _player.SetPause(true);
        else _player.Play();
    }

    public void Pause()
    {
        if (_player.IsPlaying) _player.SetPause(true);
    }

    public void Play()
    {
        if (_current == null)
        {
            DayIndex? index;
            lock (_sync) index = _index;
            if (index is { IsEmpty: false }) SeekToDaySecond(index.FirstSecond);
            return;
        }
        _player.Play();
    }

    /// <summary>Nastepny/poprzedni plik minutowy (przeskakuje luki).</summary>
    public void StepSegment(int delta)
    {
        DayIndex? index;
        lock (_sync) index = _index;
        if (index is null or { IsEmpty: true }) return;

        if (_current == null)
        {
            SeekToDaySecond(index.FirstSecond);
            return;
        }

        int i = index.IndexOf(_current) + delta;
        if (i < 0 || i >= index.Segments.Count) return;
        OpenSegment(index.Segments[i], 0, _player.IsPlaying);
    }

    public void StepFrame() => _player.NextFrame();

    public void Stop() => StopInternal();

    private void OpenSegment(VideoSegment segment, double offset, bool autoPlay)
    {
        int gen;
        lock (_sync) gen = ++_generation;

        var media = new Media(_libVlc, segment.Path, FromType.FromPath);
        if (offset > 0.05)
            media.AddOption(":start-time=" + offset.ToString("0.###", CultureInfo.InvariantCulture));
        media.AddOption(":file-caching=1200");
        media.AddOption(":avcodec-skiploopfilter=0");

        lock (_sync)
        {
            if (gen != _generation)
            {
                media.Dispose();
                return;
            }

            var old = _currentMedia;
            _currentMedia = media;
            _current = segment;
            _pendingDaySecond = segment.StartOfDay + offset;
            _hasPending = true;
            old?.Dispose();
        }

        _player.Play(media);
        _player.SetRate(_rate);

        SegmentChanged?.Invoke(segment);
        PositionChanged?.Invoke(segment.StartOfDay + offset);

        if (!autoPlay)
        {
            // Pauza musi poczekac, az VLC faktycznie otworzy plik - inaczej zostaje zignorowana.
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(25).ConfigureAwait(false);
                    lock (_sync) if (gen != _generation) return;
                    if (_player.IsPlaying) { _player.SetPause(true); return; }
                }
            });
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Zabronione jest wolanie API playera z watku eventowego libVLC.
        _ = Task.Run(() =>
        {
            DayIndex? index;
            VideoSegment? current;
            lock (_sync) { index = _index; current = _current; }
            if (index == null || current == null) return;

            var next = index.Next(current);
            if (next == null)
            {
                ReachedEndOfDay?.Invoke();
                PlayStateChanged?.Invoke(false);
                return;
            }
            OpenSegment(next, 0, autoPlay: true);
        });
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        _ = Task.Run(() =>
        {
            DayIndex? index;
            VideoSegment? current;
            lock (_sync) { index = _index; current = _current; }
            if (index == null || current == null) return;

            // Uszkodzony/niedostepny plik: probujemy isc dalej zamiast zatrzymywac odtwarzanie.
            var next = index.Next(current);
            if (next != null) OpenSegment(next, 0, autoPlay: true);
        });
    }

    private void Tick()
    {
        if (_disposed) return;
        var seg = _current;
        if (seg == null) return;
        if (!_player.IsPlaying && !_hasPending) return;

        _hasPending = false;
        PositionChanged?.Invoke(seg.StartOfDay + Math.Max(0, _player.Time) / 1000.0);
    }

    private void StopInternal()
    {
        lock (_sync) _generation++;
        try { _player.Stop(); } catch { }

        lock (_sync)
        {
            _currentMedia?.Dispose();
            _currentMedia = null;
            _current = null;
        }
        SegmentChanged?.Invoke(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ticker.Stop();
        _ticker.Dispose();

        _player.EndReached -= OnEndReached;
        _player.EncounteredError -= OnEncounteredError;

        try { _player.Stop(); } catch { }
        _currentMedia?.Dispose();
        _player.Dispose();
    }
}
