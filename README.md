# Now Playing Art + Lyrics

Macro Deck 2 plugin for Windows that turns a button into a live now-playing cover-art tile and can open a full-grid synced lyrics screen.

The plugin reads Windows media session metadata locally, cleans it up with public online music metadata where possible, renders the current cover art as a square button image, and overlays:

`Song title - artist`

If Windows does not provide usable cover art, the plugin searches public online music metadata sources for artwork using the current title and artist. It also writes generated tiles into local Macro Deck icon packs so phone clients can receive them as normal button icons. No login, API key, browser extension, upload, or admin permission is required.

## Use

1. Install the plugin into Macro Deck.
2. Open Macro Deck and edit the button you want to use.
3. Add the `Now Playing Art + Lyrics` action named `Use as now-playing art`.
4. Save the button.
5. Press that button once on Macro Deck. The plugin will remember that button after restarts.
6. Start media in an app that exposes Windows media metadata, such as Spotify, Edge, Chrome, or YouTube Music.

The button updates about once per second when the song changes.

The plugin also auto-detects existing label buttons that use common now-playing variables such as `{current_playing_title}`, `{current_playing_artist}`, `{youtube_title}`, `{youtube_artist}`, `{wnp_title}`, or `{wnp_artist}` and turns those into cover-art tiles.

## Synced lyrics profile

Add the `Open Synced Lyrics Profile` action to a normal Macro Deck button. Pressing it creates or refreshes a `Now Playing Lyrics` profile using the same grid size as your current profile, switches to it, and remembers the previous profile for that device.

Every button on the lyrics profile is a back button. Press any tile to return to the profile you came from.

While the lyrics profile is open, the plugin:

- reads the current song title, artist, playback position, duration, and artwork from the Windows media session
- fetches synced LRC lyrics from LRCLIB when available
- caches synced lyrics locally under `%APPDATA%\Macro Deck\plugins\lenno.NowPlayingArtButton\lyrics_cache`
- renders a generated lyric-video style animated background across the whole Macro Deck grid
- splits that full-screen render into dynamic icon tiles so desktop and phone clients see one joined screen

For performance, the lyrics profile polls the current playback position several times per second, but it only redraws the Macro Deck grid when the active lyric line or song changes. This keeps seeking and skipping responsive without constantly reloading every button image.

The plugin does not bundle lyric files, copyrighted videos, fan-made videos, or artwork assets. The moving background is generated locally by the plugin.

## Online artwork lookup

Only the song title and artist are sent to public music search endpoints. The plugin does not upload audio, files, playlists, profiles, accounts, tokens, or Macro Deck data.

## Online lyrics lookup

Only the song title, artist, and duration are sent to LRCLIB to request synchronized LRC timing. LRCLIB does not require a login or API key. If synced lyrics are not available for a track, the lyrics profile shows a clear fallback message and keeps the generated background running.

## Queue detection

Windows media sessions do not expose YouTube or YouTube Music queue items, so true next-song artwork requires a companion browser extension. The plugin keeps the online metadata cache warm for the current song and is ready for queue metadata when an extension provides it.

## Build

```powershell
dotnet build .\NowPlayingArtButton.csproj -c Release
```

Copy the release output and manifest files into:

`%APPDATA%\Macro Deck\plugins\lenno.NowPlayingArtButton`
