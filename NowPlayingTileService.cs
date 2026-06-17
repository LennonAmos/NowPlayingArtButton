using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Folders;
using SuchByte.MacroDeck.Icons;
using SuchByte.MacroDeck.Logging;
using SuchByte.MacroDeck.Plugins;
using SuchByte.MacroDeck.Profiles;
using SuchByte.MacroDeck.Server;
using SuchByte.MacroDeck.Utils;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NowPlayingArtButton;

internal static class NowPlayingTileService
{
    private const int TileSize = 512;
    private const string DynamicIconPackName =
#if DEV_BUILD
        "Now Playing Art Button Dev";
#else
        "Now Playing Art Button";
#endif
    private const string DynamicIconPackAuthor = "lenno";
    private const string DynamicIconPackPackageId =
#if DEV_BUILD
        "lenno.NowPlayingArtButton.Dev.DynamicIcons";
#else
        "lenno.NowPlayingArtButton.DynamicIcons";
#endif
    private const string DynamicIconId = "current";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ActionButton> Buttons = new();
    private static readonly Dictionary<string, OnlineMetadata?> MetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private static readonly System.Threading.Timer Timer = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static MacroDeckPlugin? plugin;
    private static bool isPolling;
    private static bool dynamicIconPackPrepared;
    private static string lastFingerprint = string.Empty;
    private static string lastBase64 = string.Empty;
    private static byte[]? lastPngBytes;

