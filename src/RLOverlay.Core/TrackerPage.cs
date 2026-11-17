using System.Text.Json;
using RLOverlay.Core.Playlists;

namespace RLOverlay.Core;

public enum TrackerCropStatus
{
    /// <summary>The profile page hasn't finished loading its playlist table yet.</summary>
    Waiting,

    /// <summary>The row is cropped and shown; <see cref="TrackerCropResult.Width"/>/<c>Height</c> are its size.</summary>
    Ok,

    /// <summary>The player has no row for this playlist (never played it).</summary>
    NoRow,

    /// <summary>tracker.gg says "Player not found".</summary>
    NotFound,

    /// <summary>The script's answer couldn't be read.</summary>
    Unknown,
}

/// <param name="Width">CSS pixels of the cropped row.</param>
/// <param name="Height">CSS pixels of the cropped row.</param>
/// <param name="Background">The row's card colour as CSS (e.g. <c>rgb(0, 21, 56)</c>), for the band behind it.</param>
public sealed record TrackerCropResult(TrackerCropStatus Status, int Width = 0, int Height = 0, string? Background = null);

/// <summary>
/// How the lobby window shows one playlist row of a tracker.gg profile page. The page is loaded normally in an
/// embedded browser when the user opens the window; this script then copies the playlist table, keeps only the
/// wanted row, drops the row's menu button and hides the rest of the page. It reports only status and size, never
/// the numbers in the row: the user reads those on tracker.gg's own page.
/// </summary>
/// <remarks>
/// Page structure (checked November 2026): <c>table.trn-table</c> → <c>tbody tr</c> per playlist, the label in
/// <c>td.name .playlist</c>, the menu in <c>td.tools</c>. The row is copied (not moved) so the site's own scripts
/// keep working. Column widths are taken from the first layout and reused, so the copy keeps tracker.gg's spacing
/// when the embedded view is resized or the playlist changes.
/// </remarks>
public static class TrackerPage
{
    /// <summary>The label tracker.gg uses for each playlist row.</summary>
    public static string RowLabel(Playlist playlist) => playlist switch
    {
        Playlist.Duel => "Ranked Duel 1v1",
        Playlist.Doubles => "Ranked Doubles 2v2",
        Playlist.Standard => "Ranked Standard 3v3",
        Playlist.Hoops => "Hoops",
        Playlist.Rumble => "Rumble",
        Playlist.Dropshot => "Dropshot",
        Playlist.SnowDay => "Snowday",
        Playlist.Tournament => "Tournament Matches",
        Playlist.Casual => "Casual",
        _ => "",
    };

    /// <summary>A script for <c>ExecuteScriptAsync</c> that crops the page to <paramref name="label"/>'s row.</summary>
    public static string CropScript(string label) => $"({CropFunction})({JsonSerializer.Serialize(label)})";

    /// <summary>Undoes the crop and shows the whole page (when the row can't be found, e.g. a browser check).</summary>
    public const string ShowWholePageScript =
        "(() => { document.getElementById('rlo-crop')?.remove(); const s = document.getElementById('rlo-crop-style'); if (s) s.textContent = ''; return true; })()";

