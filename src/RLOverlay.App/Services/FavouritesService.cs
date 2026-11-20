using System.IO;
using Microsoft.Extensions.Logging;
using RLOverlay.Core.Lobby;
using RLOverlay.Core.Players;

namespace RLOverlay.App.Services;

/// <summary>The stars next to the names: which players are favourites, and adding or removing one.</summary>
internal interface IFavouriteMarks
{
    bool IsFavourite(PlatformId id);

    /// <summary>
    /// Whether this player's star can be clicked: they need a tracker.gg profile, and the Preview's made-up players can't
    /// be saved (the demo's can: it is a test run with its own data).
    /// </summary>
    bool CanMark(LobbyPlayer player, bool sample);

    void Toggle(LobbyPlayer player);

    /// <summary>A player was added, removed or renamed.</summary>
    event Action? Changed;
}

/// <summary>
/// The favourites list behind the stars and the favourites screen (Ctrl+F), saved to <c>favourites.json</c> on every
/// change. UI thread only.
/// </summary>
internal sealed class FavouritesService : IFavouriteMarks
{
    private readonly FavouritesStore _store;
    private readonly ILogger _logger;
    private readonly FavouriteList _list;

    public FavouritesService(FavouritesStore store, ILogger<FavouritesService> logger)
    {
        _store = store;
        _logger = logger;
        _list = store.Load();
        _logger.LogInformation("Favourites: {Count} saved", _list.Count);
    }

    /// <summary><c>--demo</c>: its made-up players can be starred (saved in the demo's data folder).</summary>
    public bool AllowSamples { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<FavouritePlayer> Players => _list.Players;

    public event Action? Changed;

    public bool IsFavourite(PlatformId id) => _list.Contains(id);

    public bool CanMark(LobbyPlayer player, bool sample) => FavouriteList.CanSave(player.Id) && (!sample || AllowSamples);

    public void Toggle(LobbyPlayer player)
    {
        if (_list.Remove(player.Id))
        {
            _logger.LogInformation("Favourites: removed {Name} ({Platform})", player.Name, player.Id.Platform);
        }
        else if (_list.Add(player.Id, DateTime.UtcNow))
        {
            _logger.LogInformation("Favourites: added {Name} ({Platform}), {Count} saved", player.Name, player.Id.Platform, _list.Count);
        }
        else
        {
            return;
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>A player in the lobby: if they are a favourite under another name now, keep the new name.</summary>
    public void Seen(LobbyPlayer player)
    {
        if (_list.Rename(player.Id))
        {
            _logger.LogInformation("Favourites: {Name} ({Platform}) has a new name", player.Name, player.Id.Platform);
            Save();
            Changed?.Invoke();
        }
    }

    private void Save()
    {
        try
        {
            _store.Save(_list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not save the favourites to {Path}: {Error}", _store.FilePath, ex.Message);
        }
    }
}
