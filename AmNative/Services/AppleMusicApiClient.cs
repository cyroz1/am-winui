using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AmNative.Models;

namespace AmNative.Services;

public sealed class AppleMusicApiClient
{
    private static readonly Uri ApiRoot = new("https://api.music.apple.com/v1/");
    private readonly CredentialStore _credentials;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _storefrontLock = new(1, 1);
    private string _storefront = "us";
    private bool _storefrontResolved;

    public AppleMusicApiClient(CredentialStore credentials)
    {
        _credentials = credentials;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<IReadOnlyList<MusicResource>> GetLibraryAsync(string kind, CancellationToken cancellationToken = default)
    {
        var endpoint = kind switch
        {
            "recently-added" => "me/library/recently-added",
            "songs" => "me/library/songs?limit=100",
            "albums" => "me/library/albums?limit=100",
            "artists" => "me/library/artists?limit=100",
            "playlists" => "me/library/playlists?limit=100",
            "music-videos" => "me/library/music-videos?limit=100",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        return await GetAllPagesAsync(endpoint, cancellationToken);
    }

    public Task<MusicResourcePage> GetLibraryPageAsync(string kind, CancellationToken cancellationToken = default)
    {
        var endpoint = kind switch
        {
            "recently-added" => "me/library/recently-added",
            "songs" => "me/library/songs?limit=100",
            "albums" => "me/library/albums?limit=100",
            "artists" => "me/library/artists?limit=100",
            "playlists" => "me/library/playlists?limit=100",
            "music-videos" => "me/library/music-videos?limit=100",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        return GetApiResourcePageAsync(new Uri(ApiRoot, endpoint), cancellationToken);
    }

    public async Task<IReadOnlyList<MusicResource>> SearchCatalogAsync(string term, CancellationToken cancellationToken = default)
    {
        var storefront = await ResolveStorefrontAsync(cancellationToken);
        var query = $"catalog/{storefront}/search?term={Uri.EscapeDataString(term)}&types=songs,albums,artists,playlists,music-videos&limit=25";
        using var request = CreateRequest(HttpMethod.Get, query);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<MusicResource>();
        }

        var resources = new List<MusicResource>();
        foreach (var type in new[] { "songs", "albums", "artists", "playlists", "music-videos" })
        {
            if (!results.TryGetProperty(type, out var collection) ||
                collection.ValueKind != JsonValueKind.Object ||
                !collection.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            resources.AddRange(data.EnumerateArray().Select(MusicResource.FromJson));
        }

        return resources;
    }

    public async Task<MusicResourcePage> SearchCatalogPageAsync(string term, CancellationToken cancellationToken = default)
    {
        var storefront = await ResolveStorefrontAsync(cancellationToken);
        var query = $"catalog/{storefront}/search?term={Uri.EscapeDataString(term)}&types=songs,albums,artists,playlists,music-videos&limit=25";
        using var request = CreateRequest(HttpMethod.Get, query);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return new MusicResourcePage(Array.Empty<MusicResource>(), Array.Empty<Uri>());
        }

        var resources = new List<MusicResource>();
        var nextPages = new List<Uri>();
        foreach (var type in new[] { "songs", "albums", "artists", "playlists", "music-videos" })
        {
            if (!results.TryGetProperty(type, out var collection) || collection.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (collection.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                resources.AddRange(data.EnumerateArray().Select(MusicResource.FromJson));
            }

            if (TryReadNextPage(collection, request.RequestUri!, out var nextPage))
            {
                nextPages.Add(nextPage);
            }
        }

        return new MusicResourcePage(resources, nextPages);
    }

    public async Task<MusicResourcePage> GetCatalogChartsPageAsync(string chartType, CancellationToken cancellationToken = default)
    {
        var resourceType = chartType switch
        {
            "songs" or "albums" or "playlists" => chartType,
            _ => throw new ArgumentOutOfRangeException(nameof(chartType), "Apple Music charts are available here for songs, albums, and playlists.")
        };

        var storefront = await ResolveStorefrontAsync(cancellationToken);
        var endpoint = $"catalog/{storefront}/charts?types={resourceType}&with=dailyGlobalTopCharts";
        return await GetApiResourcePageAsync(new Uri(ApiRoot, endpoint), cancellationToken);
    }

    public void ResetSession()
    {
        _storefrontResolved = false;
        _storefront = "us";
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "me/storefront");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String)
        {
            var storefront = idValue.GetString();
            if (storefront?.Length == 2 && storefront.All(char.IsAsciiLetter))
            {
                return storefront.ToUpperInvariant();
            }
        }

        throw new InvalidOperationException("Apple Music accepted the credentials but did not return a valid storefront.");
    }

    public async Task<IReadOnlyList<MusicResource>> SearchLibraryAsync(string term, CancellationToken cancellationToken = default)
    {
        var query = $"me/library/search?term={Uri.EscapeDataString(term)}&types=library-songs,library-albums,library-artists,library-playlists,library-music-videos&limit=25";
        using var request = CreateRequest(HttpMethod.Get, query);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<MusicResource>();
        }

        var resources = new List<MusicResource>();
        foreach (var type in new[] { "library-songs", "library-albums", "library-artists", "library-playlists", "library-music-videos" })
        {
            if (results.TryGetProperty(type, out var collection) &&
                collection.ValueKind == JsonValueKind.Object &&
                collection.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                resources.AddRange(data.EnumerateArray().Select(MusicResource.FromJson));
            }
        }

        return resources;
    }

