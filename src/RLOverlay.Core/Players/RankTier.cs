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

    private static readonly (string Stem, int Group)[] ParseStems =
    [
        ("grand champion", 7), ("champion", 6), ("diamond", 5), ("platinum", 4), ("gold", 3), ("silver", 2), ("bronze", 1),
    ];

    /// <summary>
    /// A tier from its name ("Gold II", "Grand Champion 3", "Supersonic Legend", "Unranked") or number ("17").
    /// Null when the text is not a rank.
    /// </summary>
    public static int? Parse(string? text)
    {
        var t = Normalise(text);
        if (t.Length == 0)
        {
            return null;
        }

        if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return number is >= Unranked and <= Max ? number : null;
        }

        if (t.StartsWith("supersonic", StringComparison.Ordinal) || t == "ssl")
        {
            return SupersonicLegend;
        }

        if (t.StartsWith("unranked", StringComparison.Ordinal) || t.StartsWith("un ranked", StringComparison.Ordinal))
        {
            return Unranked;
        }

        foreach (var (stem, group) in ParseStems)
        {
            if (!t.StartsWith(stem, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = t[stem.Length..].Trim();
            var sub = rest.Length == 0 ? 1 : ParseNumeral(rest);
            return sub is >= 1 and <= 3 ? ((group - 1) * 3) + sub : null;
        }

        return null;
    }

    /// <summary>A division (1–4) from "Division III", "Div 2", "IV" or "3". Null when it can't be read.</summary>
    public static int? ParseDivision(string? text)
    {
        var t = Normalise(text);
        foreach (var prefix in new[] { "division", "div" })
        {
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                t = t[prefix.Length..].Trim();
                break;
            }
        }

        // A bare 0 can only mean a 0-based division I.
        return t == "0" ? 1 : ParseNumeral(t) is { } n and >= 1 and <= 4 ? n : null;
    }

    private static int? ParseNumeral(string text) => text.Trim() switch
    {
        "i" or "1" => 1,
        "ii" or "2" => 2,
        "iii" or "3" => 3,
        "iv" or "4" => 4,
        _ => null,
    };

    private static string Normalise(string? text) =>
        string.Join(' ', (text ?? "").ToLowerInvariant().Replace('_', ' ').Replace('-', ' ').Replace('.', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