    /// <summary>Reads the JSON <c>ExecuteScriptAsync</c> returns for <see cref="CropScript"/>.</summary>
    public static TrackerCropResult ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TrackerCropResult(TrackerCropStatus.Unknown);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var s))
            {
                return new TrackerCropResult(TrackerCropStatus.Unknown);
            }

            var status = s.GetString() switch
            {
                "ok" => TrackerCropStatus.Ok,
                "waiting" => TrackerCropStatus.Waiting,
                "no-row" => TrackerCropStatus.NoRow,
                "not-found" => TrackerCropStatus.NotFound,
                _ => TrackerCropStatus.Unknown,
            };
            int Int(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
            var background = root.TryGetProperty("background", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            return status == TrackerCropStatus.Ok && (Int("width") <= 0 || Int("height") <= 0)
                ? new TrackerCropResult(TrackerCropStatus.Unknown)
                : new TrackerCropResult(status, Int("width"), Int("height"), background);
        }
        catch (JsonException)
        {
            return new TrackerCropResult(TrackerCropStatus.Unknown);
        }
    }

    /// <summary>
    /// Parses a CSS colour such as <c>rgb(0, 21, 56)</c> or <c>rgba(0, 21, 56, 1)</c> into bytes. False for anything
    /// else (and for fully transparent colours).
    /// </summary>
    public static bool TryParseCssColor(string? css, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var t = (css ?? "").Trim();
        var open = t.IndexOf('(');
        var close = t.LastIndexOf(')');
        if (open < 0 || close <= open || !t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = t[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3
            || !byte.TryParse(parts[0], out r) || !byte.TryParse(parts[1], out g) || !byte.TryParse(parts[2], out b))
        {
            return false;
        }

        return parts.Length < 4
               || !double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var alpha)
               || alpha > 0;
    }

    private const string CropFunction = """
        (label) => {
          const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
          const old = document.getElementById('rlo-crop');
          const cells = [...document.querySelectorAll('table.trn-table td.name .playlist')].filter(d => !(old && old.contains(d)));
          if (cells.length === 0) {
            const text = norm(document.body ? document.body.innerText.slice(0, 3000) : '');
            return { status: text.includes('player not found') ? 'not-found' : 'waiting' };
          }
          if (old) old.remove();
          let st = document.getElementById('rlo-crop-style');
          if (!st) { st = document.createElement('style'); st.id = 'rlo-crop-style'; document.head.appendChild(st); }
          const cell = cells.find(d => norm(d.textContent) === norm(label));
          if (!cell) { st.textContent = 'body>*{display:none!important}'; return { status: 'no-row' }; }
          const tr = cell.closest('tr'), table = tr.closest('table'), card = table.closest('.card') || table.parentElement;
          const idx = [...table.tBodies[0].rows].indexOf(tr);
          if (!window.__rloCols || window.__rloCols.length !== tr.cells.length) {
            st.textContent = ''; // measure the page's own layout (a "no-row" answer may have hidden it)
            window.__rloCols = [...tr.cells].map(c => Math.ceil(c.getBoundingClientRect().width));
          }
          const cols = window.__rloCols;
          const clone = table.cloneNode(true);
          [...clone.tBodies[0].rows].forEach((r, i) => { if (i !== idx) r.remove(); });
          if (clone.tHead) clone.tHead.remove();
          if (clone.tFoot) clone.tFoot.remove();
          clone.querySelectorAll('colgroup').forEach(c => c.remove());
          const row = clone.tBodies[0].rows[0];
          [...row.cells].forEach((c, i) => {
            const last = i === row.cells.length - 1;
            if (c.classList.contains('tools') || (last && norm(c.textContent) === '' && !c.querySelector('img'))) { c.remove(); return; }
            c.style.minWidth = (cols[i] || Math.ceil(c.getBoundingClientRect().width)) + 'px';
          });
          const bg = getComputedStyle(card).backgroundColor;
          const host = document.createElement('div');
          host.id = 'rlo-crop';
          const shell = card.cloneNode(false);
          shell.appendChild(clone);
          host.appendChild(shell);
          document.body.appendChild(host);
          st.textContent =
            'html,body{overflow:hidden!important;margin:0!important;background:' + bg + '!important}' +
            'body>*:not(#rlo-crop){display:none!important}' +
            '#rlo-crop{position:fixed;left:0;top:0;z-index:2147483647;width:max-content}' +
            '#rlo-crop>*{margin:0!important;padding:0!important;border:0!important;border-radius:0!important;box-shadow:none!important;' +
            'width:max-content!important;min-width:0!important;background:' + bg + '!important}' +
            '#rlo-crop table{table-layout:auto!important;width:max-content!important;min-width:0!important;margin:0!important}' +
            '#rlo-crop td{white-space:nowrap!important}';
          const r = clone.getBoundingClientRect();
          return { status: 'ok', width: Math.ceil(r.width), height: Math.ceil(r.height), background: bg };
        }
        """;
}
