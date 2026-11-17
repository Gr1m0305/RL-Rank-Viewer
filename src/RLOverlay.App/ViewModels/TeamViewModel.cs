using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RLOverlay.App.ViewModels;

/// <summary>One team block: name, "present / team size", and its rows (players, then empty seats).</summary>
public sealed partial class TeamViewModel : ObservableObject
{
    [ObservableProperty]
    private string _countText = "";

    public TeamViewModel(int teamNum)
    {
        TeamNum = teamNum;
        Label = teamNum == 0 ? "BLUE" : "ORANGE";
    }

    public int TeamNum { get; }

    public bool IsOrange => TeamNum == 1;

    public string Label { get; }

    public ObservableCollection<PlayerRowViewModel> Rows { get; } = [];

    /// <summary>Makes <see cref="Rows"/> equal to <paramref name="desired"/> with moves/inserts/removes (no reset, so
    /// existing row visuals and their animations survive).</summary>
    public void SyncRows(IReadOnlyList<PlayerRowViewModel> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < Rows.Count && ReferenceEquals(Rows[i], item))
            {
                continue;
            }

            var existing = IndexOf(item, i + 1);
            if (existing >= 0)
            {
                Rows.Move(existing, i);
            }
            else
            {
                Rows.Insert(i, item);
            }
        }

        while (Rows.Count > desired.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    private int IndexOf(PlayerRowViewModel item, int start)
    {
        for (var i = start; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
