using System.Text.Json;
using RLOverlay.Core.Config;
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

/// <param name="Width">CSS pixels of the cropped row (with its stat tiles).</param>
/// <param name="Height">CSS pixels of the cropped row (with its stat tiles).</param>
/// <param name="Background">The row's card colour as CSS (e.g. <c>rgb(0, 21, 56)</c>), for the band behind it.</param>
/// <param name="Missing">Stat tiles that were asked for but aren't on the page (yet).</param>
/// <param name="Cells">
/// The shown cells and then the tiles with their natural widths, before lining up with other rows; used to give every
/// player's row the same columns.
/// </param>
public sealed record TrackerCropResult(
    TrackerCropStatus Status,
    int Width = 0,
    int Height = 0,
    string? Background = null,
    int Missing = 0,
    IReadOnlyList<TrackerCell>? Cells = null);

/// <summary>
/// One column of a cropped row: a table cell named by its class ("rating", "peak", "matches", ...) or a stat tile
/// ("tile:wins"), and its width in CSS pixels.
/// </summary>
public sealed record TrackerCell(string Key, int Width);

/// <summary>
/// What is shown next to a player's rank (tracker.gg's playlist icon, rank, rating and division up/down, which are
/// always there): the playlist row's <see cref="Peak"/> rating and <see cref="Streak"/> (matches) cells, the all-time
/// <see cref="Best"/> rating from the Peak Rating card (Ranked Duel, Doubles and Standard only), and tracker.gg's
/// lifetime stat tiles named in <see cref="Tiles"/> ("Wins", "Goals", ...) in that order.
/// </summary>
public sealed record TrackerCropOptions(bool Peak, bool Streak, IReadOnlyList<string> Tiles, bool Best = false)
{
    /// <summary>Just the rank: no extra cells or tiles.</summary>
    public static TrackerCropOptions RankOnly { get; } = new(false, false, []);

    /// <summary>The stats ticked in the settings, in the order they are shown.</summary>
    public static TrackerCropOptions From(StatColumns columns)
    {
        var tiles = new List<string>();
        void Tile(bool on, string label)
        {
            if (on)
            {
                tiles.Add(label);
            }
        }

        Tile(columns.Wins, "Wins");
        Tile(columns.Goals, "Goals");
        Tile(columns.Shots, "Shots");
        Tile(columns.Assists, "Assists");
        Tile(columns.Saves, "Saves");
        Tile(columns.Mvps, "MVPs");
        return new TrackerCropOptions(columns.Peak, columns.Streak, tiles, columns.AllTimePeak);
    }

    /// <summary>Compares by content (records compare lists by reference).</summary>
    public string Key => $"{Peak}|{Streak}|{Best}|{string.Join(",", Tiles)}";
}

