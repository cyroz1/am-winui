using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AmNative.Models;

public sealed class MusicResource
{
    private ImageSource? _artworkSource;
    private bool _artworkSourceInitialized;

    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Name { get; init; }
    public string ArtistName { get; init; } = "";
    public string AlbumName { get; init; } = "";
    public string? ArtworkUrl { get; init; }
    public string? CatalogId { get; init; }
    public string? PreviewUrl { get; init; }
    public string? ShareUrl { get; init; }
    public int? DurationInMillis { get; init; }
    public bool? IsFavorited { get; set; }
    public bool IsMusicVideo => Type is "music-videos" or "music-video" or "library-music-videos";
    public bool CanEdit { get; init; }
    public bool CanBeAddedToLibrary => Type is
        "songs" or "song" or "albums" or "album" or "artists" or "artist" or
        "playlists" or "playlist" or "music-videos" or "music-video";
    public bool CanBeFavorited => Type switch
    {
        "songs" or "song" or "albums" or "album" or "artists" or "artist" or "playlists" or "playlist" or "music-videos" or "music-video" => true,
        "library-songs" or "library-albums" or "library-artists" or "library-playlists" or "library-music-videos" =>
            !string.IsNullOrWhiteSpace(CatalogId),
        _ => false
    };
    public bool CanBeAddedToPlaylist => Type is
        "songs" or "song" or "library-songs" or "music-videos" or "music-video" or "library-music-videos";
    public bool CanPlayPreview => !string.IsNullOrWhiteSpace(PreviewUrl) ||
                                 (Type == "library-songs" && !string.IsNullOrWhiteSpace(CatalogId));

    public MusicResource WithPreviewUrl(string? previewUrl) => new()
    {
        Id = Id,
        Type = Type,
        Name = Name,
        ArtistName = ArtistName,
        AlbumName = AlbumName,
        ArtworkUrl = ArtworkUrl,
        CatalogId = CatalogId,
        PreviewUrl = previewUrl,
        ShareUrl = ShareUrl,
        DurationInMillis = DurationInMillis,
        IsFavorited = IsFavorited,
        CanEdit = CanEdit
    };

    public string KindLabel => Type switch
    {
        "library-playlist-folders" => "Folder",
        "library-playlists" or "playlists" or "playlist" => "Playlist",
        "library-songs" => "Library song",
        "library-albums" => "Library album",
        "library-artists" => "Library artist",
        "library-music-videos" => "Library video",
        "songs" or "song" => "Song",
        "albums" or "album" => "Album",
        "artists" or "artist" => "Artist",
        "music-videos" or "music-video" => "Music video",
        _ => Type
    };

    public string Subtitle => Type == "library-playlist-folders"
        ? "Playlist folder"
        : Type.Contains("playlist", StringComparison.OrdinalIgnoreCase)
            ? (ArtistName.Length > 0 ? ArtistName : "Playlist")
            : string.Join(" · ", new[] { ArtistName, AlbumName }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    public ImageSource? ArtworkSource
    {
        get
        {
            if (_artworkSourceInitialized)
            {
                return _artworkSource;
            }

            _artworkSourceInitialized = true;
            if (string.IsNullOrWhiteSpace(ArtworkUrl))
            {
                return null;
            }

            var url = ArtworkUrl.Replace("{w}", "96", StringComparison.Ordinal)
                .Replace("{h}", "96", StringComparison.Ordinal);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var bitmap = new BitmapImage { DecodePixelWidth = 96, DecodePixelHeight = 96 };
            bitmap.UriSource = uri;
            _artworkSource = bitmap;
            return _artworkSource;
        }
    }

    public static MusicResource FromJson(JsonElement resource)
    {
        var id = ReadString(resource, "id") ?? "";
        var type = ReadString(resource, "type") ?? "songs";
        var attributes = resource.TryGetProperty("attributes", out var value) ? value : default;

        var artworkUrl = ReadNestedString(attributes, "artwork", "url");
        var previewUrl = ReadFirstPreviewUrl(attributes);
        var catalogId = ReadNestedString(attributes, "playParams", "catalogId");
        if (catalogId is null && (type is "songs" or "song"))
        {
            catalogId = id;
        }

        var duration = ReadInt(attributes, "durationInMillis");
        var name = ReadString(attributes, "name") ?? "Unknown title";
        var artist = ReadString(attributes, "artistName") ?? ReadString(attributes, "curatorName") ?? "";
        var album = ReadString(attributes, "albumName") ?? "";

        return new MusicResource
        {
            Id = id,
            Type = type,
            Name = name,
            ArtistName = artist,
            AlbumName = album,
            ArtworkUrl = artworkUrl,
            CatalogId = catalogId,
            PreviewUrl = previewUrl,
            ShareUrl = ReadString(attributes, "url"),
            DurationInMillis = duration,
            IsFavorited = ReadNullableBool(attributes, "inFavorites"),
            CanEdit = ReadBool(attributes, "canEdit")
        };
    }

    private static string? ReadFirstPreviewUrl(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object ||
            !attributes.TryGetProperty("previews", out var previews) || previews.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var preview in previews.EnumerateArray())
        {
            var url = ReadString(preview, "url");
            if (IsHttpsUrl(url))
            {
                return url;
            }

            var hlsUrl = ReadString(preview, "hlsUrl");
            if (IsHttpsUrl(hlsUrl))
            {
                return hlsUrl;
            }
        }

        return null;
    }

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string? ReadNestedString(JsonElement element, string parent, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var nested)
            ? ReadString(nested, property)
            : null;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static bool ReadBool(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static bool? ReadNullableBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
