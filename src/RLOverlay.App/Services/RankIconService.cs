using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using RLOverlay.Core;
using RLOverlay.Core.Players;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace RLOverlay.App.Services;

/// <summary>
/// Resolves the image for a rank tier, in this order:
/// <list type="number">
/// <item>the user's own files: <c>%LOCALAPPDATA%\RLOverlay\ranks\</c> (override) then <c>assets\ranks\</c> next to the exe
/// (<c>bronze_1.png</c> … <c>supersonic_legend.png</c>, <c>unranked.png</c>; PNG or SVG);</item>
/// <item>the official in-game icons, downloaded once at runtime (from Tracker Network's CDN, see
/// <see cref="TrackerUrls.RankIcon"/>, or the URL the stats provider returned) and kept in
/// <c>%LOCALAPPDATA%\RLOverlay\cache\ranks\</c>;</item>
/// <item>a built-in placeholder until the download finishes, or if it cannot be made.</item>
/// </list>
/// No rank artwork is stored in the repository or compiled into the exe.
/// </summary>
public sealed class RankIconService
{
    private const int MaxIconBytes = 4 * 1024 * 1024;

    /// <summary>The CDN images are up to ~1000 px; decode them small (icons are drawn at 34 px × scale × DPI).</summary>
    private const int DecodeWidth = 128;

    private static readonly string[] Extensions = [".png", ".svg"];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly SemaphoreSlim DownloadGate = new(4);

    private readonly string[] _userDirs;
    private readonly string _cacheDir;
    private readonly HttpClient? _http;
    private readonly ILogger _logger;
    private readonly Dictionary<int, ImageSource> _resolved = new();
    private readonly ConcurrentDictionary<int, bool> _downloading = new();

    public RankIconService(IEnumerable<string> userDirs, string cacheDir, HttpClient? http, ILogger<RankIconService> logger)
    {
        _userDirs = userDirs.ToArray();
        _cacheDir = cacheDir;
        _http = http;
        _logger = logger;
    }

    /// <summary>Raised on the UI thread when a better icon for a tier became available (provider download finished).</summary>
    public event Action<int>? IconUpdated;

    /// <summary>Where each tier's icon currently comes from (for the README / diagnostics).</summary>
    public string DescribeSource(int tier) =>
        FindUserFile(RankTier.FileStem(tier)) ?? FindCachedDownload(tier) ?? "built-in placeholder";

    public ImageSource Get(int tier)
    {
        tier = RankTier.Clamp(tier);
        if (_resolved.TryGetValue(tier, out var cached))
        {
            return cached;
        }

        var image = TryLoad(FindUserFile(RankTier.FileStem(tier)))
                    ?? TryLoad(FindCachedDownload(tier))
                    ?? Placeholder(tier);
        _resolved[tier] = image;
        return image;
    }

    /// <summary>
    /// Downloads the official icon for every tier that has neither a user file nor a cached copy, in the background
    /// (at most four at a time). Rows update through <see cref="IconUpdated"/> as each one arrives.
    /// </summary>
    public void PrefetchOfficialIcons()
    {
        foreach (var tier in RankTier.All)
        {
            OfferProviderIcon(tier, TrackerUrls.RankIcon(tier));
        }
    }

    /// <summary>Like <see cref="PrefetchOfficialIcons"/>, but waits for the downloads (used by <c>--screenshots</c>).</summary>
    public async Task EnsureOfficialIconsAsync(TimeSpan timeout)
    {
        var downloads = Task.WhenAll(RankTier.All.Select(tier => DownloadIfMissingAsync(tier, TrackerUrls.RankIcon(tier))));
        await Task.WhenAny(downloads, Task.Delay(timeout));
    }

    /// <summary>
    /// Tells the service which icon URL the provider serves for a tier. If there is no user file and no cached copy,
    /// it is downloaded once in the background and <see cref="IconUpdated"/> fires.
    /// </summary>
    public void OfferProviderIcon(int tier, string? url) => _ = DownloadIfMissingAsync(tier, url);

