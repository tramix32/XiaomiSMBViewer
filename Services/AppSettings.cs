using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiaomiSMBViewer.Services;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XiaomiSMBViewer", "settings.json");

    public string LibraryRoot { get; set; } = @"Z:\xiaomi_camera_videos";
    public string? FfmpegDirectory { get; set; }
    public bool HardwareDecoding { get; set; } = true;
    public int Volume { get; set; } = 60;
    public bool Muted { get; set; } = true;
    public string? LastCameraId { get; set; }

    /// <summary>Kod jezyka albo "auto" (jezyk systemu).</summary>
    public string Language { get; set; } = Loc.AutoCode;
    public List<string> ExtraRoots { get; set; } = new();

    [JsonIgnore]
    public bool ProbeDurations { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
