using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using RLOverlay.Core.Players;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace RLOverlay.App.Services;

/// <summary>
/// Resolves the image for a rank tier, in this order:
/// <list type="number">
/// <item>the user's own files: <c>%LOCALAPPDATA%\RLOverlay\ranks\</c> (override) then <c>assets\ranks\</c> next to the exe
/// (<c>bronze_1.png</c> … <c>supersonic_legend.png</c>, <c>unranked.png</c>; PNG or SVG);</item>
/// <item>the icon URL the stats provider returned, downloaded once to <c>%LOCALAPPDATA%\RLOverlay\cache\ranks\</c>;</item>
/// <item>a built-in tier-coloured placeholder, so a broken image is never shown.</item>
/// </list>
/// No Psyonix artwork ships with the app.
/// </summary>
public sealed class RankIconService
{
    private static readonly string[] Extensions = [".png", ".svg"];

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
    /// Tells the service which URL the provider serves for a tier. If there is no user file and no cached download,
    /// it is downloaded once in the background and <see cref="IconUpdated"/> fires.
    /// </summary>
    public void OfferProviderIcon(int tier, string? url)
    {
        tier = RankTier.Clamp(tier);
        if (_http is null || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || FindUserFile(RankTier.FileStem(tier)) is not null || FindCachedDownload(tier) is not null
            || !_downloading.TryAdd(tier, true))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(uri).ConfigureAwait(false);
                var ext = uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? ".svg" : ".png";
                Directory.CreateDirectory(_cacheDir);
                var path = Path.Combine(_cacheDir, $"tier_{tier}{ext}");
                await File.WriteAllBytesAsync(path + ".tmp", bytes).ConfigureAwait(false);
                File.Move(path + ".tmp", path, overwrite: true);
                dispatcher?.BeginInvoke(() =>
                {
                    if (TryLoad(path) is { } image)
                    {
                        _resolved[tier] = image;
                        IconUpdated?.Invoke(tier);
                    }
                    else
                    {
                        File.Delete(path); // not an image; never use it
                    }
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                _logger.LogInformation("Could not download rank icon for tier {Tier}: {Error}", tier, ex.Message);
            }
            finally
            {
                _downloading.TryRemove(tier, out _);
            }
        });
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

    /// <summary>Tier-coloured circle with one to three pips for divisions I–III.</summary>
    public static ImageSource Placeholder(int tier)
    {
        var group = RankTier.Group(tier);
        var (light, dark) = GroupColors(group);
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            // Transparent frame so the image is a stable 64×64.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 64));
            var fill = new RadialGradientBrush(light, dark) { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75 };
            var ring = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, dark.R, dark.G, dark.B)), 3);
            dc.DrawEllipse(fill, ring, new Point(32, 32), 24, 24);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), 1.5), new Point(32, 32), 17, 17);

            if (group == RankGroup.Unranked)
            {
                var cross = new Pen(new SolidColorBrush(Color.FromArgb(0xDD, 0x2A, 0x2E, 0x36)), 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(cross, new Point(24, 24), new Point(40, 40));
                dc.DrawLine(cross, new Point(40, 24), new Point(24, 40));
            }
            else if (group == RankGroup.SupersonicLegend)
            {
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD9, 0x46, 0xEF)), null, new Point(32, 32), 8, 8);
            }
            else
            {
                var pips = RankTier.SubRank(tier);
                var pip = new SolidColorBrush(Color.FromArgb(0xF0, 255, 255, 255));
                var startX = 32 - ((pips - 1) * 7 / 2.0);
                for (var i = 0; i < pips; i++)
                {
                    dc.DrawEllipse(pip, null, new Point(startX + (i * 7), 32), 2.6, 2.6);
                }
            }
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

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
