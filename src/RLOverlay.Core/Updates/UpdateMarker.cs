namespace RLOverlay.Core.Updates;

/// <summary>What the copy that starts after an update finds it has to say.</summary>
public enum UpdateOutcome
{
    /// <summary>Nothing to say (no note, or the note is from this version and names no other).</summary>
    None,

    /// <summary>This copy replaced the one that left the note.</summary>
    Updated,

    /// <summary>The note is from this same version and names the one it was installing: setup didn't manage it.</summary>
    Failed,
}

/// <summary>
/// The note a copy leaves in <c>updates\updated-from.txt</c> right before setup replaces it: the version it is and the
/// version being installed. The copy that starts afterwards (the new version, or the old one again if setup failed)
/// reads it to say what happened.
/// </summary>
/// <param name="From">The version that started the update.</param>
/// <param name="To">The version being installed (null in notes from 0.6.0 and older, which only held the version).</param>
public sealed record UpdateMarker(string From, string? To)
{
    public const string FileName = "updated-from.txt";

    /// <summary>First line the version it came from (all that notes from 0.6.0 and older hold), then <c>to=</c>.</summary>
    public string Format() => To is { } to ? $"{From}\nto={to}\n" : $"{From}\n";

    /// <summary>
    /// Reads a note; null when it is empty. Lines it doesn't know are skipped, such as the <c>open=</c> line 0.7.0 wrote
    /// (whether its settings window was open, which no longer matters).
    /// </summary>
    public static UpdateMarker? Parse(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        string? to = null;
        foreach (var line in lines.Skip(1))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (key.Equals("to", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                to = value;
            }
        }

        return new UpdateMarker(lines[0], to);
    }

    /// <summary>What happened, as seen by the copy running <paramref name="current"/>.</summary>
    public UpdateOutcome OutcomeFor(string current) =>
        !Same(From, current) ? UpdateOutcome.Updated
        : To is { } to && !Same(to, current) ? UpdateOutcome.Failed
        : UpdateOutcome.None;

    private static bool Same(string a, string b) =>
        UpdateFeed.TryParseVersion(a, out var va) && UpdateFeed.TryParseVersion(b, out var vb)
            ? !UpdateFeed.IsNewer(va, vb) && !UpdateFeed.IsNewer(vb, va)
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
