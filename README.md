# Atlas

A [GH](https://github.com/Gordin/GameHelper2) plugin that overlays
the endgame Atlas: it labels each map node with its name, content badges and biome border, can hide
completed / not-accessible maps, draws routing lines to citadels / towers / searched maps, and
colour-codes maps via custom groups.

All per-node data (map id, biome, completed / accessible state) is read straight from game memory
using offsets verified live for build **0.5.x**.

## Requirements

- A current [GH](https://github.com/Gordin/GameHelper2) checkout (this is a plugin, not a
  standalone app).
- .NET 10 SDK (targets `net10.0-windows`, x64).

## Build & install

Drop this repo into the GameHelper2 `Plugins` directory so the layout is:

```
<GameHelper2>/
  GameHelper/GameHelper.csproj
  Plugins/
    Atlas/               ← contents of this repo
      Atlas.csproj
      json/biome.json
      ...
```

The `.csproj` expects `..\..\GameHelper\GameHelper.csproj` and, on build, copies `Atlas.dll` plus
the `json/` data into `GameHelper/<OutDir>/Plugins/Atlas/`. Build, then enable **Atlas** in
GameHelper's plugin list and open the in-game Atlas (World) screen.

> The `json/` folder (biome / content definitions) is **source data and must ship with the repo** —
> without it biome borders and content badges are empty.

## Settings (highlights)

- **Search Maps** — highlight and draw lines to matching maps (comma-separated).
- **Draw Lines Settings** — route lines through nodes (A\*), and to citadels / towers / search hits.
- **Hide Completed Maps** / **Hide Not Accessible Maps** — filter nodes by state.
- **Show Biome Border** — colour each node's border by biome.
- **Layout Settings** — nudge / scale the labels.
- **Map Groups** — colour-code custom lists of maps (Citadels, Towers, …).

## Credits

- Built as a plugin for [GH](https://github.com/Gordin/GameHelper2).

## Disclaimer

This is a read-only overlay tool for personal use. Use at your own risk and in accordance with the game's terms of service.
