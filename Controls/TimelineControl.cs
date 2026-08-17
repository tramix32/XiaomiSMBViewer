using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace XiaomiSMBViewer.Controls;

public sealed class TimelineRow
{
    public required string Label { get; init; }
    public required IReadOnlyList<(double Start, double End)> Blocks { get; init; }
    public Brush Fill { get; init; } = Brushes.SteelBlue;
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Oś czasu calego dnia (0..24h) z zaznaczonymi blokami nagran, zoomem,
/// przewijaniem, glowica i zakresem do eksportu.
///
/// Sterowanie: LPM = przewijanie, Shift+LPM = zaznaczanie zakresu,
/// PPM/srodkowy = przesuwanie widoku, kolko = zoom, dwuklik = pelna doba.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double DaySeconds = 86400;
    private const double MinSpan = 20;          // maksymalny zoom: 20 s w oknie
    private const double RulerHeight = 22;
    private const double RowGap = 4;

    private double _viewStart;
    private double _viewEnd = DaySeconds;
    private double _position;
    private double? _hoverTime;
    private double? _selStart, _selEnd;

    private bool _scrubbing, _selecting, _panning;
    private Point _dragOrigin;
    private double _panStartView;

    private readonly Typeface _typeface = new("Segoe UI");
    private readonly Pen _gridPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x40)), 1);
    private readonly Pen _hourPen = new(new SolidColorBrush(Color.FromRgb(0x4a, 0x51, 0x5c)), 1);
    private readonly Pen _playheadPen = new(new SolidColorBrush(Color.FromRgb(0xff, 0x5c, 0x4d)), 1.6);
    private readonly Pen _hoverPen = new(new SolidColorBrush(Color.FromRgb(0x9a, 0xa4, 0xb2)), 1) { DashStyle = DashStyles.Dash };
    private readonly Brush _trackBrush = new SolidColorBrush(Color.FromRgb(0x1b, 0x1f, 0x25));
    private readonly Brush _selBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x4d, 0x9d, 0xff));
    private readonly Brush _labelBrush = new SolidColorBrush(Color.FromRgb(0x8b, 0x94, 0xa3));
    private readonly Brush _rowLabelBrush = new SolidColorBrush(Color.FromRgb(0xc8, 0xd0, 0xda));

    public TimelineControl()
    {
        _gridPen.Freeze(); _hourPen.Freeze(); _playheadPen.Freeze(); _hoverPen.Freeze();
        _trackBrush.Freeze(); _selBrush.Freeze(); _labelBrush.Freeze(); _rowLabelBrush.Freeze();
        Focusable = true;
        ClipToBounds = true;
    }

    public IReadOnlyList<TimelineRow> Rows { get; private set; } = Array.Empty<TimelineRow>();

    public double Position
    {
        get => _position;
        set
        {
            if (Math.Abs(_position - value) < 0.05) return;
            _position = value;
            if (FollowPlayhead && !_scrubbing && !_panning) EnsureVisible(value);
            InvalidateVisual();
        }
    }

    public bool FollowPlayhead { get; set; } = true;

    public double? HoverTime => _hoverTime;

    public (double Start, double End)? Selection =>
        _selStart is { } a && _selEnd is { } b && Math.Abs(a - b) > 0.5
            ? (Math.Min(a, b), Math.Max(a, b))
            : null;

    public double ViewStart => _viewStart;
    public double ViewEnd => _viewEnd;

    public event Action<double>? SeekRequested;

    /// <summary>Ciagniecie glowicy - podnoszone przy kazdym ruchu myszy (throttling po stronie odbiorcy).</summary>
    public event Action<double>? ScrubPreview;

    /// <summary>true = uzytkownik zaczal ciagnac glowice, false = puscil.</summary>
    public event Action<bool>? ScrubStateChanged;

    public event Action<double?>? HoverTimeChanged;
    public event Action? SelectionChanged;

    public bool IsScrubbing => _scrubbing;

    public void SetRows(IReadOnlyList<TimelineRow> rows)
    {
        Rows = rows;
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _selStart = _selEnd = null;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetSelection(double from, double to)
    {
        _selStart = from; _selEnd = to;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void ResetZoom()
    {
        _viewStart = 0; _viewEnd = DaySeconds;
        InvalidateVisual();
    }

    public void ZoomToRange(double from, double to, double paddingFraction = 0.15)
    {
        if (to <= from) return;
        double pad = (to - from) * paddingFraction;
        SetView(from - pad, to + pad);
    }

    // ---------- rysowanie ----------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        DrawRuler(dc, w);

        int rowCount = Math.Max(1, Rows.Count);
        double areaTop = RulerHeight + 2;
        double areaHeight = Math.Max(8, h - areaTop - 2);
        double rowHeight = (areaHeight - RowGap * (rowCount - 1)) / rowCount;

        for (int i = 0; i < rowCount; i++)
        {
            double top = areaTop + i * (rowHeight + RowGap);
            var rect = new Rect(0, top, w, rowHeight);
            dc.DrawRoundedRectangle(_trackBrush, null, rect, 3, 3);

            if (i < Rows.Count) DrawRow(dc, Rows[i], rect);
        }

        DrawSelection(dc, h);
        DrawHover(dc, h);
        DrawPlayhead(dc, h);
    }

    private void DrawRow(DrawingContext dc, TimelineRow row, Rect rect)
    {
        double span = _viewEnd - _viewStart;
        var fill = row.Fill;
        if (!row.IsActive)
        {
            fill = fill.Clone();
            fill.Opacity = 0.35;
            fill.Freeze();
        }

        foreach (var (start, end) in row.Blocks)
        {
            if (end <= _viewStart || start >= _viewEnd) continue;
            double x1 = TimeToX(Math.Max(start, _viewStart));
            double x2 = TimeToX(Math.Min(end, _viewEnd));
            double bw = Math.Max(1.0, x2 - x1);
            dc.DrawRectangle(fill, null, new Rect(x1, rect.Y + 2, bw, rect.Height - 4));
        }

        if (Rows.Count > 1 && rect.Height >= 18)
        {
            var ft = Text(row.Label, 11, _rowLabelBrush);
            dc.DrawText(ft, new Point(6, rect.Y + (rect.Height - ft.Height) / 2));
        }

        _ = span;
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        double span = _viewEnd - _viewStart;
        double step = ChooseStep(span, w);

        double first = Math.Floor(_viewStart / step) * step;
        for (double t = first; t <= _viewEnd; t += step)
        {
            if (t < _viewStart) continue;
            double x = TimeToX(t);
            bool major = Math.Abs(t % (step * 5)) < 0.001 || step >= 3600;

            dc.DrawLine(major ? _hourPen : _gridPen,
                new Point(x, major ? 4 : 10), new Point(x, ActualHeight));

            if (major || span < 7200)
            {
                var ft = Text(FormatTime(t, step), 10.5, _labelBrush);
                double lx = Math.Min(w - ft.Width - 2, Math.Max(2, x + 3));
                dc.DrawText(ft, new Point(lx, 3));
            }
        }
    }

    private void DrawPlayhead(DrawingContext dc, double h)
    {
        if (_position < _viewStart || _position > _viewEnd) return;
        double x = TimeToX(_position);
        dc.DrawLine(_playheadPen, new Point(x, 0), new Point(x, h));

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(x - 5, 0), true, true);
            g.LineTo(new Point(x + 5, 0), true, false);
            g.LineTo(new Point(x, 7), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(_playheadPen.Brush, null, geo);
    }

    private void DrawHover(DrawingContext dc, double h)
    {
        if (_hoverTime is not { } t) return;
        if (t < _viewStart || t > _viewEnd) return;
        double x = TimeToX(t);
        dc.DrawLine(_hoverPen, new Point(x, RulerHeight), new Point(x, h));
    }

    private void DrawSelection(DrawingContext dc, double h)
    {
        if (Selection is not { } sel) return;
        double x1 = TimeToX(Math.Max(sel.Start, _viewStart));
        double x2 = TimeToX(Math.Min(sel.End, _viewEnd));
        if (x2 <= x1) return;
        dc.DrawRectangle(_selBrush, null, new Rect(x1, RulerHeight, x2 - x1, h - RulerHeight));
    }

    // ---------- interakcja ----------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double anchor = XToTime(e.GetPosition(this).X);
        double span = _viewEnd - _viewStart;
        double factor = e.Delta > 0 ? 0.75 : 1 / 0.75;
        double newSpan = Math.Clamp(span * factor, MinSpan, DaySeconds);

        double ratio = span <= 0 ? 0.5 : (anchor - _viewStart) / span;
        SetView(anchor - ratio * newSpan, anchor - ratio * newSpan + newSpan);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();

        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        CaptureMouse();
        _dragOrigin = e.GetPosition(this);
        double t = XToTime(_dragOrigin.X);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _selecting = true;
            _selStart = t;
            _selEnd = t;
        }
        else
        {
            _scrubbing = true;
            _position = t;
            SetHover(t);
            ScrubStateChanged?.Invoke(true);
            SeekRequested?.Invoke(t);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        double t = XToTime(p.X);

        if (_panning)
        {
            double dt = (p.X - _dragOrigin.X) / Math.Max(1, ActualWidth) * (_viewEnd - _viewStart);
            double span = _viewEnd - _viewStart;
            SetView(_panStartView - dt, _panStartView - dt + span);
            return;
        }

        SetHover(t);

        if (_selecting)
        {
            _selEnd = t;
            SelectionChanged?.Invoke();
            InvalidateVisual();
        }
        else if (_scrubbing)
        {
            // Glowica idzie za kursorem natychmiast; realny seek jest throttlowany wyzej,
            // zeby nie otwierac dziesiatek plikow na sekunde.
            _position = t;
            ScrubPreview?.Invoke(t);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_scrubbing)
        {
            _scrubbing = false;
            ScrubStateChanged?.Invoke(false);
            SeekRequested?.Invoke(XToTime(e.GetPosition(this).X));
        }
        _scrubbing = false;
        _selecting = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = true;
            _dragOrigin = e.GetPosition(this);
            _panStartView = _viewStart;
            CaptureMouse();
            e.Handled = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _panning = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        SetHover(null);
        InvalidateVisual();
    }

    // ---------- pomocnicze ----------

    private void SetHover(double? t)
    {
        if (Nullable.Equals(_hoverTime, t)) return;
        _hoverTime = t;
        HoverTimeChanged?.Invoke(t);
        InvalidateVisual();
    }

    private void SetView(double start, double end)
    {
        double span = Math.Clamp(end - start, MinSpan, DaySeconds);
        if (start < 0) start = 0;
        if (start + span > DaySeconds) start = DaySeconds - span;
        _viewStart = start;
        _viewEnd = start + span;
        InvalidateVisual();
    }

    private void EnsureVisible(double t)
    {
        double span = _viewEnd - _viewStart;
        if (span >= DaySeconds) return;
        if (t >= _viewStart + span * 0.08 && t <= _viewEnd - span * 0.08) return;
        SetView(t - span / 2, t + span / 2);
    }

    public double TimeToX(double t) => (t - _viewStart) / (_viewEnd - _viewStart) * ActualWidth;

    public double XToTime(double x) =>
        Math.Clamp(_viewStart + x / Math.Max(1, ActualWidth) * (_viewEnd - _viewStart), 0, DaySeconds);

    private static double ChooseStep(double span, double width)
    {
        double targetPx = 90;
        double raw = span / Math.Max(1, width / targetPx);
        double[] steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600];
        foreach (var s in steps) if (s >= raw) return s;
        return 21600;
    }

    private static string FormatTime(double seconds, double step)
    {
        var ts = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DaySeconds));
        return step >= 3600 ? $"{ts.Hours:00}:00" :
               step >= 60 ? $"{ts.Hours:00}:{ts.Minutes:00}" :
                            $"{ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
