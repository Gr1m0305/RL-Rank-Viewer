namespace RLOverlay.Core.Players;

public enum RankGroup
{
    Unranked,
    Bronze,
    Silver,
    Gold,
    Platinum,
    Diamond,
    Champion,
    GrandChampion,
    SupersonicLegend,
}

/// <summary>
/// Rank tiers as Rocket League / tracker.gg number them:
/// 0 = Unranked, 1–3 Bronze I–III, 4–6 Silver, 7–9 Gold, 10–12 Platinum, 13–15 Diamond, 16–18 Champion,
/// 19–21 Grand Champion I–III, 22 = Supersonic Legend.
/// </summary>
public static class RankTier
{
    public const int Unranked = 0;
    public const int SupersonicLegend = 22;
    public const int Max = SupersonicLegend;

    private static readonly string[] GroupStems =
        ["unranked", "bronze", "silver", "gold", "platinum", "diamond", "champion", "grand_champion", "supersonic_legend"];

    private static readonly string[] GroupNames =
        ["Unranked", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Champion", "Grand Champion", "Supersonic Legend"];

    public static int Clamp(int? tier) => tier is null ? Unranked : Math.Clamp(tier.Value, Unranked, Max);

    public static RankGroup Group(int tier)
    {
        tier = Clamp(tier);
        return tier switch
        {
            Unranked => RankGroup.Unranked,
            SupersonicLegend => RankGroup.SupersonicLegend,
            _ => (RankGroup)(((tier - 1) / 3) + 1),
        };
    }

    /// <summary>1–3 within the group (I, II, III); 0 for Unranked and Supersonic Legend.</summary>
    public static int SubRank(int tier)
    {
        tier = Clamp(tier);
        return tier is Unranked or SupersonicLegend ? 0 : ((tier - 1) % 3) + 1;
    }

    /// <summary>File name stem used for user-supplied icons, e.g. <c>gold_2</c>, <c>supersonic_legend</c>, <c>unranked</c>.</summary>
    public static string FileStem(int tier)
    {
        var group = Group(tier);
        var stem = GroupStems[(int)group];
        var sub = SubRank(tier);
        return sub == 0 ? stem : $"{stem}_{sub}";
    }

    /// <summary>Display name, e.g. "Champion II".</summary>
    public static string Name(int tier)
    {
        var group = Group(tier);
        var sub = SubRank(tier);
        return sub == 0 ? GroupNames[(int)group] : $"{GroupNames[(int)group]} {new string('I', sub)}";
    }

    /// <summary>All tier numbers, 0..22.</summary>
    public static IEnumerable<int> All => Enumerable.Range(Unranked, Max + 1);
}
