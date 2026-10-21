# AM Native

AM Native is a native Windows desktop client for Apple Music. The UI uses WinUI 3 from the Windows App SDK; it does not embed a browser or ship a web runtime. The build targets Windows 10 2004+ and produces x64 and ARM64 installers.

[Home screen preview](screenshots/am-native-home-preview.png) — static UI preview with sample data, not a capture of the running app.

## Current state

- Apple Music catalog search for songs, albums, artists, playlists, and music videos, with live catalog search suggestions.
- Browse Apple's daily global charts for songs, albums, and playlists.
- Load library, catalog-search, and library-search results incrementally using Apple's pagination links.
- Browse album, artist, and playlist contents incrementally; the full track list is fetched only when starting a preview queue.
- Browse paginated library songs, albums, artists, playlists, and music videos.
- Browse the mixed recently added library feed.
- Search the user's library.
- Load playlist folders, their child folders and playlists, and playlist tracks one page at a time; fetching the full queue is deferred until the user starts playback.
- Batch-add selected catalog songs, albums, artists, playlists, and music videos to the library from Home, Search, and item details; create nested playlist folders and batch-add selected catalog or library tracks and videos to editable playlists from Home, Search, Library, or item details.
- Batch-add selected catalog songs, albums, artists, playlists, and music videos to favorites from Home, Search, Library, and item details; favorite library items when Apple returns their matching catalog ID.
- Apply or clear like/dislike ratings for supported catalog and library items.
- Resolve Apple-provided catalog previews for library songs when their `playParams` include a catalog ID.
- Show personalized recommendations and recently played tracks on the home page.
- Browse Apple Music [Replay summaries](https://developer.apple.com/documentation/applemusicapi/get-the-user%27s-replay-data) for top songs, albums, and artists in the latest eligible year, with pagination and native preview/library actions.
- Open albums, artists, and playlists to browse their related albums or tracks. Catalog artist pages include a paginated top songs view and native preview queues.
- Store Music User Tokens in Windows Credential Manager and optionally sign one-hour Apple Music developer tokens locally from a `.p8` key, Team ID, and Key ID. The private key stays in Windows Credential Manager.
- Retain an optional full Apple Music web cookie using Windows DPAPI in local app data; extract its Music User Token for documented library API requests without forwarding the cookie header.
- Queue Apple-provided previews from multi-select Home, Search, Library, Replay, album, artist, and playlist views through the native Windows media stack. Add selections to the active queue without interrupting the current preview, inspect the visible queue, play from any queued item, reorder upcoming previews, remove queue entries, or clear the queue and stop playback. Upcoming previews can be reordered while shuffle is off; removing the current item advances to the next available preview. The player also has seeking, volume and mute controls, system media controls, shuffle, and repeat. Recently played tracks load incrementally on the home page.
- Verify saved credentials against the user's Apple Music storefront before using the library features.
- GitHub Actions builds an Inno Setup installer for x64 and ARM64 on every push.

Apple's public API documentation describes the Music User Token for personal library calls and `playParams` as a way for a MusicKit player to begin playback. Apple's [MusicKit overview](https://developer.apple.com/musickit/) documents native playback SDKs for Apple platforms and Android, and browser playback through MusicKit on the Web; it does not list a native Windows playback SDK. This client therefore plays catalog previews only. Full subscription playback remains unimplemented.

## Credentials

Open **Settings** and either paste an Apple Music developer token or provide the Team ID and Key ID for your MusicKit key and select its `.p8` file. AM Native signs one-hour ES256 developer tokens on the device and stores the key in Windows Credential Manager. For personal library access, also provide a Music User Token or a full Apple Music cookie header containing `media-user-token`. AM Native extracts the Music User Token for Apple's documented `Music-User-Token` header and stores it in Windows Credential Manager. It retains a supplied cookie header in DPAPI-protected local app data, but does not forward the cookie to Apple's REST API. Apple documents how to [generate developer tokens](https://developer.apple.com/documentation/applemusicapi/generating-developer-tokens) and [authorize a user](https://developer.apple.com/documentation/applemusicapi/user-authentication-for-musickit). The app does not log token or cookie values.

## Build on Windows

Install the .NET 10 SDK and build tools for WinUI 3, then run:

```powershell
dotnet publish AmNative/AmNative.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o artifacts/publish/win-x64
```

The GitHub Actions workflow publishes x64 and ARM64 installers as downloadable artifacts for each push. Direct-download installers are currently unsigned; signing credentials are not present in this repository.
