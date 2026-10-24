using System.Collections.Generic;
using BepInEx.Configuration;

namespace ValheimNavigator
{
    // the switches of the mod, bound once. the panel's settings tab changes the same entries, so the cfg always agrees with it
    internal static class Settings
    {
        internal static ConfigEntry<bool> AutoMark;
        internal static ConfigEntry<bool> NearestPin;
        internal static ConfigEntry<bool> IncludeUnexplored;
        internal static ConfigEntry<bool> PortalsOn;
        internal static ConfigEntry<bool> SearchPins;
        internal static ConfigEntry<bool> ShareLocations, ShareNavigation;
        internal static ConfigEntry<float> SyncSeconds, SyncTimeout;
        internal static ConfigEntry<int> SyncKilobytes;
        internal static ConfigEntry<bool> Beds, Vehicles, Tombstones, Tamed, Chests, Contents, CursorTerrain;
        internal static ConfigEntry<float> RouteRefresh, OffRouteMeters, TeleportMeters, RouteRebuildSeconds, SearchWeight;
        internal static ConfigEntry<bool> MiniRoute;
        internal static readonly Dictionary<string, ConfigEntry<bool>> OreTypes = new Dictionary<string, ConfigEntry<bool>>();
        internal static ConfigEntry<bool> PortalLines;
        internal static ConfigEntry<float> PortalSeconds;
        internal static ConfigEntry<int> MaxPins;
        internal static ConfigEntry<int> ClusterPixels;
        internal static ConfigEntry<bool> ShowUnexplored;
        internal static ConfigEntry<float> SliceMilliseconds;
        internal static ConfigEntry<float> ArrivalSeconds;
        internal static ConfigEntry<float> RefreshSeconds;
        internal static ConfigEntry<float> ScrollSpeed;
        internal static ConfigEntry<float> Opacity;
        internal static ConfigEntry<bool> ClickSounds;
        internal static ConfigEntry<bool> GameLook;
        internal static ConfigEntry<float> PanelScale;
        internal static ConfigEntry<float> DotOpacity;
        internal static ConfigEntry<float> DotSize;
        internal static ConfigEntry<float> KnownSize;
        internal static ConfigEntry<bool> TypeIcons;
        internal static ConfigEntry<bool> Labels;
        internal static ConfigEntry<int> SpawnFilter;
        internal static ConfigEntry<float> FogTint;
        internal static ConfigEntry<int> TerrainView;
        internal static ConfigEntry<bool> Detail;
        internal static ConfigEntry<float> DetailMilliseconds;
        internal static ConfigEntry<bool> Contours;
        internal static ConfigEntry<float> ContourMeters;
        internal static ConfigEntry<bool> ContourAuto;
        internal static ConfigEntry<bool> Mist;
        internal static ConfigEntry<bool> Shallows;
        internal static ConfigEntry<bool> Steepness;
        internal static ConfigEntry<bool> AshlandsHeat;
        internal static ConfigEntry<float> BoatDangerDepth;
        internal static ConfigEntry<float> BoatWarningDepth;
        internal static ConfigEntry<float> CellMeters;
        internal static ConfigEntry<float> SampleMeters;
        internal static ConfigEntry<float> RouteBudget;
        internal static ConfigEntry<float> MaxSlope;
        internal static ConfigEntry<float> SlopePenalty;
        internal static ConfigEntry<float> WalkSpeed;
        internal static ConfigEntry<float> SwimSpeed, SwimRisk, WadeDepth, SailSpeed, ShipDraft, ShipMeters, SailBias, DangerPenalty;
        internal static ConfigEntry<bool> UseShip, RoutePortals, CarryingOre, UseWind, MoreBoats, LoadedTerrain, SpeedyPaths, RememberedWorld, DetailedStructures;
        internal static ConfigEntry<int> Danger, BoatSource;
        internal static ConfigEntry<string> ShipSpeedList, BuildShip;
        internal static ConfigEntry<float> BuildSeconds, ExtraBoatSeconds, LandmarkWeight, CorridorWeight, MaxSwim, JumpSlope, JumpMeters;
        internal static ConfigEntry<float> MapMilliseconds;
        internal static ConfigEntry<int> MaxNodes;
        internal static ConfigEntry<bool> SeaHazards;
        internal static ConfigEntry<bool> Ore;
        internal static ConfigEntry<bool> SpawnSafe;
        internal static ConfigEntry<float> KnownRefresh;
        internal static ConfigEntry<float> HazardClearance;
        internal static readonly Dictionary<string, ConfigEntry<bool>> AutoMarkCategory = new Dictionary<string, ConfigEntry<bool>>();
        // one switch per kind of place, made when the index is ready. the key is the kind's own key (category and kind)
        internal static readonly Dictionary<string, ConfigEntry<bool>> LayerKind = new Dictionary<string, ConfigEntry<bool>>();

