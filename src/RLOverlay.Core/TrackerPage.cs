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
/// One column of a cropped row: a table cell named by its column ("name", "rating", "up", "down", "peak", "matches",
/// "streak"), the all-time peak ("best") or a stat tile ("tile:wins"), and its width in CSS pixels.
/// </summary>
public sealed record TrackerCell(string Key, int Width);

/// <summary>
/// What is shown next to a player's rank (tracker.gg's rank icon, playlist, rank, rating and division up/down, which
/// are always there): the playlist row's <see cref="Peak"/> rating cell and, for <see cref="Streak"/>, its Matches and
/// Streak cells, the all-time <see cref="Best"/> rating from the Ratings card (Ranked Duel, Doubles and Standard only),
/// and tracker.gg's lifetime stat tiles named in <see cref="Tiles"/> ("Wins", "Goals", ...) in that order.
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
/// drops the row's chart link, puts copies of the picked lifetime stat tiles next to it and hides the rest of the
/// page. It reports only status and size, never the numbers: the user reads those on tracker.gg's own page.
/// </summary>
/// <remarks>
/// Page structure (tracker.gg's new layout, checked 26 November 2026): the Playlists card holds a <c>table</c> laid out
/// as a CSS grid, its header row naming the columns ("Playlist/Rank", "Rating", "Div Up", "Div Down", "Peak Rating",
/// "Matches", "Streak", then an unnamed chart link). The cells have no column classes, so the columns are told apart by
/// that header. Each <c>tbody tr</c> starts with a cell holding the rank image, the playlist's name in its own
/// <c>span</c> and the division. The sidebar's Ratings card has an <c>h4</c> per ranked playlist (Duel, Doubles,
/// Standard) followed by a grid of "Current" and "All-Time Best" blocks, each captioned by a <c>span.caption</c>; its
/// Lifetime card is a grid of <c>button</c> tiles, each named by a <c>.stat-name</c>. Everything is copied (not moved)
/// so the site's own scripts keep working, and the copy is laid out as one row: each column at least as wide as its
/// header on the site (the first as wide as the site makes it), in tracker.gg's colours whatever the row's stripe.
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
          const outside = el => !(old && old.contains(el));
          // The playlist table: the one whose header starts with "Playlist/Rank" and has a "Rating" column.
          const heads = t => t.tHead && t.tHead.rows[0] ? [...t.tHead.rows[0].cells].map(h => norm(h.textContent)) : [];
          const table = [...document.querySelectorAll('table')].filter(outside)
            .find(t => { const h = heads(t); return h.length > 2 && h[0].includes('playlist') && h.includes('rating') && t.tBodies[0]; });
          if (!table || table.tBodies[0].rows.length === 0) {
            const text = norm(document.body ? document.body.innerText.slice(0, 3000) : '');
            return { status: text.includes('player not found') ? 'not-found' : 'waiting' };
          }
          if (old) old.remove();
          let st = document.getElementById('rlo-crop-style');
          if (!st) { st = document.createElement('style'); st.id = 'rlo-crop-style'; document.head.appendChild(st); }
          const want = norm(opt.label);
          const isLabel = c => [...c.querySelectorAll('span')].some(s => norm(s.textContent) === want);
          const rows = [...table.tBodies[0].rows];
          const tr = rows.find(r => r.cells[0] && isLabel(r.cells[0]));
          if (!tr) { st.textContent = 'body>*{display:none!important}'; return { status: 'no-row' }; }
          // Columns are told apart by their header ("Rating", "Div Up", "Div Down", "Peak Rating", "Matches", "Streak"); the
          // last one (a chart link) has none and is dropped, as is any column added later that isn't one of these.
          const names = heads(table);
          const captions = [...table.tHead.rows[0].cells].map(h => (h.textContent || '').replace(/\s+/g, ' ').trim());
          const role = i => {
            const h = names[i] || '';
            if (i === 0) return 'name';
            if (h === 'rating') return 'rating';
            if (/div\s*up/.test(h)) return 'up';
            if (/div\s*down/.test(h)) return 'down';
            if (/peak/.test(h)) return 'peak';
            if (/match/.test(h)) return 'matches';
            if (/streak/.test(h)) return 'streak';
            return '';
          };
          const keep = r => ['name', 'rating', 'up', 'down'].includes(r) || (r === 'peak' && opt.peak) || ((r === 'matches' || r === 'streak') && opt.streak);
          const group = table.parentElement, provider = group ? group.parentElement : null;
          const firstMin = (() => { const m = /minmax\(\s*(\d+)px/.exec((provider && provider.getAttribute('style')) || ''); return m ? +m[1] : 0; })();
          const solid = c => !!c && c !== 'transparent' && !/^rgba\(.*,\s*0\)$/.test(c);
          let bg = 'rgb(0, 18, 48)';
          // The table's colour, not the row's: tracker.gg alternates row colours, and every player's band should match.
          for (let e = table; e; e = e.parentElement) { const c = getComputedStyle(e).backgroundColor; if (solid(c)) { bg = c; break; } }
          const head0 = table.tHead.rows[0].cells[1] || table.tHead.rows[0].cells[0];
          let capColor = head0 ? getComputedStyle(head0).color : 'rgb(181, 208, 255)';

          // All-time peak: the "All-Time Best" block under this playlist's heading in the Ratings card. Only Ranked Duel,
          // Doubles and Standard have one; other playlists simply show none.
          let bestPage = null, bestMissing = 0;
          if (opt.best) {
            const blocks = h => h.nextElementSibling && h.nextElementSibling.querySelector(':scope > * > .caption') ? h.nextElementSibling : null;
            const headings = [...document.querySelectorAll('h4')].filter(outside).filter(blocks);
            const heading = headings.find(h => norm(h.textContent) === want);
            const cap = d => { const t = d.querySelector(':scope > .caption'); return norm(t ? t.textContent : ''); };
            bestPage = heading ? [...blocks(heading).children].find(d => cap(d).includes('best')) || null : null;
            if (!bestPage && headings.length === 0) bestMissing = 1; // the card may still be on its way
          }

          // Lifetime stat tiles: the buttons in the sidebar's Lifetime card, found by the stat's name.
          const wantTiles = (opt.tiles || []).map(norm);
          const statName = t => { const n = t.querySelector('.stat-name'); return norm(n ? n.textContent : ''); };
          const scope = document.querySelector('aside') || document;
          const buttons = wantTiles.length ? [...scope.querySelectorAll('button')].filter(outside).filter(b => b.querySelector('.stat-name')) : [];
          const first = wantTiles.map(w => buttons.find(b => statName(b) === w)).find(t => t);
          const tilesPage = first ? wantTiles.map(w => buttons.find(b => statName(b) === w && b.parentElement === first.parentElement)).filter(t => t) : [];
          if (tilesPage.length) { const n = tilesPage[0].querySelector('.stat-name'); if (n) capColor = getComputedStyle(n).color; }

          // The copy: the row (in its table and the table's wrappers, so the site's styles still apply), the all-time peak, the tiles.
          const host = document.createElement('div');
          host.id = 'rlo-crop';
          const clone = table.cloneNode(true);
          const idx = rows.indexOf(tr);
          [...clone.tBodies[0].rows].forEach((r, i) => { if (i !== idx) r.remove(); });
          if (clone.tFoot) clone.tFoot.remove();
          clone.querySelectorAll('colgroup').forEach(c => c.remove());
          // "td--overflow" (the page's table is wider than the view) draws a scroll shadow beside the sticky first cell.
          clone.querySelectorAll('[class*="--overflow"]').forEach(e => [...e.classList].filter(c => c.endsWith('--overflow')).forEach(c => e.classList.remove(c)));
          const shell = (provider || group || table).cloneNode(false);
          shell.removeAttribute('style');
          shell.classList.add('rlo-flat');
          let inner = shell;
          if (provider && group) { inner = group.cloneNode(false); inner.removeAttribute('style'); inner.classList.add('rlo-flat'); shell.appendChild(inner); }
          inner.appendChild(clone);
          host.appendChild(shell);
          let bestEl = null, bestShell = null;
          if (bestPage) {
            bestShell = bestPage.parentElement.cloneNode(false);
            bestShell.classList.add('rlo-flat');
            bestEl = bestPage.cloneNode(true);
            bestEl.querySelectorAll(':scope > .caption, .more').forEach(x => x.remove());
            bestEl.classList.remove('border-r');
            bestEl.classList.add('rlo-best');
            const c = document.createElement('div'); c.className = 'rlo-cap'; c.textContent = 'All-Time Peak';
            bestEl.prepend(c);
            bestShell.appendChild(bestEl);
            host.appendChild(bestShell);
          }
          let tileGrid = null, tileShell = null;
          const tileEls = [];
          if (tilesPage.length) {
            const grid = tilesPage[0].parentElement, statsCard = grid.closest('section') || grid.parentElement;
            tileShell = statsCard.cloneNode(false);
            tileGrid = grid.cloneNode(false);
            tileGrid.classList.add('rlo-tiles');
            tilesPage.forEach(t => {
              const c = t.cloneNode(true);
              c.querySelectorAll('.more').forEach(m => m.remove()); // an expand chevron, if any
              c.dataset.rloName = statName(t);
              tileGrid.appendChild(c);
              tileEls.push(c);
            });
            tileShell.appendChild(tileGrid);
            host.appendChild(tileShell);
          }
          st.textContent =
            'html,body{overflow:hidden!important;margin:0!important;background:' + bg + '!important}' +
            'body>*:not(#rlo-crop){display:none!important}' +
            '#rlo-crop{position:fixed;left:0;top:0;z-index:2147483647;width:max-content;display:flex;align-items:center;background:' + bg + '}' +
            '#rlo-crop>*{margin:0!important;padding:0!important;border:0!important;border-radius:0!important;box-shadow:none!important;' +
            'width:max-content!important;min-width:0!important;background:' + bg + '!important}' +
            '#rlo-crop .rlo-flat{display:block!important;overflow:visible!important;width:max-content!important;min-width:0!important;max-width:none!important;' +
            'margin:0!important;padding:0!important;gap:0!important}' +
            '#rlo-crop table,#rlo-crop thead,#rlo-crop tbody,#rlo-crop tr{display:flex!important;flex-direction:row!important;width:max-content!important;min-width:0!important;' +
            'max-width:none!important;margin:0!important;gap:0!important}' +
            '#rlo-crop tr{background:' + bg + '!important}' +
            '#rlo-crop th,#rlo-crop td{flex:none!important;position:relative!important;left:auto!important;top:auto!important;' +
            'white-space:nowrap!important;box-sizing:border-box!important}' +
            '#rlo-crop,#rlo-crop *{animation:none!important;transition:none!important}' +
            '#rlo-crop>*,#rlo-crop .rlo-flat,#rlo-crop .rlo-tiles{container-type:normal!important}' + // a size container would measure as empty
            '#rlo-crop .rlo-capped{flex-direction:column!important;align-items:flex-end!important;justify-content:center!important}' +
            '#rlo-crop .rlo-tiles{display:flex!important;flex-direction:row!important;flex-wrap:nowrap!important;margin:0!important;padding:0!important;gap:0!important;' +
            'border:0!important;border-radius:0!important;box-shadow:none!important;background:transparent!important}' +
            '#rlo-crop .rlo-tiles>*{flex:none!important;width:max-content!important;min-width:0!important;max-width:none!important;margin:0!important;' +
            'padding-top:0!important;padding-bottom:0!important}' +
            '#rlo-crop .rlo-best{padding:0 18px!important;box-sizing:border-box}' +
            '#rlo-crop .rlo-cap{font-size:13px;font-weight:500;line-height:1.2;color:' + capColor + '}';
          document.body.appendChild(host);

          // Each column at least as wide as its header on tracker.gg (the first as wide as the site makes it), then the
          // header goes: the row is shown on its own.
          const headW = clone.tHead ? [...clone.tHead.rows[0].cells].map(h => Math.ceil(h.getBoundingClientRect().width)) : [];
          if (clone.tHead) clone.tHead.remove();
          const row = clone.tBodies[0].rows[0];
          const used = new Set();
          const keyed = (el, key) => { let k = key, n = 2; while (used.has(k)) k = key + '#' + n++; used.add(k); el.dataset.rlo = k; };
          [...row.cells].forEach((c, i) => {
            const r = role(i);
            if (!keep(r)) { c.remove(); return; }
            if (r === 'peak' || r === 'matches' || r === 'streak') {
              const cap = document.createElement('div'); cap.className = 'rlo-cap'; cap.textContent = captions[i];
              c.prepend(cap);
              c.classList.add('rlo-capped');
            }
            const min = Math.max(headW[i] || 0, i === 0 ? firstMin : 0);
            if (min > 0) c.style.setProperty('min-width', min + 'px', 'important');
            keyed(c, r);
          });
          const kept = [...row.cells];
          if (bestEl) { keyed(bestEl, 'best'); kept.push(bestEl); }
          tileEls.forEach(c => { keyed(c, 'tile:' + c.dataset.rloName); kept.push(c); });

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
              parent.insertBefore(gap, next || null);
              have.set(a.key, gap);
            });
            const set = (k, w) => {
              k.style.setProperty('min-width', w + 'px', 'important');
              if (k.tagName === 'TD') k.style.setProperty('width', w + 'px', 'important');
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
          return { status: 'ok', width: Math.ceil(r.width), height: Math.ceil(r.height), background: bg, missing: wantTiles.length - tilesPage.length + bestMissing, cells: cellsOut };
        }
        """;
}
