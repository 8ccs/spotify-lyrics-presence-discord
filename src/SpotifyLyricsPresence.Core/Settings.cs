using System.Text.Json;

namespace SpotifyLyricsPresence.Core;

public sealed class AppSettings
{
    public string ApplicationId { get; set; } = "";
    /// <summary>Positive = lyrics appear later, negative = earlier.</summary>
    public int OffsetMs { get; set; }
    /// <summary>
    /// Optional override for the activity's name (the header text after the activity-type prefix). Empty = let Discord use the
    /// application's registered name. Sending a name is not part of Discord's documented RPC fields.
    /// </summary>
    public string ActivityName { get; set; } = "";
    /// <summary>Optional public direct HTTPS link to the large image (e.g. a GIF). Empty = the "music_cover" art asset.</summary>
    public string ImageUrl { get; set; } = "";
    public bool StartSharingOnLaunch { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyLyricsPresence");

    public static AppSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
