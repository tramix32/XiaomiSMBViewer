using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using XiaomiSMBViewer.Models;
using XiaomiSMBViewer.Services;

namespace XiaomiSMBViewer.Playback;

/// <summary>Jedna kamera: silnik odtwarzania + kafelek wideo + indeks dnia.</summary>
public sealed class CameraPane : IDisposable
{
    public CameraPane(string cameraId, LibVLC libVlc, bool hardwareDecoding, Color accent)
    {
        CameraId = cameraId;
        Accent = accent;
        Engine = new PlaybackEngine(libVlc, hardwareDecoding);

        VideoView = new VideoView { Background = System.Windows.Media.Brushes.Black };
        VideoView.Loaded += (_, _) => VideoView.MediaPlayer = Engine.Player;

        Label = new TextBlock
        {
            Text = cameraId,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD0, 0xDA)),
            FontSize = 11,
            Margin = new Thickness(8, 4, 8, 4),
        };

        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x22)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Child = Label,
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(VideoView, 1);
        grid.Children.Add(header);
        grid.Children.Add(VideoView);

        Root = new Border
        {
            Background = System.Windows.Media.Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x38)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Child = grid,
        };
    }

    public string CameraId { get; }
    public Color Accent { get; }
    public PlaybackEngine Engine { get; }
    public VideoView VideoView { get; }
    public Border Root { get; }
    public TextBlock Label { get; }
    public DayIndex Index { get; private set; } = DayIndex.Empty("", default);

    /// <summary>Kamera nadajaca takt (zegar + zdarzenia konca dnia).</summary>
    public bool IsPrimary { get; set; }

    public void SetIndex(DayIndex index)
    {
        Index = index;
        Engine.SetIndex(index);
        RefreshLabel();
    }

    public void RefreshLabel()
    {
        Label.Text = Index.IsEmpty
            ? $"{CameraId}  —  {Loc.T("pane.norecordings")}"
            : $"{CameraId}  —  {Loc.T("pane.info", Index.Segments.Count, TimeSpan.FromSeconds(Index.RecordedSeconds).ToString(@"hh\:mm\:ss"))}";
    }

    public void Dispose()
    {
        VideoView.MediaPlayer = null;
        Engine.Dispose();
        VideoView.Dispose();
    }
}
