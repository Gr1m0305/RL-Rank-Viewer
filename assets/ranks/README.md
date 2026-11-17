# Rank icons

Put your own rank icon images in this folder (next to `RLOverlay.exe` after publishing), or in
`%LOCALAPPDATA%\RLOverlay\ranks\`, which overrides this folder.

The repository deliberately contains **no** Psyonix artwork. If a file is missing, the app uses the icon URL served
by the stats provider at runtime (cached in `%LOCALAPPDATA%\RLOverlay\cache\ranks\`), and if that is unavailable,
a simple built-in tier-coloured placeholder.

* Format: **PNG** or **SVG**, transparent background, ideally **128×128** (icons render at 34 px).
* File names are lower-case; use either `.png` or `.svg` (PNG wins if both exist).

| Rank | File name |
|---|---|
| Unranked / Casual | `unranked.png` |
| Bronze I | `bronze_1.png` |
| Bronze II | `bronze_2.png` |
| Bronze III | `bronze_3.png` |
| Silver I | `silver_1.png` |
| Silver II | `silver_2.png` |
| Silver III | `silver_3.png` |
| Gold I | `gold_1.png` |
| Gold II | `gold_2.png` |
| Gold III | `gold_3.png` |
| Platinum I | `platinum_1.png` |
| Platinum II | `platinum_2.png` |
| Platinum III | `platinum_3.png` |
| Diamond I | `diamond_1.png` |
| Diamond II | `diamond_2.png` |
| Diamond III | `diamond_3.png` |
| Champion I | `champion_1.png` |
| Champion II | `champion_2.png` |
| Champion III | `champion_3.png` |
| Grand Champion I | `grand_champion_1.png` |
| Grand Champion II | `grand_champion_2.png` |
| Grand Champion III | `grand_champion_3.png` |
| Supersonic Legend | `supersonic_legend.png` |

The app re-reads this folder when it starts.
