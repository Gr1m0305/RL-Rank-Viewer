using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RLOverlay.Core.Config;

namespace RLOverlay.Core.Players;

/// <summary>A player saved with the star next to their name: their account and the name it last had.</summary>
public sealed record FavouritePlayer(Platform Platform, string Uid, string Name, DateTime AddedUtc)
{
    /// <summary>The same account on the same platform (case-insensitive), whatever it is called now.</summary>
    [JsonIgnore]
    public string Key => KeyFor(Platform, Uid);

    /// <summary>The account as the overlay's rows use it (never a split-screen guest).</summary>
    public PlatformId ToPlatformId() => new(Platform, Uid, 0, Name, $"{Platform}|{Uid}|0");

    public static string KeyFor(Platform platform, string uid) => $"{platform}|{uid.Trim()}".ToLowerInvariant();

    public static string KeyFor(PlatformId id) => KeyFor(id.Platform, id.Uid);
}

/// <summary>
/// The players saved as favourites, newest first. Only players tracker.gg can show are kept: no bots, split-screen
/// guests or unknown platforms.
/// </summary>
public sealed class FavouriteList
{
    private readonly List<FavouritePlayer> _players;

    public FavouriteList(IEnumerable<FavouritePlayer>? players = null)
    {
        _players = (players ?? [])
            .Where(p => p is not null && CanSave(p.ToPlatformId()))
            .OrderByDescending(p => p.AddedUtc)
            .DistinctBy(p => p.Key)
            .ToList();
    }

    public IReadOnlyList<FavouritePlayer> Players => _players;

    public int Count => _players.Count;

    /// <summary>Only players with a tracker.gg profile can be saved.</summary>
    public static bool CanSave(PlatformId id) => id.CanLookup;

    public bool Contains(PlatformId id) => CanSave(id) && IndexOf(id) >= 0;

    /// <summary>Saves the player at the top of the list; false if they can't be saved or already are.</summary>
    public bool Add(PlatformId id, DateTime nowUtc)
    {
        if (!CanSave(id) || IndexOf(id) >= 0)
        {
            return false;
        }

        _players.Insert(0, new FavouritePlayer(id.Platform, id.Uid.Trim(), NameOf(id), nowUtc));
        return true;
    }

    public bool Remove(PlatformId id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }

        _players.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// A saved player was seen under another name: keep the new one, since tracker.gg finds every platform but Steam by
    /// name. True if it changed.
    /// </summary>
    public bool Rename(PlatformId id)
    {
        var index = IndexOf(id);
        var name = id.DisplayName.Trim();
        if (index < 0 || name.Length == 0 || name == _players[index].Name)
        {
            return false;
        }

        _players[index] = _players[index] with { Name = name };
        return true;
    }

    private int IndexOf(PlatformId id)
    {
        var key = FavouritePlayer.KeyFor(id);
        return _players.FindIndex(p => p.Key == key);
    }

    private static string NameOf(PlatformId id) => id.DisplayName.Trim() is { Length: > 0 } name ? name : id.Uid.Trim();
}

/// <summary>The favourites screen shows the list a page at a time: 5 or 10 players per page.</summary>
public static class FavouritePages
{
    public const int DefaultSize = 5;

    public static IReadOnlyList<int> Sizes { get; } = [5, 10];

    /// <summary>A page size from a settings file: one of <see cref="Sizes"/>, else the default.</summary>
    public static int NormalizeSize(int perPage) => Sizes.Contains(perPage) ? perPage : DefaultSize;

    /// <summary>How many pages <paramref name="total"/> players fill (at least one, the empty page).</summary>
    public static int Count(int total, int perPage) => Math.Max(1, (Math.Max(0, total) + perPage - 1) / perPage);

    /// <summary>A page number (from 0) that exists.</summary>
    public static int Clamp(int page, int total, int perPage) => Math.Clamp(page, 0, Count(total, perPage) - 1);

    /// <summary>The players on one page.</summary>
    public static IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items, int page, int perPage) =>
        items.Skip(Clamp(page, items.Count, perPage) * perPage).Take(perPage).ToList();

    /// <summary>After the page size changed: the page that still starts with (or holds) the same first player.</summary>
    public static int Resized(int page, int oldPerPage, int newPerPage) => Math.Max(0, page) * oldPerPage / newPerPage;
}

/// <summary>
/// Loads and saves the favourites as JSON (<c>favourites.json</c> next to the settings). They live in their own file
/// because the star changes them at any time, while the settings window edits a copy of the settings until Save.
/// An unreadable file is kept as a copy and the list starts empty.
/// </summary>
public sealed class FavouritesStore
{
    public const int CurrentVersion = 1;

    private readonly ILogger _logger;

    public FavouritesStore(string path, ILogger<FavouritesStore>? logger = null)
    {
        FilePath = path;
        _logger = logger ?? NullLogger<FavouritesStore>.Instance;
    }

    public string FilePath { get; }

    public FavouriteList Load()
    {
        if (!File.Exists(FilePath))
        {
            return new FavouriteList();
        }

        try
        {
            var file = JsonSerializer.Deserialize<FavouritesFile>(File.ReadAllText(FilePath), SettingsStore.JsonOptions);
            return new FavouriteList(file?.Players);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var backup = FilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            _logger.LogWarning("Favourites file unreadable ({Error}); starting with none and keeping a copy at {Backup}", ex.Message, backup);
            try
            {
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
            }

            return new FavouriteList();
        }
    }

    public void Save(FavouriteList favourites)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        var file = new FavouritesFile { Version = CurrentVersion, Players = [.. favourites.Players] };
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, SettingsStore.JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private sealed class FavouritesFile
    {
        public int Version { get; set; } = CurrentVersion;

        public List<FavouritePlayer> Players { get; set; } = [];
    }
}
