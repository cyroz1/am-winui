using AmNative.Models;
using Windows.System;

namespace AmNative.Services;

public static class AppleMusicLink
{
    public static bool CanOpen(MusicResource? resource) =>
        TryGetShareUri(resource, out _) ||
        resource is { Type: "library-songs" or "library-music-videos" };

    public static bool TryGetShareUri(MusicResource? resource, out Uri uri)
    {
        if (Uri.TryCreate(resource?.ShareUrl, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps &&
            parsed.IsDefaultPort &&
            string.Equals(parsed.Host, "music.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    public static async Task<bool> LaunchAsync(MusicResource? resource, AppleMusicApiClient api)
    {
        if (!TryGetShareUri(resource, out var uri))
        {
            if (resource is null || !CanOpen(resource))
            {
                return false;
            }

            resource = await api.ResolveAppleMusicLinkAsync(resource);
            if (!TryGetShareUri(resource, out uri))
            {
                return false;
            }
        }

        return await Launcher.LaunchUriAsync(uri);
    }
}
