# Atlas content icons

Drop the in-game content icons here as **PNG** files. The plugin loads `icons/<basename>.png`
on demand and draws it above a map node when **Show Content Icons** is enabled (Atlas settings).
Content with no matching PNG falls back to its text name (when **Show Content Names** is on).

## What to extract

`MANIFEST.tsv` lists every needed file: the **PNG filename** to save (left column) and the
**game asset path** to extract it from (right column). Two icon sources are used:

- `Art/2DArt/UIImages/InGame/AtlasScreen/AtlasIconContent/AtlasIconContent*` — the actual atlas
  node content icons (no extension in the dat; they are textures in the bundles).
- `Art/2DArt/SkillIcons/passives/...*.dds` — atlas passive-tree icons, used for content that has
  no dedicated AtlasIconContent sprite.

Extract each asset, convert to PNG, and save it as `<basename>.png` (the left column), e.g.
`AtlasIconContentMapBoss.png`, `Anarchy4.png`. Square icons render best (drawn at row height,
aspect-preserved).

The basename→content mapping is derived from the game data and lives in
`../json/mapcontent.json` (`{ "<id>": { "name": ..., "icon": "<basename>" } }`), generated from
`game_dat/EndgameMapContent.tsv` + `EndgameMapContentVisualIdentity.tsv`. See
`docs/re-findings.md §2.10.3`.
