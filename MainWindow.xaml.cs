using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LibVLCSharp.Shared;
using XiaomiSMBViewer.Controls;
using XiaomiSMBViewer.Models;
using XiaomiSMBViewer.Playback;
using XiaomiSMBViewer.Services;

namespace XiaomiSMBViewer;

public partial class MainWindow : Window
{
    private static readonly Color[] PaneColors =
    [
        Color.FromRgb(0x4D, 0x9D, 0xFF),
        Color.FromRgb(0x4D, 0xD9, 0x9A),
        Color.FromRgb(0xFF, 0xB4, 0x4D),
        Color.FromRgb(0xC4, 0x8D, 0xFF),
    ];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly IndexCache _indexCache = new();
    private readonly ThumbnailService _thumbs = new();
    private readonly DurationProber _prober;
    private readonly List<CameraPane> _panes = [];

    private LibVLC? _libVlc;
    private DateOnly _currentDay;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _thumbCts;
    private bool _suppressCameraEvent;
    private bool _isFullscreen;
    private WindowState _preFullscreenState;
    private double _lastPosition;
    private double _lastResyncAt = double.MinValue;
    private readonly System.Diagnostics.Stopwatch _scrubThrottle = new();
    private bool _scrubWasPlaying;
    private bool _suppressLanguageEvent;
    private List<DateOnly> _days = [];
    private string _statusKey = "status.ready";
    private object?[] _statusArgs = [];
    private readonly List<ToggleButton> _speedButtons = [];

    public MainWindow()
    {
        InitializeComponent();
        _prober = new DurationProber(_indexCache);
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private CameraPane? Primary => _panes.FirstOrDefault(p => p.IsPrimary) ?? _panes.FirstOrDefault();

    // ================= start / koniec =================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _libVlc = new LibVLC(
            "--no-osd",
            "--no-video-title-show",
            "--no-snapshot-preview",
            "--quiet",
            "--no-stats",
            "--drop-late-frames",
            "--skip-frames");

        FfmpegTools.OverrideDirectory(_settings.FfmpegDirectory);

        Loc.Initialize();
        Loc.Use(_settings.Language);
        PopulateLanguageBox();
        ApplyLanguage();

        RootBox.Text = _settings.LibraryRoot;
        HwDecodeBox.IsChecked = _settings.HardwareDecoding;
        VolumeSlider.Value = _settings.Volume;
        MuteBox.IsChecked = _settings.Muted;

        BuildSpeedButtons();

        Timeline.SeekRequested += OnTimelineSeek;
        Timeline.ScrubPreview += OnScrubPreview;
        Timeline.ScrubStateChanged += OnScrubStateChanged;
        Timeline.HoverTimeChanged += OnTimelineHover;
        Timeline.SelectionChanged += UpdateSelectionLabel;

        await LoadCamerasAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _loadCts?.Cancel();
        _probeCts?.Cancel();
        _thumbCts?.Cancel();

        _settings.LibraryRoot = RootBox.Text.Trim();
        _settings.HardwareDecoding = HwDecodeBox.IsChecked == true;
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.Muted = MuteBox.IsChecked == true;
        _settings.LastCameraId = Primary?.CameraId;
        _settings.Language = (LanguageBox.SelectedItem as LanguageOption)?.Code ?? Loc.AutoCode;
        _settings.Save();

