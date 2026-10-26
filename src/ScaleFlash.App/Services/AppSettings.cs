using System.IO;
using System.Text.Json;

namespace ScaleFlash.App.Services;

/// <summary>User preferences, stored in %LOCALAPPDATA%\ScaleFlash\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScaleFlash", "settings.json");

    public string? LastImagePath { get; set; }
    public List<string> RecentImages { get; set; } = [];
    public bool SkipIdentical { get; set; } = true;
    public bool Verify { get; set; }
    public int ChunkSizeMB { get; set; } = 4;
    public bool ShowFixedUsbDisks { get; set; }
    public string? LastBackupFolder { get; set; }
    public bool AutoHideLinuxDrives { get; set; }

    public static AppSettings Current { get; } = Load();

    public void AddRecent(string path)
    {
        RecentImages.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentImages.Insert(0, path);
        if (RecentImages.Count > 8) RecentImages.RemoveRange(8, RecentImages.Count - 8);
        LastImagePath = path;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* settings are best-effort */ }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s is not null)
                {
                    if (s.ChunkSizeMB is not (1 or 2 or 4 or 8 or 16)) s.ChunkSizeMB = 4;
                    return s;
                }
            }
        }
        catch (Exception) { /* fall back to defaults */ }
        return new AppSettings();
    }
}
