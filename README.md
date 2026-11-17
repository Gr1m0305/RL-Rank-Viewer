# RL Overlay

A small companion overlay for Rocket League. When you load into a match, a compact glass panel appears just below
the scoreboard with every player's rank icon, MMR and stats for the playlist you choose (2v2 by default). Rows fill
in live as players join, the panel fades out at kickoff, and clicking a name opens that player's tracker profile.

> Status: in development — see [PROGRESS.md](PROGRESS.md).

## How it stays safe with Easy Anti-Cheat

Rocket League uses Easy Anti-Cheat. This app is a completely separate process:

* It **never** injects into, reads memory from, hooks or opens a handle to the game process.
* Its only game data comes from Rocket League's **official Stats API** — a local socket the game itself opens on
  `127.0.0.1:49123` when you enable it in the game's ini file.
* It draws its own transparent, always-on-top window. That is why Rocket League must run in **Borderless** or
  **Windowed** mode: exclusive fullscreen hides every other window.

## Where player ranks come from — read this

Rank/MMR data comes from a pluggable stats provider. The app ships a `TrackerGgProvider` that uses Tracker Network's
official developer API with **your own API key**. Be aware:

* Tracker Network's developer API requires a key, and Rocket League has **not** been offered to public keys
  (a maintainer said in 2024 they are not allowed to offer a Rocket League API; Rocket League is not in their list of
  supported titles; the developer portal returned 404 when this was written).
* The tracker.gg website's internal endpoints are protected and their Terms of Service restrict scraping. This app
  does not scrape or get around that protection.

So with a public key the provider will most likely return *No data*. The provider is an interface
(`IPlayerStatsProvider`) so a sanctioned data source can be dropped in later. The `--demo` flag and the
*Preview* button use a built-in mock provider so you can see the overlay without a key.

## Requirements

* Windows 10 or 11, Rocket League (Steam or Epic) in Borderless or Windowed mode.
* To build: .NET SDK 8 or newer (the projects target .NET 8).

## Build and run

```bash
dotnet build RLOverlay.sln
```

```bash
dotnet test RLOverlay.sln
```

```bash
dotnet run --project src/RLOverlay.App -- --demo
```

Full setup instructions (enabling the Stats API, Borderless mode, API key) are completed in Part 7.

## Docs

* [docs/STATS_API_NOTES.md](docs/STATS_API_NOTES.md) — what the Stats API sends and how the app reads it
* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — components, data flow, match lifecycle state machine

## Licence notes

Rank artwork belongs to Psyonix and is **not** included in the repository or the exe. On first start the app
downloads the official in-game rank icons once (from Tracker Network's CDN, the images tracker.gg shows) into
`%LOCALAPPDATA%\RLOverlay\cache\ranks\`. Until then, or if downloads are turned off, it draws simple built-in
placeholders, which is also what the screenshots in `docs/screenshots` show. Your own icons in `assets/ranks/` win over
the download (see [assets/ranks/README.md](assets/ranks/README.md)). The bundled Inter font is licensed under the SIL Open Font License 1.1.
