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

        public string SearchQuery = string.Empty;
        public bool DrawLinesSearchQuery = true;
        public float DrawSearchInRange = 1.3f;

        // Route to all reachable maps flagged 'unique' in maps.json.
        public bool DrawLinesToUniqueMaps = false;
        // Route to reachable maps carrying the 'lineage' / 'arbiter' tag in maps.json.
        public bool PathToLineageMaps = false;
        public bool PathToArbiterMaps = false;

        public bool HideCompletedMaps = true;
        public bool HideNotAccessibleMaps = false;
        public bool HideFailedMaps = true;
        public bool ShowMapBadges = true;
        public bool ShowBiomeBorder = true;
        public float BiomeBorderThickness = 2.5f;

        public bool RouteLinesThroughNodes = true;
        public float PathLineThickness = 6f;

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

        public AtlasSettings()
        {
            var citadels = new MapGroupSettings("Citadels", new Vector4(1f, 1f, 1f, 0.85f), new Vector4(1f, 0f, 0f, 1f));
            citadels.Maps.Add("The Copper Citadel");
            citadels.Maps.Add("The Iron Citadel");
            citadels.Maps.Add("The Stone Citadel");

            var pinnacleBosses = new MapGroupSettings("Pinnacle Boss", new Vector4(0.471f, 0.196f, 0.471f, 0.85f), new Vector4(1f, 1f, 1f, 1f));
            pinnacleBosses.Maps.Add("The Burning Monolith");

            var special = new MapGroupSettings("Special", new Vector4(0.737f, 0.376f, 0.145f, 0.85f), new Vector4(0f, 0f, 0f, 1f));
            special.Maps.Add("Untainted Paradise");
            special.Maps.Add("Vaults of Kamasa");
            special.Maps.Add("Moment of Zen");
            special.Maps.Add("The Ezomyte Megaliths");
            special.Maps.Add("Derelict Mansion");
            special.Maps.Add("The Viridian Wildwood");
            special.Maps.Add("The Jade Isles");
            special.Maps.Add("Castaway");
            special.Maps.Add("The Fractured Lake");
            special.Maps.Add("Ice Cave");

            var good = new MapGroupSettings("Good", new Vector4(0.157f, 0.157f, 0f, 0.85f), new Vector4(1f, 1f, 0f, 1f));
            good.Maps.Add("Burial Bog");
            good.Maps.Add("Creek");
            good.Maps.Add("Rustbowl");
            good.Maps.Add("Sandspit");
            good.Maps.Add("Savannah");
            good.Maps.Add("Steaming Springs");
            good.Maps.Add("Steppe");
            good.Maps.Add("Wetlands");
            good.Maps.Add("Willow");

            var towers = new MapGroupSettings("Towers", new Vector4(0.863f, 0f, 0.882f, 0.85f), new Vector4(0f, 0f, 0f, 1f));
            towers.Maps.Add("Bluff");
            towers.Maps.Add("Lost Towers");
            towers.Maps.Add("Mesa");
            towers.Maps.Add("Sinking Spire");
            towers.Maps.Add("Alpine Ridge");

            MapGroups.Add(citadels);
            MapGroups.Add(towers);
            MapGroups.Add(pinnacleBosses);
            MapGroups.Add(good);
            MapGroups.Add(special);
        }
    }

    public class MapGroupSettings(string name, Vector4 backgroundColor, Vector4 fontColor)
    {
        public string Name = name;
        public Vector4 BackgroundColor = backgroundColor;
        public Vector4 FontColor = fontColor;
        public List<string> Maps = [];
        public string MapNameInput = string.Empty;
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