using System.Text.Json;
using AmNative.Models;

namespace AmNative.Services;

public sealed class PlaybackQueueSnapshot
{
    public PlaybackQueueItem[] Items { get; init; } = Array.Empty<PlaybackQueueItem>();
    public int CurrentIndex { get; init; }
    public bool ShuffleEnabled { get; init; }
    public bool RepeatEnabled { get; init; }
    public double Volume { get; init; } = 0.5;
}

public sealed class PlaybackQueueItem
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public string? ArtworkUrl { get; init; }
    public string? CatalogId { get; init; }
    public string? PreviewUrl { get; init; }
    public int? DurationInMillis { get; init; }

    public static PlaybackQueueItem FromResource(MusicResource resource) => new()
    {
        Id = resource.Id,
        Type = resource.Type,
        Name = resource.Name,
        ArtistName = resource.ArtistName,
        AlbumName = resource.AlbumName,
        ArtworkUrl = resource.ArtworkUrl,
        CatalogId = resource.CatalogId,
        PreviewUrl = resource.PreviewUrl,
        DurationInMillis = resource.DurationInMillis
    };

    public MusicResource ToResource() => new()
    {
        Id = Id,
        Type = Type,
        Name = Name,
        ArtistName = ArtistName,
        AlbumName = AlbumName,
        ArtworkUrl = ArtworkUrl,
        CatalogId = CatalogId,
        PreviewUrl = PreviewUrl,
        DurationInMillis = DurationInMillis
    };
}

public static class PlaybackQueueStore
{
    private const int MaximumQueueItems = 10000;
    private const long MaximumStateFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new();

    public static PlaybackQueueSnapshot? Load()
    {
        try
        {
            var path = GetPath();
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MaximumStateFileBytes)
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<PlaybackQueueSnapshot>(File.ReadAllText(path), SerializerOptions);
            if (snapshot is null || snapshot.Items is null ||
                snapshot.Items.Length > MaximumQueueItems ||
                snapshot.Items.Any(static item => item is null ||
                    string.IsNullOrWhiteSpace(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Type) ||
                    string.IsNullOrWhiteSpace(item.Name) ||
                    !Uri.TryCreate(item.PreviewUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(PlaybackQueueSnapshot snapshot)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AMNative",
        "playback-queue.json");
}
