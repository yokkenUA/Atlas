namespace Atlas
{
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Text;

    public sealed class Atlas : PCore<AtlasSettings>
    {
        private const uint SearchLineColor = 0xFFFFFFFF;   // white  — routes to search hits
        private const uint UniqueLineColor = 0xFFFF00FF;   // magenta — routes to unique maps
        private const uint LineageLineColor = 0xFF00E000;  // green  — routes to 'lineage' maps
        private const uint ArbiterLineColor = 0xFF0000FF;  // red    — routes to 'arbiter' maps
        private const uint CompletedNodeDotColor = 0xFF00FF00;
        private const uint DotOutlineColor = 0xFF000000;

        private const int ChannelGrid = 0;
        private const int ChannelLines = 1;
        private const int ChannelDots = 2;
        private const int ChannelLabels = 3;

        // Atlas connection (edge) list — a flat StdVector on the atlas-panel UiElement. Each entry
        // is {int unknown; grid Source; grid Target}; Source/Target are grid coords matched against
        // each node's grid (node+0x320). Verified live in GameHelper2-main for PoE2 0.5.x.
        private const int AtlasConnectionsVectorOffset = 0x5A8;

        // fp of the "you are here" marker child (shares the node-list container fp, not the
        // map-node fp 0x542EF3). Used to locate the player's current atlas node by screen position.
        private const uint AtlasCurrentNodeFp = 0x502EF3;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct AtlasConnectionEdge
        {
            public int Unknown;
            public StdTuple2D<int> Source;
            public StdTuple2D<int> Target;
        }

        private string SettingPathname => Path.Join(DllDirectory, "config", "settings.txt");
        private string MapGroupsPathname => Path.Join(DllDirectory, "config", "mapgroups.json");
        private string NewGroupName = string.Empty;

        private static readonly Dictionary<string, ContentInfo> MapTags = [];
        private static readonly Dictionary<string, ContentInfo> MapPlain = [];
        private static readonly Dictionary<byte, BiomeInfo> Biomes = [];
        // Internal WorldArea MapId (e.g. "MapUniqueMerchant03_Beach") → map info (display name +
        // type/group/tags), loaded from json/maps.json (generated from WorldAreaNames.tsv).
        // Multiple internal ids can map to the same display name, so searching/grouping by the
        // display name highlights every variant at once; group/tags drive category highlights.
        private static readonly Dictionary<string, MapInfo> MapInfos = new(StringComparer.OrdinalIgnoreCase);

        public static IntPtr Handle { get; set; }
        private static int _handlePid;

        // ── Per-node static-data cache ──────────────────────────────────────
        // Reading + chasing pointers for all ~1700 atlas nodes every frame was the FPS killer
        // (tens of thousands of cross-process reads per frame). The slow-changing per-node data
        // (map id, biome, completed/accessible state, content badges) is cached and refreshed on
        // an interval instead; each frame we only read the node's UiElementBase for a live screen
        // position (so panning/zoom stay exact) and draw the nodes that are actually on-screen.
        private struct NodeData
        {
            public IntPtr Address;
            public string InternalId;       // internal WorldArea MapId, e.g. "MapUniqueMerchant03_Beach"
            public string MapName;          // in-game display name (falls back to InternalId), normalized
            public MapInfo MapInfo;         // maps.json classification (type/group/tags); null when unmapped
            public byte BiomeId;
            public AtlasNodeState State;
            public List<string> RawContents;
            public StdTuple2D<int> GridPosition;
        }
        private readonly List<NodeData> nodeCache = new();
        // Addresses of "you are here" marker candidates (fp 0x502EF3), refreshed with nodeCache.
        private readonly List<IntPtr> markerCandidates = new();
        private int cacheFrameCounter = int.MaxValue;   // force refresh on first frame
        private int cachedAtlasCount = -1;
        private const int CacheRefreshFrames = 20;       // rebuild static data ~3×/sec at 60fps

        // Per-frame memo for GetFinalTopLeft's parent-chain reads: every atlas node shares the
        // same ancestors, so without this each node re-reads the whole chain. Cleared each frame.
        private static readonly Dictionary<IntPtr, UiElementBaseOffset> frameBaseCache = new();


        public override void OnDisable()
        {
            CloseAndResetHandle();
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(SettingPathname))
            {
                var content = File.ReadAllText(SettingPathname);
                var serializerSettings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                Settings = JsonConvert.DeserializeObject<AtlasSettings>(content, serializerSettings);
            }

            LoadMapGroups();
            LoadBiomeMap();
            LoadContentMap();
            LoadMaps();
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(SettingPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var settingsData = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(SettingPathname, settingsData);

            SaveMapGroups();
        }

        // MapGroups live in their own config/mapgroups.json (kept out of settings.txt via [JsonIgnore]).
        // Loads that file when present; otherwise migrates the MapGroups array from an older settings.txt
        // so customizations survive the split, falling back to the constructor defaults.
        private void LoadMapGroups()
        {
            if (Settings == null)
                return;

            if (File.Exists(MapGroupsPathname))
            {
                var groups = JsonConvert.DeserializeObject<List<MapGroupSettings>>(File.ReadAllText(MapGroupsPathname));
                if (groups != null)
                    Settings.MapGroups = groups;
                return;
            }

            if (File.Exists(SettingPathname))
            {
                try
                {
                    var root = JObject.Parse(File.ReadAllText(SettingPathname));
                    if (root.TryGetValue(nameof(AtlasSettings.MapGroups), out var token) && token.Type == JTokenType.Array)
                    {
                        var groups = token.ToObject<List<MapGroupSettings>>();
                        if (groups != null && groups.Count > 0)
                            Settings.MapGroups = groups;
                    }
                }
                catch (JsonException) { /* malformed legacy settings — keep constructor defaults */ }
            }

            SaveMapGroups();   // materialize the new file so subsequent loads use it directly
        }

        private void SaveMapGroups()
        {
            if (Settings?.MapGroups == null)
                return;

            var dir = Path.GetDirectoryName(MapGroupsPathname);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(MapGroupsPathname, JsonConvert.SerializeObject(Settings.MapGroups, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            #region SettingsUI
            ImGui.SeparatorText("Search Maps");
            ImGui.InputTextWithHint("Search Map", "You can search multiple maps at once using a comma separator ','", ref Settings.SearchQuery, 256);
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear"))
                Settings.SearchQuery = string.Empty;
            if (ImGui.TreeNode("Draw Lines Settings"))
            {
                ImGui.Checkbox("Route Lines Through Nodes (Shortest Path)", ref Settings.RouteLinesThroughNodes);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(150);
                ImGui.SliderFloat("Path Thickness", ref Settings.PathLineThickness, 1.0f, 8.0f);
                ImGui.Checkbox("Draw Lines to Search in range", ref Settings.DrawLinesSearchQuery);
                ImGui.SameLine();
                ImGui.SliderFloat("##DrawSearchInRange", ref Settings.DrawSearchInRange, 1.0f, 10.0f);
                ImGui.Checkbox("Draw Routes to Unique Maps", ref Settings.DrawLinesToUniqueMaps);
                ImGui.Checkbox("Path to Lineage gem maps", ref Settings.PathToLineageMaps);
                ImGui.Checkbox("Path to Arbiter fragments", ref Settings.PathToArbiterMaps);
                ImGui.TreePop();
            }

            ImGui.SeparatorText("Atlas Settings");
            ImGui.Checkbox("Hide Completed Maps", ref Settings.HideCompletedMaps);
            ImGui.Checkbox("Hide Not Accessible Maps", ref Settings.HideNotAccessibleMaps);
            ImGui.Checkbox("Show Biome Border", ref Settings.ShowBiomeBorder);
            if (Settings.ShowBiomeBorder)
                if (ImGui.TreeNode("Biome Settings"))
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderFloat("Biome Border Thickness", ref Settings.BiomeBorderThickness, 1.0f, 6.0f);

                    if (ImGui.BeginTable("split", 3))
                    {
                        foreach (var biome in Biomes)
                        {
                            ImGui.TableNextColumn();
                            var id = biome.Key;
                            var info = biome.Value;

                            if (!Settings.BiomeOverrides.TryGetValue(id, out var ov))
                            {
                                ov = new ContentOverride();
                                Settings.BiomeOverrides[id] = ov;
                            }

                            bool show = ov.Show ?? info.Show;
                            if (ImGui.Checkbox($"##Show##{id}", ref show))
                            {
                                ov.Show = show;
                                ApplyBiomeOverrides();
                            }

                            var border = ov.BorderColor ?? info.BdColor;
                            ImGui.SameLine();
                            ColorSwatch($"Border Color##Biome{id}", ref border);
                            if (!ColorsEqual(border, ov.BorderColor ?? info.BdColor))
                            {
                                ov.BorderColor = border;
                                ApplyBiomeOverrides();
                            }

                            var label = string.IsNullOrWhiteSpace(info.Label) ? $"Biome {id}" : info.Label;
                            ImGui.SameLine();
                            ImGui.Text(label);
                        }
                        ImGui.EndTable();
                    }

                    ImGui.TreePop();
                }

            ImGui.SeparatorText("Layout Settings");
            var nudge = Settings.AnchorNudge;
            if (ImGui.SliderFloat2("Layout Nudge (px)", ref nudge, -60f, 60f))
                Settings.AnchorNudge = nudge;
            ImGui.SliderFloat("Scale Multiplier", ref Settings.ScaleMultiplier, 0.5f, 3.0f);

            ImGui.SeparatorText("Map Groups");

            if (ImGui.TreeNode("Settings"))
            {
                ImGui.InputTextWithHint("##MapGroupName", "group name", ref Settings.GroupNameInput, 256);
                ImGui.SameLine();
                if (ImGui.Button("Add new map group"))
                {
                    Settings.MapGroups.Add(new MapGroupSettings(Settings.GroupNameInput, Settings.DefaultBackgroundColor, Settings.DefaultFontColor));
                    Settings.GroupNameInput = string.Empty;
                }

                for (int i = 0; i < Settings.MapGroups.Count; i++)
                {
                    var mapGroup = Settings.MapGroups[i];
                    if (ImGui.TreeNode($"{mapGroup.Name}##MapGroup{i}"))
                    {
                        float buttonSize = ImGui.GetFrameHeight();
                        if (TriangleButton($"##Up{i}", buttonSize, new Vector4(1, 1, 1, 1), true))
                        {
                            MoveMapGroup(i, -1);
                        }
                        ImGui.SameLine();
                        if (TriangleButton($"##Down{i}", buttonSize, new Vector4(1, 1, 1, 1), false))
                        {
                            MoveMapGroup(i, 1);
                        }
                        ImGui.SameLine();
                        if (ImGui.Button($"Rename Group##{i}"))
                        {
                            NewGroupName = mapGroup.Name;
                            ImGui.OpenPopup($"RenamePopup##{i}");
                        }
                        ImGui.SameLine();
                        if (ImGui.Button($"Delete Group##{i}"))
                        {
                            DeleteMapGroup(i);
                        }
                        ImGui.SameLine();
                        ColorSwatch($"##MapGroupBackgroundColor{i}", ref mapGroup.BackgroundColor);
                        ImGui.SameLine();
                        ImGui.Text("Background Color");
                        ImGui.SameLine();
                        ColorSwatch($"##MapGroupFontColor{i}", ref mapGroup.FontColor);
                        ImGui.SameLine(); ImGui.Text("Font Color");

                        for (int j = 0; j < mapGroup.Maps.Count; j++)
                        {
                            var mapName = mapGroup.Maps[j];
                            if (ImGui.InputTextWithHint($"##MapName{i}-{j}", "map name", ref mapName, 256))
                                mapGroup.Maps[j] = mapName;

                            ImGui.SameLine();
                            if (ImGui.Button($"Delete##MapNameDelete{i}-{j}"))
                            {
                                mapGroup.Maps.RemoveAt(j);
                                break;
                            }
                        }

                        if (ImGui.Button($"Add new map##AddNewMap{i}"))
                            mapGroup.Maps.Add(string.Empty);

                        if (ImGui.BeginPopupModal($"RenamePopup##{i}", ImGuiWindowFlags.AlwaysAutoResize))
                        {
                            ImGui.InputText("New Name", ref NewGroupName, 256);
                            if (ImGui.Button("OK"))
                            {
                                mapGroup.Name = NewGroupName;
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.SameLine();
                            if (ImGui.Button("Cancel"))
                            {
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.EndPopup();
                        }
                        ImGui.TreePop();
                    }
                }
                ImGui.TreePop();
            }
            #endregion
        }

        public override void DrawUI()
        {
            var inventoryPanel = InventoryPanel();

            var isGameHelperForeground = Process.GetCurrentProcess().MainWindowHandle == GetForegroundWindow();
            if (!Core.Process.Foreground && !isGameHelperForeground)
                return;

            EnsureProcessHandle();

            var player = Core.States.InGameStateObject.CurrentAreaInstance.Player;
            if (!player.TryGetComponent<Render>(out var playerRender))
                return;

            var drawList = ImGui.GetBackgroundDrawList();

            drawList.ChannelsSplit(4);

            var atlasPanelAddr = GetAtlasPanelAddress();
            var atlasUi = atlasPanelAddr == IntPtr.Zero ? default : Read<UiElement>(atlasPanelAddr);
            if (!atlasUi.IsVisible)
                return;

            // Node positions/connections come from the live UI tree + the panel's edge list
            // (panel+0x5A8); the 0.4.x inline vectors at +0x510/+0x528 no longer apply.
            var atlasCount = atlasUi.Length;

            if (atlasCount <= 0 || atlasCount > 10000)
                return;

            // Reset the per-frame parent-read memo, then rebuild the slow-changing per-node data
            // only on an interval (or when the node count changes / cache is empty).
            frameBaseCache.Clear();
            if (++cacheFrameCounter >= CacheRefreshFrames || cachedAtlasCount != atlasCount || nodeCache.Count == 0)
            {
                this.RefreshNodeCache(atlasUi, atlasCount);
                cacheFrameCounter = 0;
            }

            var panelTopLeft = GetFinalTopLeft(in atlasUi.UiElementBase);
            var panelScale = ComputeScalePair(in atlasUi.UiElementBase);
            var panelSize = new Vector2(
                atlasUi.UiElementBase.UnscaledSize.X * panelScale.X,
                atlasUi.UiElementBase.UnscaledSize.Y * panelScale.Y);
            var panelRect = new RectangleF(panelTopLeft.X, panelTopLeft.Y, panelSize.X, panelSize.Y);

            var searchQuery = NormalizeName(Settings.SearchQuery);
            bool doSearch = !string.IsNullOrWhiteSpace(searchQuery);
            List<string> searchList = [];
            if (doSearch)
            {
                searchList = searchQuery
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(NormalizeName)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            var boundsSearch = CalculateBounds(Settings.DrawSearchInRange);

            var playerLocation = Core.States.InGameStateObject.CurrentWorldInstance.WorldToScreen(playerRender.WorldPosition);

            float resScale = ComputeRelativeUiScale(in atlasUi.UiElementBase, Settings.BaseWidth, Settings.BaseHeight);
            float uiScale = Math.Clamp(Settings.ScaleMultiplier * resScale, 0.5f, 4.0f);
            using (new FontScaleScope(uiScale))
            {
                if (!Settings.ControllerMode)
                    if (inventoryPanel)
                        return;

                // ── Route planning (shortest hops over the revealed atlas edges) ──────────
                // Built once per frame when a routed target is wanted: the edge graph from
                // panel+0x5A8, screen centers for on-screen nodes, the impassable set (failed
                // maps), the accessible frontier, and one multi-source BFS from it. Each target's
                // route is reconstructed from that BFS tree (shortest hops from nearest entry).
                Dictionary<StdTuple2D<int>, Vector2> routeCenters = null;
                Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> routeGraph = null;
                HashSet<StdTuple2D<int>> routeBlocked = null;
                HashSet<StdTuple2D<int>> accessibleSet = null;        // nodes you can run from now
                Dictionary<StdTuple2D<int>, StdTuple2D<int>> accessibleCameFrom = null; // multi-source BFS tree
                bool routeReady = false;
                Vector2 routeAnchor = playerLocation;   // "you are here" marker (context dot only, not the route start)
                bool markerFound = false;

                bool wantRoute = Settings.RouteLinesThroughNodes &&
                    ((Settings.DrawLinesSearchQuery && doSearch) || Settings.DrawLinesToUniqueMaps
                     || Settings.PathToLineageMaps || Settings.PathToArbiterMaps);
                if (wantRoute)
                {
                    routeCenters = new Dictionary<StdTuple2D<int>, Vector2>(nodeCache.Count);
                    routeBlocked = new HashSet<StdTuple2D<int>>();
                    accessibleSet = new HashSet<StdTuple2D<int>>();
                    foreach (var nd in nodeCache)
                    {
                        var ub = Read<UiElementBaseOffset>(nd.Address);
                        var sc = ComputeScalePair(in ub);
                        var tl = GetFinalTopLeft(in ub);
                        var sz = new Vector2(ub.UnscaledSize.X * sc.X, ub.UnscaledSize.Y * sc.Y);
                        var center = tl + sz * 0.5f;
                        if (panelRect.Contains(center.X, center.Y))
                            routeCenters[nd.GridPosition] = center;
                        if (nd.State == AtlasNodeState.Failed)
                            routeBlocked.Add(nd.GridPosition);
                        if (nd.State == AtlasNodeState.AccessibleNow)
                            accessibleSet.Add(nd.GridPosition);
                    }
                    routeGraph = BuildConnectionGraph(atlasPanelAddr);

                    // Routes start from the accessible frontier (nodes you can run now), NOT from the
                    // player: one multi-source BFS from all accessible nodes gives, for every target,
                    // the shortest hop path back to its nearest accessible entry.
                    accessibleCameFrom = MultiSourceBfs(routeGraph, accessibleSet, routeBlocked);
                    routeReady = accessibleSet.Count > 0;

                    // Anchor at the "you are here" marker (it renders on the current map node):
                    // among visible candidates inside the panel, pick the one sitting closest to a
                    // map node. Fall back to the player's world projection if none is found.
                    float bestMarkerD = float.MaxValue;
                    foreach (var mAddr in markerCandidates)
                    {
                        var mb = Read<UiElementBaseOffset>(mAddr);
                        var msc = ComputeScalePair(in mb);
                        var mtl = GetFinalTopLeft(in mb);
                        var msz = new Vector2(mb.UnscaledSize.X * msc.X, mb.UnscaledSize.Y * msc.Y);
                        var mc = mtl + msz * 0.5f;
                        if (!panelRect.Contains(mc.X, mc.Y))
                            continue;

                        float dNode = float.MaxValue;
                        foreach (var c in routeCenters.Values)
                        {
                            float d = Vector2.DistanceSquared(mc, c);
                            if (d < dNode) dNode = d;
                        }
                        if (dNode < bestMarkerD)
                        {
                            bestMarkerD = dNode;
                            routeAnchor = mc;
                            markerFound = true;
                        }
                    }
                }

                // Off-screen labels/badges are culled (nothing to draw); a margin keeps
                // partially-visible labels alive. Lines below are drawn before this cull so
                // off-screen citadel/tower/search targets still get their line.
                var screenBounds = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);
                screenBounds.Inflate(64f, 64f);

                foreach (var nd in nodeCache)
                {
                    var mapName = nd.MapName;

                    if (string.IsNullOrWhiteSpace(mapName))
                        continue;
                    if (!IsPrintableUnicode(mapName))
                        continue;
                    if (doSearch && !searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    bool completed = nd.State == AtlasNodeState.CompletedBase;
                    bool notAccessible = nd.State != AtlasNodeState.AccessibleNow && nd.State != AtlasNodeState.CompletedBase;

                    // Route targets (position-independent): a reachable, not-yet-completed map that a
                    // "Draw Lines" checkbox points at. These override "Hide Not Accessible Maps" so the
                    // map you're routing to stays visible even when other inaccessible maps are hidden.
                    bool targetUnique = Settings.DrawLinesToUniqueMaps && !completed
                        && string.Equals(nd.MapInfo?.Type, "unique", StringComparison.OrdinalIgnoreCase);
                    bool targetLineage = Settings.PathToLineageMaps && !completed && (nd.MapInfo?.HasTag("lineage") ?? false);
                    bool targetArbiter = Settings.PathToArbiterMaps && !completed && (nd.MapInfo?.HasTag("arbiter") ?? false);
                    bool routeTarget = targetUnique || targetLineage || targetArbiter
                        || (Settings.DrawLinesSearchQuery && doSearch);

                    if (Settings.HideCompletedMaps && completed)
                        continue;
                    if (Settings.HideNotAccessibleMaps && notAccessible && !routeTarget)
                        continue;

                    // Live screen position: read only the node's own UiElementBase. Its ancestors
                    // are shared and memoized in frameBaseCache, so the parent walk in
                    // GetFinalTopLeft costs ~nothing per node after the first.
                    var uiBase = Read<UiElementBaseOffset>(nd.Address);
                    var nodeScale = ComputeScalePair(in uiBase);
                    var nodeTopLeft = GetFinalTopLeft(in uiBase);
                    var nodeSize = new Vector2(uiBase.UnscaledSize.X * nodeScale.X,
                                               uiBase.UnscaledSize.Y * nodeScale.Y);

                    var textSize = ImGui.CalcTextSize(mapName);
                    var nodeCenter = nodeTopLeft + nodeSize * 0.5f;
                    Vector2 drawPosition = nodeCenter - textSize * 0.5f + Settings.AnchorNudge;

                    var padding = new Vector2(5, 2) * uiScale;
                    var bgPos = drawPosition - padding;
                    var bgSize = textSize + padding * 2;
                    var rectCenter = (bgPos + (bgPos + bgSize)) * 0.5f;

                    // Routes to search hits / unique / lineage / arbiter maps — drawn even when the
                    // target is off-screen, so this happens before the visibility cull.
                    bool shouldDrawSearch = Settings.DrawLinesSearchQuery && doSearch
                        && searchList.Any(searchTerm => mapName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        && boundsSearch.Contains(new PointF(drawPosition.X, drawPosition.Y));
                    if (shouldDrawSearch || targetUnique || targetLineage || targetArbiter)
                    {
                        uint lineColor = shouldDrawSearch ? SearchLineColor
                            : targetUnique ? UniqueLineColor
                            : targetLineage ? LineageLineColor
                            : ArbiterLineColor;
                        float thickness = MathF.Max(1f, uiScale * Settings.PathLineThickness);
                        bool drewRoute = false;

                        // Shortest hop path from the nearest accessible node to this target
                        // (skipping failed maps). path[0] = the accessible entry you'd run first.
                        if (routeReady && accessibleCameFrom != null)
                        {
                            var path = PathFromAccessible(nd.GridPosition, accessibleCameFrom, accessibleSet);
                            if (path != null && path.Count > 0)
                            {
                                DrawNodePath(drawList, path, routeCenters, lineColor, thickness);
                                int hops = path.Count - 1;

                                // Green dot on the accessible entry node (where you start running).
                                if (routeCenters.TryGetValue(path[0], out var startC))
                                {
                                    drawList.ChannelsSetCurrent(ChannelDots);
                                    float sr = MathF.Max(3f, thickness * 1.3f);
                                    drawList.AddCircleFilled(startC, sr, ImGuiHelper.Color(new Vector4(0.2f, 1f, 0.2f, 1f)));
                                    drawList.AddCircle(startC, sr, DotOutlineColor, 0, MathF.Max(1f, sr * 0.35f));
                                }

                                // Hop count above the target node.
                                drawList.ChannelsSetCurrent(ChannelLabels);
                                string ht = hops.ToString();
                                var hts = ImGui.CalcTextSize(ht);
                                var hp = new Vector2(nodeCenter.X - hts.X * 0.5f, nodeCenter.Y - nodeSize.Y * 0.5f - hts.Y - 2f * uiScale);
                                var hpad = new Vector2(4, 1) * uiScale;
                                drawList.AddRectFilled(hp - hpad, hp + hts + hpad, ImGuiHelper.Color(new Vector4(0, 0, 0, 0.75f)), 3f * uiScale);
                                drawList.AddText(hp, ImGuiHelper.Color(new Vector4(1f, 0.9f, 0.2f, 1f)), ht);

                                drewRoute = true;
                            }
                        }

                        // Straight-line fallback only when node-routing isn't active (toggle off /
                        // no accessible nodes) — never the player-anchored fan when routing is on.
                        if (!drewRoute && !routeReady)
                        {
                            var intersectionPoint = GetLineRectangleIntersection(playerLocation, rectCenter, bgPos, bgPos + bgSize);

                            drawList.ChannelsSetCurrent(ChannelLines);
                            drawList.AddLine(playerLocation, intersectionPoint, lineColor, thickness);
                            var endDot = OffsetPointOutsideRect(intersectionPoint, rectCenter, thickness * 0.6f);
                            drawList.ChannelsSetCurrent(ChannelDots);
                            drawList.AddCircleFilled(endDot, thickness, lineColor);
                            drawList.AddCircle(endDot, thickness, DotOutlineColor, 0, MathF.Max(1f, thickness * 0.35f));
                        }
                    }

                    if (!screenBounds.IntersectsWith(new RectangleF(bgPos.X, bgPos.Y, bgSize.X, bgSize.Y)))
                        continue;

                    var group = Settings.MapGroups.Find(g => g.Maps.Exists(
                        m => NormalizeName(m).Equals(mapName, StringComparison.OrdinalIgnoreCase)));

                    var backgroundColor = group?.BackgroundColor ?? Settings.DefaultBackgroundColor;
                    var fontColor = group?.FontColor ?? Settings.DefaultFontColor;
                    if (completed)
                        backgroundColor.W *= 0.4f;

                    drawList.ChannelsSetCurrent(ChannelLabels);
                    float rounding = 3f * uiScale;

                    if (Settings.ShowBiomeBorder && Biomes.TryGetValue(nd.BiomeId, out var biome) && biome.Show)
                    {
                        var biomeColor = biome.BdColor;
                        if (completed)
                            biomeColor.W *= 0.4f;

                        float bBorderTh = MathF.Max(1f, uiScale * Settings.BiomeBorderThickness);
                        var half = bBorderTh * 0.5f;
                        var outMin = bgPos - new Vector2(half, half);
                        var outMax = (bgPos + bgSize) + new Vector2(half, half);
                        var outRounding = MathF.Max(0f, rounding + half);

                        drawList.AddRect(outMin, outMax, ImGuiHelper.Color(biomeColor),
                            outRounding, ImDrawFlags.RoundCornersAll, bBorderTh);
                    }

                    drawList.AddRectFilled(bgPos, bgPos + bgSize, ImGuiHelper.Color(backgroundColor), rounding);
                    drawList.AddText(drawPosition, ImGuiHelper.Color(fontColor), mapName);

                    float labelCenterX = drawPosition.X + textSize.X * 0.5f;
                    float nextRowTopY = drawPosition.Y + textSize.Y + (4f * uiScale);
                    float rowGap = 4f * uiScale;

                    CategorizeContents(nd.RawContents, MapTags, MapPlain, out var flags, out var contents);

                    if (Settings.ShowMapBadges)
                        DrawSquares(drawList, flags, labelCenterX, ref nextRowTopY, rowGap, uiScale);

                    DrawSquares(drawList, contents, labelCenterX, ref nextRowTopY, rowGap, uiScale);
                }

                // "You are here" marker dot (context only — not the route start).
                if (wantRoute && markerFound)
                {
                    drawList.ChannelsSetCurrent(ChannelDots);
                    float r = MathF.Max(3f, uiScale * 4f);
                    drawList.AddCircleFilled(routeAnchor, r, ImGuiHelper.Color(new Vector4(1f, 0.3f, 0.3f, 1f)));
                    drawList.AddCircle(routeAnchor, r, DotOutlineColor, 0, MathF.Max(1f, r * 0.35f));
                }

                drawList.ChannelsMerge();
            }
        }

        // Rebuild the per-node static-data cache (map id / biome / state / content names). This is
        // the expensive pass (pointer chains + wide-string reads per node), so it runs only on an
        // interval — not every frame. Positions are NOT cached here; they're read live each frame.
        private void RefreshNodeCache(UiElement atlasUi, int atlasCount)
        {
            nodeCache.Clear();
            markerCandidates.Clear();
            for (int i = 0; i < atlasCount; i++)
            {
                var addr = atlasUi.GetChildAddress(i);
                if (addr == IntPtr.Zero)
                    continue;

                var nodeUi = Read<UiElement>(addr);

                // The "you are here" marker is a child sharing the node-list container's fp
                // (0x502EF3) rather than the map-node fp (0x542EF3); it has no grid/MapId, so we
                // can't key it by grid — we locate it by screen position (it renders on the
                // player's current map node). Collect visible candidates; the live frame picks one.
                uint f = nodeUi.Flags;
                if ((f & ~IsVisibleMask) == (AtlasCurrentNodeFp & ~IsVisibleMask) && (f & IsVisibleMask) != 0)
                {
                    markerCandidates.Add(addr);
                    // The marker is not a map node (garbage grid / no MapId). Keep it out of
                    // nodeCache so it never pollutes routeCenters and gets picked as the start.
                    continue;
                }

                var node = AtlasNode.Load(addr);
                var internalId = NormalizeName(node.MapName);
                var mapInfo = GetMapInfo(internalId);
                nodeCache.Add(new NodeData
                {
                    Address = addr,
                    InternalId = internalId,
                    MapName = mapInfo != null && !string.IsNullOrWhiteSpace(mapInfo.Name)
                        ? NormalizeName(mapInfo.Name) : internalId,
                    MapInfo = mapInfo,
                    BiomeId = node.BiomeId,
                    State = node.State,
                    RawContents = GetContentName(nodeUi),
                    GridPosition = node.GridPosition,
                });
            }
            cachedAtlasCount = atlasCount;
        }

        #region Routing helpers

        // Adjacency graph of the revealed atlas, built from the panel's connection (edge) list at
        // panel+0x5A8. Keyed by grid position; undirected. Source/Target are grid coords that match
        // each node's grid (node+0x320).
        private static Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> BuildConnectionGraph(IntPtr atlasPanelAddr)
        {
            var graph = new Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>>();
            if (atlasPanelAddr == IntPtr.Zero)
                return graph;

            var vec = Read<StdVector>(atlasPanelAddr + AtlasConnectionsVectorOffset);
            if (!TryVectorCount<AtlasConnectionEdge>(vec, out int count))
                return graph;

            for (int i = 0; i < count; i++)
            {
                var e = ReadVectorAt<AtlasConnectionEdge>(vec, i);
                if (e.Source.Equals(e.Target))
                    continue;
                AddEdge(graph, e.Source, e.Target);
                AddEdge(graph, e.Target, e.Source);
            }

            return graph;
        }

        private static void AddEdge(
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph,
            StdTuple2D<int> a,
            StdTuple2D<int> b)
        {
            if (!graph.TryGetValue(a, out var list))
            {
                list = new List<StdTuple2D<int>>(4);
                graph[a] = list;
            }

            if (!list.Contains(b))
                list.Add(b);
        }

        // Multi-source BFS over the undirected graph seeded from every accessible (frontier) node,
        // skipping blocked (failed) nodes. Returns a cameFrom tree pointing back toward the nearest
        // source — reconstruct any target's path with PathFromAccessible. Sources have no cameFrom.
        private static Dictionary<StdTuple2D<int>, StdTuple2D<int>> MultiSourceBfs(
            Dictionary<StdTuple2D<int>, List<StdTuple2D<int>>> graph,
            HashSet<StdTuple2D<int>> sources,
            HashSet<StdTuple2D<int>> blocked)
        {
            var cameFrom = new Dictionary<StdTuple2D<int>, StdTuple2D<int>>();
            var visited = new HashSet<StdTuple2D<int>>();
            var queue = new Queue<StdTuple2D<int>>();

            foreach (var s in sources)
                if (graph.ContainsKey(s) && !blocked.Contains(s) && visited.Add(s))
                    queue.Enqueue(s);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var nb in graph[cur])
                {
                    if (blocked.Contains(nb) || !visited.Add(nb))
                        continue;
                    cameFrom[nb] = cur;
                    queue.Enqueue(nb);
                }
            }

            return cameFrom;
        }

        // Reconstruct the shortest path accessible-source → target from a MultiSourceBfs tree.
        // Returns source..target inclusive (target alone if it's already accessible), or null if
        // the target wasn't reached.
        private static List<StdTuple2D<int>> PathFromAccessible(
            StdTuple2D<int> target,
            Dictionary<StdTuple2D<int>, StdTuple2D<int>> cameFrom,
            HashSet<StdTuple2D<int>> sources)
        {
            if (sources.Contains(target))
                return new List<StdTuple2D<int>> { target };
            if (!cameFrom.ContainsKey(target))
                return null;

            var path = new List<StdTuple2D<int>> { target };
            var cur = target;
            while (cameFrom.TryGetValue(cur, out var prev))
            {
                cur = prev;
                path.Add(cur);
            }
            path.Reverse();
            return path;
        }

        // Draw a node path (accessible source → target): connect consecutive on-screen node centers,
        // plus a dot at each. Off-screen path nodes break the line into visible segments.
        private static void DrawNodePath(
            ImDrawListPtr drawList,
            List<StdTuple2D<int>> path,
            Dictionary<StdTuple2D<int>, Vector2> centers,
            uint color,
            float thickness)
        {
            drawList.ChannelsSetCurrent(ChannelLines);
            Vector2? prev = null;
            foreach (var g in path)
            {
                if (!centers.TryGetValue(g, out var c))
                {
                    prev = null;
                    continue;
                }

                if (prev.HasValue)
                    drawList.AddLine(prev.Value, c, color, thickness);
                prev = c;
            }

            drawList.ChannelsSetCurrent(ChannelDots);
            foreach (var g in path)
                if (centers.TryGetValue(g, out var c))
                    drawList.AddCircleFilled(c, thickness * 0.9f, color);
        }

#endregion

        private void LoadBiomeMap()
        {
            var path = Path.Join(DllDirectory, "json", "biome.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, BiomeInfo>>(json);

            Biomes.Clear();

            if (contents is null)
                return;

            foreach (var content in contents)
            {
                if (byte.TryParse(content.Key, out var id))
                    Biomes[id] = content.Value;
            }

            ApplyBiomeOverrides();
        }

        private void LoadContentMap()
        {
            var path = Path.Join(DllDirectory, "json", "content.json");
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, ContentInfo>>(json);

            MapTags.Clear();
            MapPlain.Clear();

            if (contents is null)
                return;

            foreach (var content in contents)
            {
                if (content.Key.All(char.IsLetter))
                    MapTags[content.Key] = content.Value;
                else
                    MapPlain[content.Key] = content.Value;
            }

            ApplyContentOverrides();
        }

        private void LoadMaps()
        {
            var path = Path.Join(DllDirectory, "json", "maps.json");
            MapInfos.Clear();
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var contents = JsonConvert.DeserializeObject<Dictionary<string, MapInfo>>(json);
            if (contents is null)
                return;

            foreach (var kv in contents)
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                    MapInfos[kv.Key] = kv.Value;
        }

        // Look up the maps.json entry for an internal MapId (null when unmapped).
        private static MapInfo GetMapInfo(string internalId) =>
            !string.IsNullOrWhiteSpace(internalId) && MapInfos.TryGetValue(internalId, out var info) ? info : null;

        // Resolve an internal MapId to its in-game display name; falls back to the internal id
        // (also normalized) when the map isn't in maps.json (new/unmapped nodes still show).
        private static string ResolveDisplayName(string internalId)
        {
            if (string.IsNullOrWhiteSpace(internalId))
                return internalId;

            var info = GetMapInfo(internalId);
            return info != null && !string.IsNullOrWhiteSpace(info.Name)
                ? NormalizeName(info.Name)
                : internalId;
        }

        private static Vector2 ComputeScalePair(in UiElementBaseOffset uiBase)
        {
            var io = ImGui.GetIO();
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = io.DisplaySize.X / MathF.Max(1f, baseW);
            float sy = io.DisplaySize.Y / MathF.Max(1f, baseH);

            Vector2 pair;
            switch (uiBase.ScaleIndex)
            {
                case 0:
                    pair = new Vector2(sx, sx);
                    break;
                case 1:
                    pair = new Vector2(sy, sy);
                    break;
                case 2:
                    float s = MathF.Min(sx, sy);
                    pair = new Vector2(s, s);
                    break;
                default:
                    pair = new Vector2(sx, sy);
                    break;
            }

            return pair * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeUniformScale(in UiElementBaseOffset uiBase, float dispW, float dispH)
        {
            float baseW = (float)UiElementBaseFuncs.BaseResolution.X;
            float baseH = (float)UiElementBaseFuncs.BaseResolution.Y;
            float sx = dispW / MathF.Max(1f, baseW);
            float sy = dispH / MathF.Max(1f, baseH);

            float s = uiBase.ScaleIndex switch
            {
                0 => sx,
                1 => sy,
                2 => MathF.Min(sx, sy),
                _ => MathF.Min(sx, sy),
            };

            return s * MathF.Max(0.0001f, uiBase.LocalScaleMultiplier);
        }

        private static float ComputeRelativeUiScale(in UiElementBaseOffset uiBase, float refW, float refH)
        {
            var io = ImGui.GetIO();
            float cur = ComputeUniformScale(in uiBase, io.DisplaySize.X, io.DisplaySize.Y);
            float pref = ComputeUniformScale(in uiBase, refW, refH);

            return pref > 0 ? cur / pref : 1f;
        }

        private static Vector2 GetFinalTopLeft(in UiElementBaseOffset leaf)
        {
            Vector2 pos = Vector2.Zero;
            UiElementBaseOffset cur = leaf;
            int guard = 0;
            IntPtr last = IntPtr.Zero;
            while (true)
            {
                var scale = ComputeScalePair(in cur);
                pos += new Vector2(cur.RelativePosition.X * scale.X,
                    cur.RelativePosition.Y * scale.Y);

                if (UiElementBaseFuncs.ShouldModifyPos(cur.Flags))
                {
                    pos += new Vector2(cur.PositionModifier.X * scale.X,
                        cur.PositionModifier.Y * scale.Y);
                }

                if (cur.ParentPtr == IntPtr.Zero || cur.ParentPtr == last || ++guard > 64)
                    break;

                last = cur.Self;
                cur = ReadBaseCached(cur.ParentPtr);
            }

            return pos;
        }

        // Per-frame-cached UiElementBase read — atlas nodes share their ancestor chain, so the
        // parent walk in GetFinalTopLeft reads each ancestor at most once per frame.
        private static UiElementBaseOffset ReadBaseCached(IntPtr addr)
        {
            if (frameBaseCache.TryGetValue(addr, out var cached))
                return cached;
            var v = Read<UiElementBaseOffset>(addr);
            frameBaseCache[addr] = v;
            return v;
        }

        private static void DrawSquares(ImDrawListPtr drawList, List<ContentInfo> infos, float centerX,
            ref float nextRowTopY, float rowGap, float uiScale)
        {
            if (infos.Count == 0)
                return;

            const float fixedHeightBase = 18f;
            const float paddingBase = 6f;
            float fixedHeight = fixedHeightBase * uiScale;
            float padding = paddingBase * uiScale;

            var widths = new List<float>(infos.Count);
            float totalW = 0f;

            foreach (var info in infos)
            {
                var abbrev = string.IsNullOrWhiteSpace(info.Abbrev) ? info.Label[..1] : info.Abbrev;
                var textSize = ImGui.CalcTextSize(abbrev);
                float w = MathF.Max(fixedHeight, textSize.X + padding);
                widths.Add(w);
                totalW += w;
            }

            var basePos = new Vector2(centerX - totalW * 0.5f, nextRowTopY);

            for (int i = 0; i < infos.Count; i++)
            {
                var info = infos[i];
                string abbrev;
                if (string.IsNullOrWhiteSpace(info.Abbrev))
                    abbrev = !string.IsNullOrEmpty(info.Label) ? info.Label.Substring(0, 1) : "?";
                else
                    abbrev = info.Abbrev;
                var boxSize = new Vector2(widths[i], fixedHeight);
                var squareMin = basePos;
                var squareMax = squareMin + boxSize;

                drawList.AddRectFilled(squareMin, squareMax, ImGuiHelper.Color(info.BgColor));

                var textSize = ImGui.CalcTextSize(abbrev);
                var textPos = squareMin + (boxSize - textSize) * 0.5f;
                drawList.AddText(textPos, ImGuiHelper.Color(info.FtColor), abbrev);

                basePos.X += boxSize.X;
            }

            nextRowTopY += fixedHeight + rowGap;
        }

        private readonly struct FontScaleScope : IDisposable
        {
            private readonly ImFontPtr _font;
            private readonly float _prevScale;
            public FontScaleScope(float scale)
            {
                _font = ImGui.GetFont();
                _prevScale = _font.Scale;
                _font.Scale = _prevScale * scale;
                ImGui.PushFont(_font);
            }
            public void Dispose()
            {
                ImGui.PopFont();
                _font.Scale = _prevScale;
            }
        }

        private static Vector2 GetLineRectangleIntersection(Vector2 lineStart, Vector2 rectCenter, Vector2 rectMin, Vector2 rectMax)
        {
            if (lineStart.X >= rectMin.X && lineStart.X <= rectMax.X &&
                lineStart.Y >= rectMin.Y && lineStart.Y <= rectMax.Y)
                return lineStart;

            Vector2 direction = rectCenter - lineStart;

            float dirX = direction.X == 0 ? 1e-6f : direction.X;
            float dirY = direction.Y == 0 ? 1e-6f : direction.Y;

            float tMinX = (rectMin.X - lineStart.X) / dirX;
            float tMaxX = (rectMax.X - lineStart.X) / dirX;
            float tMinY = (rectMin.Y - lineStart.Y) / dirY;
            float tMaxY = (rectMax.Y - lineStart.Y) / dirY;

            if (tMinX > tMaxX)
                (tMaxX, tMinX) = (tMinX, tMaxX);

            if (tMinY > tMaxY)
                (tMaxY, tMinY) = (tMinY, tMaxY);

            float tEnter = Math.Max(tMinX, tMinY);
            float tExit = Math.Min(tMaxX, tMaxY);

            if (tEnter > tExit || tEnter < 0)
                return rectCenter;

            float t = Math.Min(tEnter, 1.0f);

            return lineStart + direction * t;
        }

        private static Vector2 OffsetPointOutsideRect(Vector2 borderPoint, Vector2 rectCenter, float distance)
        {
            var dir = borderPoint - rectCenter;
            float lenSq = dir.X * dir.X + dir.Y * dir.Y;
            if (lenSq< 1e-6f)
                return borderPoint;
            dir /= MathF.Sqrt(lenSq);

            return borderPoint + dir* distance;
        }

        private void MoveMapGroup(int index, int direction)
        {
            if (index < 0 || index >= Settings.MapGroups.Count)
                return;

            int to = index + direction;
            if (to < 0 || to >= Settings.MapGroups.Count)
                return;

            var item = Settings.MapGroups[index];
            Settings.MapGroups.RemoveAt(index);
            Settings.MapGroups.Insert(to, item);
        }

        private void DeleteMapGroup(int index)
        {
            if (index < 0 || index >= Settings.MapGroups.Count)
                return;

            Settings.MapGroups.RemoveAt(index);
        }

        private static void ColorSwatch(string label, ref Vector4 color)
        {
            if (ImGui.ColorButton(label, color))
                ImGui.OpenPopup(label);

            if (ImGui.BeginPopup(label))
            {
                ImGui.ColorPicker4(label, ref color);
                ImGui.EndPopup();
            }
        }

        private static bool TriangleButton(string id, float buttonSize, Vector4 color, bool isUp)
        {
            var pressed = ImGui.Button(id, new Vector2(buttonSize, buttonSize));
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetItemRectMin();
            var triSize = buttonSize * 0.5f;
            var center = new Vector2(pos.X + buttonSize * 0.5f, pos.Y + buttonSize * 0.5f);

            Vector2 p1, p2, p3;
            if (isUp)
            {
                p1 = new Vector2(center.X, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X - triSize * 0.5f, center.Y + triSize * 0.5f);
                p3 = new Vector2(center.X + triSize * 0.5f, center.Y + triSize * 0.5f);
            }
            else
            {
                p1 = new Vector2(center.X - triSize * 0.5f, center.Y - triSize * 0.5f);
                p2 = new Vector2(center.X + triSize * 0.5f, center.Y - triSize * 0.5f);
                p3 = new Vector2(center.X, center.Y + triSize * 0.5f);
            }

            drawList.AddTriangleFilled(p1, p2, p3, ImGuiHelper.Color(color));

            return pressed;
        }

        private static void EnsureProcessHandle()
        {
            int pid = (int)Core.Process.Pid;
            if (Handle == IntPtr.Zero)
            {
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;

                return;
            }

            if (_handlePid != pid)
            {
                CloseAndResetHandle();
                Handle = ProcessMemoryUtilities.Managed.NativeWrapper.OpenProcess(
                               ProcessMemoryUtilities.Native.ProcessAccessFlags.Read, pid);
                _handlePid = pid;
            }
        }

        private static void CloseAndResetHandle()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
            _handlePid = 0;
        }

        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero)
                return default;

            EnsureProcessHandle();
            T result = default;
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemory(Handle, address, ref result);

            return result;
        }

        private static bool TryVectorCount<T>(in StdVector vector, out int count)
            where T : unmanaged
        {
            count = 0;
            if (vector.First == IntPtr.Zero || vector.Last == IntPtr.Zero)
                return false;

            long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (bytes <= 0)
                return false;

            int stride = Marshal.SizeOf<T>();
            if (stride <= 0 || (bytes % stride) != 0)
                return false;

            long c = bytes / stride;
            if (c <= 0 || c > 10000)
                return false;

            count = (int)c;

            return true;
        }

        private static T ReadVectorAt<T>(in StdVector vector, int index)
            where T : unmanaged
        {
            int stride = Marshal.SizeOf<T>();
            var addr = IntPtr.Add(vector.First, index * stride);

            return Read<T>(addr);
        }

        public static string ReadWideString(nint address, int stringLength)
        {
            if (address == IntPtr.Zero || stringLength <= 0)
                return string.Empty;

            EnsureProcessHandle();
            byte[] result = new byte[stringLength * 2];
            ProcessMemoryUtilities.Managed.NativeWrapper.ReadProcessMemoryArray(Handle, address, result);

            return Encoding.Unicode.GetString(result).Split('\0')[0];
        }

        static bool IsPrintableUnicode(string str)
        {
            if (string.IsNullOrEmpty(str))
                return false;

            if (str.All(ch => ch == '?' || char.IsWhiteSpace(ch)))
                return false;

            foreach (var rune in str.EnumerateRunes())
            {
                if (rune.Value == 0xFFFD)
                    return false;

                var cat = Rune.GetUnicodeCategory(rune);
                switch (cat)
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.OtherNotAssigned:
                        return false;
                }
            }

            return true;
        }

        private static string NormalizeName(string s) =>
            string.IsNullOrWhiteSpace(s)
                ? s
                : CollapseWhitespace(s.Replace('\u00A0', ' ').Trim());

        private static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (var ch in s)
            {
                bool isSpace = char.IsWhiteSpace(ch);
                if (isSpace)
                {
                    if (!prevSpace) sb.Append(' ');
                }
                else
                {
                    sb.Append(ch);
                }
                prevSpace = isSpace;
            }

            return sb.ToString();
        }

        // Flags-fingerprint walk from GameUi to the atlas node-lists container — replaces the
        // fragile fixed-index walk (GetChild(25, 0, 6)) that broke between PoE2 patches. Path
        // verified live 2026-06 for build 0.5.x:
        //
        //   GameUi (fp 0x502EF0, ~123 children)
        //     └─ child fp 0x562EF5 — atlas panel container (~5 sibling matches, only the
        //                            IsVisible one continues the chain when atlas is open)
        //          └─ child fp 0x502EF1 — sub-container / gate (IsVisible toggles with panel)
        //               └─ child fp 0x502EF3 — node lists (direct parent of ~470 atlas nodes,
        //                                       each with fp 0x542EF3)
        //
        // Multiple siblings can match at each step (esp. step 0 has 5 candidates), so we
        // backtrack: try every matching child and recurse, keep the branch whose continuation
        // reaches a leaf with ≥ 1 atlas-node-fp child. Prefers visible candidates first so the
        // gate naturally picks the open instance.
        private const uint AtlasPanelFp = 0x00562EF5;
        private const uint AtlasGateFp = 0x00502EF1;
        private const uint AtlasNodeListFp = 0x00502EF3;
        private const uint IsVisibleMask = 0x800u;

        private IntPtr GetAtlasPanelAddress()
        {
            var gameUi = Core.States.InGameStateObject.GameUi.Address;
            if (gameUi == IntPtr.Zero)
                return IntPtr.Zero;

            uint[] fps = { AtlasPanelFp, AtlasGateFp, AtlasNodeListFp };
            int gateStep = 1; // step where IsVisible MUST be set (the toggling sub-container)
            return WalkFp(gameUi, fps, gateStep, 0);
        }

        private UiElement GetAtlasPanelUi()
        {
            var addr = GetAtlasPanelAddress();
            return addr == IntPtr.Zero ? default : Read<UiElement>(addr);
        }

        private static IntPtr WalkFp(IntPtr parentAddr, uint[] fps, int gateStep, int step)
        {
            if (step == fps.Length)
                return parentAddr;

            var parent = Read<UiElement>(parentAddr);
            int n = parent.Length;
            if (n <= 0 || n > 5000)
                return IntPtr.Zero;

            uint target = fps[step] & ~IsVisibleMask;

            // Visible matches first, then non-visible — backtracking finds whichever branch
            // has a full continuation.
            for (int pass = 0; pass < 2; pass++)
            {
                bool wantVisible = pass == 0;
                for (int i = 0; i < n; i++)
                {
                    var childAddr = parent.GetChildAddress(i);
                    if (childAddr == IntPtr.Zero)
                        continue;
                    var child = Read<UiElement>(childAddr);
                    uint f = child.Flags;
                    if ((f & ~IsVisibleMask) != target)
                        continue;
                    bool visible = (f & IsVisibleMask) != 0;
                    if (visible != wantVisible)
                        continue;
                    if (step == gateStep && !visible)
                        continue;

                    var deeper = WalkFp(childAddr, fps, gateStep, step + 1);
                    if (deeper != IntPtr.Zero)
                        return deeper;
                }
            }
            return IntPtr.Zero;
        }

        private static bool InventoryPanel()
        {
            var uiElement = Read<UiElement>(Core.States.InGameStateObject.GameUi.Address);
            var invetoryPanel = uiElement.GetChild(33);

            return invetoryPanel.IsVisible;
        }

        private static void CategorizeContents(IEnumerable<string> raws,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap,
            out List<ContentInfo> flags,
            out List<ContentInfo> contents)
        {
            flags = [];
            contents = [];
            foreach (var raw in raws)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var info = MatchContent(NormalizeName(raw), tagMap, plainMap);
                if (info is null || !info.Show)
                    continue;

                if (info.IsFlag) flags.Add(info);
                else contents.Add(info);
            }
        }

        public static List<string> GetContentName(UiElement nodeUi)
        {
            const int ContentOffset = 0x290;
            var result = new List<string>();

            nodeUi = nodeUi.GetChild(0);
            nodeUi = nodeUi.GetChild(0);

            var len = nodeUi.Length;
            if (len <= 0)
                return result;

            for (int i = 0; i < len; i++)
            {
                var childAddr = nodeUi.GetChildAddress(i);
                var contentPtr = Read<IntPtr>(childAddr + ContentOffset);
                if (contentPtr == IntPtr.Zero)
                    continue;

                var contentName = ReadWideString(contentPtr, 64);
                if (string.IsNullOrWhiteSpace(contentName))
                    continue;

                result.Add(contentName);
            }

            return result;
        }

        private static ContentInfo MatchContent(string contentName,
            Dictionary<string, ContentInfo> tagMap,
            Dictionary<string, ContentInfo> plainMap)
        {
            if (string.IsNullOrWhiteSpace(contentName))
                return null;

            var normalized = contentName.Replace("\u00A0", " ").Trim();

            int lb = normalized.IndexOf('[');
            int rb = lb >= 0 ? normalized.IndexOf(']', lb + 1) : -1;
            if (lb >= 0 && rb > lb + 1)
            {
                var inside = normalized.Substring(lb + 1, rb - lb - 1);
                var pipe = inside.IndexOf('|');
                var tag = (pipe >= 0 ? inside[..pipe] : inside).Trim();

                if (tagMap.TryGetValue(tag, out var tagInfo))
                    return tagInfo;

                if (plainMap.TryGetValue(tag, out var tagAsPlain))
                    return tagAsPlain;
            }

            foreach (var map in plainMap)
            {
                if (normalized.Contains(map.Key, StringComparison.OrdinalIgnoreCase))
                    return map.Value;
            }

            foreach (var tag in tagMap)
            {
                if (normalized.Contains(tag.Key, StringComparison.OrdinalIgnoreCase))
                    return tag.Value;
            }

            return null;
        }

        private void ApplyBiomeOverrides()
        {
            foreach (var entry in Settings.BiomeOverrides)
            {
                if (Biomes.TryGetValue(entry.Key, out var info))
                {
                    var ov = entry.Value;
                    if (ov.BorderColor.HasValue)
                        info.BorderColor = [ov.BorderColor.Value.X, ov.BorderColor.Value.Y, ov.BorderColor.Value.Z, ov.BorderColor.Value.W];

                    if (ov.Show.HasValue)
                        info.Show = ov.Show.Value;
                }
            }
        }

        private void ApplyContentOverrides()
        {
            foreach (var entry in Settings.ContentOverrides)
            {
                if (MapTags.TryGetValue(entry.Key, out var info) ||
                    MapPlain.TryGetValue(entry.Key, out info))
                {
                    var ov = entry.Value;
                    if (ov.BackgroundColor.HasValue)
                        info.BackgroundColor = [ov.BackgroundColor.Value.X, ov.BackgroundColor.Value.Y, ov.BackgroundColor.Value.Z, ov.BackgroundColor.Value.W];

                    if (ov.FontColor.HasValue)
                        info.FontColor = [ov.FontColor.Value.X, ov.FontColor.Value.Y, ov.FontColor.Value.Z, ov.FontColor.Value.W];

                    if (ov.Show.HasValue)
                        info.Show = ov.Show.Value;

                    if (!string.IsNullOrEmpty(ov.Abbrev))
                        info.Abbrev = ov.Abbrev;
                }
            }
        }

        private static bool ColorsEqual(Vector4 a, Vector4 b, float eps = 0.001f)
        {
            return Math.Abs(a.X - b.X) < eps &&
                   Math.Abs(a.Y - b.Y) < eps &&
                   Math.Abs(a.Z - b.Z) < eps &&
                   Math.Abs(a.W - b.W) < eps;
        }

        private static RectangleF CalculateBounds(float range)
        {
            var baseBoundsTowers = new RectangleF(0, 0, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);

            return RectangleF.Inflate(baseBoundsTowers, baseBoundsTowers.Width * (range - 1.0f), baseBoundsTowers.Height * (range - 1.0f));
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
