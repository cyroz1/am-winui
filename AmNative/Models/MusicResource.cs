using System.Collections.Generic;
using System.Text.Json;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AmNative.Models;

public sealed class MusicResource
{
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
    public bool? IsPublic { get; init; }
    public IReadOnlyList<string> GenreNames { get; init; } = Array.Empty<string>();
    public string? ContentRating { get; init; }
    public bool? HasLyrics { get; init; }
    public bool? IsFavorited { get; set; }
    public bool IsMusicVideo => Type is "music-videos" or "music-video" or "library-music-videos";
    public bool CanBeRated => Type is
        "songs" or "albums" or "playlists" or "music-videos" or "stations" or
        "library-songs" or "library-albums" or "library-playlists" or "library-music-videos";
    public bool CanEdit { get; init; }
    public bool CanBeAddedToLibrary => Type is
        "songs" or "song" or "albums" or "album" or
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
                                 ((Type is "library-songs" or "library-music-videos") &&
                                  !string.IsNullOrWhiteSpace(CatalogId));

    public MusicResource WithPreviewUrl(string? previewUrl, string? shareUrl = null) => new()
    {
        Id = Id,
        Type = Type,
        Name = Name,
        ArtistName = ArtistName,
        AlbumName = AlbumName,
        ArtworkUrl = ArtworkUrl,
        CatalogId = CatalogId,
        PreviewUrl = previewUrl,
        ShareUrl = ShareUrl ?? shareUrl,
        DurationInMillis = DurationInMillis,
        IsPublic = IsPublic,
        GenreNames = GenreNames,
        ContentRating = ContentRating,
        HasLyrics = HasLyrics,
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
        "stations" or "station" => "Radio station",
        _ => Type
    };

    public string Subtitle => Type == "library-playlist-folders"
        ? "Playlist folder"
        : Type.Contains("playlist", StringComparison.OrdinalIgnoreCase)
            ? (ArtistName.Length > 0 ? ArtistName : "Playlist")
            : string.Join(" · ", new[] { ArtistName, AlbumName }.Where(static value => !string.IsNullOrWhiteSpace(value)));

    public string MetadataSummary
    {
        get
        {
            var parts = new List<string>();
            if (Type == "library-playlists" && IsPublic is bool isPublic)
            {
                parts.Add(isPublic ? "Public playlist" : "Private playlist");
            }

            if (GenreNames.Count > 0)
            {
                parts.Add(string.Join(", ", GenreNames));
            }

            if (string.Equals(ContentRating, "explicit", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add("Explicit");
            }

            if (HasLyrics == true)
            {
                parts.Add("Lyrics available");
            }

            if (DurationInMillis is > 0)
            {
                var duration = TimeSpan.FromMilliseconds(DurationInMillis.Value);
                parts.Add($"{(int)duration.TotalMinutes}:{duration.Seconds:00}");
            }

            return string.Join(" · ", parts);
        }
    }

    public ImageSource? ArtworkSource => ArtworkImageCache.Get(ArtworkUrl);

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
            IsPublic = ReadNullableBool(attributes, "isPublic"),
            GenreNames = ReadStringArray(attributes, "genreNames"),
            ContentRating = ReadString(attributes, "contentRating"),
            HasLyrics = ReadNullableBool(attributes, "hasLyrics"),
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

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static class ArtworkImageCache
    {
        private const int Capacity = 128;
        private static readonly object Sync = new();
        private static readonly Dictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> Entries = new(StringComparer.Ordinal);
        private static readonly LinkedList<(string Key, BitmapImage Image)> MostRecentFirst = new();

        public static ImageSource? Get(string? artworkUrl)
        {
            if (string.IsNullOrWhiteSpace(artworkUrl))
            {
                return null;
            }

            var url = artworkUrl.Replace("{w}", "96", StringComparison.Ordinal)
                .Replace("{h}", "96", StringComparison.Ordinal);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var key = uri.AbsoluteUri;
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var cached))
                {
                    MostRecentFirst.Remove(cached);
                    MostRecentFirst.AddFirst(cached);
                    return cached.Value.Image;
                }

                var image = new BitmapImage { DecodePixelWidth = 96, DecodePixelHeight = 96 };
                image.UriSource = uri;
                var entry = MostRecentFirst.AddFirst((key, image));
                Entries.Add(key, entry);

                if (Entries.Count > Capacity)
                {
                    var leastRecent = MostRecentFirst.Last!;
                    MostRecentFirst.RemoveLast();
                    Entries.Remove(leastRecent.Value.Key);
                }

                return image;
            }
        }
    }
}