    public static void SetPlugin(MacroDeckPlugin macroDeckPlugin)
    {
        plugin = macroDeckPlugin;
        EnsureDynamicIconPack();
        DiscoverButtons();
        Timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static void RegisterButton(ActionButton actionButton)
    {
        lock (Gate)
        {
            Buttons[actionButton.Guid] = actionButton;
        }

        ApplyImage(actionButton, lastBase64);
        _ = UpdateNowPlayingAsync();
    }

    public static void PruneMissingButtons()
    {
        HashSet<string> liveGuids;
        try
        {
            liveGuids = ProfileManager.Profiles
                .ToArray()
                .SelectMany(profile => profile.Folders.ToArray())
                .SelectMany(folder => folder.ActionButtons.ToArray())
                .Select(button => button.Guid)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        lock (Gate)
        {
            foreach (var guid in Buttons.Keys.Where(guid => !liveGuids.Contains(guid)).ToArray())
            {
                Buttons.Remove(guid);
            }
        }
    }

    private static void DiscoverButtons()
    {
        List<ActionButton> buttons;
        try
        {
            buttons = ProfileManager.Profiles
                .ToArray()
                .SelectMany(profile => profile.Folders.ToArray())
                .SelectMany(folder => folder.ActionButtons.ToArray())
                .Where(button => HasNowPlayingAction(button) ||
                                 UsesNowPlayingIcon(button) ||
                                 HasNowPlayingVariableLabel(button) ||
                                 LooksLikeExistingNowPlayingTile(button))
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        lock (Gate)
        {
            foreach (var button in buttons)
            {
                Buttons[button.Guid] = button;
            }
        }
    }

    private static bool HasNowPlayingAction(ActionButton button)
    {
        return button.Actions.Any(action =>
            action is UseNowPlayingArtButtonAction ||
            action is OpenLyricsProfileAction);
    }

    private static bool UsesNowPlayingIcon(ActionButton button)
    {
        return IsNowPlayingIcon(button.IconOff) || IsNowPlayingIcon(button.IconOn);
    }

    private static bool IsNowPlayingIcon(string? icon)
    {
        return !string.IsNullOrWhiteSpace(icon) &&
               icon.StartsWith("Now Playing Art Button", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExistingNowPlayingTile(ActionButton button)
    {
        return string.IsNullOrEmpty(button.IconOff) &&
               string.IsNullOrWhiteSpace(button.LabelOff?.LabelText) &&
               string.IsNullOrWhiteSpace(button.LabelOn?.LabelText) &&
               (button.LabelOff?.LabelBase64?.Length ?? 0) > 100000;
    }

    private static bool HasNowPlayingVariableLabel(ActionButton button)
    {
        return IsNowPlayingVariableText(button.LabelOff?.LabelText) ||
               IsNowPlayingVariableText(button.LabelOn?.LabelText);
    }

    private static bool IsNowPlayingVariableText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("current_playing_title", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("current_playing_artist", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("youtube_title", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("youtube_artist", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("wnp_title", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("wnp_artist", StringComparison.OrdinalIgnoreCase);
    }

    private static void Tick()
    {
        lock (Gate)
        {
            DiscoverButtons();
            if (isPolling)
            {
                return;
            }

            isPolling = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateNowPlayingAsync();
            }
            finally
            {
                lock (Gate)
                {
                    isPolling = false;
                }
            }
        });
    }

    private static async Task UpdateNowPlayingAsync()
    {
        try
        {
            var track = await GetCurrentTrackAsync();
            track = await EnrichOnlineMetadataAsync(track);
            var fingerprint = track.GetFingerprint();
            if (fingerprint == lastFingerprint)
            {
                ApplyCachedImageToDiscoveredButtons();
                _ = PrefetchLikelyNextTrackAsync(track);
                return;
            }

            var renderedTile = RenderTile(track);
            lastFingerprint = fingerprint;
            lastBase64 = renderedTile.Base64;
            lastPngBytes = renderedTile.PngBytes;
            SaveDynamicIcon(renderedTile.PngBytes);

            List<ActionButton> buttons;
            lock (Gate)
            {
                buttons = Buttons.Values.ToList();
            }

            foreach (var button in buttons)
            {
                ApplyImage(button, renderedTile.Base64);
            }

            _ = PrefetchLikelyNextTrackAsync(track);
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Error(plugin, $"Now Playing Art Button update failed.\r\n{ex}");
            }
        }
    }

    private static void ApplyCachedImageToDiscoveredButtons()
    {
        if (string.IsNullOrEmpty(lastBase64))
        {
            return;
        }

        List<ActionButton> buttons;
        lock (Gate)
        {
            buttons = Buttons.Values
                .Where(button => button.LabelOff?.LabelBase64 != lastBase64 ||
                                 button.LabelOn?.LabelBase64 != lastBase64 ||
                                 HasNowPlayingVariableLabel(button))
                .ToList();
        }

        foreach (var button in buttons)
        {
            ApplyImage(button, lastBase64);
        }
    }

    private static async Task<TrackInfo> GetCurrentTrackAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession()
            ?? manager.GetSessions().FirstOrDefault();

        if (session is null)
        {
            return TrackInfo.Empty;
        }

        var properties = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var status = playback.PlaybackStatus.ToString();
        var thumbnailBytes = await ReadThumbnailAsync(properties.Thumbnail);

        var title = CleanTrackText(properties.Title);
        var artist = CleanTrackText(properties.Artist);
        if (artist.Equals("Topic", StringComparison.OrdinalIgnoreCase))
        {
            var separator = title.LastIndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator < title.Length - 3)
            {
                artist = title[(separator + 3)..].Trim();
                title = title[..separator].Trim();
            }
            else
            {
                artist = string.Empty;
            }
        }

        SplitYouTubeByTitle(ref title, ref artist);

        return new TrackInfo(
            Normalize(title, "Nothing playing"),
            Normalize(artist, "Unknown artist"),
            status,
            thumbnailBytes);
    }

    private static async Task<TrackInfo> EnrichOnlineMetadataAsync(TrackInfo track)
    {
        if (track.IsEmpty)
        {
            return track;
        }

        var lookup = BuildLookup(track);
        if (string.IsNullOrWhiteSpace(lookup))
        {
            return track;
        }

        OnlineMetadata? metadata;
        lock (Gate)
        {
            if (MetadataCache.TryGetValue(lookup, out metadata))
            {
                return ApplyOnlineMetadata(track, metadata);
            }
        }

        metadata = await LookupOnlineMetadataAsync(lookup);
        if (plugin is not null)
        {
            MacroDeckLogger.Info(plugin, metadata is { ArtworkBytes.Length: > 0 }
                ? $"Now Playing Art Button: online metadata found for \"{lookup}\" -> \"{metadata.Title}\" / \"{metadata.Artist}\"."
                : $"Now Playing Art Button: online metadata not found for \"{lookup}\".");
        }

        lock (Gate)
        {
            MetadataCache[lookup] = metadata;
            if (MetadataCache.Count > 100)
            {
                MetadataCache.Remove(MetadataCache.Keys.First());
            }
        }

        return ApplyOnlineMetadata(track, metadata);
    }

    private static TrackInfo ApplyOnlineMetadata(TrackInfo track, OnlineMetadata? metadata)
    {
        if (metadata is null)
        {
            return track;
        }

        return track with
        {
            Title = Normalize(CleanTrackText(metadata.Title), track.Title),
            Artist = Normalize(CleanTrackText(metadata.Artist), track.Artist),
            ThumbnailBytes = metadata.ArtworkBytes is { Length: > 0 }
                ? metadata.ArtworkBytes
                : track.ThumbnailBytes
        };
    }

    private static string BuildLookup(TrackInfo track)
    {
        var title = CleanTrackText(track.Title);
        var artist = CleanTrackText(track.Artist);
        if (title.Equals("Nothing playing", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (artist.Equals("Unknown artist", StringComparison.OrdinalIgnoreCase))
        {
            artist = string.Empty;
        }

        return string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
    }

    private static string CleanTrackText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = value.Trim();
        foreach (var suffix in new[]
                 {
                     " - YouTube Music",
                     " - YouTube",
                     " - Topic",
                     " (Official Video)",
                     " [Official Video]",
                     " (Official Audio)",
                     " [Official Audio]"
                 })
        {
            cleaned = cleaned.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return cleaned.Trim();
    }

    private static void SplitYouTubeByTitle(ref string title, ref string artist)
    {
        if (!string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var byIndex = title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (byIndex <= 0 || byIndex >= title.Length - 4)
        {
            return;
        }

        artist = title[(byIndex + 4)..].Trim();
        title = title[..byIndex].Trim();
    }

    private static async Task<OnlineMetadata?> LookupOnlineMetadataAsync(string lookup)
    {
        return await LookupITunesMetadataAsync(lookup)
            ?? await LookupDeezerMetadataAsync(lookup);
    }

    private static async Task<OnlineMetadata?> LookupITunesMetadataAsync(string lookup)
    {
        try
        {
            var url = $"https://itunes.apple.com/search?media=music&entity=song&limit=1&term={Uri.EscapeDataString(lookup)}";
            using var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var results = json.RootElement.GetProperty("results");
            if (results.GetArrayLength() == 0)
            {
                return null;
            }

            var artworkUrl = results[0].TryGetProperty("artworkUrl100", out var artwork)
                ? artwork.GetString()
                : null;
            artworkUrl = UpgradeArtworkUrl(artworkUrl);
            var title = results[0].TryGetProperty("trackName", out var trackName)
                ? trackName.GetString() ?? string.Empty
                : string.Empty;
            var artist = results[0].TryGetProperty("artistName", out var artistName)
                ? artistName.GetString() ?? string.Empty
                : string.Empty;
            return new OnlineMetadata(title, artist, await DownloadArtworkAsync(artworkUrl));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<OnlineMetadata?> LookupDeezerMetadataAsync(string lookup)
    {
        try
        {
            var url = $"https://api.deezer.com/search?q={Uri.EscapeDataString(lookup)}&limit=1";
            using var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            if (!json.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
            {
                return null;
            }

            if (!data[0].TryGetProperty("album", out var album))
            {
                return null;
            }

            var artworkUrl = album.TryGetProperty("cover_xl", out var coverXl)
                ? coverXl.GetString()
                : album.TryGetProperty("cover_big", out var coverBig)
                    ? coverBig.GetString()
                    : null;
            var title = data[0].TryGetProperty("title_short", out var titleShort)
                ? titleShort.GetString() ?? string.Empty
                : data[0].TryGetProperty("title", out var titleFull)
                    ? titleFull.GetString() ?? string.Empty
                    : string.Empty;
            var artist = data[0].TryGetProperty("artist", out var artistNode) &&
                         artistNode.TryGetProperty("name", out var artistName)
                ? artistName.GetString() ?? string.Empty
                : string.Empty;
            return new OnlineMetadata(title, artist, await DownloadArtworkAsync(artworkUrl));
        }
        catch
        {
            return null;
        }
    }

    private static async Task PrefetchLikelyNextTrackAsync(TrackInfo currentTrack)
    {
        // Windows media sessions do not expose YouTube/YouTube Music queue items.
        // This keeps the online cache warm for the current canonical song; a browser
        // extension can later provide the actual queue when available.
        var lookup = BuildLookup(currentTrack);
        if (string.IsNullOrWhiteSpace(lookup))
        {
            return;
        }

        lock (Gate)
        {
            if (MetadataCache.ContainsKey(lookup))
            {
                return;
            }
        }

        _ = await LookupOnlineMetadataAsync(lookup);
    }

    private static string? UpgradeArtworkUrl(string? artworkUrl)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            return null;
        }

        return artworkUrl
            .Replace("100x100bb", "600x600bb", StringComparison.OrdinalIgnoreCase)
            .Replace("100x100-75", "600x600-75", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]?> DownloadArtworkAsync(string? artworkUrl)
    {
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            return null;
        }

        try
        {
            using var response = await HttpClient.GetAsync(artworkUrl);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        using var stream = await thumbnail.OpenReadAsync();
        if (stream.Size == 0)
        {
            return null;
        }

        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static RenderedTile RenderTile(TrackInfo track)
    {
        using var bitmap = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var cover = CreateCoverImage(track.ThumbnailBytes);
        DrawCoverFill(graphics, cover);
        DrawScrim(graphics);
        DrawText(graphics, track);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new RenderedTile(Base64.GetBase64FromImage(bitmap), stream.ToArray());
    }

    private static Image CreateCoverImage(byte[]? thumbnailBytes)
    {
        if (thumbnailBytes is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(thumbnailBytes);
                using var image = Image.FromStream(stream);
                return new Bitmap(image);
            }
            catch
            {
                // Fall through to generated placeholder art.
            }
        }

        var placeholder = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(placeholder);
        using var background = new LinearGradientBrush(
            new Rectangle(0, 0, TileSize, TileSize),
            Color.FromArgb(28, 42, 72),
            Color.FromArgb(104, 68, 138),
            35f);
        g.FillRectangle(background, 0, 0, TileSize, TileSize);
        using var glow = new SolidBrush(Color.FromArgb(72, 255, 255, 255));
        g.FillEllipse(glow, 110, 90, 300, 300);
        using var pen = new Pen(Color.FromArgb(210, 255, 255, 255), 24f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawArc(pen, 184, 154, 128, 156, 92, 265);
        g.FillEllipse(Brushes.White, 282, 286, 62, 48);
        return placeholder;
    }

    private static void DrawCoverFill(Graphics graphics, Image cover)
    {
        var scale = Math.Max(TileSize / (float)cover.Width, TileSize / (float)cover.Height);
        var width = cover.Width * scale;
        var height = cover.Height * scale;
        var x = (TileSize - width) / 2f;
        var y = (TileSize - height) / 2f;
        graphics.DrawImage(cover, x, y, width, height);
    }

    private static void DrawScrim(Graphics graphics)
    {
        using var bottomScrim = new LinearGradientBrush(
            new Rectangle(0, 210, TileSize, 302),
            Color.FromArgb(0, 0, 0, 0),
            Color.FromArgb(235, 0, 0, 0),
            90f);
        graphics.FillRectangle(bottomScrim, 0, 210, TileSize, 302);

        using var vignette = new GraphicsPath();
        vignette.AddEllipse(-110, -80, TileSize + 220, TileSize + 180);
        using var brush = new PathGradientBrush(vignette)
        {
            CenterColor = Color.FromArgb(0, 0, 0, 0),
            SurroundColors = [Color.FromArgb(120, 0, 0, 0)]
        };
        graphics.FillRectangle(brush, 0, 0, TileSize, TileSize);
    }

    private static void DrawText(Graphics graphics, TrackInfo track)
    {
        var title = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title
            : $"{track.Title} - {track.Artist}";
        var rect = new RectangleF(28, 268, TileSize - 56, 178);
        using var titleFont = CreateFittedFont(graphics, title, rect, 54f, 26f, FontStyle.Bold);
        using var statusFont = new Font("Segoe UI Semibold", 20f, FontStyle.Regular);
        using var white = new SolidBrush(Color.FromArgb(248, 255, 255, 255));
        using var muted = new SolidBrush(Color.FromArgb(206, 255, 255, 255));
        using var shadow = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Far,
            Trimming = StringTrimming.EllipsisWord,
            FormatFlags = StringFormatFlags.LineLimit
        };

        var shadowRect = new RectangleF(rect.X + 2, rect.Y + 3, rect.Width, rect.Height);
        graphics.DrawString(title, titleFont, shadow, shadowRect, format);
        graphics.DrawString(title, titleFont, white, rect, format);

        var status = track.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase)
            ? "Now playing"
            : track.PlaybackStatus;
        graphics.DrawString(status, statusFont, shadow, 36, 458);
        graphics.DrawString(status, statusFont, muted, 34, 456);
    }

    private static Font CreateFittedFont(Graphics graphics, string text, RectangleF rect, float max, float min, FontStyle style)
    {
        for (var size = max; size >= min; size -= 1f)
        {
            var font = new Font("Segoe UI Semibold", size, style);
            var measured = graphics.MeasureString(text, font, (int)rect.Width);
            if (measured.Height <= rect.Height)
            {
                return font;
            }

            font.Dispose();
        }

        return new Font("Segoe UI Semibold", min, style);
    }

    private static void ApplyImage(ActionButton button, string imageBase64)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return;
        }

        var structuralChange = !string.IsNullOrWhiteSpace(button.IconOff) ||
                               !string.IsNullOrWhiteSpace(button.IconOn) ||
                               button.BackColorOff != Color.FromArgb(16, 18, 24) ||
                               button.BackColorOn != Color.FromArgb(16, 18, 24);

        button.IconOff = string.Empty;
        button.IconOn = string.Empty;
        button.LabelOff ??= new ButtonLabel();
        button.LabelOn ??= new ButtonLabel();
        button.LabelOff.LabelBase64 = imageBase64;
        button.LabelOn.LabelBase64 = imageBase64;
        button.LabelOff.LabelText = ".";
        button.LabelOn.LabelText = ".";
        button.LabelOff.LabelColor = Color.Transparent;
        button.LabelOn.LabelColor = Color.Transparent;
        button.LabelOff.LabelPosition = ButtonLabelPosition.CENTER;
        button.LabelOn.LabelPosition = ButtonLabelPosition.CENTER;
        button.BackColorOff = Color.FromArgb(16, 18, 24);
        button.BackColorOn = Color.FromArgb(16, 18, 24);
        if (button.Actions.All(action => action is not UseNowPlayingArtButtonAction))
        {
            button.Actions.Add(new UseNowPlayingArtButtonAction());
            structuralChange = true;
        }

        if (button.Actions.All(action => action is not OpenLyricsProfileAction))
        {
            button.Actions.Add(new OpenLyricsProfileAction());
            structuralChange = true;
        }

        RefreshButton(button);
        if (structuralChange)
        {
            ProfileManager.Save();
        }
    }

    private static void EnsureDynamicIconPack()
    {
        if (dynamicIconPackPrepared)
        {
            return;
        }

        try
        {
            var folder = GetDynamicIconPackFolder();
            Directory.CreateDirectory(folder);
            var manifestPath = Path.Combine(folder, "ExtensionManifest.json");
            if (!File.Exists(manifestPath))
            {
                File.WriteAllText(manifestPath, $$"""
                {
                  "type": 1,
                  "name": "{{DynamicIconPackName}}",
                  "author": "lenno",
                  "repository": "https://github.com/LennonAmos/NowPlayingArtButton",
                  "packageId": "{{DynamicIconPackPackageId}}",
                  "version": "1.0.0",
                  "target-plugin-api-version": 40,
                  "dll": ""
                }
                """);
            }

            var iconPath = Path.Combine(folder, $"{DynamicIconId}.png");
            if (!File.Exists(iconPath))
            {
                TryWriteAllBytes(iconPath, CreatePlaceholderIcon());
            }

            dynamicIconPackPrepared = true;
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Warning(plugin, $"Now Playing Art Button could not prepare dynamic icon pack.\r\n{ex}");
            }
        }
    }

    private static void SaveDynamicIcon(byte[] pngBytes)
    {
        try
        {
            var folder = GetDynamicIconPackFolder();
            Directory.CreateDirectory(folder);
            TryWriteAllBytes(Path.Combine(folder, $"{DynamicIconId}.png"), pngBytes);
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Warning(plugin, $"Now Playing Art Button could not update dynamic icon.\r\n{ex}");
            }
        }
    }

    private static bool TryWriteAllBytes(string path, byte[] bytes)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllBytes(tempPath, bytes);
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }

        return false;
    }

    private static byte[] CreatePlaceholderIcon()
    {
        using var bitmap = new Bitmap(TileSize, TileSize);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(16, 18, 24));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static string GetDynamicIconPackFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macro Deck",
            "iconpacks",
            DynamicIconPackPackageId);
    }