        private static ConfigFile _config;

        internal static void Bind(ConfigFile config)
        {
            _config = config;
            ShareLocations = config.Bind("Multiplayer", "ShareLocations", true, "When hosting, share the world's location list with requesting Navigator players. Does not share personal pins, exploration or completion marks.");
            ShareNavigation = config.Bind("Multiplayer", "ShareNavigation", true, "When hosting, also share portal endpoints and ship positions with requesting Navigator players. Does not share inventories.");
            SyncSeconds = Number(config, "Multiplayer", "RefreshSeconds", 60f, 10f, 600f, "How often a supported host is asked for changed locations and navigation objects.");
            SyncTimeout = Number(config, "Multiplayer", "TimeoutSeconds", 15f, 5f, 120f, "How long a guest waits without data before showing limited mode. Local routes remain usable.");
            SyncKilobytes = config.Bind("Multiplayer", "KilobytesPerFrame", 48, new ConfigDescription("Host transfer budget shared by all requesting players, in kilobytes per frame.", new AcceptableValueRange<int>(24, 240)));
            AutoMark = config.Bind("Done", "AutoMark", true,
                "Mark a location done when you arrive at it. Only kinds you searched are marked, and only the categories switched on under Auto mark.");
            NearestPin = config.Bind("Pins", "NearestPin", true,
                "Drop a temporary pin on the nearest result and center the map on it.");
            IncludeUnexplored = config.Bind("Search", "IncludeUnexplored", true,
                "The nearest search also finds places you have not found yet. Off: only places in explored parts of the map.");
            PortalsOn = config.Bind("Portals", "Enabled", true,
                "Search your portals by their name. Off by default, nothing is read while it is off.");
            SearchPins = config.Bind("Pins", "SearchPins", false, "Include saved personal map pins in Find and the World overview. The game's existing pin display stays available.");
            PortalLines = config.Bind("Portals", "Lines", false,
                "Draw your portals and a line between portals that share a name on the large map. Needs Enabled.");
            PortalSeconds = config.Bind("Portals", "RefreshSeconds", 5f,
                new ConfigDescription("How often the portal list is read while the panel is open.", new AcceptableValueRange<float>(2f, 120f)));
            MaxPins = config.Bind("Layers", "MaxPins", 25,
                new ConfigDescription("The most single markers drawn per category (and per known object family) on the large map, the ones nearest to you are kept. Groups of places are drawn as numbered circles and are not counted. 0 switches the limit off.",
                    new AcceptableValueRange<int>(0, 5000)));
            ClusterPixels = config.Bind("Layers", "ClusterPixels", 30,
                new ConfigDescription("Places closer together than this many screen pixels are drawn as one numbered circle. Zoom in to see them apart.", new AcceptableValueRange<int>(12, 120)));
            ShowUnexplored = config.Bind("Overview", "ShowUnexplored", false,
                "Show the terrain of unexplored parts on the large map and in the overview. It does not change your explored map. It spoils exploring.");
            SliceMilliseconds = config.Bind("Performance", "SliceMilliseconds", 1f,
                new ConfigDescription("The most time a frame spends building the world index when a world loads.", new AcceptableValueRange<float>(0.2f, 5f)));
            MapMilliseconds = Number(config, "Performance", "MapMilliseconds", 4f, 1f, 10f, "The most time a frame spends building map overlays and reading known objects while the large map is open.");
            ArrivalSeconds = config.Bind("Performance", "ArrivalSeconds", 2f,
                new ConfigDescription("How often the arrival check for auto mark runs.", new AcceptableValueRange<float>(0.5f, 10f)));
            RefreshSeconds = config.Bind("Performance", "RefreshSeconds", 5f,
                new ConfigDescription("How often the open panel refreshes its numbers and the overview dots.", new AcceptableValueRange<float>(1f, 30f)));
            ScrollSpeed = Number(config, "Panel", "ScrollSpeed", 120f, 20f, 400f, "List scroll distance per mouse wheel step.");
            Opacity = Number(config, "Panel", "Opacity", 0.78f, 0.3f, 0.95f, "Panel cover opacity. The map remains visible through it.");
            DotOpacity = Number(config, "Layers", "DotOpacity", 0.88f, 0.4f, 1f, "Opacity of the muted map markers.");
            DotSize = Number(config, "Layers", "DotSize", 5f, 3f, 14f, "Radius of single map markers in screen pixels.");
            KnownSize = Number(config, "Layers", "ObjectSize", 7f, 3f, 14f, "Radius of known object markers (ore, sea rocks, beds, ships, tombstones, tamed animals, containers) in screen pixels.");
            TypeIcons = config.Bind("Layers", "TypeIcons", true, "Use small type symbols instead of plain dots. Groups remain numbered.");
            Labels = config.Bind("Layers", "Labels", false, "Show names next to individual location markers when zoomed in.");
            SpawnFilter = config.Bind("Layers", "SpawnFilter", 0, new ConfigDescription("0 all locations, 1 generated locations, 2 not yet generated. This is separate from explored terrain.", new AcceptableValueRange<int>(0, 2)));
            FogTint = Number(config, "Overview", "FogTint", 0.30f, 0.05f, 0.65f, "Transparent shade over personally unexplored terrain when ShowUnexplored is on.");
            TerrainView = config.Bind("Terrain", "View", 0, new ConfigDescription("0 native terrain, 1 biome colors, 2 shaded height above sea level, 3 detailed terrain with hillshade, forests and shores.", new AcceptableValueRange<int>(0, 3)));
            Detail = config.Bind("Terrain", "Detail", true, "Draw the large map's current view at screen resolution with smooth heights, so contours are continuous lines and views stay sharp when zoomed in.");
            DetailMilliseconds = Number(config, "Terrain", "DetailMilliseconds", 3f, 1f, 10f, "Time per frame spent drawing the detailed view after the map stops moving.");
            Contours = config.Bind("Terrain", "Contours", true, "Draw elevation contours on land. Zoom in for detail.");
            ContourMeters = Number(config, "Terrain", "ContourMeters", 20f, 5f, 100f, "Vertical interval between contour lines. Every fifth line is stronger.");
            ContourAuto = config.Bind("Terrain", "ContourAuto", true, "Use 10 m and then 5 m contours when zoomed in, never coarser than ContourMeters.");
            Mist = config.Bind("Terrain", "Mist", false, "Show natural Mistlands mist, reduced around loaded clearing sources at ground level. Distant sources and mist particles are unavailable. Map resolution limits small clearings.");
            Shallows = config.Bind("Sailing", "Shallows", false, "Mark shallow water red below DangerDepth and yellow below WarningDepth. Uses base terrain, not waves or constructed obstacles.");
            Steepness = config.Bind("Terrain", "Steepness", false, "Shade steep terrain light or dark violet using the map height texture.");
            AshlandsHeat = config.Bind("Sailing", "AshlandsHeat", true, "Tint boiling Ashlands water magenta and lava orange, inside the world's edge only.");
            BoatDangerDepth = Number(config, "Sailing", "DangerDepth", 1.5f, 0.2f, 10f, "Depth in meters below which shallow water is red.");
            BoatWarningDepth = Number(config, "Sailing", "WarningDepth", 3f, 0.5f, 15f, "Depth in meters below which shallow water is yellow.");
            CellMeters = Number(config, "Navigator", "CellMeters", 24f, 12f, 64f, "Route search grid spacing in meters. Smaller grids take longer.");
            SampleMeters = Number(config, "Navigator", "SampleMeters", 6f, 2f, 8f, "Maximum distance between terrain checks along a route edge.");
            RouteBudget = Number(config, "Navigator", "SliceMilliseconds", 2f, 0.5f, 5f, "Route calculation time budget per frame. One terrain check may exceed it.");
            MaxSlope = Number(config, "Navigator", "MaxSlopeDegrees", 38f, 20f, 60f, "Maximum sampled ground slope in either route mode. The player slides on ground steeper than 38 degrees.");
            SlopePenalty = Number(config, "Navigator", "SlopePenalty", 5f, 0f, 20f, "Comfortable mode penalty for steep ground.");
            WalkSpeed = Number(config, "Navigator", "WalkMetersPerSecond", 4f, 0.5f, 12f, "Assumed ground travel speed for the approximate time.");
            SwimSpeed = Number(config, "Navigator", "SwimMetersPerSecond", 2f, 0.5f, 6f, "Swimming speed. The game swims only where water is deeper than WadeDepth.");
            SwimRisk = Number(config, "Navigator", "SwimPenalty", 5f, 1f, 20f, "Swimming time is multiplied by this, because swimming drains stamina. Long swims are warned about.");
            WadeDepth = Number(config, "Navigator", "WadeDepth", 2f, 0.5f, 4f, "Water shallower than this is waded at walking pace with a small penalty.");
            SailSpeed = Number(config, "Navigator", "SailMetersPerSecond", 7f, 1f, 20f, "Assumed sailing speed of a ship. A raft is capped at 3. An estimate, wind is ignored.");
            ShipDraft = Number(config, "Navigator", "ShipDraft", 1f, 0.3f, 4f, "Water depth a ship needs. Shallower water is avoided when sailing.");
            UseShip = config.Bind("Navigator", "UseShip", true, "Routes may sail. BoatSource chooses a boat built at the shore or your nearest known ship.");
            BoatSource = config.Bind("Navigator", "BoatSource", 1, new ConfigDescription("1 a boat is built at the best shore (you can build one anywhere), 2 the route walks to your nearest known ship within ShipMeters and sails from there. Aboard a ship, that ship is used.", new AcceptableValueRange<int>(1, 2)));
            MoreBoats = config.Bind("Navigator", "MoreBoats", false, "Off: one sailing leg a trip. On: more legs, each with a boat built at a shore. The second boat costs only its build time, every later one ExtraBoatSeconds more.");
            BuildSeconds = Number(config, "Navigator", "BuildBoatSeconds", 60f, 0f, 600f, "Time counted for building a boat at the shore, including fetching materials.");
            ExtraBoatSeconds = Number(config, "Navigator", "ExtraBoatSeconds", 120f, 0f, 1800f, "Extra time counted for every boat after the second when MoreBoats is on.");
            LoadedTerrain = config.Bind("Navigator", "LoadedTerrain", false, "Work in progress. In loaded areas the route reads the real ground (your terraforming included) and avoids rocks and trees, and searches again when a zone on the route loads. Off uses only the generated world.");
            DetailedStructures = config.Bind("Navigator", "DetailedStructures", false, "Work in progress. Detailed routes also read your buildings: floors, walls, stairs and doors, with the fine way through a base. It may fail inside buildings.");
            RememberedWorld = config.Bind("Navigator", "RememberedWorld", false, "Detailed routes also use remembered buildings, rocks and terraforming outside the loaded area. Off: outside the loaded area routes work like Fast. Experimental, slower.");
            SpeedyPaths = config.Bind("Navigator", "SpeedyPaths", true, "With the SpeedyPaths mod installed, Detailed routes count its faster walking on paths and built floors in the loaded area.");
            BuildShip = config.Bind("Navigator", "BuildShip", "Karve", "The kind of boat you build at the shore (Raft, Karve, VikingShip, or a name from ShipSpeeds). A known ship of yours nearby sets the kind instead.");
            ShipSpeedList = config.Bind("Navigator", "ShipSpeeds", "", "Speeds of ships from other mods as Name=paddle/low/high in m/s, separated by commas, for example WarShip=3/5/10. Low is full sail in a light wind, high in a full wind. Unlisted ships sail like a longship.");
            UseWind = config.Bind("Navigator", "UseWind", true, "Routes plan sailing with the predicted wind, from the game's own wind clock.");
            MaxSwim = Number(config, "Navigator", "MaxSwimMeters", 35f, 0f, 1000f, "The longest unbroken swim a route may hold. 0 is no limit.");
            JumpSlope = Number(config, "Navigator", "JumpSlopeDegrees", 50f, 20f, 70f, "A bank steeper than MaxSlopeDegrees but at most this steep is jump-run up or slid down when it is short (JumpMeters).");
            JumpMeters = Number(config, "Navigator", "JumpMeters", 12f, 0f, 30f, "The longest unbroken steep stretch a route may jump-run, at three times the walking time. 0 turns jumping off.");
            RoutePortals = config.Bind("Navigator", "UsePortals", true, "Routes may use your known portal pairs (portals with the same name).");
            CarryingOre = config.Bind("Navigator", "CarryingOre", false, "You carry ore or other items that cannot go through portals, so routes do not use portals.");
            ShipMeters = Number(config, "Navigator", "ShipMeters", 3000f, 200f, 10000f, "A ship farther than this from you is not used for the route.");
            SailBias = Number(config, "Navigator", "SailBias", 1f, 0.6f, 1.6f, "Foot or sail. Below 1 prefers sailing, above 1 prefers walking.");
            Danger = config.Bind("Navigator", "BiomeDanger", 1, new ConfigDescription("0 off, 1 avoid biomes harder than the destination's, 2 never walk through them, 3 avoid biomes past your progress (bosses beaten). Sailing past them is fine.", new AcceptableValueRange<int>(0, 3)));
            DangerPenalty = Number(config, "Navigator", "BiomeDangerPenalty", 4f, 1f, 20f, "Walking time in a harder biome is multiplied by this when BiomeDanger is 1.");
            MaxNodes = config.Bind("Navigator", "MaxNodes", 160000, new ConfigDescription("Search node limit. An incomplete search is reported as a partial route, never as arrival.", new AcceptableValueRange<int>(5000, 500000)));
            SeaHazards = config.Bind("Sailing", "SeaHazards", false, "Show known offshore rocks and ice from generated areas. Undiscovered objects outside the client's world data are unavailable.");
            Ore = config.Bind("Known world", "Ore", false, "Show known ore deposits from generated areas. This does not predict deposits in ungenerated areas.");
            SpawnSafe = config.Bind("Known world", "SpawnSafe", false, "Show where enemies cannot spawn, from the game's PlayerBase areas. Loaded areas are read live with any mod changes, others use the radius last seen on a loaded object of the same kind.");
            KnownRefresh = Number(config, "Known world", "RefreshSeconds", 60f, 5f, 300f, "Minimum interval between sliced scans of the world's objects while the large map is open and known-object layers are on. Each scan spends up to SliceMilliseconds a frame.");
            HazardClearance = Number(config, "Navigator", "HazardClearance", 12f, 3f, 60f, "Extra clearance around known sea rocks and ice. Unknown offshore spires still require attention.");
            ScrollSpeed.SettingChanged += (s, e) => Panel.ApplyStyle();
            Beds = config.Bind("Known world", "Beds", false, "Show known beds and their owners.");
            Vehicles = config.Bind("Known world", "Vehicles", false, "Show known ships and carts. Leave off if another mod already shows them.");
            Tombstones = config.Bind("Known world", "Tombstones", false, "Show known player tombstones and their owners.");
            Tamed = config.Bind("Known world", "Tamed", false, "Show known tamed creatures and their names. Distant positions can be stale.");
            Chests = config.Bind("Known world", "Chests", false, "Show known player-built containers. This does not open or modify inventories.");
            Contents = config.Bind("Known world", "Contents", false, "Read available container contents for Find search and inspection. Only data already available to this client is used.");
            foreach (string ore in new[] { "Copper", "Tin", "Silver", "Obsidian", "Iron", "Flametal" })
                OreTypes[ore] = config.Bind("Ore types", ore, true, "Include known " + ore.ToLowerInvariant() + " deposits when the ore layer is on.");
            CursorTerrain = config.Bind("Terrain", "CursorReadout", true, "Show map cursor coordinates, ground height above sea level and water depth.");
            RouteRefresh = Number(config, "Navigator", "RefreshSeconds", 1f, 0.5f, 60f, "How often the route follows you. Each update only trims the line behind you, a new search runs only off the route or after a teleport. No world generation is requested.");
            MiniRoute = config.Bind("Navigator", "MinimapRoute", true, "Draw the route on the small map in the corner as well as on the large map.");
            OffRouteMeters = Number(config, "Navigator", "OffRouteMeters", 25f, 10f, 300f, "Distance from the route line that makes the route recalculate from where you are.");
            TeleportMeters = Number(config, "Navigator", "TeleportMeters", 100f, 30f, 1000f, "A jump this far between two route updates counts as a teleport and recalculates the route.");
            RouteRebuildSeconds = Number(config, "Navigator", "RebuildSeconds", 180f, 30f, 1800f, "The kept search behind the route is rebuilt from scratch after this many seconds, in case the world changed.");
            SearchWeight = Number(config, "Navigator", "SearchWeight", 1.5f, 1f, 3f, "How strongly the route search heads straight for the goal before the landmark tables are ready. 1 is the exact best route, 1.5 measured at most 4.6 percent longer.");
            LandmarkWeight = Number(config, "Navigator", "LandmarkWeight", 1.2f, 1f, 3f, "The search weight once the landmark tables are built (a few seconds after a world loads). 1.2 measured at most 1.9 percent longer with half the work of 1.5.");
            CorridorWeight = Number(config, "Navigator", "CorridorWeight", 1f, 1f, 3f, "The search weight inside the corridor of a long route. 1 finds the best route the corridor holds.");
            Opacity.SettingChanged += (s, e) => Panel.ApplyStyle();
            ClickSounds = config.Bind("Panel", "ClickSounds", true, "Play the game's menu click sound on ValheimNavigator buttons.");
            ClickSounds.SettingChanged += (s, e) => Panel.ApplyStyle();
            GameLook = config.Bind("Panel", "GameLook", true, "Use the game's own inventory window and button images for the panel. Off gives the flat dark look.");
            GameLook.SettingChanged += (s, e) => Panel.ApplyStyle();
            PanelScale = Number(config, "Panel", "Scale", 0f, 0f, 3f, "Panel size. 0 fits the panel to the height of the map window, other values scale the panel by that factor.");
            PanelScale.SettingChanged += (s, e) => Panel.ApplyStyle();

            foreach (var category in Categories.Order)
            {
                string title = Categories.Titles[category];
                AutoMarkCategory[category] = config.Bind("Auto mark", title, true, "Auto mark searched " + title + " when you arrive.");
            }
        }