/// <summary>
/// How the overlay shows one playlist row of a tracker.gg profile page. The page is loaded normally in an embedded
/// browser; this script then copies the playlist table, keeps only the wanted row (and the cells the user picked),
/// drops the row's menu button, puts copies of the picked lifetime stat tiles next to it and hides the rest of the
/// page. It reports only status and size, never the numbers: the user reads those on tracker.gg's own page.
/// </summary>
/// <remarks>
/// Page structure (checked November 2026): <c>table.trn-table</c> → <c>tbody tr</c> per playlist, the label in
/// <c>td.name .playlist</c>, then <c>td.rating</c> (rating), two <c>td.change</c> (division up/down), a second
/// <c>td.rating</c> (peak rating), <c>td.matches</c> (matches and streak) and the menu in <c>td.tools</c>. The lifetime
/// tiles are <c>div.stats &gt; div.stat</c> with the label in <c>.name</c> and a <c>.more</c> chevron. The sidebar's
/// <c>.peak-rating-widget</c> has an <c>h3</c> per ranked playlist (Duel, Doubles, Standard) followed by a
/// <c>.ratings-grid</c> of "Current" and "Best" blocks, each titled by an <c>h4.rating__title</c>. Everything is
/// copied (not moved) so the site's own scripts keep working. Column widths are taken from the first layout and
/// reused, so the copy keeps tracker.gg's spacing when the embedded view is resized or the playlist changes.
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
        Playlist.Heatseeker => "Heatseeker",
        Playlist.Tournament => "Tournament Matches",
        Playlist.Casual => "Casual",
        _ => "",
    };

    /// <summary>Sites a tracker.gg page needs: its own pages, images, data and fonts, and Cloudflare's browser check.</summary>
    private static readonly string[] AllowedHosts =
    [
        "tracker.network", "tracker.gg", "trackercdn.com", "fonts.googleapis.com", "fonts.gstatic.com", "challenges.cloudflare.com",
    ];

    /// <summary>
    /// May the embedded page load this? Only tracker.gg's own sites (and subdomains), its web fonts and Cloudflare's
    /// browser check. Ads, video players, analytics and trackers are refused: they never showed on the overlay, and they
    /// were most of what loading a lobby cost. Non-web addresses (data:, blob:) are left alone.
    /// </summary>
    public static bool AllowsRequest(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return true;
        }

        var host = uri.Host.TrimEnd('.');
        return AllowedHosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A script for <c>ExecuteScriptAsync</c> that crops the page to <paramref name="label"/>'s row plus what
    /// <paramref name="options"/> asks for. <paramref name="columns"/> (from <see cref="AlignWidths"/>) makes each column
    /// at least that wide and leaves a gap for any this player's page doesn't have, so every row lines up.
    /// </summary>
    public static string CropScript(string label, TrackerCropOptions options, IReadOnlyList<TrackerCell>? columns = null)
    {
        var widths = (columns ?? []).Select(c => new { key = c.Key, width = c.Width });
        var args = JsonSerializer.Serialize(new { label, peak = options.Peak, streak = options.Streak, best = options.Best, tiles = options.Tiles, widths });
        return $"({CropFunction})({args})";
    }

    /// <summary>
    /// The columns that line up all <paramref name="rows"/> (each a <see cref="TrackerCropResult.Cells"/> list): every
    /// column any row has, in page order, at the widest width it has in any row.
    /// </summary>
    public static IReadOnlyList<TrackerCell> AlignWidths(IEnumerable<IReadOnlyList<TrackerCell>?> rows)
    {
        var order = new List<string>();
        var widest = new Dictionary<string, int>();
        foreach (var row in rows.OfType<IReadOnlyList<TrackerCell>>().OrderByDescending(r => r.Count))
        {
            var at = 0; // a column this row adds goes right after the row's previous column
            foreach (var cell in row)
            {
                var i = order.IndexOf(cell.Key);
                if (i < 0)
                {
                    order.Insert(at, cell.Key);
                    i = at;
                }

                at = i + 1;
                widest[cell.Key] = Math.Max(widest.GetValueOrDefault(cell.Key), cell.Width);
            }
        }

        return order.Select(k => new TrackerCell(k, widest[k])).ToList();
    }

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
            List<TrackerCell>? cells = null;
            if (root.TryGetProperty("cells", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                cells = [];
                foreach (var cell in c.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object))
                {
                    var key = cell.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                    var width = cell.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var n) ? n : 0;
                    if (!string.IsNullOrEmpty(key) && cells.All(x => x.Key != key))
                    {
                        cells.Add(new TrackerCell(key, Math.Clamp(width, 0, 2000)));
                    }
                }
            }

            return status == TrackerCropStatus.Ok && (Int("width") <= 0 || Int("height") <= 0)
                ? new TrackerCropResult(TrackerCropStatus.Unknown)
                : new TrackerCropResult(status, Int("width"), Int("height"), background, Math.Max(0, Int("missing")), cells);
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
        (opt) => {
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
          const cell = cells.find(d => norm(d.textContent) === norm(opt.label));
          if (!cell) { st.textContent = 'body>*{display:none!important}'; return { status: 'no-row' }; }
          const tr = cell.closest('tr'), table = tr.closest('table'), card = table.closest('.card') || table.parentElement;
          const idx = [...table.tBodies[0].rows].indexOf(tr);
          if (!window.__rloCols || window.__rloCols.length !== tr.cells.length) {
            st.textContent = ''; // measure the page's own layout (a "no-row" answer may have hidden it)
            window.__rloCols = [...tr.cells].map(c => Math.ceil(c.getBoundingClientRect().width));
          }
          const cols = window.__rloCols;
          const head = table.tHead ? [...table.tHead.querySelectorAll('th')].map(h => (h.textContent || '').trim()) : [];
          const peakCaption = head.find(h => /peak/i.test(h)) || 'Peak Rating';
          const clone = table.cloneNode(true);
          [...clone.tBodies[0].rows].forEach((r, i) => { if (i !== idx) r.remove(); });
          if (clone.tHead) clone.tHead.remove();
          if (clone.tFoot) clone.tFoot.remove();
          clone.querySelectorAll('colgroup').forEach(c => c.remove());
          const row = clone.tBodies[0].rows[0];
          let ratings = 0;
          const used = new Set();
          const keyed = (el, key) => { let k = key, n = 2; while (used.has(k)) k = key + '#' + n++; used.add(k); el.dataset.rlo = k; };
          [...row.cells].forEach((c, i) => {
            const last = i === row.cells.length - 1;
            const peak = c.classList.contains('rating') && ratings++ > 0; // the second rating cell is the peak
            if (c.classList.contains('tools') || (last && norm(c.textContent) === '' && !c.querySelector('img'))
                || (peak && !opt.peak) || (c.classList.contains('matches') && !opt.streak)) { c.remove(); return; }
            if (peak) { const cap = document.createElement('div'); cap.className = 'rlo-cap'; cap.textContent = peakCaption; c.prepend(cap); }
            c.style.minWidth = (cols[i] || Math.ceil(c.getBoundingClientRect().width)) + 'px';
            keyed(c, peak ? 'peak' : c.classList.contains('rating') ? 'rating' : (c.classList[0] || 'cell' + i));
          });
          const kept = [...row.cells];
          const bg = getComputedStyle(card).backgroundColor;
          const host = document.createElement('div');
          host.id = 'rlo-crop';
          const shell = card.cloneNode(false);
          shell.appendChild(clone);
          host.appendChild(shell);
          let bestMissing = 0;
          if (opt.best) {
            // All-time peak: the "Best" block under this playlist in the Peak Rating card. Only Ranked Duel, Doubles
            // and Standard have one; other playlists simply show none.
            const widget = document.querySelector('.peak-rating-widget');
            const sec = widget ? [...widget.querySelectorAll('h3')].find(h => norm(h.textContent) === norm(opt.label)) : null;
            const title = d => { const t = d.querySelector('.rating__title'); return norm(t ? t.textContent : ''); };
            const best = sec ? [...sec.parentElement.querySelectorAll('.ratings-grid > div')].find(d => title(d) === 'best') : null;
            if (best) {
              const bestShell = widget.cloneNode(false), bestGrid = best.parentElement.cloneNode(false), b = best.cloneNode(true);
              b.querySelectorAll('.rating__title, .more').forEach(x => x.remove());
              b.classList.add('rlo-best');
              const cap = document.createElement('div'); cap.className = 'rlo-cap'; cap.textContent = 'All-Time Peak';
              b.prepend(cap);
              bestGrid.appendChild(b);
              bestShell.appendChild(bestGrid);
              host.appendChild(bestShell);
              keyed(b, 'best');
              kept.push(b);
            } else if (!widget) bestMissing = 1; // the card may still be on its way
          }
          const want = (opt.tiles || []).map(norm);
          const all = want.length ? [...document.querySelectorAll('div.stats > div.stat')] : [];
          const label = t => { const n = t.querySelector('.name'); return norm(n ? n.textContent : ''); };
          const tiles = want.map(w => all.find(t => label(t) === w)).filter(t => t);
          let capColor = 'rgb(181, 208, 255)', tileGrid = null, tileShell = null;
          if (tiles.length) {
            const grid = tiles[0].parentElement, statsCard = grid.closest('.card') || grid.parentElement;
            const name = tiles[0].querySelector('.name');
            if (name) capColor = getComputedStyle(name).color;
            tileShell = statsCard.cloneNode(false);
            tileGrid = grid.cloneNode(false);
            tiles.forEach(t => {
              const c = t.cloneNode(true);
              c.classList.remove('feature-hint'); // its pulsing highlight
              c.querySelectorAll('.more').forEach(m => m.remove()); // the expand chevron
              tileGrid.appendChild(c);
              keyed(c, 'tile:' + label(t));
              kept.push(c);
            });
            tileShell.appendChild(tileGrid);
            host.appendChild(tileShell);
          }
          document.body.appendChild(host);
          st.textContent =
            'html,body{overflow:hidden!important;margin:0!important;background:' + bg + '!important}' +
            'body>*:not(#rlo-crop){display:none!important}' +
            '#rlo-crop{position:fixed;left:0;top:0;z-index:2147483647;width:max-content;display:flex;align-items:center;background:' + bg + '}' +
            '#rlo-crop>*{margin:0!important;padding:0!important;border:0!important;border-radius:0!important;box-shadow:none!important;' +
            'width:max-content!important;min-width:0!important;background:' + bg + '!important}' +
            '#rlo-crop table{table-layout:auto!important;width:max-content!important;min-width:0!important;margin:0!important}' +
            '#rlo-crop td{white-space:nowrap!important}' +
            '#rlo-crop,#rlo-crop *{animation:none!important;transition:none!important}' +
            '#rlo-crop .stats{display:flex!important;flex-wrap:nowrap!important;margin:0!important;padding:0!important}' +
            '#rlo-crop .stat{flex:none!important;width:max-content!important;min-width:0!important;max-width:none!important;margin:0!important}' +
            '#rlo-crop .ratings-grid{display:block!important;margin:0!important;padding:0!important}' +
            '#rlo-crop .rlo-best{padding:0 18px!important;box-sizing:border-box}' +
            '#rlo-crop .rlo-cap{font-size:13px;font-weight:500;line-height:1.2;color:' + capColor + '}';
          // Sizes only (layout, never the numbers): each column's natural width, then line up with the other rows.
          const cellsOut = kept.map(k => ({ key: k.dataset.rlo, width: Math.ceil(k.getBoundingClientRect().width) }));
          const align = (opt.widths || []).filter(a => a && typeof a.key === 'string' && a.width > 0);
          if (align.length) {
            const have = new Map(kept.map(k => [k.dataset.rlo, k]));
            const isTile = key => key.startsWith('tile:');
            align.forEach((a, i) => {
              if (have.has(a.key)) return;
              // A column this player's page doesn't have (e.g. no peak yet): leave a gap so the rest still line up.
              if (a.key === 'best') {
                const gap = document.createElement('div');
                gap.className = 'rlo-best';
                host.insertBefore(gap, tileShell || null);
                have.set(a.key, gap);
                return;
              }
              const parent = isTile(a.key) ? tileGrid : row;
              if (!parent) return;
              const next = align.slice(i + 1).map(b => have.get(b.key)).find(n => n && n.parentElement === parent);
              const gap = document.createElement(isTile(a.key) ? 'div' : 'td');
              if (isTile(a.key)) gap.className = 'stat';
              parent.insertBefore(gap, next || null);
              have.set(a.key, gap);
            });
            const set = (k, w) => {
              k.style.setProperty('min-width', w + 'px', 'important');
              if (k.tagName === 'TD') k.style.setProperty('width', w + 'px', 'important'); // a cell's width is its minimum
            };
            const grow = align.map(a => have.get(a.key)).map((k, i) => k && k.getBoundingClientRect().width < align[i].width ? k : null);
            grow.forEach((k, i) => { if (k) set(k, align[i].width); });
            grow.forEach((k, i) => {
              if (!k) return;
              const off = k.getBoundingClientRect().width - align[i].width; // padding the width didn't include
              if (Math.abs(off) > 0.5) set(k, Math.max(0, align[i].width - off));
            });
          }
          const r = host.getBoundingClientRect();
          return { status: 'ok', width: Math.ceil(r.width), height: Math.ceil(r.height), background: bg, missing: want.length - tiles.length + bestMissing, cells: cellsOut };
        }
        """;
}
