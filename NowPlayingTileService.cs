using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Folders;
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
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ActionButton> Buttons = new();
    private static readonly Dictionary<string, byte[]?> ArtworkCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private static readonly System.Threading.Timer Timer = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static MacroDeckPlugin? plugin;
    private static bool isPolling;
    private static string lastFingerprint = string.Empty;
    private static string lastBase64 = string.Empty;

    public static void SetPlugin(MacroDeckPlugin macroDeckPlugin)
    {
        plugin = macroDeckPlugin;
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
        lock (Gate)
        {
            var liveGuids = ProfileManager.Profiles
                .SelectMany(profile => profile.Folders)
                .SelectMany(folder => folder.ActionButtons)
                .Select(button => button.Guid)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var guid in Buttons.Keys.Where(guid => !liveGuids.Contains(guid)).ToArray())
            {
                Buttons.Remove(guid);
            }
        }
    }

    private static void DiscoverButtons()
    {
        lock (Gate)
        {
            foreach (var button in ProfileManager.Profiles
                         .SelectMany(profile => profile.Folders)
                         .SelectMany(folder => folder.ActionButtons)
                         .Where(button => button.Actions.Any(action => action is UseNowPlayingArtButtonAction)))
            {
                Buttons[button.Guid] = button;
            }
        }
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
                return;
            }

            var imageBase64 = RenderTile(track);
            lastFingerprint = fingerprint;
            lastBase64 = imageBase64;

            List<ActionButton> buttons;
            lock (Gate)
            {
                buttons = Buttons.Values.ToList();
            }

            foreach (var button in buttons)
            {
                ApplyImage(button, imageBase64);
            }
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Error(plugin, $"Now Playing Art Button update failed.\r\n{ex}");
            }
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

        return new TrackInfo(
            Normalize(properties.Title, "Nothing playing"),
            Normalize(properties.Artist, "Unknown artist"),
            status,
            thumbnailBytes);
    }

    private static async Task<TrackInfo> EnrichOnlineMetadataAsync(TrackInfo track)
    {
        if (track.IsEmpty || track.HasUsableThumbnail)
        {
            return track;
        }

        var lookup = BuildLookup(track);
        if (string.IsNullOrWhiteSpace(lookup))
        {
            return track;
        }

        byte[]? bytes;
        lock (Gate)
        {
            if (ArtworkCache.TryGetValue(lookup, out bytes))
            {
                return track with { ThumbnailBytes = bytes };
            }
        }

        bytes = await LookupArtworkAsync(lookup);
        lock (Gate)
        {
            ArtworkCache[lookup] = bytes;
            if (ArtworkCache.Count > 80)
            {
                ArtworkCache.Remove(ArtworkCache.Keys.First());
            }
        }

        return track with { ThumbnailBytes = bytes };
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
        foreach (var suffix in new[] { " - YouTube Music", " - YouTube", " (Official Video)", " [Official Video]", " (Official Audio)", " [Official Audio]" })
        {
            cleaned = cleaned.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return cleaned.Trim();
    }

    private static async Task<byte[]?> LookupArtworkAsync(string lookup)
    {
        return await LookupITunesArtworkAsync(lookup)
            ?? await LookupDeezerArtworkAsync(lookup);
    }

    private static async Task<byte[]?> LookupITunesArtworkAsync(string lookup)
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
            return await DownloadArtworkAsync(artworkUrl);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> LookupDeezerArtworkAsync(string lookup)
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
            return await DownloadArtworkAsync(artworkUrl);
        }
        catch
        {
            return null;
        }
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

    private static string RenderTile(TrackInfo track)
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

        return Base64.GetBase64FromImage(bitmap);
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
            Color.FromArgb(220, 0, 0, 0),
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
        var rect = new RectangleF(34, 304, TileSize - 68, 128);
        using var titleFont = CreateFittedFont(graphics, title, rect, 37f, 19f, FontStyle.Bold);
        using var statusFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular);
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
        graphics.DrawString(status, statusFont, shadow, 36, 454);
        graphics.DrawString(status, statusFont, muted, 34, 452);
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

        button.IconOff = string.Empty;
        button.IconOn = string.Empty;
        button.LabelOff ??= new ButtonLabel();
        button.LabelOn ??= new ButtonLabel();
        button.LabelOff.LabelBase64 = imageBase64;
        button.LabelOn.LabelBase64 = imageBase64;
        button.LabelOff.LabelText = string.Empty;
        button.LabelOn.LabelText = string.Empty;
        button.BackColorOff = Color.FromArgb(16, 18, 24);
        button.BackColorOn = Color.FromArgb(16, 18, 24);

        RefreshButton(button);
        ProfileManager.Save();
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