        // background map work gets the larger budget while the large map is open and someone is waiting for it
        internal static float MapBudget => Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large ? MapMilliseconds.Value : SliceMilliseconds.Value;

        private static ConfigEntry<float> Number(ConfigFile config, string section, string key, float value, float min, float max, string description)
        {
            return config.Bind(section, key, value, new ConfigDescription(description, new AcceptableValueRange<float>(min, max)));
        }

        // the characters a cfg key cannot hold are left out
        private static string KeyName(KindData kind)
        {
            var text = Categories.Titles[kind.Category] + " - " + kind.Kind;
            foreach (var bad in new[] { '=', '\n', '\t', '"', '\'', '[', ']' }) text = text.Replace(bad, ' ');
            return text;
        }

        // makes the layer switch of every kind in one go, with a single save at the end. old cfg values are read back
        internal static void BindKinds(IEnumerable<KindData> kinds)
        {
            if (_config == null) return;
            bool save = _config.SaveOnConfigSet;
            _config.SaveOnConfigSet = false;
            foreach (var kind in kinds)
                if (!LayerKind.ContainsKey(kind.Key))
                    LayerKind[kind.Key] = _config.Bind("Layer kinds", KeyName(kind), false, "Show every " + kind.Kind + " on the large map.");
            _config.SaveOnConfigSet = save;
            if (save) _config.Save();
        }

        internal static bool AutoMarkFor(string category)
        {
            return AutoMarkCategory.TryGetValue(category, out var entry) && entry.Value;
        }

        internal static bool KindOn(KindData kind)
        {
            return LayerKind.TryGetValue(kind.Key, out var entry) && entry.Value;
        }

        // switches several kinds at once with a single save
        internal static void SetKinds(IEnumerable<KindData> kinds, bool on)
        {
            bool save = _config != null && _config.SaveOnConfigSet;
            if (_config != null) _config.SaveOnConfigSet = false;
            foreach (var kind in kinds)
                if (LayerKind.TryGetValue(kind.Key, out var entry) && entry.Value != on) entry.Value = on;
            if (_config != null)
            {
                _config.SaveOnConfigSet = save;
                if (save) _config.Save();
            }
        }
    }
}
