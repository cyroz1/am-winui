namespace AmNative.Models;

public sealed record ReplaySummary(
    int? Year,
    MusicResourcePage TopSongs,
    MusicResourcePage TopAlbums,
    MusicResourcePage TopArtists);
