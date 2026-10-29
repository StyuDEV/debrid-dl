using System.IO;
using System.Text.Json;

namespace DebridDl;

public record QueuedItem(string Link, string Folder, string? FileName, long Size, State State);

public class Settings
{
    static readonly string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "debrid-dl");
    static readonly string file = Path.Combine(dir, "settings.json");
    static readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public string ApiKey { get; set; } = "";
    public string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int Connections { get; set; } = 16;
    public int Parallel { get; set; } = 2;
    public bool WatchClipboard { get; set; } = true;
    public List<QueuedItem> Queue { get; set; } = [];

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, options));
        File.Move(file + ".tmp", file, true);
    }
}
