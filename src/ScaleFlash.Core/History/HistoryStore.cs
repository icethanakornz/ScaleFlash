using System.Text.Json;

namespace ScaleFlash.Core.History;

public sealed record HistoryEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Kind { get; init; } = "write"; // write | backup
    public string ImagePath { get; init; } = "";
    public long ImageSize { get; init; }
    public string DiskModel { get; init; } = "";
    public long DiskSize { get; init; }
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
    public long BytesWritten { get; init; }
    public long BytesSkipped { get; init; }
    public bool Verified { get; init; }
    public double Seconds { get; init; }
    public double AverageMBps { get; init; }
}

/// <summary>Append-only JSON-lines log in %LOCALAPPDATA%\ScaleFlash\history.jsonl.</summary>
public static class HistoryStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScaleFlash");

    public static string FilePath => Path.Combine(Folder, "history.jsonl");

    public static void Append(HistoryEntry entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
            }
        }
        catch (Exception) { /* history is best-effort */ }
    }

    public static List<HistoryEntry> Load(int max = 500)
    {
        var list = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (string line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<HistoryEntry>(line, Json);
                    if (e is not null) list.Add(e);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        list.Reverse();
        return list.Take(max).ToList();
    }
}