    private static void RefreshButton(ActionButton button)
    {
        try
        {
            var method = typeof(MacroDeckServer).GetMethod(
                "UpdateState",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                [typeof(ActionButton)],
                null);
            method?.Invoke(null, [button]);
        }
        catch
        {
            var folder = FindFolder(button);
            if (folder is not null)
            {
                MacroDeckServer.UpdateFolder(folder);
            }
        }
    }

    private static MacroDeckFolder? FindFolder(ActionButton button)
    {
        return ProfileManager.Profiles
            .SelectMany(profile => profile.Folders)
            .FirstOrDefault(folder => folder.ActionButtons.Contains(button));
    }

    private static string Normalize(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record RenderedTile(string Base64, byte[] PngBytes);

    private sealed record OnlineMetadata(string Title, string Artist, byte[]? ArtworkBytes);

    private sealed record TrackInfo(string Title, string Artist, string PlaybackStatus, byte[]? ThumbnailBytes)
    {
        public static TrackInfo Empty { get; } = new("Nothing playing", "", "Stopped", null);

        public bool IsEmpty => Title.Equals("Nothing playing", StringComparison.OrdinalIgnoreCase);

        public bool HasUsableThumbnail => ThumbnailBytes is { Length: > 4096 };

        public string GetFingerprint()
        {
            return $"{Title}\u001f{Artist}\u001f{PlaybackStatus}\u001f{ThumbnailBytes?.Length ?? 0}";
        }
    }
}
