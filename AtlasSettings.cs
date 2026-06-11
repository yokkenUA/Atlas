using GameHelper.Plugin;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Numerics;

namespace Atlas
{
    public sealed class AtlasSettings : IPSettings
    {
        public Vector4 DefaultBackgroundColor = new(0f, 0f, 0f, 0.85f);
        public Vector4 DefaultFontColor = new(1f, 1f, 1f, 1.0f);

        public bool ControllerMode = false;

        // When on, the plugin loads its bundled fonts (DejaVuSans + GNU Unifont fallback) into the
        // overlay so map names in ANY language render without the user configuring a font in GH.
        // Default on so the plugin works out-of-the-box on a vanilla GameHelper.
        public bool UniversalFont = true;

        // Client-language token used to resolve map-node display names from maps.json "translates"
        // (e.g. "english", "russian", "korean"). Default English. Changing it re-labels nodes live.
        public string Language = "english";

        public string SearchQuery = string.Empty;
        public bool DrawLinesSearchQuery = true;
        public float DrawSearchInRange = 1.3f;

        // Route to all reachable maps flagged 'unique' in maps.json.
        public bool DrawLinesToUniqueMaps = false;
        // Route to reachable maps carrying the 'lineage' / 'arbiter' tag in maps.json.
        public bool PathToLineageMaps = false;
        public bool PathToArbiterMaps = false;

        public bool HideCompletedMaps = true;
        public bool HideNotAccessibleMaps = true;
        public bool HideFailedMaps = true;
        public bool ShowMapBadges = true;
        // Show a small badge with the number of content markers on each node (Essence/Breach/
        // Ritual/Boss…). The exact content TYPE is not persisted by the client for non-rendered
        // nodes (it's rolled from a per-node seed), so only the reliable count is surfaced — it
        // works for every node including off-screen/hidden ones. See docs/re-findings.md §2.7.
        public bool ShowContentCount = true;
        // DEBUG/RE: render the raw per-node content tokens (the StdVector<u32> at element+0x350)
        // above the map name, for visually correlating tokens with known content types across maps.
        // See docs/re-findings.md §2.10. Off by default.
        public bool ShowContentTokens = false;
        // Render per-node content as in-game icons (icons\<basename>.png) instead of / alongside text.
        // Content without a loaded icon still falls back to its text name. See docs/re-findings.md §2.10.3.
        public bool ShowContentIcons = true;
        // Height (px, before UI scaling) of a content icon drawn above a node.
        public float ContentIconSize = 32f;
        // Pixel nudge (before UI scaling) applied to a content icon's position above a node.
        public Vector2 ContentIconOffset = Vector2.Zero;
        // DEBUG/RE: draw the node's child-index (its number in the atlas-panel child list) as a small
        // badge to the LEFT of the map name, so a node called out by number is easy to find on-screen.
        public bool ShowNodeIndex = false;
        public bool ShowBiomeBorder = true;
        public float BiomeBorderThickness = 2.5f;

        public bool RouteLinesThroughNodes = true;
        public float PathLineThickness = 1f;

        public float BaseWidth = 1920f;
        public float BaseHeight = 1080f;
        public Vector2 AnchorNudge = Vector2.Zero;
        public float ScaleMultiplier = 1.1f;

        // Persisted in its own config/mapgroups.json (loaded/saved by the plugin), not settings.txt.
        [JsonIgnore]
        public List<MapGroupSettings> MapGroups = [];
        public string GroupNameInput = string.Empty;

        public Dictionary<string, ContentOverride> ContentOverrides = [];
        public Dictionary<byte, ContentOverride> BiomeOverrides = [];

        // Map Content route groups: user-defined sets of content types. For each content type a route
        // line is drawn from the accessible frontier to the nearest node carrying it (color/thickness/
        // hop-limit per entry). Persisted in settings.txt. See docs/re-findings.md §2.10.5.
        public List<ContentGroupSettings> ContentGroups = [];
        public string ContentGroupNameInput = string.Empty;

        // No seeded Map Groups — the list starts empty and the user creates their own.
    }

    public class MapGroupSettings(string name, Vector4 backgroundColor, Vector4 fontColor)
    {
        public string Name = name;
        public Vector4 BackgroundColor = backgroundColor;
        public Vector4 FontColor = fontColor;
        public List<string> Maps = [];
        public string MapNameInput = string.Empty;
    }

    // A named group of content-route entries. DrawPaths is the group master switch: when off, no
    // entry in the group draws a route, but each entry keeps its own independent DrawPath flag.
    public class ContentGroupSettings
    {
        public string Name { get; set; } = string.Empty;
        public bool DrawPaths { get; set; } = true;
        public List<ContentRouteEntry> Contents { get; set; } = [];
        // Built-in group: can't be deleted and its content list is fixed (the preset). Per-entry
        // colour/hops/draw toggle and the group master toggle stay editable.
        public bool Locked { get; set; } = false;
        // Group-level line thickness, shown under "Draw paths". Used (for all entries) by the built-in
        // group instead of per-entry thickness; user groups keep their per-entry thickness.
        public float LineThickness { get; set; } = 1f;
    }

    // One content type inside a group: routes to the nearest node carrying ContentName (canonical
    // English name, the mapcontent.json key). MaxHops 0 = unlimited; >0 suppresses longer routes.
    public class ContentRouteEntry
    {
        public string ContentName { get; set; } = string.Empty;
        public Vector4 LineColor { get; set; } = new(1f, 0.85f, 0.2f, 1f);
        public float LineThickness { get; set; } = 1f;
        public bool DrawPath { get; set; } = true;
        public int MaxHops { get; set; } = 0;
        // Optional map-based matcher used by the built-in group: "tag:<tag>" or "type:<type>" matches
        // by maps.json classification instead of by node content. Null/empty = match by ContentName.
        public string Match { get; set; } = null;
    }

    public class ContentOverride
    {
        public Vector4? BackgroundColor { get; set; }
        public Vector4? BorderColor { get; set; }
        public Vector4? FontColor { get; set; }
        public bool? Show { get; set; }
        public string Abbrev { get; set; }
    }
}