    public async Task<MusicResourcePage> SearchLibraryPageAsync(string term, CancellationToken cancellationToken = default)
    {
        var query = $"me/library/search?term={Uri.EscapeDataString(term)}&types=library-songs,library-albums,library-artists,library-playlists,library-music-videos&limit=25";
        using var request = CreateRequest(HttpMethod.Get, query);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
        {
            return new MusicResourcePage(Array.Empty<MusicResource>(), Array.Empty<Uri>());
        }

        var resources = new List<MusicResource>();
        var nextPages = new List<Uri>();
        foreach (var type in new[] { "library-songs", "library-albums", "library-artists", "library-playlists", "library-music-videos" })
        {
            if (!results.TryGetProperty(type, out var collection) || collection.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (collection.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                resources.AddRange(data.EnumerateArray().Select(MusicResource.FromJson));
            }

            if (TryReadNextPage(collection, request.RequestUri!, out var nextPage))
            {
                nextPages.Add(nextPage);
            }
        }

        return new MusicResourcePage(resources, nextPages);
    }

    public Task<MusicResourcePage> GetNextPageAsync(Uri nextPage, CancellationToken cancellationToken = default) =>
        GetApiResourcePageAsync(nextPage, cancellationToken);

    public Task<IReadOnlyList<MusicResource>> GetRecentlyPlayedTracksAsync(CancellationToken cancellationToken = default) =>
        GetAllPagesAsync("me/recent/played/tracks?types=songs,library-songs,music-videos,library-music-videos&limit=30", cancellationToken);

    public Task<MusicResourcePage> GetRecentlyPlayedTracksPageAsync(CancellationToken cancellationToken = default) =>
        GetApiResourcePageAsync(
            new Uri(ApiRoot, "me/recent/played/tracks?types=songs,library-songs,music-videos,library-music-videos&limit=30"),
            cancellationToken);

    public async Task<IReadOnlyList<MusicResource>> GetRecommendationsAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "me/recommendations?include=contents");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var recommendations) || recommendations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MusicResource>();
        }

        var resources = new List<MusicResource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recommendation in recommendations.EnumerateArray())
        {
            if (!recommendation.TryGetProperty("relationships", out var relationships) ||
                relationships.ValueKind != JsonValueKind.Object ||
                !relationships.TryGetProperty("contents", out var contents) ||
                contents.ValueKind != JsonValueKind.Object ||
                !contents.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var resource in data.EnumerateArray())
            {
                var item = MusicResource.FromJson(resource);
                if (item.Id.Length > 0 && seen.Add($"{item.Type}:{item.Id}"))
                {
                    resources.Add(item);
                }
            }
        }

        return resources;
    }

    public async Task<IReadOnlyList<MusicResource>> GetPlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"me/library/playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit=100";
        var tracks = await GetAllPagesAsync(endpoint, cancellationToken);
        return await ResolvePreviewsAsync(tracks, cancellationToken);
    }

    public Task<MusicResourcePage> GetPlaylistTracksPageAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"me/library/playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit=100";
        return GetApiResourcePageAsync(new Uri(ApiRoot, endpoint), cancellationToken);
    }

    public async Task<MusicResource> ResolvePreviewAsync(MusicResource resource, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolvePreviewsAsync(new[] { resource }, cancellationToken);
        return resolved[0];
    }

    private async Task<IReadOnlyList<MusicResource>> ResolvePreviewsAsync(
        IReadOnlyList<MusicResource> resources,
        CancellationToken cancellationToken)
    {
        var catalogIds = resources
            .Where(static item => item.Type == "library-songs" &&
                                  string.IsNullOrWhiteSpace(item.PreviewUrl) &&
                                  !string.IsNullOrWhiteSpace(item.CatalogId))
            .Select(static item => item.CatalogId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (catalogIds.Length == 0)
        {
            return resources;
        }

        var storefront = await ResolveStorefrontAsync(cancellationToken);
        var previewUrls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var idBatch in catalogIds.Chunk(300))
        {
            var ids = string.Join(",", idBatch.Select(Uri.EscapeDataString));
            using var request = CreateRequest(HttpMethod.Get, $"catalog/{storefront}/songs?ids={ids}");
            using var response = await SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            await EnsureSuccessAsync(response, cancellationToken);

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var song in data.EnumerateArray().Select(MusicResource.FromJson))
            {
                if (!string.IsNullOrWhiteSpace(song.PreviewUrl))
                {
                    previewUrls[song.Id] = song.PreviewUrl;
                }
            }
        }

        return resources.Select(item =>
        {
            return item.Type == "library-songs" &&
                   item.CatalogId is not null &&
                   previewUrls.TryGetValue(item.CatalogId, out var previewUrl)
                ? item.WithPreviewUrl(previewUrl)
                : item;
        }).ToArray();
    }

    public Task<IReadOnlyList<MusicResource>> GetPlaylistFolderContentsAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var endpoint = $"me/library/playlist-folders/{Uri.EscapeDataString(folderId)}/children?limit=100";
        return GetAllPagesAsync(endpoint, cancellationToken);
    }

    public async Task<IReadOnlyList<MusicResource>> GetRelatedResourcesAsync(MusicResource resource, CancellationToken cancellationToken = default)
    {
        var endpoint = await GetRelatedResourcesEndpointAsync(resource, cancellationToken);
        var related = await GetAllPagesAsync(endpoint, cancellationToken);
        return await ResolvePreviewsAsync(related, cancellationToken);
    }

    public async Task<MusicResourcePage> GetRelatedResourcesPageAsync(MusicResource resource, CancellationToken cancellationToken = default)
    {
        var endpoint = await GetRelatedResourcesEndpointAsync(resource, cancellationToken);
        return await GetApiResourcePageAsync(new Uri(ApiRoot, endpoint), cancellationToken);
    }

    public async Task<MusicResourcePage> GetArtistTopSongsPageAsync(MusicResource artist, CancellationToken cancellationToken = default)
    {
        var endpoint = await GetArtistTopSongsEndpointAsync(artist, cancellationToken);
        return await GetApiResourcePageAsync(new Uri(ApiRoot, endpoint), cancellationToken);
    }

    public async Task<IReadOnlyList<MusicResource>> GetArtistTopSongsAsync(MusicResource artist, CancellationToken cancellationToken = default)
    {
        var endpoint = await GetArtistTopSongsEndpointAsync(artist, cancellationToken);
        return await GetAllPagesAsync(endpoint, cancellationToken);
    }

    private async Task<string> GetArtistTopSongsEndpointAsync(MusicResource artist, CancellationToken cancellationToken)
    {
        if (artist.Type is not ("artists" or "artist"))
        {
            throw new InvalidOperationException("Top songs are available for catalog artists.");
        }

        var storefront = await ResolveStorefrontAsync(cancellationToken);
        return $"catalog/{storefront}/artists/{Uri.EscapeDataString(artist.Id)}/view/top-songs";
    }

    private async Task<string> GetRelatedResourcesEndpointAsync(MusicResource resource, CancellationToken cancellationToken)
    {
        var id = Uri.EscapeDataString(resource.Id);
        switch (resource.Type)
        {
            case "albums":
            case "album":
                return $"catalog/{await ResolveStorefrontAsync(cancellationToken)}/albums/{id}/tracks?limit=100";
            case "library-albums":
                return $"me/library/albums/{id}/tracks?limit=100";
            case "artists":
            case "artist":
                return $"catalog/{await ResolveStorefrontAsync(cancellationToken)}/artists/{id}/albums?limit=100";
            case "library-artists":
                return $"me/library/artists/{id}/albums?limit=100";
            case "playlists":
            case "playlist":
                return $"catalog/{await ResolveStorefrontAsync(cancellationToken)}/playlists/{id}/tracks?limit=100";
            case "library-playlists":
                return $"me/library/playlists/{id}/tracks?limit=100";
            default:
                throw new InvalidOperationException("This Apple Music item has no browsable album, artist, or playlist contents.");
        }
    }

    public async Task AddToLibraryAsync(MusicResource resource, CancellationToken cancellationToken = default)
    {
        var catalogType = resource.Type switch
        {
            "song" or "songs" => "songs",
            "album" or "albums" => "albums",
            "artist" or "artists" => "artists",
            "playlist" or "playlists" => "playlists",
            "music-videos" or "music-video" => "music-videos",
            _ => throw new InvalidOperationException("This Apple Music item type cannot be added to the library.")
        };

        var endpoint = $"me/library?ids%5B{catalogType}%5D={Uri.EscapeDataString(resource.Id)}";
        using var request = CreateRequest(HttpMethod.Post, endpoint);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task AddToFavoritesAsync(MusicResource resource, CancellationToken cancellationToken = default)
    {
        var catalogType = resource.Type switch
        {
            "songs" or "song" => "songs",
            "library-songs" => "songs",
            "albums" or "album" => "albums",
            "artists" or "artist" => "artists",
            "playlists" or "playlist" => "playlists",
            _ => throw new InvalidOperationException("Apple Music favorites are currently supported here for songs, albums, artists, and playlists.")
        };

        var resourceId = resource.Type == "library-songs"
            ? resource.CatalogId
            : resource.Id;
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            throw new InvalidOperationException("This library song has no catalog ID, so Apple Music can’t add it to favorites from this view.");
        }

        var endpoint = $"me/favorites?ids%5B{catalogType}%5D={Uri.EscapeDataString(resourceId)}";
        using var request = CreateRequest(HttpMethod.Post, endpoint);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetRatingAsync(MusicResource resource, int? rating, CancellationToken cancellationToken = default)
    {
        if (rating.HasValue && rating.Value != 1 && rating.Value != -1)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), "Apple Music ratings must be 1, -1, or null to clear the rating.");
        }

        var ratingType = resource.Type switch
        {
            "songs" or "song" => "songs",
            "library-songs" => "library-songs",
            "albums" or "album" => "albums",
            "library-albums" => "library-albums",
            "playlists" or "playlist" => "playlists",
            "library-playlists" => "library-playlists",
            "music-videos" => "music-videos",
            "library-music-videos" => "library-music-videos",
            _ => throw new InvalidOperationException("Apple Music does not support ratings for this resource type.")
        };

        var method = rating is null ? HttpMethod.Delete : HttpMethod.Put;
        using var request = CreateRequest(method, $"me/ratings/{ratingType}/{Uri.EscapeDataString(resource.Id)}");
        if (rating is not null)
        {
            var body = JsonSerializer.Serialize(new
            {
                type = "rating",
                attributes = new { value = rating.Value }
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<MusicResource?> CreatePlaylistAsync(string name, string? description = null, string? parentFolderId = null, CancellationToken cancellationToken = default)
    {
        var attributes = new Dictionary<string, object?> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(description))
        {
            attributes["description"] = description;
        }

        var body = parentFolderId is null
            ? JsonSerializer.Serialize(new { attributes })
            : JsonSerializer.Serialize(new
            {
                attributes,
                relationships = new
                {
                    parent = new
                    {
                        data = new[] { new { id = parentFolderId, type = "library-playlist-folders" } }
                    }
                }
            });
        using var request = CreateRequest(HttpMethod.Post, "me/library/playlists");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            ? MusicResource.FromJson(data[0])
            : null;
    }

    public async Task<MusicResource?> CreatePlaylistFolderAsync(string name, string parentFolderId, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            attributes = new { name },
            relationships = new
            {
                parent = new
                {
                    data = new[] { new { id = parentFolderId, type = "library-playlist-folders" } }
                }
            }
        });
        using var request = CreateRequest(HttpMethod.Post, "me/library/playlist-folders");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
            ? MusicResource.FromJson(data[0])
            : null;
    }

    public async Task AddTracksToPlaylistAsync(string playlistId, IEnumerable<MusicResource> songs, CancellationToken cancellationToken = default)
    {
        var data = songs.Select(song => new
        {
            id = song.Id,
            type = song.Type switch
            {
                "library-songs" => "library-songs",
                "library-music-videos" => "library-music-videos",
                "music-videos" => "music-videos",
                "songs" or "song" => "songs",
                "music-video" => "music-videos",
                _ => throw new InvalidOperationException("Only songs and music videos can be added to an Apple Music playlist.")
            }
        }).ToArray();
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Select at least one song or music video to add to the playlist.");
        }

        var body = JsonSerializer.Serialize(new { data });
        using var request = CreateRequest(HttpMethod.Post, $"me/library/playlists/{Uri.EscapeDataString(playlistId)}/tracks");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<IReadOnlyList<MusicResource>> GetAllPagesAsync(string endpoint, CancellationToken cancellationToken)
    {
        var resources = new List<MusicResource>();
        var next = new Uri(ApiRoot, endpoint);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (visited.Add(next.AbsoluteUri))
        {
            var page = await GetApiResourcePageAsync(next, cancellationToken);
            resources.AddRange(page.Items);
            if (page.NextPages.Count == 0)
            {
                break;
            }

            next = page.NextPages[0];
        }

        return resources;
    }

    private async Task<MusicResourcePage> GetApiResourcePageAsync(Uri pageUri, CancellationToken cancellationToken)
    {
        if (!IsApiUri(pageUri))
        {
            throw new InvalidOperationException("Refusing to fetch a pagination link outside the Apple Music API.");
        }

        using var request = CreateRequest(HttpMethod.Get, pageUri);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var resources = new List<MusicResource>();
        var nextPages = new List<Uri>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            resources.AddRange(data.EnumerateArray().Select(MusicResource.FromJson));
            if (TryReadNextPage(document.RootElement, pageUri, out var nextPage))
            {
                nextPages.Add(nextPage);
            }
        }
        else if (document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object)
        {
            foreach (var type in new[]
                     {
                         "songs", "albums", "artists", "playlists", "music-videos",
                         "library-songs", "library-albums", "library-artists", "library-playlists", "library-music-videos"
                     })
            {
                if (!results.TryGetProperty(type, out var collection))
                {
                    continue;
                }

                if (collection.ValueKind == JsonValueKind.Array)
                {
                    foreach (var chart in collection.EnumerateArray())
                    {
                        if (chart.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (chart.TryGetProperty("data", out var chartData) && chartData.ValueKind == JsonValueKind.Array)
                        {
                            resources.AddRange(chartData.EnumerateArray().Select(MusicResource.FromJson));
                        }

                        if (TryReadNextPage(chart, pageUri, out var chartNextPage))
                        {
                            nextPages.Add(chartNextPage);
                        }
                    }

                    continue;
                }

                if (collection.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (collection.TryGetProperty("data", out var resultData) && resultData.ValueKind == JsonValueKind.Array)
                {
                    resources.AddRange(resultData.EnumerateArray().Select(MusicResource.FromJson));
                }

                if (TryReadNextPage(collection, pageUri, out var nextPage))
                {
                    nextPages.Add(nextPage);
                }
            }
        }

        return new MusicResourcePage(resources, nextPages);
    }

    private static bool TryReadNextPage(JsonElement collection, Uri baseUri, out Uri nextPage)
    {
        nextPage = null!;
        if (!collection.TryGetProperty("next", out var nextValue) || nextValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var nextText = nextValue.GetString();
        if (string.IsNullOrWhiteSpace(nextText) || !Uri.TryCreate(baseUri, nextText, out var candidate) || !IsApiUri(candidate))
        {
            return false;
        }

        nextPage = candidate;
        return true;
    }

    private static bool IsApiUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals(ApiRoot.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == ApiRoot.Port &&
        uri.UserInfo.Length == 0 &&
        uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal);

    private async Task<string> ResolveStorefrontAsync(CancellationToken cancellationToken)
    {
        if (_storefrontResolved)
        {
            return _storefront;
        }

        await _storefrontLock.WaitAsync(cancellationToken);
        try
        {
            if (_storefrontResolved)
            {
                return _storefront;
            }

            var userToken = _credentials.MusicUserToken;
            if (string.IsNullOrWhiteSpace(userToken))
            {
                _storefrontResolved = true;
                return _storefront;
            }

            using var request = CreateRequest(HttpMethod.Get, "me/storefront");
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                data[0].TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String)
            {
                var id = idValue.GetString();
                if (id?.Length == 2 && id.All(char.IsAsciiLetter))
                {
                    _storefront = id.ToLowerInvariant();
                }
            }

            _storefrontResolved = true;
            return _storefront;
        }
        finally
        {
            _storefrontLock.Release();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath) =>
        CreateRequest(method, new Uri(ApiRoot, relativePath));

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var developerToken = _credentials.DeveloperToken?.Trim();
        if (string.IsNullOrWhiteSpace(developerToken))
        {
            throw new InvalidOperationException("Add an Apple Music developer token in Settings before connecting.");
        }

        var userToken = _credentials.MusicUserToken?.Trim();

        if (!uri.Host.Equals(ApiRoot.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != ApiRoot.Port ||
            uri.UserInfo.Length > 0 ||
            !uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to send Apple Music credentials outside the Apple Music API.");
        }

        var request = new HttpRequestMessage(method, uri);
        if (developerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            developerToken = developerToken[7..].Trim();
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", developerToken);
        if (!string.IsNullOrWhiteSpace(userToken))
        {
            request.Headers.TryAddWithoutValidation("Music-User-Token", userToken);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AMNative/0.1 (Windows; native client)");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var userToken = _credentials.MusicUserToken;
        if (string.IsNullOrWhiteSpace(userToken) &&
            request.RequestUri?.AbsolutePath.Contains("/me/", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException("Add a Music User Token or a web cookie containing media-user-token in Settings to access your library.");
        }

        if (request.RequestUri?.Port != ApiRoot.Port)
        {
            throw new InvalidOperationException("Refusing to send Apple Music credentials to a nonstandard API endpoint.");
        }

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new InvalidOperationException("Apple redirected the API request. The redirect was refused to protect your credentials.");
        }

        return response;
    }

    private static Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        var status = response.StatusCode;
        var detail = status switch
        {
            HttpStatusCode.Unauthorized => "The developer token was rejected. Check its value and expiry.",
            HttpStatusCode.Forbidden => "The Music User Token was rejected, or the account lacks access.",
            (HttpStatusCode)429 => "Apple Music is rate limiting requests. Wait a moment and try again.",
            _ => $"Apple Music returned {(int)status} ({status})."
        };

        // Deliberately do not include response bodies because upstream errors can echo request metadata.
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(detail);
    }

}
