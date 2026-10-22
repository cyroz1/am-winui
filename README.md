# AM Native

AM Native is a native Windows desktop client for Apple Music. Its interface uses WinUI 3 from the Windows App SDK. Music playback uses Apple's hosted MusicKit on the Web player in a small WebView2 surface; the Evergreen WebView2 runtime is shared with Microsoft Edge rather than bundled with the app. The build targets Windows 10 2004+ and produces x64 and ARM64 installers.

[Home screen preview](screenshots/am-native-home-preview.png) — static UI preview with sample data, not a capture of the running app.

## Current state

- Apple Music catalog search for songs, albums, artists, playlists, music videos, radio stations, activities, curators, Apple curators, and record labels, with live catalog search suggestions; filter by type or genre; browse activity and curator playlists or a label's latest and top releases; apply documented likes and dislikes to supported catalog results.
- Browse Apple's daily global charts for songs, albums, playlists, and music videos.
- Load library, catalog-search, and library-search results incrementally using Apple's pagination links.
- Browse album, artist, and playlist contents incrementally; the full track list is fetched only when starting playback.
- Browse paginated library songs, albums, artists, playlists, and music videos.
- Browse the mixed recently added library feed.
- Search the user's library.
- Sort loaded library and library-search results by Apple order, title, artist, album, date added, release date, or duration.
- Sort loaded catalog search and chart results by Apple order, title, artist, album, release date, or duration.
- Filter library and library-search results to favorited items while continuing through paginated results; show when Apple omits favorite status from a response.
- Filter loaded library, library-search, catalog-search, and chart results by Apple-provided genre; options expand as additional pages are loaded.
- Show genres, release dates, track/disc numbers, composers, duration, explicit-content labels, and lyrics availability in shared native music rows when Apple provides that metadata.
- Load playlist folders, their child folders and playlists, and playlist tracks one page at a time; filter loaded tracks or sort them locally while preserving Apple order by default, and defer fetching the full queue until playback starts.
- Batch-add selected catalog songs, albums, playlists, and music videos to the library from Home, Search, and item details; create private or public playlists with optional descriptions and nested playlist folders, include selected tracks when creating a playlist, or batch-add selected catalog or library tracks and videos to editable playlists from Home, Search, Library, item details, or another playlist.
- Batch-add selected catalog songs, albums, artists, playlists, and music videos to favorites from Home, Search, Library, and item details; favorite library items when Apple returns their matching catalog ID.
- Load, apply, and clear like/dislike ratings for supported catalog and library items; Library shows the saved rating for the current selection.
- Play library songs and music videos through MusicKit using their Apple library IDs, including items without a catalog match. Use catalog IDs and relationships when available for video batching, preview fallback, favorites, and Apple Music share links.
- Show personalized recommendations, heavy rotation, and paginated recently played tracks, albums, artists, playlists, and stations on the home page.
- Browse Apple Music [Replay summaries](https://developer.apple.com/documentation/applemusicapi/get-the-user%27s-replay-data) for top songs, albums, and artists in the latest eligible year, with pagination and playback/library actions.
- Open albums, artists, and playlists to browse their related albums or tracks. Catalog artist pages include every [documented Apple Music artist view](https://developer.apple.com/documentation/applemusicapi/artists/views-data.dictionary): appears-on albums, compilations, featured and full albums, latest release, live albums, singles, featured playlists, similar artists, top songs, and featured or top music videos. Full catalog songs and music videos can play through MusicKit on the Web; preview audio remains available as a fallback.
- Browse the [documented catalog album views](https://developer.apple.com/documentation/applemusicapi/albums/views-data.dictionary) for playlists an album appears on, other versions, related albums, and related music videos; related videos can play through MusicKit or use an Apple-provided preview.
- Open catalog and library songs from Home, Search, or Library to browse their documented album and artist relationships, catalog matches, and (for catalog songs) music videos and radio stations. Play full songs from their detail pages after MusicKit authorization.
- Open catalog and library music videos from Home, Search, or Library to browse their documented albums and artists, catalog video matches for library videos, and songs for catalog videos. Play supported videos through MusicKit or use Apple-provided preview audio.
- Store Music User Tokens in Windows Credential Manager and optionally sign REST and origin-restricted MusicKit developer tokens locally from a `.p8` key, Team ID, and Key ID. The private key stays in Windows Credential Manager.
- Retain an optional full Apple Music web cookie using Windows DPAPI in local app data; extract its Music User Token for documented library API requests without forwarding the cookie header.
- Play full catalog and library songs and supported music videos from Home, Search, Library, Replay, album, artist, and playlist views after authorizing Apple Music. MusicKit manages Apple Music web authorization; the returned Music User Token is stored with the other credentials. Items that only expose preview audio use the native Windows media stack. Add tracks to the active queue, inspect it, play from any queued item, remove items, or clear the queue. The native player shows track artwork and playback errors, and has seeking, volume and mute controls, system media controls, shuffle, and repeat. **Open in Apple Music** hands a validated Apple Music link to the Windows default app. Recently played tracks load incrementally on the home page.
- Restore the current queue, selected track, shuffle/repeat state, and volume after relaunch; the restored queue stays paused until you press Play.
- Navigate between Listen now, Search, Library, Replay, and Playlists with `Ctrl+1` through `Ctrl+5`; press `Ctrl+F` to open Search and focus its search field.
- Use the native WinUI Mica window backdrop, which falls back to a solid surface on unsupported Windows versions.
- Follow the Windows light/dark theme or choose a persistent app-specific appearance in Settings.
- Verify saved credentials against the user's Apple Music storefront before using the library features.
- GitHub Actions builds an Inno Setup installer for x64 and ARM64 on every push.

Apple documents MusicKit on the Web as a way to stream songs, music videos, and radio in a browser, and says it automatically manages Music User Tokens for web apps. AM Native hosts that official player in WebView2 and keeps the rest of the client native. Microsoft recommends the shared Evergreen runtime for this model; the installer carries only Microsoft's signed bootstrapper, which installs the runtime when it is missing. See Apple's [MusicKit overview](https://developer.apple.com/musickit/) and [MusicKit user authentication](https://developer.apple.com/documentation/applemusicapi/user-authentication-for-musickit), and Microsoft's [WebView2 distribution guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

## Credentials

Open **Settings** and either paste an Apple Music developer token or provide the Team ID and Key ID for your MusicKit key and select its `.p8` file. If a pasted token includes an `origin` claim, it must allow `https://appassets.amnative`. AM Native signs one-hour ES256 tokens for REST calls and a six-month, origin-restricted token for MusicKit on the Web; the key stays in Windows Credential Manager. Select **Authorize Apple Music playback** or start a full-track queue, then sign in through Apple's MusicKit flow. MusicKit stores its web session in the local WebView2 profile and AM Native stores its Music User Token in Windows Credential Manager for library API access, including syncing the token from an already-authorized WebView session when that player session is initialized. **Forget credentials** also clears Apple sign-in site data from that isolated profile. You can also provide a Music User Token or a full Apple Music cookie header containing `media-user-token`; AM Native extracts the token for Apple's documented `Music-User-Token` header and retains a supplied cookie header in DPAPI-protected local app data without forwarding it to Apple's REST API. Apple documents how to [generate developer tokens](https://developer.apple.com/documentation/applemusicapi/generating-developer-tokens) and [authorize a user](https://developer.apple.com/documentation/applemusicapi/user-authentication-for-musickit). The app does not log token or cookie values.

## Build on Windows

Install the .NET 10 SDK and build tools for WinUI 3, then run:

```powershell
dotnet publish AmNative/AmNative.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o artifacts/publish/win-x64
```

The GitHub Actions workflow publishes x64 and ARM64 installers as downloadable artifacts for each push. Each installer includes Microsoft's Authenticode-verified Visual C++ Redistributable for its target architecture and the Evergreen WebView2 bootstrapper. Setup requests administrator permission to install those shared runtimes. WebView2 itself is not bundled with the app. Direct-download installers are currently unsigned; signing credentials are not present in this repository.
