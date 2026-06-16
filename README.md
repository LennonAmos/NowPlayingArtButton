# Now Playing Art Button

Macro Deck 2 plugin for Windows that turns a button into a live now-playing tile.

The plugin reads Windows media session metadata locally, renders the current cover art as a square button image, and overlays:

`Song title - artist`

No login, API key, browser extension, upload, or admin permission is required.

## Use

1. Install the plugin into Macro Deck.
2. Add the `Use as now-playing art` action to any button.
3. Press that button once.
4. Start media in an app that exposes Windows media metadata, such as Spotify, Edge, Chrome, or YouTube Music.

The button updates about once per second when the song changes.

## Build

```powershell
dotnet build .\NowPlayingArtButton.csproj -c Release
```

Copy the release output and manifest files into:

`%APPDATA%\Macro Deck\plugins\lenno.NowPlayingArtButton`