        DisposePanes();
        _libVlc?.Dispose();
    }

    // ================= biblioteka =================

    private async Task LoadCamerasAsync()
    {
        var root = RootBox.Text.Trim();
        Status("status.scanning", root);

        if (!Directory.Exists(root))
        {
            Status("status.nodir", root);
            CameraList.ItemsSource = null;
            DayList.ItemsSource = null;
            return;
        }

        try
        {
            var cameras = await LibraryScanner.GetCamerasAsync(root);
            _suppressCameraEvent = true;
            CameraList.ItemsSource = cameras;
            _suppressCameraEvent = false;

            if (cameras.Count == 0) { Status("status.nocameras"); return; }

            var preselect = cameras.Contains(_settings.LastCameraId ?? "") ? _settings.LastCameraId : cameras[0];
            CameraList.SelectedItem = preselect;
            Status("status.cameras", cameras.Count);
        }
        catch (Exception ex)
        {
            Status("status.scanerror", ex.Message);
        }
    }

    private async void CameraList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCameraEvent) return;

        var selected = CameraList.SelectedItems.Cast<string>().Take(PaneColors.Length).ToList();
        if (selected.Count == 0) return;

        RebuildPanes(selected);
        await LoadDaysAsync(selected[0]);
    }

    private async Task LoadDaysAsync(string cameraId)
    {
        var root = RootBox.Text.Trim();
        Status("status.readingdays", cameraId);

        try
        {
            _days = await LibraryScanner.GetDaysAsync(root, cameraId);
            DayList.ItemsSource = _days.Select(d => new DayItem(d)).ToList();
            if (_days.Count > 0) DayList.SelectedIndex = 0;
            Status("status.days", cameraId, _days.Count);
        }
        catch (Exception ex)
        {
            Status("status.scanerror", ex.Message);
        }
    }

    private async void DayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DayList.SelectedItem is not DayItem item) return;
        _currentDay = item.Date;
        await LoadDayAsync(item.Date);
    }

    private async Task LoadDayAsync(DateOnly day)
    {
        _loadCts?.Cancel();
        _probeCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        var root = RootBox.Text.Trim();
        DateLabel.Text = day.ToString("yyyy-MM-dd (ddd)", Loc.Culture);
        Timeline.ClearSelection();
        Status("status.loadingday", day.ToString("yyyy-MM-dd"));

        try
        {
            foreach (var pane in _panes)
            {
                var index = await LibraryScanner.ScanDayAsync(root, pane.CameraId, day, ct);
                _indexCache.Apply(index);
                pane.SetIndex(index);
            }

            RefreshTimelineRows();

            var primary = Primary;
            if (primary is { Index.IsEmpty: false })
            {
                Timeline.ResetZoom();
                SeekAll(primary.Index.FirstSecond, autoPlay: false);
                Status("status.dayloaded", day.ToString("yyyy-MM-dd"), primary.Index.Segments.Count,
                    TimeSpan.FromSeconds(primary.Index.RecordedSeconds).ToString(@"hh\:mm\:ss"));
            }
            else
            {
                Status("status.dayempty", day.ToString("yyyy-MM-dd"));
            }

            if (ProbeBox.IsChecked == true && FfmpegTools.Available)
                StartProbing();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status("status.loaderror", ex.Message);
        }
    }

    private void StartProbing()
    {
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        var panes = _panes.ToList();

        BusyBar.Visibility = Visibility.Visible;
        BusyBar.Value = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var pane in panes)
                {
                    var index = pane.Index;
                    var progress = new Progress<double>(v =>
                        Dispatcher.BeginInvoke(() => BusyBar.Value = v));

                    await _prober.ProbeAsync(index, progress,
                        () => Dispatcher.BeginInvoke(RefreshTimelineRows), ct);
                }

                await Dispatcher.BeginInvoke(() =>
                {
                    BusyBar.Visibility = Visibility.Collapsed;
                    RefreshTimelineRows();
                    Status("status.durations");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(() =>
                {
                    BusyBar.Visibility = Visibility.Collapsed;
                    Status("status.loaderror", ex.Message);
                });
            }
        }, ct);
    }

    // ================= kafelki kamer =================

    private void RebuildPanes(List<string> cameraIds)
    {
        DisposePanes();
        if (_libVlc == null) return;

        bool hw = HwDecodeBox.IsChecked == true;
        var grid = new UniformGrid { Columns = cameraIds.Count <= 1 ? 1 : 2 };

        for (int i = 0; i < cameraIds.Count; i++)
        {
            var pane = new CameraPane(cameraIds[i], _libVlc, hw, PaneColors[i % PaneColors.Length])
            {
                IsPrimary = i == 0,
            };

            pane.Engine.Volume = (int)VolumeSlider.Value;
            pane.Engine.Muted = MuteBox.IsChecked == true || i > 0;

            if (pane.IsPrimary)
            {
                pane.Engine.PositionChanged += OnEnginePosition;
                pane.Engine.PlayStateChanged += OnEnginePlayState;
                pane.Engine.ReachedEndOfDay += OnReachedEnd;
            }

            _panes.Add(pane);
            grid.Children.Add(pane.Root);
        }

        VideoHost.Children.Clear();
        VideoHost.Children.Add(grid);
    }

    private void DisposePanes()
    {
        foreach (var pane in _panes)
        {
            if (pane.IsPrimary)
            {
                pane.Engine.PositionChanged -= OnEnginePosition;
                pane.Engine.PlayStateChanged -= OnEnginePlayState;
                pane.Engine.ReachedEndOfDay -= OnReachedEnd;
            }
            pane.Dispose();
        }
        _panes.Clear();
        VideoHost.Children.Clear();
    }

    private void RefreshTimelineRows()
    {
        var rows = _panes.Select(p => new TimelineRow
        {
            Label = p.CameraId,
            Blocks = p.Index.Blocks(),
            Fill = new SolidColorBrush(p.Accent),
            IsActive = p.IsPrimary,
        }).ToList();

        foreach (var r in rows) r.Fill.Freeze();
        Timeline.SetRows(rows);
    }

    // ================= odtwarzanie =================

    private void SeekAll(double daySecond, bool autoPlay)
    {
        foreach (var pane in _panes)
            pane.Engine.SeekToDaySecond(daySecond, autoPlay);

        _lastPosition = daySecond;
        _lastResyncAt = daySecond;
        Timeline.Position = daySecond;
        TimeLabel.Text = FormatClock(daySecond);
    }

    private void OnTimelineSeek(double t)
    {
        bool resume = _scrubWasPlaying || (Primary?.Engine.IsPlaying ?? false);
        _scrubWasPlaying = false;
        SeekAll(t, resume);
    }

    /// <summary>
    /// Przewijanie przytrzymana myszka: glowica idzie za kursorem bez opoznienia,
    /// a realne otwarcie pliku jest ograniczone do ~5 na sekunde (SMB + reopen dekodera).
    /// </summary>
    private void OnScrubPreview(double t)
    {
        _lastPosition = t;
        TimeLabel.Text = FormatClock(t);

        if (_scrubThrottle.ElapsedMilliseconds < 200) return;

        _scrubThrottle.Restart();
        foreach (var pane in _panes)
            pane.Engine.SeekToDaySecond(t, autoPlay: false);
    }

    private void OnScrubStateChanged(bool scrubbing)
    {
        if (scrubbing)
        {
            _scrubWasPlaying = Primary?.Engine.IsPlaying ?? false;
            foreach (var pane in _panes) pane.Engine.Pause();
            _scrubThrottle.Restart();
        }
        else
        {
            _scrubThrottle.Reset();
        }
    }

    private void OnEnginePosition(double daySecond)
    {
        if (Timeline.IsScrubbing) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (Timeline.IsScrubbing) return;
            _lastPosition = daySecond;
            Timeline.Position = daySecond;
            TimeLabel.Text = FormatClock(daySecond);

            // Kamery poboczne dryfują (osobne pliki, osobne dekodery) — okresowa korekta.
            if (_panes.Count > 1 && daySecond - _lastResyncAt > 10)
            {
                _lastResyncAt = daySecond;
                foreach (var pane in _panes.Where(p => !p.IsPrimary))
                {
                    if (Math.Abs(pane.Engine.CurrentDaySecond - daySecond) > 1.5)
                        pane.Engine.SeekToDaySecond(daySecond, pane.Engine.IsPlaying);
                }
            }
        });
    }

    private void OnEnginePlayState(bool playing) =>
        Dispatcher.BeginInvoke(() => PlayButton.Content = playing ? "❚❚" : "▶");

    private void OnReachedEnd() =>
        Dispatcher.BeginInvoke(() => Status("status.endofday"));

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        var primary = Primary;
        if (primary == null) return;

        bool willPlay = !primary.Engine.IsPlaying;
        foreach (var pane in _panes)
        {
            if (willPlay) pane.Engine.Play();
            else pane.Engine.Pause();
        }
        PlayButton.Content = willPlay ? "❚❚" : "▶";
    }

    private void PrevSegment_Click(object sender, RoutedEventArgs e) => StepSegment(-1);
    private void NextSegment_Click(object sender, RoutedEventArgs e) => StepSegment(+1);

    private void StepSegment(int delta)
    {
        var primary = Primary;
        if (primary == null) return;

        primary.Engine.StepSegment(delta);
        var seg = primary.Engine.CurrentSegment;
        if (seg == null) return;

        foreach (var pane in _panes.Where(p => !p.IsPrimary))
            pane.Engine.SeekToDaySecond(seg.StartOfDay, pane.Engine.IsPlaying);
    }

    private void Nudge(double seconds)
    {
        var primary = Primary;
        if (primary == null || primary.Index.IsEmpty) return;

        double target = Math.Clamp(_lastPosition + seconds, 0, 86400);

        // Do tyłu w luce: wskakujemy na koniec poprzedniego nagrania, nie na jego początek.
        if (seconds < 0 && primary.Index.SegmentAt(target) == null)
        {
            var prev = primary.Index.SegmentBefore(target);
            if (prev != null) target = Math.Max(prev.StartOfDay, prev.EndOfDay - 0.5);
        }

        SeekAll(target, primary.Engine.IsPlaying);
    }

    private void BuildSpeedButtons()
    {
        double[] rates = [0.5, 1, 2, 4, 8, 16];
        foreach (var rate in rates)
        {
            var btn = new ToggleButton
            {
                Content = rate == Math.Floor(rate) ? $"{rate:0}×" : $"{rate:0.0}×",
                Style = (Style)FindResource("ToggleBtn"),
                Margin = new Thickness(3, 0, 0, 0),
                MinWidth = 40,
                IsChecked = Math.Abs(rate - 1) < 0.01,
                Tag = rate,
            };
            btn.Click += (_, _) => SetRate(rate);
            _speedButtons.Add(btn);
            SpeedPanel.Children.Add(btn);
        }
    }

    private void SetRate(double rate)
    {
        foreach (var b in _speedButtons)
            b.IsChecked = Math.Abs((double)b.Tag! - rate) < 0.01;

        foreach (var pane in _panes)
            pane.Engine.Rate = (float)rate;

        Status("status.rate", rate);
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        foreach (var pane in _panes.Where(p => p.IsPrimary))
            pane.Engine.Volume = (int)e.NewValue;
    }

    private void Mute_Changed(object sender, RoutedEventArgs e)
    {
        bool muted = MuteBox.IsChecked == true;
        foreach (var pane in _panes)
            pane.Engine.Muted = muted || !pane.IsPrimary;
    }

    // Zdarzenia z IsChecked ustawionego w XAML potrafia odpalic sie w trakcie
    // parsowania, zanim pola kontrolek zostana przypisane - stad straznik na null.
    private void Follow_Changed(object sender, RoutedEventArgs e)
    {
        if (Timeline == null) return;
        Timeline.FollowPlayhead = FollowBox.IsChecked == true;
    }

    private async void HwDecode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _libVlc == null) return;

        var selected = CameraList.SelectedItems.Cast<string>().Take(PaneColors.Length).ToList();
        if (selected.Count == 0) return;

        RebuildPanes(selected);
        await LoadDayAsync(_currentDay);
    }

    // ================= miniatury =================

    private void OnTimelineHover(double? time)
    {
        if (ThumbBox.IsChecked != true || time == null || Primary == null)
        {
            ThumbPopup.IsOpen = false;
            return;
        }

        var index = Primary.Index;
        var segment = index.SegmentAt(time.Value);
        if (segment == null)
        {
            ThumbPopup.IsOpen = false;
            return;
        }

        ThumbTime.Text = FormatClock(time.Value);

        const double popupWidth = 344;   // 320 obrazu + ramka + margines
        const double popupHeight = 246;
        double x = Timeline.TimeToX(time.Value);
        ThumbPopup.HorizontalOffset = Math.Clamp(x - popupWidth / 2, -6, Math.Max(0, Timeline.ActualWidth - popupWidth + 6));
        ThumbPopup.VerticalOffset = -popupHeight;
        ThumbPopup.IsOpen = true;

        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;
        double offset = time.Value - segment.StartOfDay;

        _ = Task.Run(async () =>
        {
            await Task.Delay(90, ct).ConfigureAwait(false);   // debounce ruchu myszy
            var bmp = await _thumbs.GetAsync(segment, offset, ct).ConfigureAwait(false);
            if (bmp == null || ct.IsCancellationRequested) return;
            await Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) ThumbImage.Source = bmp; });
        }, ct);
    }

    // ================= zaznaczenie i eksport =================

    private void SelectionStart_Click(object sender, RoutedEventArgs e)
    {
        var end = Timeline.Selection?.End;
        Timeline.SetSelection(_lastPosition, end is { } v && v > _lastPosition ? v : _lastPosition + 60);
    }

    private void SelectionEnd_Click(object sender, RoutedEventArgs e)
    {
        var start = Timeline.Selection?.Start;
        Timeline.SetSelection(start is { } v && v < _lastPosition ? v : Math.Max(0, _lastPosition - 60), _lastPosition);
    }

    private void UpdateSelectionLabel()
    {
        if (Timeline.Selection is not { } sel)
        {
            SelLabel.Text = Loc.T("lbl.noselection");
            return;
        }
        SelLabel.Text = $"{FormatClock(sel.Start)} → {FormatClock(sel.End)}  " +
                        $"({TimeSpan.FromSeconds(sel.End - sel.Start):mm\\:ss})";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var primary = Primary;
        if (primary == null || primary.Index.IsEmpty) return;

        if (Timeline.Selection is not { } sel)
        {
            MessageBox.Show(Loc.T("export.needselection"), Loc.T("export.title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!FfmpegTools.Available)
        {
            MessageBox.Show(Loc.T("export.needffmpeg"), Loc.T("export.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MP4|*.mp4",
            FileName = $"{primary.CameraId}_{_currentDay:yyyyMMdd}_" +
                       $"{TimeSpan.FromSeconds(sel.Start):hhmmss}-{TimeSpan.FromSeconds(sel.End):hhmmss}.mp4",
        };
        if (dlg.ShowDialog() != true) return;

        bool reencode = MessageBox.Show(
            Loc.T("export.reencode"), Loc.T("export.title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        Status("status.exporting");
        BusyBar.Visibility = Visibility.Visible;
        BusyBar.IsIndeterminate = true;

        try
        {
            await ExportService.ExportRangeAsync(primary.Index, sel.Start, sel.End, dlg.FileName, reencode, null, default);
            Status("status.saved", dlg.FileName);
        }
        catch (Exception ex)
        {
            Status("status.exportfailed");
            MessageBox.Show(ex.Message, Loc.T("export.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BusyBar.IsIndeterminate = false;
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    // ================= katalog / okno / klawisze =================

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("dlg.choosefolder") };
        if (Directory.Exists(RootBox.Text)) dlg.InitialDirectory = RootBox.Text;
        if (dlg.ShowDialog() != true) return;

        RootBox.Text = dlg.FolderName;
        await LoadCamerasAsync();
    }

    private async void ReloadRoot_Click(object sender, RoutedEventArgs e) => await LoadCamerasAsync();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _preFullscreenState = WindowState;
            Sidebar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _preFullscreenState;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Nudge(shift ? -60 : -5); break;
            case Key.Right: Nudge(shift ? 60 : 5); break;
            case Key.Up: Nudge(300); break;
            case Key.Down: Nudge(-300); break;
            case Key.OemComma: StepSegment(-1); break;
            case Key.OemPeriod: StepSegment(+1); break;
            case Key.Home: if (Primary is { Index.IsEmpty: false } p1) SeekAll(p1.Index.FirstSecond, false); break;
            case Key.End: if (Primary is { Index.IsEmpty: false } p2) SeekAll(Math.Max(0, p2.Index.LastSecond - 5), false); break;
            case Key.S: SelectionStart_Click(this, new RoutedEventArgs()); break;
            case Key.E: SelectionEnd_Click(this, new RoutedEventArgs()); break;
            case Key.D0: Timeline.ResetZoom(); break;
            case Key.D1: SetRate(1); break;
            case Key.D2: SetRate(2); break;
            case Key.D3: SetRate(4); break;
            case Key.D4: SetRate(8); break;
            case Key.D5: SetRate(16); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape: if (_isFullscreen) ToggleFullscreen(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>
    /// Zapamietuje klucz i argumenty, zeby po zmianie jezyka przerysowac tresc paska stanu.
    /// </summary>
    private void Status(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusLabel.Text = Loc.T(key, args);
    }

    private static string FormatClock(double daySeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Clamp(daySeconds, 0, 86399.9));
        return $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    // ================= jezyk =================

    private void PopulateLanguageBox()
    {
        _suppressLanguageEvent = true;
        var options = Loc.AvailableLanguages();
        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem =
            options.FirstOrDefault(o => o.Code.Equals(_settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? options[0];
        _suppressLanguageEvent = false;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageEvent || LanguageBox.SelectedItem is not LanguageOption option) return;

        Loc.Use(option.Code);
        _settings.Language = option.Code;
        ApplyLanguage();
    }

    /// <summary>Przepisuje wszystkie napisy w UI. Wolane przy starcie i po zmianie jezyka.</summary>
    private void ApplyLanguage()
    {
        FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        FolderCaption.Text = Loc.T("sidebar.folder");
        CamerasCaption.Text = Loc.T("sidebar.cameras");
        DaysCaption.Text = Loc.T("sidebar.days");
        LanguageCaption.Text = Loc.T("lang.label");

        HwDecodeBox.Content = Loc.T("opt.hw");
        ProbeBox.Content = Loc.T("opt.probe");
        ThumbBox.Content = Loc.T("opt.thumbs");

        FfmpegStatus.Text = Loc.T(FfmpegTools.Available ? "ffmpeg.ok" : "ffmpeg.missing");

        BrowseBtn.ToolTip = Loc.T("tt.browse");
        RescanBtn.ToolTip = Loc.T("tt.rescan");
        PrevBtn.ToolTip = Loc.T("tt.prev");
        PlayButton.ToolTip = Loc.T("tt.playpause");
        NextBtn.ToolTip = Loc.T("tt.next");
        SelStartBtn.ToolTip = Loc.T("tt.selstart");
        SelEndBtn.ToolTip = Loc.T("tt.selend");
        FullscreenBtn.ToolTip = Loc.T("tt.fullscreen");
        FollowBox.ToolTip = Loc.T("tt.follow");

        ExportBtn.Content = Loc.T("btn.export");
        MuteBox.Content = Loc.T("lbl.mute");
        FollowBox.Content = Loc.T("lbl.follow");

        UpdateSelectionLabel();
        StatusLabel.Text = Loc.T(_statusKey, _statusArgs);

        // Lista jezykow zawiera pozycje "automatycznie", ktora sama jest tlumaczona.
        var selected = LanguageBox.SelectedItem as LanguageOption;
        _suppressLanguageEvent = true;
        var options = Loc.AvailableLanguages();
        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem = options.FirstOrDefault(o => o.Code == selected?.Code) ?? options[0];
        _suppressLanguageEvent = false;

        // Nazwy dni tygodnia zaleza od jezyka - odswiezamy liste dni i etykiete daty.
        if (_days.Count > 0)
        {
            var keep = DayList.SelectedIndex;
            DayList.ItemsSource = _days.Select(d => new DayItem(d)).ToList();
            DayList.SelectedIndex = keep;
        }
        if (_currentDay != default)
            DateLabel.Text = _currentDay.ToString("yyyy-MM-dd (ddd)", Loc.Culture);

        foreach (var pane in _panes) pane.RefreshLabel();
    }

    private sealed record DayItem(DateOnly Date)
    {
        public override string ToString() =>
            Date.ToString("yyyy-MM-dd (ddd)", Loc.Culture);
    }
}
