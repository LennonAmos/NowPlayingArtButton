# Now Playing Art Button

Macro Deck 2 plugin for Windows that turns a button into a live now-playing tile.

The plugin reads Windows media session metadata locally, renders the current cover art as a square button image, and overlays:

`Song title - artist`

If Windows does not provide usable cover art, the plugin searches public online music metadata sources for artwork using the current title and artist. No login, API key, browser extension, upload, or admin permission is required.

## Use

1. Install the plugin into Macro Deck.
2. Open Macro Deck and edit the button you want to use.
3. Add the `Now Playing Art Button` action named `Use as now-playing art`.
4. Save the button.
5. Press that button once on Macro Deck. The plugin will remember that button after restarts.
6. Start media in an app that exposes Windows media metadata, such as Spotify, Edge, Chrome, or YouTube Music.

The button updates about once per second when the song changes.

The plugin also auto-detects existing label buttons that use common now-playing variables such as `{current_playing_title}`, `{current_playing_artist}`, `{youtube_title}`, `{youtube_artist}`, `{wnp_title}`, or `{wnp_artist}` and turns those into cover-art tiles.

## Online artwork lookup

Only the song title and artist are sent to public music search endpoints. The plugin does not upload audio, files, playlists, profiles, accounts, tokens, or Macro Deck data.

## Build

```powershell
dotnet build .\NowPlayingArtButton.csproj -c Release
```

Copy the release output and manifest files into:

`%APPDATA%\Macro Deck\plugins\lenno.NowPlayingArtButton`
