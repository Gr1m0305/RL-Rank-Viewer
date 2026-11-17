using System.Globalization;
using System.Text.Json;

namespace RLOverlay.Core.Providers;

/// <summary>
/// Counts paid API requests per calendar month (UTC) and refuses more than the user's limit, so a busy month can't
/// run up overage charges. The count is kept in a small JSON file so it survives restarts. A limit of 0 means no
/// limit (requests are still counted).
/// </summary>
public sealed class LookupBudget
{
    private readonly string? _path;
    private readonly Func<int> _limit;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string _month = "";
    private int _used;

    public LookupBudget(string? path, Func<int> limit, TimeProvider? time = null)
    {
        _path = path;
        _limit = limit;
        _time = time ?? TimeProvider.System;
        Load();
    }

    /// <summary>Requests made this month.</summary>
    public int UsedThisMonth
    {
        get
        {
            lock (_gate)
            {
                RollOver();
                return _used;
            }
        }
    }

    /// <summary>Takes one request slot. False (and nothing counted) when this month's limit is reached.</summary>
    public bool TryTake(out int used, out int limit)
    {
        lock (_gate)
        {
            RollOver();
            limit = Math.Max(0, _limit());
            if (limit > 0 && _used >= limit)
            {
                used = _used;
                return false;
            }

            _used++;
            used = _used;
            Save();
            return true;
        }
    }

    private string CurrentMonth() => _time.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private void RollOver()
    {
        var month = CurrentMonth();
        if (month != _month)
        {
            _month = month;
            _used = 0;
        }
    }

    private void Load()
    {
        _month = CurrentMonth();
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path));
            if (saved is not null && saved.Month == _month)
            {
                _used = Math.Max(0, saved.Used);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file starts the month's count again rather than blocking lookups.
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(_month, _used)));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counting still works in memory for this session.
        }
    }

    private sealed record Saved(string Month, int Used);
}
