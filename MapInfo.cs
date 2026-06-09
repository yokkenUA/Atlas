using System.Collections.Generic;
using Newtonsoft.Json;

namespace Atlas
{
    /// <summary>
    ///     One atlas-map entry from json/maps.json, keyed by the internal WorldArea MapId
    ///     (e.g. "MapUniqueMerchant03_Beach"). Multiple internal ids can share a display name
    ///     (the three "Moment of Zen" variants), so searching/grouping by <see cref="Name"/>
    ///     highlights every variant at once.
    /// </summary>
    public sealed class MapInfo
    {
        /// <summary>In-game display name, e.g. "Moment of Zen".</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>"normal" or "unique" (auto-inferred: unique == MapUnique* id prefix).</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Structural group, e.g. "map", "arbiter" (curated by hand).</summary>
        [JsonProperty("group")]
        public string Group { get; set; }

        /// <summary>Cross-cutting feature tags (e.g. "lineage") a UI toggle can highlight by.</summary>
        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new();

        public bool HasTag(string tag) =>
            Tags != null && Tags.Exists(t => string.Equals(t, tag, System.StringComparison.OrdinalIgnoreCase));
    }
}
