using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RLOverlay.Core.Playlists;

namespace RLOverlay.App.ViewModels;

/// <summary>The Ctrl+G strip: every playlist, the current one highlighted, ones not in the cycle struck through.</summary>
public sealed class PlaylistStripViewModel
{
    public PlaylistStripViewModel()
    {
        foreach (var info in PlaylistCatalog.All)
        {
            Items.Add(new PlaylistStripItem(info.Playlist, info.StripLabel));
        }
    }

    public ObservableCollection<PlaylistStripItem> Items { get; } = [];

    public void Update(Playlist current, IReadOnlyCollection<Playlist> enabled)
    {
        foreach (var item in Items)
        {
            item.IsCurrent = item.Playlist == current;
            item.IsEnabled = enabled.Contains(item.Playlist);
        }
    }
}

public sealed partial class PlaylistStripItem(Playlist playlist, string label) : ObservableObject
{
    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isEnabled = true;

    public Playlist Playlist { get; } = playlist;

    public string Label { get; } = label;
}
