# AM Native

AM Native is a native Windows desktop client for Apple Music. The UI uses WinUI 3 from the Windows App SDK; it does not embed a browser or ship a web runtime. The build targets Windows 10 2004+ and produces x64 and ARM64 installers.

[Home screen preview](screenshots/am-native-home-preview.png) — static UI preview with sample data, not a capture of the running app.

## Current state

- Apple Music catalog search for songs, albums, artists, playlists, music videos, and radio stations, with live catalog search suggestions; apply documented likes and dislikes to supported catalog results.
- Browse Apple's daily global charts for songs, albums, playlists, and music videos.
- Load library, catalog-search, and library-search results incrementally using Apple's pagination links.
- Browse album, artist, and playlist contents incrementally; the full track list is fetched only when starting a preview queue.
- Browse paginated library songs, albums, artists, playlists, and music videos.
- Browse the mixed recently added library feed.
- Search the user's library.
- Sort loaded library and library-search results by Apple order, title, artist, album, or duration.
- Filter library and library-search results to favorited items while continuing through paginated results; show when Apple omits favorite status from a response.
- Filter loaded library, library-search, catalog-search, and chart results by Apple-provided genre; options expand as additional pages are loaded.
- Show genres, duration, explicit-content labels, and lyrics availability in shared native music rows when Apple provides that metadata.
- Load playlist folders, their child folders and playlists, and playlist tracks one page at a time; filter loaded tracks or sort them locally while preserving Apple order by default, and defer fetching the full queue until playback starts.
- Batch-add selected catalog songs, albums, playlists, and music videos to the library from Home, Search, and item details; create private or public playlists with optional descriptions and nested playlist folders, include selected tracks when creating a playlist, or batch-add selected catalog or library tracks and videos to editable playlists from Home, Search, Library, item details, or another playlist.
- Batch-add selected catalog songs, albums, artists, playlists, and music videos to favorites from Home, Search, Library, and item details; favorite library items when Apple returns their matching catalog ID.
- Load, apply, and clear like/dislike ratings for supported catalog and library items; Library shows the saved rating for the current selection.
- Resolve Apple-provided catalog previews for library songs and music videos when their `playParams` include a catalog ID; video previews render in the native player.
- Show personalized recommendations, heavy rotation, and paginated recently played tracks, albums, artists, playlists, and stations on the home page.
- Browse Apple Music [Replay summaries](https://developer.apple.com/documentation/applemusicapi/get-the-user%27s-replay-data) for top songs, albums, and artists in the latest eligible year, with pagination and native preview/library actions.
- Open albums, artists, and playlists to browse their related albums or tracks. Catalog artist pages include every [documented Apple Music artist view](https://developer.apple.com/documentation/applemusicapi/artists/views-data.dictionary): appears-on albums, compilations, featured and full albums, latest release, live albums, singles, featured playlists, similar artists, top songs, and featured or top music videos. Song and video views can play Apple-provided previews.
- Browse the [documented catalog album views](https://developer.apple.com/documentation/applemusicapi/albums/views-data.dictionary) for playlists an album appears on, other versions, related albums, and related music videos; related videos can play Apple-provided previews.
- Open catalog and library songs from Home, Search, or Library to browse their documented album and artist relationships, catalog matches, and (for catalog songs) music videos and radio stations. Play the song preview from its detail page.
- Open catalog and library music videos from Home, Search, or Library to browse their documented albums and artists, catalog video matches for library videos, and songs for catalog videos. Play an Apple-provided video preview from its detail page.
- Store Music User Tokens in Windows Credential Manager and optionally sign one-hour Apple Music developer tokens locally from a `.p8` key, Team ID, and Key ID. The private key stays in Windows Credential Manager.
- Retain an optional full Apple Music web cookie using Windows DPAPI in local app data; extract its Music User Token for documented library API requests without forwarding the cookie header.
- Play Apple-provided audio and music-video previews from multi-select Home, Search, Library, Replay, album, artist, and playlist views through the native Windows media stack; use Apple's HLS preview URL when no direct preview URL is available, and render music-video previews in the player. Add selections to the active queue without interrupting the current preview, inspect the visible queue, play from any queued item, reorder upcoming previews, remove queue entries, or clear the queue and stop playback. Upcoming previews can be reordered while shuffle is off; removing the current item advances to the next available preview. The player shows current track artwork and playback errors, and has seeking, volume and mute controls, system media controls, shuffle, and repeat. Use **Open in Apple Music** to hand the current item’s Apple Music link to the Windows default app, typically a browser, for full playback outside AM Native. Recently played tracks load incrementally on the home page.
- Restore the current preview queue, selected track, shuffle/repeat state, and volume after relaunch; the restored queue stays paused until you press Play.
- Navigate between Listen now, Search, Library, Replay, and Playlists with `Ctrl+1` through `Ctrl+5`; press `Ctrl+F` to open Search and focus its search field.
- Use the native WinUI Mica window backdrop, which falls back to a solid surface on unsupported Windows versions.
- Follow the Windows light/dark theme or choose a persistent app-specific appearance in Settings.
- Verify saved credentials against the user's Apple Music storefront before using the library features.
- GitHub Actions builds an Inno Setup installer for x64 and ARM64 on every push.

Apple's public API documentation describes the Music User Token for personal library calls and `playParams` as a way for a MusicKit player to begin playback. Apple's [MusicKit overview](https://developer.apple.com/musickit/) documents native playback SDKs for Apple platforms and Android, and browser playback through MusicKit on the Web; it does not list a native Windows playback SDK. Apple's [Windows Music guide](https://support.apple.com/guide/music-windows/mus8af39b362/windows) says shared music links open in Apple Music on the web. AM Native passes only validated `https://music.apple.com` links to the [Windows default URI handler](https://learn.microsoft.com/en-us/uwp/api/windows.system.launcher.launchuriasync). In-app playback therefore remains limited to catalog previews; full subscription playback inside AM Native remains unimplemented.

## Credentials

Open **Settings** and either paste an Apple Music developer token or provide the Team ID and Key ID for your MusicKit key and select its `.p8` file. AM Native signs one-hour ES256 developer tokens on the device and stores the key in Windows Credential Manager. For personal library access, also provide a Music User Token or a full Apple Music cookie header containing `media-user-token`. AM Native extracts the Music User Token for Apple's documented `Music-User-Token` header and stores it in Windows Credential Manager. It retains a supplied cookie header in DPAPI-protected local app data, but does not forward the cookie to Apple's REST API. Apple documents how to [generate developer tokens](https://developer.apple.com/documentation/applemusicapi/generating-developer-tokens) and [authorize a user](https://developer.apple.com/documentation/applemusicapi/user-authentication-for-musickit). The app does not log token or cookie values.

## Build on Windows

Install the .NET 10 SDK and build tools for WinUI 3, then run:

```powershell
dotnet publish AmNative/AmNative.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o artifacts/publish/win-x64
```

The GitHub Actions workflow publishes x64 and ARM64 installers as downloadable artifacts for each push. Direct-download installers are currently unsigned; signing credentials are not present in this repository.