    private async Task DownloadIfMissingAsync(int tier, string? url)
    {
        tier = RankTier.Clamp(tier);
        if (_http is null || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || FindUserFile(RankTier.FileStem(tier)) is not null || FindCachedDownload(tier) is not null
            || !_downloading.TryAdd(tier, true))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        try
        {
            byte[] bytes;
            await DownloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxIconBytes)
                {
                    throw new InvalidDataException("icon is larger than 4 MB");
                }

                bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            finally
            {
                DownloadGate.Release();
            }

            // Only ever keep real PNG files.
            if (bytes.Length > MaxIconBytes || !bytes.AsSpan().StartsWith(PngSignature))
            {
                throw new InvalidDataException("response is not a PNG image");
            }

            Directory.CreateDirectory(_cacheDir);
            var path = Path.Combine(_cacheDir, $"tier_{tier}.png");
            await File.WriteAllBytesAsync(path + ".tmp", bytes).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
            _logger.LogInformation("Rank icon for tier {Tier} downloaded from {Url}", tier, uri);

            if (dispatcher is not null)
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (TryLoad(path) is { } image)
                    {
                        _resolved[tier] = image;
                        IconUpdated?.Invoke(tier);
                    }
                    else
                    {
                        File.Delete(path); // undecodable; never use it
                    }
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogInformation("Could not download rank icon for tier {Tier}: {Error}", tier, ex.Message);
        }
        finally
        {
            _downloading.TryRemove(tier, out _);
        }
    }

    private string? FindUserFile(string stem)
    {
        foreach (var dir in _userDirs)
        {
            foreach (var ext in Extensions)
            {
                var path = Path.Combine(dir, stem + ext);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private string? FindCachedDownload(int tier)
    {
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_cacheDir, $"tier_{tier}{ext}");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private ImageSource? TryLoad(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
                using var reader = new FileSvgReader(settings);
                var drawing = reader.Read(path);
                if (drawing is null)
                {
                    return null;
                }

                var svg = new DrawingImage(drawing);
                svg.Freeze();
                return svg;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = DecodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load rank icon {Path}: {Error}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Built-in placeholder: a simple geometric badge per rank group (circle, hexagon, triangle, star, gem, rhombus,
    /// winged rhombus, wings) in the group's colours, with one to three pips for divisions I–III. Drawn in code; not
    /// derived from any game artwork.
    /// </summary>
    public static ImageSource Placeholder(int tier)
    {
        var group = RankTier.Group(tier);
        var (light, dark) = GroupColors(group);
        var fill = new LinearGradientBrush(light, dark, new Point(0.3, 0), new Point(0.7, 1));
        var edge = new Pen(new SolidColorBrush(Darken(dark, 0.55)), 2.2) { LineJoin = PenLineJoin.Round };
        var shine = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1.4) { LineJoin = PenLineJoin.Round };
        var pipY = 32.0;

        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // Transparent frame so every icon is a stable 64×64.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            switch (group)
            {
                case RankGroup.Unranked:
                    var grey = new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x7E)), 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x30, 0x6B, 0x72, 0x7E)), grey, new Point(32, 32), 24, 24);
                    dc.DrawLine(grey, new Point(21, 21), new Point(43, 43));
                    dc.DrawLine(grey, new Point(43, 21), new Point(21, 43));
                    break;
                case RankGroup.Bronze:
                    dc.DrawEllipse(fill, edge, new Point(32, 32), 23, 23);
                    dc.DrawEllipse(null, shine, new Point(32, 32), 16, 16);
                    break;
                case RankGroup.Silver:
                    dc.DrawGeometry(fill, edge, Poly((7, 32), (19.5, 10), (44.5, 10), (57, 32), (44.5, 54), (19.5, 54)));
                    dc.DrawGeometry(null, shine, Poly((15, 32), (23.5, 17), (40.5, 17), (49, 32), (40.5, 47), (23.5, 47)));
                    break;
                case RankGroup.Gold:
                    dc.DrawGeometry(fill, edge, Poly((32, 7), (58, 52), (6, 52)));
                    dc.DrawGeometry(null, shine, Poly((32, 18), (48, 46), (16, 46)));
                    pipY = 38;
                    break;
                case RankGroup.Platinum:
                    dc.DrawGeometry(fill, edge, Star(new Point(32, 34), 27, 11.5));
                    pipY = 35;
                    break;
                case RankGroup.Diamond:
                    dc.DrawGeometry(fill, edge, Poly((15, 12), (49, 12), (59, 25), (32, 57), (5, 25)));
                    dc.DrawGeometry(null, shine, Poly((5, 25), (59, 25)));
                    pipY = 33;
                    break;
                case RankGroup.Champion:
                    dc.DrawGeometry(fill, edge, Poly((32, 5), (57, 32), (32, 59), (7, 32)));
                    dc.DrawGeometry(null, shine, Poly((32, 15), (47, 32), (32, 49), (17, 32)));
                    break;
                case RankGroup.GrandChampion:
                    dc.DrawGeometry(fill, edge, Poly((3, 16), (22, 24), (22, 42), (9, 35)));
                    dc.DrawGeometry(fill, edge, Poly((61, 16), (42, 24), (42, 42), (55, 35)));
                    dc.DrawGeometry(fill, edge, Poly((32, 9), (49, 32), (32, 57), (15, 32)));
                    dc.DrawGeometry(null, shine, Poly((32, 18), (42, 32), (32, 47), (22, 32)));
                    break;
                case RankGroup.SupersonicLegend:
                    dc.DrawGeometry(fill, edge, Poly((2, 14), (26, 26), (24, 44), (8, 36)));
                    dc.DrawGeometry(fill, edge, Poly((62, 14), (38, 26), (40, 44), (56, 36)));
                    var gem = new LinearGradientBrush(Color.FromRgb(0xF5, 0x8B, 0xFF), Color.FromRgb(0x8E, 0x17, 0xC9), 90);
                    dc.DrawGeometry(gem, edge, Poly((20, 24), (44, 24), (32, 52)));
                    break;
            }

            if (group is not (RankGroup.Unranked or RankGroup.SupersonicLegend))
            {
                var pips = RankTier.SubRank(tier);
                var pip = new SolidColorBrush(Colors.White);
                var ring = new Pen(new SolidColorBrush(Darken(dark, 0.5)), 1);
                var startX = 32 - ((pips - 1) * 7.5 / 2.0);
                for (var i = 0; i < pips; i++)
                {
                    dc.DrawEllipse(pip, ring, new Point(startX + (i * 7.5), pipY), 2.9, 2.9);
                }
            }
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static Geometry Poly(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true, isClosed: points.Length > 2);
            ctx.PolyLineTo(points.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry Star(Point centre, double outer, double inner)
    {
        var points = new (double, double)[10];
        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outer : inner;
            var angle = (-90 + (i * 36)) * Math.PI / 180;
            points[i] = (centre.X + (radius * Math.Cos(angle)), centre.Y + (radius * Math.Sin(angle)));
        }

        return Poly(points);
    }

    private static Color Darken(Color c, double factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    private static (Color Light, Color Dark) GroupColors(RankGroup group) => group switch
    {
        RankGroup.Bronze => (Color.FromRgb(0xE3, 0xA0, 0x6A), Color.FromRgb(0x8A, 0x4E, 0x22)),
        RankGroup.Silver => (Color.FromRgb(0xE8, 0xEC, 0xF1), Color.FromRgb(0x7D, 0x86, 0x93)),
        RankGroup.Gold => (Color.FromRgb(0xFF, 0xDC, 0x6B), Color.FromRgb(0xB0, 0x82, 0x10)),
        RankGroup.Platinum => (Color.FromRgb(0xA8, 0xF0, 0xFF), Color.FromRgb(0x2D, 0x9C, 0xB8)),
        RankGroup.Diamond => (Color.FromRgb(0x7F, 0xB6, 0xFF), Color.FromRgb(0x1E, 0x4F, 0xD8)),
        RankGroup.Champion => (Color.FromRgb(0xD3, 0xA6, 0xFF), Color.FromRgb(0x6D, 0x28, 0xD9)),
        RankGroup.GrandChampion => (Color.FromRgb(0xFF, 0x8A, 0x8A), Color.FromRgb(0xB9, 0x1C, 0x1C)),
        RankGroup.SupersonicLegend => (Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x9C, 0xA3, 0xAF)),
        _ => (Color.FromRgb(0x8B, 0x93, 0x9F), Color.FromRgb(0x4B, 0x52, 0x5C)),
    };
}
