# AM Native

AM Native is a native Windows desktop client for Apple Music. The UI uses WinUI 3 from the Windows App SDK; it does not embed a browser or ship a web runtime. The build targets Windows 10 2004+ and produces x64 and ARM64 installers.

[Home screen preview](screenshots/am-native-home-preview.png) — static UI preview with sample data, not a capture of the running app.

## Current state

- Apple Music catalog search for songs, albums, artists, playlists, and music videos.
- Browse Apple's daily global charts for songs, albums, and playlists.
- Load library, catalog-search, and library-search results incrementally using Apple's pagination links.
- Browse album, artist, and playlist contents incrementally; the full track list is fetched only when starting a preview queue.
- Browse paginated library songs, albums, artists, playlists, and music videos.
- Browse the mixed recently added library feed.
- Search the user's library.
- Load playlist tracks one page at a time; fetching the full queue is deferred until the user starts playback.
- Add catalog items to the library, create and organize nested playlist folders, and add catalog or library tracks to a playlist.
- Add catalog songs, albums, artists, and playlists to favorites from home, search, and item detail views; favorite library items when Apple returns their matching catalog ID.
- Apply or clear like/dislike ratings for supported catalog and library items.
- Resolve Apple-provided catalog previews for library songs when their `playParams` include a catalog ID.
- Show personalized recommendations and recently played tracks on the home page.
- Open albums, artists, and playlists to browse their related albums or tracks. Catalog artist pages include a paginated top songs view and native preview queues.
- Store developer tokens and Music User Tokens in Windows Credential Manager; retain an optional full Apple Music web cookie using Windows DPAPI in local app data.
- Queue Apple-provided previews from album and playlist contents through the native Windows media playback stack, with a visible queue, play-from-here, seeking, system media controls, shuffle, and repeat. Recently played tracks load incrementally on the home page.
- Verify saved credentials against the user's Apple Music storefront before using the library features.
- GitHub Actions builds an Inno Setup installer for x64 and ARM64 on every push.

Apple's public API documentation describes the Music User Token for personal library calls and `playParams` as a way for a MusicKit player to begin playback. Apple's [MusicKit overview](https://developer.apple.com/musickit/) documents native playback SDKs for Apple platforms and Android, and browser playback through MusicKit on the Web; it does not list a native Windows playback SDK. This client therefore plays catalog previews only. Full subscription playback remains unimplemented.

## Credentials

Open **Settings** and paste an Apple Music developer token generated for your MusicKit media identifier. For personal library access, also provide a Music User Token or a full Apple Music cookie header containing `media-user-token`. AM Native extracts the Music User Token for Apple's documented `Music-User-Token` header and stores it in Windows Credential Manager. It also retains a supplied cookie header in DPAPI-protected local app data for a future native playback adapter. The REST API client sends only the documented token headers to `api.music.apple.com`; it never sends the web cookie. Apple documents how to [generate developer tokens](https://developer.apple.com/documentation/applemusicapi/generating-developer-tokens) and [authorize a user](https://developer.apple.com/documentation/applemusicapi/user-authentication-for-musickit). The app does not log token or cookie values.

## Build on Windows

Install the .NET 10 SDK and build tools for WinUI 3, then run:

```powershell
dotnet publish AmNative/AmNative.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o artifacts/publish/win-x64
```

The GitHub Actions workflow publishes x64 and ARM64 installers as downloadable artifacts for each push. Direct-download installers are currently unsigned; signing credentials are not present in this repository.
