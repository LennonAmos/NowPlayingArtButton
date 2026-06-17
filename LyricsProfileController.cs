using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuchByte.MacroDeck;
using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Device;
using SuchByte.MacroDeck.Folders;
using SuchByte.MacroDeck.Icons;
using SuchByte.MacroDeck.Logging;
using SuchByte.MacroDeck.Plugins;
using SuchByte.MacroDeck.Profiles;
using SuchByte.MacroDeck.Server;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace NowPlayingArtButton;

internal readonly record struct LyricsGridSize(int Rows, int Columns);

internal static class LyricsProfileController
{
    public const string LyricsProfileName = "Now Playing Lyrics";
    private const int TileSize = 256;
    private const string DynamicIconPackName = "Now Playing Lyrics";
    private const string DynamicIconPackAuthor = "lenno";
    private const string DynamicIconPackPackageId = "lenno.NowPlayingArtButton.LyricsDynamicIcons";
    private static readonly TimeSpan LyricDisplayLead = TimeSpan.FromMilliseconds(550);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> PreviousProfileByClient = new();
    private static readonly HashSet<string> ActiveClients = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LyricsDocument> LyricsCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> TileHashes = new(StringComparer.Ordinal);
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex LrcTimestampRegex = new(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly System.Threading.Timer Timer = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static MacroDeckPlugin? plugin;
    private static bool isRendering;
    private static string lastRenderedFingerprint = string.Empty;
    private static string lastSongKey = string.Empty;
    private static LyricsDocument currentLyrics = LyricsDocument.Empty("Waiting for music");

    public static void SetPlugin(MacroDeckPlugin macroDeckPlugin)
    {
        plugin = macroDeckPlugin;
        EnsureDynamicIconPack();
        Timer.Change(TimeSpan.FromSeconds(1), PollInterval);
    }

    public static bool GoToLyricsProfile(string clientId)
    {
        EnsureLyricsProfileLayout();
        var lyricsProfile = ProfileManager.FindProfileByDisplayName(LyricsProfileName);
        if (lyricsProfile is null)
        {
            return false;
        }

        var currentProfile = GetCurrentProfile(clientId);
        if (currentProfile is not null && currentProfile.ProfileId != lyricsProfile.ProfileId)
        {
            PreviousProfileByClient[GetMemoryKey(clientId)] = currentProfile.ProfileId;
        }

        lock (Gate)
        {
            ActiveClients.Add(GetMemoryKey(clientId));
        }

        _ = RenderFrameAsync(force: true);
        return SetProfile(clientId, lyricsProfile);
    }

    public static bool ExitLyricsProfile(string clientId)
    {
        var key = GetMemoryKey(clientId);
        MacroDeckProfile? previousProfile = null;
        if (PreviousProfileByClient.TryGetValue(key, out var previousProfileId))
        {
            previousProfile = ProfileManager.FindProfileById(previousProfileId);
        }

        previousProfile ??= ProfileManager.Profiles.FirstOrDefault(profile =>
            !profile.DisplayName.Equals(LyricsProfileName, StringComparison.OrdinalIgnoreCase));
        if (previousProfile is null)
        {
            return false;
        }

        var changed = SetProfile(clientId, previousProfile);
        if (changed)
        {
            lock (Gate)
            {
                ActiveClients.Remove(key);
            }

            PreviousProfileByClient.Remove(key);
        }

        return changed;
    }

    private static void Tick()
    {
        lock (Gate)
        {
            if (ActiveClients.Count == 0 || isRendering)
            {
                return;
            }

            isRendering = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RenderFrameAsync(force: false);
            }
            finally
            {
                lock (Gate)
                {
                    isRendering = false;
                }
            }
        });
    }

    private static async Task RenderFrameAsync(bool force)
    {
        try
        {
            var profile = ProfileManager.FindProfileByDisplayName(LyricsProfileName);
            var root = profile?.Folders.FirstOrDefault(folder => folder.IsRootFolder)
                ?? profile?.Folders.FirstOrDefault();
            if (profile is null || root is null)
            {
                return;
            }

            var grid = new LyricsGridSize(Math.Max(1, profile.Rows), Math.Max(1, profile.Columns));
            var media = await ReadCurrentMediaAsync();
            if (!media.IsEmpty)
            {
                var songKey = media.GetSongKey();
                if (!songKey.Equals(lastSongKey, StringComparison.OrdinalIgnoreCase))
                {
                    lastSongKey = songKey;
                    currentLyrics = await GetLyricsForMediaAsync(media);
                }
            }
            else
            {
                currentLyrics = LyricsDocument.Empty("Nothing playing");
                lastSongKey = string.Empty;
            }

            var now = DateTimeOffset.UtcNow;
            var lyricPosition = media.IsPlaying ? media.Position + LyricDisplayLead : media.Position;
            var activeLine = currentLyrics.GetActiveLine(lyricPosition);
            var fingerprint = $"{grid.Rows}x{grid.Columns}|{media.GetRenderIdentity()}|{activeLine.Index}";
            if (!force && fingerprint.Equals(lastRenderedFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var tiles = RenderTiles(grid, media, currentLyrics, activeLine, now);
            SaveDynamicTiles(tiles);
            MacroDeckServer.UpdateFolder(root);
            lastRenderedFingerprint = fingerprint;
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Error(plugin, $"Now Playing Lyrics profile update failed.\r\n{ex}");
            }
        }
    }

    private static void EnsureLyricsProfileLayout()
    {
        if (HasValidLyricsProfileLayout())
        {
            return;
        }

        BuildLyricsProfileLayout();
    }

    private static bool HasValidLyricsProfileLayout()
    {
        var profile = ProfileManager.FindProfileByDisplayName(LyricsProfileName);
        var root = profile?.Folders.FirstOrDefault(folder => folder.IsRootFolder)
            ?? profile?.Folders.FirstOrDefault();
        var grid = GetTargetGridSize();
        if (profile is null || root is null || profile.Rows != grid.Rows || profile.Columns != grid.Columns)
        {
            return false;
        }

        if (root.ActionButtons.Count != grid.Rows * grid.Columns)
        {
            return false;
        }

        return root.ActionButtons.All(button =>
            button.Actions.Any(action => action is ExitLyricsProfileAction) &&
            button.IconOff.StartsWith($"{DynamicIconPackName}.tile_", StringComparison.Ordinal) &&
            button.IconOn.StartsWith($"{DynamicIconPackName}.tile_", StringComparison.Ordinal));
    }

    private static void BuildLyricsProfileLayout()
    {
        EnsureDynamicIconPack();
        var profile = ProfileManager.FindProfileByDisplayName(LyricsProfileName)
            ?? ProfileManager.CreateProfile(LyricsProfileName);
        if (profile is null)
        {
            return;
        }

        var sourceProfile = GetSourceProfile();
        var grid = GetTargetGridSize();
        profile.Rows = grid.Rows;
        profile.Columns = grid.Columns;
        profile.ButtonSpacing = sourceProfile?.ButtonSpacing ?? 10;
        profile.ButtonRadius = sourceProfile?.ButtonRadius ?? 40;
        profile.ButtonBackground = sourceProfile?.ButtonBackground ?? true;

        var root = profile.Folders.FirstOrDefault(folder => folder.IsRootFolder)
            ?? profile.Folders.FirstOrDefault();
        if (root is null)
        {
            root = new MacroDeckFolder
            {
                DisplayName = "*Root*",
                FolderId = Guid.NewGuid().ToString(),
                Childs = [],
                ActionButtons = []
            };
            profile.Folders.Add(root);
        }

        foreach (var button in root.ActionButtons)
        {
            button.Dispose();
        }

        root.ActionButtons.Clear();

        for (var row = 0; row < profile.Rows; row++)
        {
            for (var column = 0; column < profile.Columns; column++)
            {
                var index = row * profile.Columns + column;
                var icon = $"{DynamicIconPackName}.tile_{index:000}";
                root.ActionButtons.Add(new ActionButton
                {
                    Position_X = column,
                    Position_Y = row,
                    IconOff = icon,
                    IconOn = icon,
                    BackColorOff = Color.FromArgb(10, 12, 18),
                    BackColorOn = Color.FromArgb(10, 12, 18),
                    LabelOff = CreateEmptyLabel(),
                    LabelOn = CreateEmptyLabel(),
                    Actions = [new ExitLyricsProfileAction()],
                    ActionsRelease = [],
                    ActionsLongPress = [],
                    ActionsLongPressRelease = [],
                    EventListeners = []
                });
            }
        }

        SaveDynamicTiles(RenderTiles(grid, MediaSnapshot.Empty, LyricsDocument.Empty("Loading lyrics"), ActiveLyricLine.Empty, DateTimeOffset.UtcNow));
        ProfileManager.Save();
        MacroDeckServer.UpdateFolder(root);
    }

    private static LyricsGridSize GetTargetGridSize()
    {
        var sourceProfile = GetSourceProfile();
        var rows = sourceProfile?.Rows ?? 3;
        var columns = sourceProfile?.Columns ?? 5;
        return new LyricsGridSize(Math.Clamp(rows, 1, 12), Math.Clamp(columns, 1, 12));
    }

    private static MacroDeckProfile? GetSourceProfile()
    {
        var currentProfile = ProfileManager.CurrentProfile;
        if (currentProfile is not null &&
            !currentProfile.DisplayName.Equals(LyricsProfileName, StringComparison.OrdinalIgnoreCase))
        {
            return currentProfile;
        }

        return ProfileManager.Profiles.FirstOrDefault(profile =>
            !profile.DisplayName.Equals(LyricsProfileName, StringComparison.OrdinalIgnoreCase));
    }

    private static ButtonLabel CreateEmptyLabel()
    {
        return new ButtonLabel
        {
            LabelText = string.Empty,
            LabelPosition = ButtonLabelPosition.CENTER,
            LabelColor = Color.White,
            Size = 16,
            FontFamily = "Segoe UI"
        };
    }

    private static Dictionary<string, byte[]> RenderTiles(
        LyricsGridSize grid,
        MediaSnapshot media,
        LyricsDocument lyrics,
        ActiveLyricLine activeLine,
        DateTimeOffset now)
    {
        var width = grid.Columns * TileSize;
        var height = grid.Rows * TileSize;
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(canvas);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var phase = (float)((now.ToUnixTimeMilliseconds() % 16000) / 16000d);
        DrawLyricVideoBackground(graphics, new Rectangle(0, 0, width, height), media, phase);
        DrawLyricVideoText(graphics, new Rectangle(0, 0, width, height), media, lyrics, activeLine);

        var tiles = new Dictionary<string, byte[]>(grid.Rows * grid.Columns, StringComparer.Ordinal);
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                var index = row * grid.Columns + column;
                using var tile = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
                using (var tileGraphics = Graphics.FromImage(tile))
                {
                    tileGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                    tileGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    tileGraphics.DrawImage(
                        canvas,
                        new Rectangle(0, 0, TileSize, TileSize),
                        new Rectangle(column * TileSize, row * TileSize, TileSize, TileSize),
                        GraphicsUnit.Pixel);
                }

                using var stream = new MemoryStream();
                tile.Save(stream, ImageFormat.Png);
                tiles[$"tile_{index:000}"] = stream.ToArray();
            }
        }

        return tiles;
    }

    private static void DrawLyricVideoBackground(Graphics graphics, Rectangle bounds, MediaSnapshot media, float phase)
    {
        using var baseGradient = new LinearGradientBrush(
            bounds,
            Color.FromArgb(12, 18, 34),
            Color.FromArgb(46, 24, 76),
            28f + phase * 360f);
        graphics.FillRectangle(baseGradient, bounds);

        using var cover = CreateCoverImage(media.ThumbnailBytes);
        if (cover is not null)
        {
            DrawCoverFill(graphics, bounds, cover);
            using var coverScrim = new SolidBrush(Color.FromArgb(188, 6, 8, 16));
            graphics.FillRectangle(coverScrim, bounds);
        }

        DrawWaves(graphics, bounds, phase);
        DrawMovingLight(graphics, bounds, phase);
        DrawParticles(graphics, bounds, phase);

        using var vignettePath = new GraphicsPath();
        vignettePath.AddEllipse(bounds.Left - bounds.Width / 5, bounds.Top - bounds.Height / 3, bounds.Width * 7 / 5, bounds.Height * 5 / 3);
        using var vignette = new PathGradientBrush(vignettePath)
        {
            CenterColor = Color.FromArgb(0, 0, 0, 0),
            SurroundColors = [Color.FromArgb(145, 0, 0, 0)]
        };
        graphics.FillRectangle(vignette, bounds);
    }

    private static Image? CreateCoverImage(byte[]? thumbnailBytes)
    {
        if (thumbnailBytes is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(thumbnailBytes);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch
        {
            return null;
        }
    }

    private static void DrawCoverFill(Graphics graphics, Rectangle bounds, Image cover)
    {
        var scale = Math.Max(bounds.Width / (float)cover.Width, bounds.Height / (float)cover.Height);
        var width = cover.Width * scale;
        var height = cover.Height * scale;
        var x = bounds.Left + (bounds.Width - width) / 2f;
        var y = bounds.Top + (bounds.Height - height) / 2f;
        using var attributes = new ImageAttributes();
        var matrix = new ColorMatrix
        {
            Matrix33 = 0.35f
        };
        attributes.SetColorMatrix(matrix);
        graphics.DrawImage(
            cover,
            Rectangle.Round(new RectangleF(x, y, width, height)),
            0,
            0,
            cover.Width,
            cover.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static void DrawWaves(Graphics graphics, Rectangle bounds, float phase)
    {
        var palette = new[]
        {
            Color.FromArgb(92, 92, 185, 255),
            Color.FromArgb(72, 178, 132, 255),
            Color.FromArgb(70, 255, 176, 116),
            Color.FromArgb(64, 255, 255, 255)
        };

        for (var wave = 0; wave < palette.Length; wave++)
        {
            using var path = new GraphicsPath();
            var yBase = bounds.Top + bounds.Height * (0.46f + wave * 0.08f);
            var amplitude = bounds.Height * (0.055f + wave * 0.012f);
            var frequency = 1.6f + wave * 0.45f;
            var offset = phase * MathF.Tau * (0.45f + wave * 0.08f) + wave * 1.4f;
            path.StartFigure();
            path.AddLine(bounds.Left, bounds.Bottom, bounds.Left, yBase);
            var lastX = (float)bounds.Left;
            var lastY = yBase;
            for (var x = 0; x <= bounds.Width; x += Math.Max(8, bounds.Width / 160))
            {
                var progress = x / (float)bounds.Width;
                var y = yBase +
                        MathF.Sin(progress * MathF.Tau * frequency + offset) * amplitude +
                        MathF.Sin(progress * MathF.Tau * (frequency * 0.55f) - offset * 0.7f) * amplitude * 0.45f;
                var nextX = bounds.Left + x;
                path.AddLine(lastX, lastY, nextX, y);
                lastX = nextX;
                lastY = y;
            }

            path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);
            path.CloseFigure();
            using var brush = new SolidBrush(palette[wave]);
            graphics.FillPath(brush, path);
        }
    }

    private static void DrawMovingLight(Graphics graphics, Rectangle bounds, float phase)
    {
        var center = bounds.Left + bounds.Width * (phase * 1.6f - 0.3f);
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(center - bounds.Width * 0.18f, bounds.Top),
            new PointF(center + bounds.Width * 0.03f, bounds.Top),
            new PointF(center + bounds.Width * 0.28f, bounds.Bottom),
            new PointF(center + bounds.Width * 0.06f, bounds.Bottom)
        });
        using var brush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(0, 255, 255, 255),
            Color.FromArgb(82, 255, 255, 255),
            24f);
        graphics.FillPath(brush, path);
    }

    private static void DrawParticles(Graphics graphics, Rectangle bounds, float phase)
    {
        using var brush = new SolidBrush(Color.FromArgb(88, 255, 255, 255));
        for (var i = 0; i < 46; i++)
        {
            var seed = i * 37.7f;
            var x = bounds.Left + bounds.Width * Frac(MathF.Sin(seed) * 43758.5453f + phase * 0.08f);
            var y = bounds.Top + bounds.Height * Frac(MathF.Sin(seed + 7.3f) * 24634.6345f + phase * (0.12f + i % 5 * 0.015f));
            var size = 2.2f + (i % 5) * 0.55f;
            graphics.FillEllipse(brush, x, y, size, size);
        }
    }

    private static float Frac(float value)
    {
        return value - MathF.Floor(value);
    }

    private static void DrawLyricVideoText(
        Graphics graphics,
        Rectangle bounds,
        MediaSnapshot media,
        LyricsDocument lyrics,
        ActiveLyricLine activeLine)
    {
        var currentLine = activeLine.Text;
        if (string.IsNullOrWhiteSpace(currentLine))
        {
            currentLine = lyrics.StatusText;
        }

        if (string.IsNullOrWhiteSpace(currentLine))
        {
            currentLine = media.IsEmpty ? "Nothing playing" : "No synced lyrics found";
        }

        var titleLine = media.IsEmpty
            ? "Now Playing Lyrics"
            : string.IsNullOrWhiteSpace(media.Artist)
                ? media.Title
                : $"{media.Title} - {media.Artist}";

        var titleRect = new RectangleF(bounds.Left + bounds.Width * 0.05f, bounds.Top + bounds.Height * 0.055f, bounds.Width * 0.9f, bounds.Height * 0.12f);
        using var titleFont = CreateFittedFont(graphics, titleLine, titleRect, bounds.Height * 0.052f, bounds.Height * 0.026f, FontStyle.Bold);
        DrawShadowedText(graphics, titleLine, titleFont, titleRect, Color.FromArgb(210, 245, 250, 255), StringAlignment.Center, StringAlignment.Center);

        var previousText = lyrics.GetLineText(activeLine.Index - 1);
        var nextText = lyrics.GetLineText(activeLine.Index + 1);
        var mainRect = new RectangleF(bounds.Left + bounds.Width * 0.07f, bounds.Top + bounds.Height * 0.27f, bounds.Width * 0.86f, bounds.Height * 0.34f);
        using var mainFont = CreateFittedFont(graphics, currentLine, mainRect, bounds.Height * 0.122f, bounds.Height * 0.045f, FontStyle.Bold);
        DrawShadowedText(graphics, currentLine, mainFont, mainRect, Color.FromArgb(252, 255, 255, 255), StringAlignment.Center, StringAlignment.Center, 7f);

        if (!string.IsNullOrWhiteSpace(previousText))
        {
            var prevRect = new RectangleF(bounds.Left + bounds.Width * 0.12f, bounds.Top + bounds.Height * 0.19f, bounds.Width * 0.76f, bounds.Height * 0.08f);
            using var prevFont = CreateFittedFont(graphics, previousText, prevRect, bounds.Height * 0.042f, bounds.Height * 0.022f, FontStyle.Regular);
            DrawShadowedText(graphics, previousText, prevFont, prevRect, Color.FromArgb(132, 255, 255, 255), StringAlignment.Center, StringAlignment.Center);
        }

        if (!string.IsNullOrWhiteSpace(nextText))
        {
            var nextRect = new RectangleF(bounds.Left + bounds.Width * 0.12f, bounds.Top + bounds.Height * 0.62f, bounds.Width * 0.76f, bounds.Height * 0.1f);
            using var nextFont = CreateFittedFont(graphics, nextText, nextRect, bounds.Height * 0.048f, bounds.Height * 0.023f, FontStyle.Regular);
            DrawShadowedText(graphics, nextText, nextFont, nextRect, Color.FromArgb(166, 255, 255, 255), StringAlignment.Center, StringAlignment.Center);
        }

        DrawProgress(graphics, bounds, media);
    }

    private static void DrawProgress(Graphics graphics, Rectangle bounds, MediaSnapshot media)
    {
        if (media.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var progress = Math.Clamp(media.Position.TotalMilliseconds / Math.Max(1, media.Duration.TotalMilliseconds), 0d, 1d);
        var barWidth = bounds.Width * 0.78f;
        var barHeight = Math.Max(7f, bounds.Height * 0.012f);
        var x = bounds.Left + (bounds.Width - barWidth) / 2f;
        var y = bounds.Bottom - bounds.Height * 0.085f;
        using var background = new SolidBrush(Color.FromArgb(72, 255, 255, 255));
        using var foreground = new LinearGradientBrush(
            new RectangleF(x, y, barWidth, barHeight),
            Color.FromArgb(245, 128, 212, 255),
            Color.FromArgb(245, 255, 210, 126),
            0f);
        using var path = RoundedRect(new RectangleF(x, y, barWidth, barHeight), barHeight / 2f);
        graphics.FillPath(background, path);
        if (progress > 0)
        {
            using var progressPath = RoundedRect(new RectangleF(x, y, (float)(barWidth * progress), barHeight), barHeight / 2f);
            graphics.FillPath(foreground, progressPath);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawShadowedText(
        Graphics graphics,
        string text,
        Font font,
        RectangleF rect,
        Color color,
        StringAlignment alignment,
        StringAlignment lineAlignment,
        float shadowRadius = 4f)
    {
        using var format = new StringFormat
        {
            Alignment = alignment,
            LineAlignment = lineAlignment,
            Trimming = StringTrimming.EllipsisWord,
            FormatFlags = StringFormatFlags.LineLimit
        };
        using var shadow = new SolidBrush(Color.FromArgb(185, 0, 0, 0));
        using var brush = new SolidBrush(color);
        graphics.DrawString(text, font, shadow, new RectangleF(rect.X + shadowRadius, rect.Y + shadowRadius, rect.Width, rect.Height), format);
        graphics.DrawString(text, font, brush, rect, format);
    }

    private static Font CreateFittedFont(Graphics graphics, string text, RectangleF rect, float max, float min, FontStyle style)
    {
        for (var size = max; size >= min; size -= Math.Max(1f, max * 0.035f))
        {
            var font = new Font("Segoe UI Semibold", size, style, GraphicsUnit.Pixel);
            var measured = graphics.MeasureString(text, font, (int)rect.Width);
            if (measured.Height <= rect.Height)
            {
                return font;
            }

            font.Dispose();
        }

        return new Font("Segoe UI Semibold", min, style, GraphicsUnit.Pixel);
    }

    private static void SaveDynamicTiles(IReadOnlyDictionary<string, byte[]> tiles)
    {
        try
        {
            var folder = GetDynamicIconPackFolder();
            Directory.CreateDirectory(folder);
            var changed = false;
            foreach (var (id, bytes) in tiles)
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (TileHashes.TryGetValue(id, out var existingHash) &&
                    existingHash.Equals(hash, StringComparison.Ordinal))
                {
                    continue;
                }

                var targetPath = Path.Combine(folder, $"{id}.png");
                var tempPath = Path.Combine(folder, $"{id}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, targetPath, overwrite: true);
                TileHashes[id] = hash;
                changed = true;
            }

            if (changed)
            {
                IconManager.LoadIconPack(folder);
            }
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Warning(plugin, $"Now Playing Lyrics could not update dynamic icon tiles.\r\n{ex}");
            }
        }
    }

    private static void EnsureDynamicIconPack()
    {
        try
        {
            var folder = GetDynamicIconPackFolder();
            Directory.CreateDirectory(folder);
            var manifestPath = Path.Combine(folder, "ExtensionManifest.json");
            File.WriteAllText(manifestPath, $$"""
            {
              "type": 1,
              "name": "{{DynamicIconPackName}}",
              "author": "{{DynamicIconPackAuthor}}",
              "repository": "https://github.com/LennonAmos/NowPlayingArtButton",
              "packageId": "{{DynamicIconPackPackageId}}",
              "version": "1.0.0",
              "target-plugin-api-version": 40,
              "dll": ""
            }
            """);
            IconManager.LoadIconPack(folder);
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Warning(plugin, $"Now Playing Lyrics could not prepare dynamic icon pack.\r\n{ex}");
            }
        }
    }

    private static string GetDynamicIconPackFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macro Deck",
            "iconpacks",
            DynamicIconPackPackageId);
    }

    private static async Task<MediaSnapshot> ReadCurrentMediaAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession()
                ?? manager.GetSessions().FirstOrDefault();
            if (session is null)
            {
                return MediaSnapshot.Empty;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var status = playback.PlaybackStatus.ToString();
            var isPlaying = status.Equals("Playing", StringComparison.OrdinalIgnoreCase);
            var position = timeline.Position;
            if (isPlaying)
            {
                var elapsed = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(10))
                {
                    position += elapsed;
                }
            }

            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            position = Clamp(position, TimeSpan.Zero, duration > TimeSpan.Zero ? duration : TimeSpan.FromDays(1));

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

            return new MediaSnapshot(
                Normalize(title, "Nothing playing"),
                Normalize(artist, string.Empty),
                status,
                isPlaying,
                position,
                duration,
                await ReadThumbnailAsync(properties.Thumbnail));
        }
        catch
        {
            return MediaSnapshot.Empty;
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
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

    private static async Task<LyricsDocument> GetLyricsForMediaAsync(MediaSnapshot media)
    {
        if (media.IsEmpty)
        {
            return LyricsDocument.Empty("Nothing playing");
        }

        var key = media.GetSongKey();
        lock (Gate)
        {
            if (LyricsCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var diskCached = TryReadLyricsFromDisk(key);
        if (diskCached is not null)
        {
            lock (Gate)
            {
                LyricsCache[key] = diskCached;
            }

            return diskCached;
        }

        var lyrics = await LookupLyricsOnlineAsync(media);
        lock (Gate)
        {
            LyricsCache[key] = lyrics;
            if (LyricsCache.Count > 80)
            {
                LyricsCache.Remove(LyricsCache.Keys.First());
            }
        }

        if (lyrics.Lines.Count > 0)
        {
            TryWriteLyricsToDisk(key, lyrics);
        }

        return lyrics;
    }

    private static LyricsDocument? TryReadLyricsFromDisk(string key)
    {
        try
        {
            var path = GetLyricsCachePath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<LyricCacheFile>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(cache?.SyncedLyrics))
            {
                return null;
            }

            return ParseLyrics(cache.SyncedLyrics, "Synced lyrics");
        }
        catch
        {
            return null;
        }
    }

    private static void TryWriteLyricsToDisk(string key, LyricsDocument lyrics)
    {
        try
        {
            var path = GetLyricsCachePath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cache = new LyricCacheFile(lyrics.RawSyncedLyrics, DateTimeOffset.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // Online lyrics still work for the current session if disk caching fails.
        }
    }

    private static string GetLyricsCachePath(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Macro Deck",
            "plugins",
            "lenno.NowPlayingArtButton",
            "lyrics_cache",
            $"{hash}.json");
    }

    private static async Task<LyricsDocument> LookupLyricsOnlineAsync(MediaSnapshot media)
    {
        try
        {
            var direct = await LookupLyricsDirectAsync(media);
            if (direct.Lines.Count > 0)
            {
                return direct;
            }

            return await SearchLyricsAsync(media);
        }
        catch (Exception ex)
        {
            if (plugin is not null)
            {
                MacroDeckLogger.Warning(plugin, $"Now Playing Lyrics lookup failed for \"{media.Title}\".\r\n{ex}");
            }

            return LyricsDocument.Empty("Lyrics unavailable");
        }
    }

    private static async Task<LyricsDocument> LookupLyricsDirectAsync(MediaSnapshot media)
    {
        var query = new StringBuilder("https://lrclib.net/api/get?");
        query.Append("track_name=").Append(Uri.EscapeDataString(media.Title));
        if (!string.IsNullOrWhiteSpace(media.Artist))
        {
            query.Append("&artist_name=").Append(Uri.EscapeDataString(media.Artist));
        }

        if (media.Duration > TimeSpan.Zero)
        {
            query.Append("&duration=").Append((int)Math.Round(media.Duration.TotalSeconds));
        }

        using var response = await HttpClient.GetAsync(query.ToString());
        if (!response.IsSuccessStatusCode)
        {
            return LyricsDocument.Empty("No synced lyrics found");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        return ParseLyricsFromElement(json.RootElement);
    }

    private static async Task<LyricsDocument> SearchLyricsAsync(MediaSnapshot media)
    {
        var query = new StringBuilder("https://lrclib.net/api/search?");
        query.Append("track_name=").Append(Uri.EscapeDataString(media.Title));
        if (!string.IsNullOrWhiteSpace(media.Artist))
        {
            query.Append("&artist_name=").Append(Uri.EscapeDataString(media.Artist));
        }

        using var response = await HttpClient.GetAsync(query.ToString());
        if (!response.IsSuccessStatusCode)
        {
            return LyricsDocument.Empty("No synced lyrics found");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return LyricsDocument.Empty("No synced lyrics found");
        }

        LyricsDocument bestLyrics = LyricsDocument.Empty("No synced lyrics found");
        var bestScore = int.MinValue;
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var parsed = ParseLyricsFromElement(item);
            if (parsed.Lines.Count == 0)
            {
                continue;
            }

            var score = ScoreLyricsCandidate(item, media);
            if (score > bestScore)
            {
                bestScore = score;
                bestLyrics = parsed;
            }
        }

        return bestLyrics;
    }

    private static int ScoreLyricsCandidate(JsonElement item, MediaSnapshot media)
    {
        var score = 0;
        var candidateTitle = GetJsonString(item, "trackName");
        var candidateArtist = GetJsonString(item, "artistName");
        if (TextMatches(candidateTitle, media.Title))
        {
            score += 60;
        }
        else if (ContainsEither(candidateTitle, media.Title))
        {
            score += 25;
        }

        if (!string.IsNullOrWhiteSpace(media.Artist))
        {
            if (TextMatches(candidateArtist, media.Artist))
            {
                score += 45;
            }
            else if (ContainsEither(candidateArtist, media.Artist))
            {
                score += 18;
            }
        }

        if (media.Duration > TimeSpan.Zero && item.TryGetProperty("duration", out var durationNode))
        {
            var candidateSeconds = durationNode.ValueKind switch
            {
                JsonValueKind.Number when durationNode.TryGetInt32(out var intSeconds) => intSeconds,
                JsonValueKind.Number when durationNode.TryGetDouble(out var doubleSeconds) => (int)Math.Round(doubleSeconds),
                _ => 0
            };
            if (candidateSeconds > 0)
            {
                var difference = Math.Abs(candidateSeconds - (int)Math.Round(media.Duration.TotalSeconds));
                score += difference switch
                {
                    <= 1 => 60,
                    <= 3 => 45,
                    <= 6 => 25,
                    <= 12 => 10,
                    _ => -30
                };
            }
        }

        return score;
    }

    private static string GetJsonString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static bool TextMatches(string left, string right)
    {
        return NormalizeForCompare(left).Equals(NormalizeForCompare(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsEither(string left, string right)
    {
        var cleanLeft = NormalizeForCompare(left);
        var cleanRight = NormalizeForCompare(right);
        return cleanLeft.Length > 0 &&
               cleanRight.Length > 0 &&
               (cleanLeft.Contains(cleanRight, StringComparison.OrdinalIgnoreCase) ||
                cleanRight.Contains(cleanLeft, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeForCompare(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = CleanTrackText(value).ToLowerInvariant();
        foreach (var token in new[] { "feat.", "ft.", "remastered", "remaster", "explicit" })
        {
            cleaned = cleaned.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var builder = new StringBuilder(cleaned.Length);
        foreach (var character in cleaned)
        {
            if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static LyricsDocument ParseLyricsFromElement(JsonElement element)
    {
        var synced = element.TryGetProperty("syncedLyrics", out var syncedNode)
            ? syncedNode.GetString()
            : null;
        if (!string.IsNullOrWhiteSpace(synced))
        {
            return ParseLyrics(synced, "Synced lyrics");
        }

        return LyricsDocument.Empty("No synced lyrics found");
    }

    private static LyricsDocument ParseLyrics(string syncedLyrics, string statusText)
    {
        var lines = new List<LyricLine>();
        foreach (var rawLine in syncedLyrics.Replace("\r", string.Empty).Split('\n'))
        {
            var matches = LrcTimestampRegex.Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var textStart = matches[^1].Index + matches[^1].Length;
            var text = textStart < rawLine.Length ? rawLine[textStart..].Trim() : string.Empty;
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups[1].Value, out var minutes) ||
                    !double.TryParse(match.Groups[2].Value, out var seconds))
                {
                    continue;
                }

                lines.Add(new LyricLine(TimeSpan.FromSeconds(minutes * 60 + seconds), text));
            }
        }

        lines.Sort((left, right) => left.Time.CompareTo(right.Time));
        return lines.Count == 0
            ? LyricsDocument.Empty("No synced lyrics found")
            : new LyricsDocument(lines, statusText, syncedLyrics);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NowPlayingArtButton/1.1 (+https://github.com/LennonAmos/NowPlayingArtButton)");
        return client;
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
                     " [Official Audio]",
                     " (Lyric Video)",
                     " [Lyric Video]",
                     " (Lyrics)",
                     " [Lyrics]"
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

    private static string Normalize(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static MacroDeckProfile? GetCurrentProfile(string clientId)
    {
        return IsDesktopClient(clientId)
            ? ProfileManager.CurrentProfile
            : MacroDeckServer.GetMacroDeckClient(clientId)?.Profile;
    }

    private static bool SetProfile(string clientId, MacroDeckProfile profile)
    {
        if (IsDesktopClient(clientId))
        {
            MacroDeck.MainWindow?.DeckView?.SetProfile(profile);
            return true;
        }

        var device = DeviceManager.GetMacroDeckDevice(clientId);
        if (device is null)
        {
            return false;
        }

        DeviceManager.SetProfile(device, profile);
        return true;
    }

    private static bool IsDesktopClient(string clientId)
    {
        return string.IsNullOrWhiteSpace(clientId) || clientId == "-1";
    }

    private static string GetMemoryKey(string clientId)
    {
        return IsDesktopClient(clientId) ? "__desktop__" : clientId;
    }

    private sealed record MediaSnapshot(
        string Title,
        string Artist,
        string PlaybackStatus,
        bool IsPlaying,
        TimeSpan Position,
        TimeSpan Duration,
        byte[]? ThumbnailBytes)
    {
        public static MediaSnapshot Empty { get; } = new("Nothing playing", string.Empty, "Stopped", false, TimeSpan.Zero, TimeSpan.Zero, null);

        public bool IsEmpty => Title.Equals("Nothing playing", StringComparison.OrdinalIgnoreCase);

        public string GetSongKey()
        {
            return $"{Title}\u001f{Artist}\u001f{(int)Math.Round(Duration.TotalSeconds)}";
        }

        public string GetRenderIdentity()
        {
            return $"{Title}\u001f{Artist}\u001f{PlaybackStatus}\u001f{(int)Duration.TotalSeconds}\u001f{ThumbnailBytes?.Length ?? 0}";
        }
    }

    private sealed record LyricLine(TimeSpan Time, string Text);

    private sealed record ActiveLyricLine(int Index, string Text)
    {
        public static ActiveLyricLine Empty { get; } = new(-1, string.Empty);
    }

    private sealed record LyricsDocument(IReadOnlyList<LyricLine> Lines, string StatusText, string RawSyncedLyrics)
    {
        public static LyricsDocument Empty(string statusText)
        {
            return new LyricsDocument(Array.Empty<LyricLine>(), statusText, string.Empty);
        }

        public ActiveLyricLine GetActiveLine(TimeSpan position)
        {
            if (Lines.Count == 0)
            {
                return new ActiveLyricLine(-1, StatusText);
            }

            var index = -1;
            for (var i = Lines.Count - 1; i >= 0; i--)
            {
                if (Lines[i].Time <= position)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                index = 0;
            }

            var text = Lines[index].Text;
            return new ActiveLyricLine(index, string.IsNullOrWhiteSpace(text) ? "~" : text);
        }

        public string GetLineText(int index)
        {
            if (index < 0 || index >= Lines.Count)
            {
                return string.Empty;
            }

            var text = Lines[index].Text;
            return string.IsNullOrWhiteSpace(text) ? "~" : text;
        }
    }

    private sealed record LyricCacheFile(string SyncedLyrics, DateTimeOffset FetchedAt);
}
