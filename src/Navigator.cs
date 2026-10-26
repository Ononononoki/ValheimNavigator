using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace ValheimNavigator
{
    internal static class Navigator
    {
        internal static NavSearch Search;
        private static NavSearch _display;
        internal static NavSearch Visible => _display;
        internal static string Name = "Pick a place in Find, or click the World map.";
        internal static bool Comfortable;
        internal static Vector3? Destination;
        internal static string Problem;
        private static WorldGenerator _world;
        private static bool _reported;
        private static bool _waiting;
        private static bool _running, _automatic;
        private static float _next;
        private static int _hazards;
        // the backward tree from the destination is kept while the destination, the mode and the known hazards stay the same
        private static NavTree _tree;
        private static float _treeBuilt;
        private static Vector3? _last;
        internal static int Reroutes, Trims, ZoneReroutes;
        // the loaded zones along the shown route when it was found, a zone loading later starts a new search at most every ZoneRerouteSeconds
        private static System.Collections.Generic.HashSet<long> _zonesSeen = new System.Collections.Generic.HashSet<long>();
        private static float _zoneNext;
        private const float ZoneRerouteSeconds = 5f;
        // a zone's ground is ready before its objects: the game, and GapCloser's ObjectLoadBudget more so, make them over several frames. a zone
        // counts as loaded once it has been loaded this long, so its rocks and buildings are there when the route reads it
        private const float ZoneSettleSeconds = 3f;
        private static readonly System.Collections.Generic.Dictionary<long, float> _zoneLoadedAt = new System.Collections.Generic.Dictionary<long, float>();

        // a Fast route is searched on a background thread, so the game's frame pays nothing for it. the game's thread treats the search as
        // running until the thread has stopped, and stops it before it changes or replaces the search (the kept tree is shared)
        private static bool _threaded;
        private static System.Threading.Thread _worker;
        private static NavSearch _workerSearch;
        private static volatile bool _workerDone, _stopWorker;
        private static Exception _workerError;
        // short slices, so a stop waits at most about this long
        private const float WorkerSliceMilliseconds = 4f;

        private static bool Working => _worker != null && !_workerDone;

        // finished for the game's thread: the search says so and no thread still works on it
        internal static bool Done(NavSearch search) { return search != null && search.Finished && !(Working && _workerSearch == search); }

        // done and its result taken over (shown, logged), for the sandbox
        internal static bool Idle(NavSearch search) { return Done(search) && _worker == null; }

        private static void StartWorker(NavSearch search)
        {
            _workerSearch = search; _workerError = null; _workerDone = false; _stopWorker = false;
            _worker = new System.Threading.Thread(() =>
            {
                try { while (!search.Finished && !_stopWorker) search.Step(WorkerSliceMilliseconds); }
                catch (Exception ex) { _workerError = ex; }
                finally { _workerDone = true; }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator route search" };
            _worker.Start();
        }

        private static void StopWorker()
        {
            if (_worker == null) return;
            _stopWorker = true;
            _worker.Join();
            _worker = null; _workerSearch = null; _stopWorker = false;
            if (_workerError != null) { Plugin.Log.LogWarning("background route search failed: " + _workerError); _workerError = null; }
        }

        private static bool Settled(float x, float z, long key)
        {
            if (!ZoneSystem.instance.IsZoneLoaded(new Vector3(x, 0f, z))) { _zoneLoadedAt.Remove(key); return false; }
            if (!_zoneLoadedAt.TryGetValue(key, out float since)) { _zoneLoadedAt[key] = since = Time.unscaledTime; }
            return Time.unscaledTime - since >= ZoneSettleSeconds;
        }

        private static long ZoneKey(float x, float z) { var zone = ZoneSystem.GetZone(new Vector3(x, 0f, z)); return ((long)zone.x << 32) | (ushort)zone.y; }

        private static System.Collections.Generic.HashSet<long> LoadedZones(NavSearch route)
        {
            var zones = new System.Collections.Generic.HashSet<long>();
            if (ZoneSystem.instance == null) return zones;
            foreach (var p in route.Points)
            {
                long key = ZoneKey(p.X, p.Z);
                if (Settled(p.X, p.Z, key)) zones.Add(key);
            }
            return zones;
        }

        private static bool ZoneLoadedOnRoute(NavSearch route)
        {
            if (ZoneSystem.instance == null) return false;
            long last = long.MinValue;
            foreach (var p in route.Points)
            {
                long key = ZoneKey(p.X, p.Z);
                if (key == last) continue;
                last = key;
                if (Settled(p.X, p.Z, key) && !_zonesSeen.Contains(key)) return true;
            }
            return false;
        }

        internal static void SetTarget(Vector3 point, string name)
        {
            Clear(); Destination = point; Name = name; _skipCorridor = false;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route target " + name + ", " + point.x.ToString("F0") + ", " + point.z.ToString("F0"));
        }

        internal static void Start()
        {
            if (!Destination.HasValue) { Problem = "Pick a destination first."; return; }
            StopWorker();
            if (Search != null && !Search.Finished) Search.Cancel();
            Search = null; Problem = null;
            _running = true; _automatic = false; BoatUsed = false;
            _next = Time.unscaledTime + Settings.RouteRefresh.Value;
            _tree = null;
            // sea hazards and ships from a recent scan are reused, so a new route does not wait for another object scan
            if (!KnownWorld.RouteFresh) KnownWorld.Request(true);
            _waiting = KnownWorld.Busy;
            if (!_waiting) Calculate(true);
        }

        // fresh builds a new tree from the destination, otherwise the kept tree is read or grown from the player's place
        private static void Calculate(bool fresh)
        {
            if (!Destination.HasValue) { Problem = "Pick a destination first."; return; }
            var player = Session.PlayerPosition();
            _world = WorldGenerator.instance;
            if (!player.HasValue || _world == null) { Problem = "Load a world and character first."; return; }
            StopWorker();
            if (Search != null && !Search.Finished) Search.Cancel();
            Problem = null; _reported = false;
            var target = Destination.Value;
            if (!ExplorerArea.Visible(target.x, target.z)) { Problem = "Explorer mode: the destination is farther than ExplorerEdgeMeters from explored land and farther than ExplorerSenseMeters from you."; return; }
            _last = player.Value;
            var aboard = Aboard();
            _wasAboard = aboard != null;
            // aboard a ship, or in water too deep to have swum out to (your boat is right here): the route starts sailing
            bool atSea = aboard == null && AtSea(player.Value);
            byte startMode = aboard != null || atSea ? (byte)1 : (byte)0;
            string key = RouteKey();
            // a player who left the corridor of a long route gets a new route rather than a search the corridor cannot hold
            if (!fresh && _tree != null && _tree.Corridor != null && !Abstraction.Inside(_tree.Corridor, player.Value.x, player.Value.z)) fresh = true;
            if (!fresh && _tree != null && _treeKey == key && _hazards == KnownWorld.HazardVersion && Time.unscaledTime - _treeBuilt < Settings.RouteRebuildSeconds.Value)
            {
                Search = new NavSearch(player.Value.x, player.Value.z, _tree, startMode);
                Reroutes++;
                Layers.Dirty();
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route rerouted on the kept tree, " + _tree.Expanded + " nodes known");
                return;
            }
            _hazards = KnownWorld.HazardVersion;
            var options = new NavOptions
            {
                Cell = Settings.CellMeters.Value, Sample = Settings.SampleMeters.Value,
                MaxSlope = Mathf.Tan(Settings.MaxSlope.Value * Mathf.Deg2Rad), Comfortable = Comfortable,
                SlopePenalty = Settings.SlopePenalty.Value, WalkSpeed = Settings.WalkSpeed.Value,
                SwimSpeed = Settings.SwimSpeed.Value, SwimRisk = Settings.SwimRisk.Value, WadeDepth = Settings.WadeDepth.Value, MaxSwim = Settings.MaxSwim.Value,
                JumpSlope = Mathf.Tan(Mathf.Max(Settings.MaxSlope.Value, Settings.JumpSlope.Value) * Mathf.Deg2Rad), JumpMeters = Settings.JumpMeters.Value,
                SailSpeed = Settings.SailSpeed.Value, ShipDraft = Settings.ShipDraft.Value, SailBias = Settings.SailBias.Value,
                DangerPenalty = Settings.DangerPenalty.Value, DangerBlock = Settings.Danger.Value == 2,
                MaxNodes = Settings.MaxNodes.Value, WaterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f,
                Weight = Settings.SearchWeight.Value
            };
            // harder means a higher biome rank than the destination's (an ocean destination sets no limit) or past your progress
            int rank = Rank(_world.GetBiome(target.x, target.z));
            options.DangerRank = Settings.Danger.Value == 3 ? ProgressRank() : Settings.Danger.Value == 0 || rank == 0 ? 99 : rank;
            ShipName = null; ShipKnown = true; _windUsed = false; NoShipNear = false;
            bool found = PickShip(player.Value, aboard, out var ship, out string prefab);
            if (Settings.UseShip.Value)
            {
                // a ship you are on, or your nearest known ship within ShipMeters, can be boarded. Build mode may also build a boat at the best
                // shore, so a raft lying close by is used and a far one is not walked to. the boat of this trip that you left stays boardable,
                // and no other is built unless MoreBoats. BoatSource 2 without a ship in reach builds one and says so
                if (found) { options.HasShip = true; options.ShipX = ship.x; options.ShipZ = ship.z; }
                else if (atSea) { options.HasShip = true; options.ShipX = player.Value.x; options.ShipZ = player.Value.z; prefab = Settings.BuildShip.Value; }
                options.BuildBoat = aboard == null && !atSea && (Settings.BoatSource.Value == 1 || !found) && (!BoatUsed || Settings.MoreBoats.Value);
                NoShipNear = Settings.BoatSource.Value == 2 && !found && !atSea;
                if (!found && !atSea) prefab = Settings.BuildShip.Value;
                options.MoreBoats = Settings.MoreBoats.Value;
                options.BuildSeconds = Settings.BuildSeconds.Value; options.ExtraBoatSeconds = Settings.ExtraBoatSeconds.Value;
                options.HeatProof = prefab.IndexOf("Ashlands", StringComparison.OrdinalIgnoreCase) >= 0;
                ShipKnown = ShipSpeeds.Of(prefab, out options.ShipPaddle, out options.ShipLow, out options.ShipHigh);
                // without wind a known ship sails at about half wind, the middle of its measured range
                options.SailSpeed = options.ShipLow + (options.ShipHigh - options.ShipLow) * 0.47f;
                ShipName = prefab;
                // both route modes plan sailing with the predicted mean wind over the expected sailing time (test 8: Shortest ignored it)
                if (Settings.UseWind.Value)
                {
                    var wind = Wind.Mean(Mathf.Min(1800f, NavSearch.Distance(player.Value.x, player.Value.z, target.x, target.z) / options.SailSpeed));
                    options.Wind = true; options.WindX = wind.X; options.WindZ = wind.Z; options.WindIntensity = wind.Intensity;
                    _windX = wind.X; _windZ = wind.Z; _windUsed = true;
                }
                options.Moder = options.Wind && ModerActive();
                _moderUsed = options.Moder;
            }
            // portal pairs: exactly two known portals with the same name are linked by the game
            if (Settings.RoutePortals.Value && !Settings.CarryingOre.Value)
            {
                Portals.Refresh(0f);
                foreach (var group in Portals.List.GroupBy(p => p.Tag ?? ""))
                {
                    var pair = group.ToList();
                    if (pair.Count != 2 || NavSearch.Distance(pair[0].X, pair[0].Z, pair[1].X, pair[1].Z) < 100f) continue;
                    options.Portals.Add(new[] { pair[0].X, pair[0].Z, pair[1].X, pair[1].Z });
                }
            }
            // a destination on lava or a cliff top moves to the nearest walkable spot, so the search has a goal it can reach
            float tx = target.x, tz = target.z;
            if (Abstraction.Snap(ref tx, ref tz) && (Math.Abs(tx - target.x) > 0.5f || Math.Abs(tz - target.z) > 0.5f))
            {
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route destination moved " + NavSearch.Distance(tx, tz, target.x, target.z).ToString("F0") + " m to walkable ground");
                target.x = tx; target.z = tz;
            }
            // the weight: exact inside a long route's corridor, 1.2 with the landmark tables, SearchWeight before they are built. the sailing
            // tables bound only ships no faster than the one they were built for
            bool corridorRoute = !_skipCorridor && Abstraction.Ready && NavSearch.Distance(player.Value.x, player.Value.z, target.x, target.z) >= CorridorMeters;
            if (Abstraction.LandmarksReady)
            {
                bool sailTables = Math.Max(options.SailSpeed, options.ShipHigh) / options.SailBias <= Abstraction.LandmarkSailSpeed + 0.001f;
                // the tables come from the 12 m map. once real ground shows dry land where the map has water (a bridge, a dock, a raised path) they
                // could price that shortcut too high, so they step aside for the rest of this route and the real data decides
                options.Landmarks = (fx, fz, tx2, tz2, sail) => sail && !sailTables || DryWater ? 0f : Abstraction.LandmarkBound(fx, fz, tx2, tz2, sail);
            }
            options.Weight = corridorRoute ? Settings.CorridorWeight.Value : Abstraction.LandmarksReady ? Settings.LandmarkWeight.Value : Settings.SearchWeight.Value;
            float water = options.WaterLevel;
            var generator = _world;
            bool loadedTerrain = Settings.LoadedTerrain.Value && ZoneSystem.instance != null;
            bool explorer = Settings.Explorer;
            float explorerEdge = Settings.ExplorerEdgeMeters.Value;
            // Fast routes read only the generated world (the game itself runs it on its heightmap thread), a copy of the sea rocks and the
            // Explorer arrays, so they are searched on a background thread. Detailed routes read physics and loaded objects, they stay here
            var obstacles = KnownWorld.ObstacleCopy();
            var nearTest = explorer ? ExplorerArea.NearTest(explorerEdge) : null;
            _threaded = !loadedTerrain && (!explorer || nearTest != null);
            float px = player.Value.x, pz = player.Value.z, gx = target.x, gz = target.z;
            // Detailed routes with SpeedyPaths: paths and floors read in the loaded area walk faster, the bound allows that only there
            float fastestPath = 1f;
            bool paths = loadedTerrain && Settings.SpeedyPaths.Value && SpeedyPaths.Read(out fastestPath) && fastestPath > 1f;
            // Detailed reads only the loaded area. remembered places (test 7: tree crowns made remembered forests solid) only when asked for
            bool remembered = loadedTerrain && Settings.RememberedWorld.Value;
            // the start, the destination and every portal end stay open within a grid cell's reach: the search joins them from the nearest node
            var open = new System.Collections.Generic.List<Vector2> { new Vector2(px, pz), new Vector2(gx, gz) };
            foreach (var p in options.Portals) { open.Add(new Vector2(p[0], p[1])); open.Add(new Vector2(p[2], p[3])); }
            bool Open(float x, float z)
            {
                foreach (var o in open) if ((o.x - x) * (o.x - x) + (o.y - z) * (o.y - z) <= KeepOpen * KeepOpen) return true;
                return false;
            }
            if (paths)
            {
                // paths are read in the loaded area around you and remembered around the destination, the bound credits only those disks
                options.PathSpeed = fastestPath; options.PathX = px; options.PathZ = pz; options.PathRadius = PathMeters;
                if (remembered) { options.PathX2 = gx; options.PathZ2 = gz; options.PathRadius2 = PathMeters; }
            }
            // Detailed: a step that passes a wall, trunk or rock is not walked, by the loaded colliders or the remembered boxes. the spots where
            // you stand and where you go stay open, the base planner takes over there
            if (loadedTerrain)
                options.Crosses = (a, b) =>
                {
                    if (Open(a.X, a.Z) || Open(b.X, b.Z)) return false;
                    float fa = Mathf.Max(a.Height, water), fb = Mathf.Max(b.Height, water);
                    bool crossed = (a.Source == 1 || b.Source == 1) && LoadedCrosses(a.X, fa, a.Z, b.X, fb, b.Z)
                        || remembered && (a.Source != 1 || b.Source != 1) && Remembered.Crosses(a.X, a.Z, fa, b.X, b.Z, fb);
                    if (crossed) WallSteps++;
                    return crossed;
                };
            Search = new NavSearch(player.Value.x, player.Value.z, target.x, target.z, (x, z) =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                Color mask;
                var biome = generator.GetBiome(x, z);
                float height = generator.GetBiomeHeight(biome, x, z, out mask, false, true);
                bool ashlands = biome == Heightmap.Biome.AshLands;
                bool blocked = false, onTop = false;
                float speed = 0f;
                bool real = loadedTerrain && Real(x, z, water, paths, ref height, out blocked, out onTop, out speed);
                byte source = real ? (byte)1 : remembered ? Remember(x, z, water, paths, px, pz, gx, gz, ref height, ref blocked, ref onTop, ref speed) : (byte)0;
                var surface = NavSearch.Classify(height, water, ashlands, mask.a, WorldGenerator.GetAshlandsOceanGradient(x, z));
                if (onTop) surface = NavSurface.Land;
                // the spots where you stand and where you go stay open, so a route can leave or reach a place inside a building
                if (blocked && !Open(x, z)) surface = NavSurface.Obstacle;
                if ((surface == NavSurface.Water || surface == NavSurface.AshWater) && KnownWorld.BlockedIn(obstacles, x, z)) surface = NavSurface.Obstacle;
                // Explorer: the route walks on explored land and up to ExplorerEdgeMeters past it, and sails on any open sea. the spots where
                // you stand and where you go stay open
                if (explorer && (nearTest != null ? ExplorerArea.BlocksWith(surface, Open(x, z), x, z, nearTest) : ExplorerArea.Blocks(surface, Open(x, z), x, z, explorerEdge))) surface = NavSurface.Obstacle;
                if (real) LoadedTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                return new NavPoint { Height = height, Surface = surface, Rank = (byte)Rank(biome), Speed = speed, Source = source };
            }, options, startMode);
            LoadedSamples = SolidSamples = BlockedSamples = PathSamples = RememberedSamples = WallSteps = 0; LoadedTicks = 0; DryWater = false;
            // long routes search inside the abstraction's corridor first, the whole map only when that fails
            string corridor = "off";
            if (corridorRoute)
            {
                Search.Tree.Corridor = Abstraction.Corridor(options, player.Value.x, player.Value.z, target.x, target.z, startMode, out int regions, out bool noWay);
                // the loaded area around you is always inside the corridor, its real ground and objects decide there, not the 12 m map
                if (Search.Tree.Corridor != null && loadedTerrain) Abstraction.Allow(Search.Tree.Corridor, player.Value.x, player.Value.z, LoadedMeters);
                corridor = Search.Tree.Corridor != null ? regions + " regions" : "none found";
                // the abstraction knows every walkable, swimmable and sailable region, with none joining you and the destination a search of the
                // whole map would only run long and fail, so the answer comes at once
                if (noWay)
                {
                    Search.Cancel(); Search = null; _tree = null; _running = false;
                    Problem = rank == 7 && !options.HeatProof
                        ? "No way there is known. The Ashlands need a heat-proof ship (a drakkar) or a portal there."
                        : "No way there is known. Lava, cliffs or boiling water cut it off. Try a nearer place.";
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route not searched, the abstraction holds no way there");
                    Layers.Dirty();
                    return;
                }
            }
            _tree = Search.Tree; _treeKey = key; _treeBuilt = Time.unscaledTime;
            Layers.Dirty();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route started, " + (Comfortable ? "comfortable" : "shortest") + ", grid " + options.Cell + " m, weight " + options.Weight.ToString("0.0")
                + ", ship " + (ShipName == null ? "none" : ShipName + (aboard != null ? " aboard" : options.BuildBoat ? " built at a shore" : " at " + options.ShipX.ToString("F0") + ", " + options.ShipZ.ToString("F0")))
                + (options.MoreBoats ? ", more boats" : "") + (_threaded ? ", background thread" : ", game thread") + ", landmarks " + (options.Landmarks != null ? "on" : Abstraction.LandmarkState) + ", danger above rank " + options.DangerRank + ", portals " + options.Portals.Count + ", wind " + (options.Wind ? options.WindIntensity.ToString("0.00") : "off") + ", corridor " + corridor + ", detail " + (loadedTerrain ? "detailed" : "fast") + (paths ? ", SpeedyPaths up to " + options.PathSpeed.ToString("0.00") : ""));
        }

        // the real ground of a loaded zone, terraforming included, and the static solids on it (rocks, trunks, buildings). the solid top
        // comes from the same downward ray as the game's GetStaticSolidHeight, which skips moving bodies such as ships. above the ground (or the
        // water) a player fills about 0.6 to 1.8 m: when that space is free, the solid is a roof or a bridge and the ground is walked. when it is
        // taken, a top within 1 m is stood on (a floor, a dock, a low rock) and the 38 degree rule judges the step onto it, a higher one blocks
        // (a wall, a trunk, a boulder). with paths on, what is stood on gives the SpeedyPaths speed. false when the zone is not loaded
        private static readonly HarmonyLib.AccessTools.FieldRef<ZoneSystem, int> StaticSolidMask = HarmonyLib.AccessTools.FieldRefAccess<ZoneSystem, int>("m_staticSolidRayMask");
        private static readonly Collider[] Hits = new Collider[8];
        private const float Knee = 0.6f, Head = 1.8f, Body = 0.3f, StepUp = 1f;
        // about the game's loaded area around the player (active area of zones of 64 m), and the reach of paths read from it (its corners)
        private const float LoadedMeters = 192f, KeepOpen = 26f, PathMeters = 260f;
        // per route: samples read from loaded zones, those with a solid above the ground, those blocked, those on a path, and their time
        internal static int LoadedSamples, SolidSamples, BlockedSamples, PathSamples;
        internal static long LoadedTicks;
        // this route met real dry ground where the generated world has water
        internal static bool DryWater;

        // the solids Detailed reads: the game's static solids, without built pieces unless DetailedStructures is on (test 9: routes in a base
        // fell back to the coarse route, buildings are work in progress)
        private static int _pieceLayers = -1;

        private static int SolidMask(ZoneSystem zones)
        {
            if (_pieceLayers == -1) _pieceLayers = LayerMask.GetMask("piece", "piece_nonsolid");
            int mask = StaticSolidMask(zones);
            return Settings.DetailedStructures.Value ? mask : mask & ~_pieceLayers;
        }

        private static bool Real(float x, float z, float water, bool paths, ref float height, out bool blocked, out bool onTop, out float speed)
        {
            blocked = onTop = false; speed = 0f;
            var point = new Vector3(x, 0f, z);
            if (!Heightmap.GetHeight(point, out float ground)) return false;
            LoadedSamples++;
            bool mapWater = height < water - 0.5f;
            height = ground;
            if (mapWater && ground >= water) DryWater = true;
            var zones = ZoneSystem.instance;
            int mask = SolidMask(zones);
            float floor = Mathf.Max(ground, water);
            Collider stand = null;
            bool solid = Physics.Raycast(new Vector3(x, 1000f, z), Vector3.down, out var hit, 2000f, mask) && hit.collider.attachedRigidbody == null;
            if (solid && hit.point.y > floor + 0.3f)
            {
                SolidSamples++;
                int count = Physics.OverlapCapsuleNonAlloc(new Vector3(x, floor + Knee, z), new Vector3(x, floor + Head, z), Body, Hits, mask, QueryTriggerInteraction.Ignore);
                bool taken = false;
                for (int i = 0; i < count && !taken; i++) taken = Hits[i] != null && Hits[i].attachedRigidbody == null;
                if (taken && hit.point.y - floor <= StepUp) { height = hit.point.y; onTop = true; stand = hit.collider; if (mapWater) DryWater = true; }
                else if (taken) { blocked = true; BlockedSamples++; }
            }
            else if (solid) stand = hit.collider;
            if (paths && !blocked && (onTop || ground >= water)) speed = PathSpeed(stand, point);
            return true;
        }

        // outside the loaded area: the remembered real ground of a zone you terraformed or painted (its paths count near either end of the
        // route), then the remembered solids by the same stand-on or block rule. 2 when remembered data was used, else 0
        internal static int RememberedSamples, WallSteps;

        private static byte Remember(float x, float z, float water, bool paths, float px, float pz, float gx, float gz, ref float height, ref bool blocked, ref bool onTop, ref float speed)
        {
            byte source = 0;
            if (Remembered.Ground(x, z, out float ground, out int type))
            {
                bool mapWater = height < water - 0.5f;
                height = ground; source = 2;
                if (mapWater && ground >= water) DryWater = true;
                if (paths && ground >= water && (NavSearch.Distance(x, z, px, pz) <= PathMeters || NavSearch.Distance(x, z, gx, gz) <= PathMeters))
                {
                    float s = SpeedyPaths.Of(type);
                    if (!Mathf.Approximately(s, 1f)) { speed = s; PathSamples++; }
                }
            }
            if (Remembered.Point(x, z, Mathf.Max(height, water), out float top, out bool block))
            {
                source = 2;
                if (block) { blocked = true; BlockedSamples++; }
                else { height = top; onTop = true; }
            }
            if (source == 2) RememberedSamples++;
            return source;
        }

        // the loaded colliders a player would walk into between two standing points: lines at knee and head height above each floor, without
        // the terrain itself, moving bodies or doors (a door opens)
        private static readonly RaycastHit[] LineHits = new RaycastHit[16];
        private static int _wallMask = -1;

        internal static bool LoadedCrosses(float ax, float ay, float az, float bx, float by, float bz)
        {
            if (ZoneSystem.instance == null) return false;
            if (_wallMask == -1) _wallMask = ~(1 << LayerMask.NameToLayer("terrain"));
            int walls = SolidMask(ZoneSystem.instance) & _wallMask;
            for (int k = 0; k < 2; k++)
            {
                float lift = k == 0 ? Knee + 0.2f : Head - 0.2f;
                var from = new Vector3(ax, ay + lift, az);
                var line = new Vector3(bx, by + lift, bz) - from;
                float length = line.magnitude;
                if (length < 0.01f) continue;
                int count = Physics.RaycastNonAlloc(from, line / length, LineHits, length, walls, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    var c = LineHits[i].collider;
                    if (c != null && c.attachedRigidbody == null && c.GetComponentInParent<Door>() == null) return true;
                }
            }
            return false;
        }

        // the base planner asks whether a step is clear, LoadedCrosses answers whether it is blocked (test 7: passed as it was, every step blocked)
        private static bool StepClear(float ax, float ay, float az, float bx, float by, float bz) { return !LoadedCrosses(ax, ay, az, bx, by, bz); }

        // the ship you are on, by steering or by standing on its deck
        private static Ship Aboard()
        {
            var ship = Ship.GetLocalShip();
            if (ship == null && Player.m_localPlayer != null) ship = Player.m_localPlayer.GetStandingOnShip();
            return ship;
        }

        // in water deeper than a swim out from the shore would leave you, 1 m past the wade depth: you came by boat
        private static bool AtSea(Vector3 at)
        {
            if (ZoneSystem.instance == null || Player.m_localPlayer == null || !Player.m_localPlayer.InWater()) return false;
            float ground = Heightmap.GetHeight(at, out float h) ? h : WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(at.x, at.z) : ZoneSystem.instance.m_waterLevel;
            return ZoneSystem.instance.m_waterLevel - ground >= Settings.WadeDepth.Value + 1f;
        }

        // every surface a player can stand on at a point, lowest first: ground, floors, walkways, upper storeys, with the space above free
        private static readonly RaycastHit[] DownHits = new RaycastHit[24];

        private static float[] Floors(float x, float z)
        {
            var zones = ZoneSystem.instance;
            if (zones == null) return null;
            int mask = StaticSolidMask(zones);
            float water = zones.m_waterLevel;
            int count = Physics.RaycastNonAlloc(new Vector3(x, 1000f, z), Vector3.down, DownHits, 2000f, mask, QueryTriggerInteraction.Ignore);
            var floors = new System.Collections.Generic.List<float>(4);
            for (int i = 0; i < count; i++)
            {
                var hit = DownHits[i];
                if (hit.collider == null || hit.collider.attachedRigidbody != null || hit.normal.y < 0.6f || hit.point.y < water - 0.3f) continue;
                float y = hit.point.y;
                int inside = Physics.OverlapCapsuleNonAlloc(new Vector3(x, y + Knee, z), new Vector3(x, y + Head, z), Body, Hits, mask, QueryTriggerInteraction.Ignore);
                bool free = true;
                for (int h = 0; h < inside && free; h++) free = Hits[h] == null || Hits[h].attachedRigidbody != null || Hits[h].GetComponentInParent<Door>() != null;
                if (free) floors.Add(y);
            }
            floors.Sort();
            for (int i = floors.Count - 1; i > 0; i--) if (floors[i] - floors[i - 1] < 0.3f) floors.RemoveAt(i);
            return floors.ToArray();
        }

        // the SpeedyPaths speed of what is stood on: a piece by its material, the ground by its paint. 0 where it gives no change
        private static float PathSpeed(Collider stand, Vector3 point)
        {
            int ground = SpeedyPaths.Untamed;
            var piece = stand != null ? stand.GetComponentInParent<WearNTear>() : null;
            if (piece != null) ground = SpeedyPaths.FromMaterial((int)piece.m_materialType);
            else
            {
                var map = stand != null ? stand.GetComponent<Heightmap>() : null;
                if (map == null) map = Heightmap.FindHeightmap(point);
                if (map != null) ground = SpeedyPaths.FromPaint(map.GetPaintMask(point));
            }
            float speed = SpeedyPaths.Of(ground);
            if (Mathf.Approximately(speed, 1f)) return 0f;
            PathSamples++;
            return speed;
        }

        // the base planner: after a Detailed route is found, a place with buildings near you or near the destination gets the fine way through its
        // floors, stairs and doors on a 2 m grid, which replaces the coarse route there. a queue of up to two jobs, start side first
        private const float LocalMeters = 60f, BuiltRadius = 30f;
        private const int BuiltPieces = 4;
        private static readonly System.Collections.Generic.List<(LocalPlan plan, bool atStart, float started)> _local = new System.Collections.Generic.List<(LocalPlan, bool, float)>();
        private static NavSearch _localFor;
        private static readonly Collider[] Near = new Collider[8];
        private static int _pieceMask = -1;
        internal static int LocalRefines;

        private static bool Built(Vector3 at)
        {
            if (_pieceMask == -1) _pieceMask = LayerMask.GetMask("piece");
            return Physics.OverlapSphereNonAlloc(at, BuiltRadius, Near, _pieceMask, QueryTriggerInteraction.Ignore) >= Math.Min(BuiltPieces, Near.Length);
        }

        private static void StartLocal(NavSearch route)
        {
            _local.Clear(); _localFor = null;
            if (!Settings.LoadedTerrain.Value || !Settings.DetailedStructures.Value || ZoneSystem.instance == null || route.Points.Count < 2 || Aboard() != null || Player.m_localPlayer == null) return;
            var at = Player.m_localPlayer.transform.position;
            float maxSlope = Mathf.Tan(Settings.MaxSlope.Value * Mathf.Deg2Rad);
            var points = route.Points;
            if (Built(at))
            {
                int exit = points.Count - 1;
                for (int i = 1; i < points.Count; i++)
                    if (points[i].Mode != 0 || NavSearch.Distance(points[i].X, points[i].Z, at.x, at.z) > LocalMeters) { exit = points[i].Mode != 0 ? Math.Max(1, i - 1) : i; break; }
                var e = points[exit];
                _local.Add((new LocalPlan(at.x, at.y, at.z, e.X, e.Height, e.Z, LocalMeters + 8f, Settings.WalkSpeed.Value, maxSlope, Floors, StepClear), true, Time.unscaledTime));
            }
            var goal = points[points.Count - 1];
            if (Destination.HasValue && NavSearch.Distance(goal.X, goal.Z, at.x, at.z) > LocalMeters * 2f && ZoneSystem.instance.IsZoneLoaded(new Vector3(goal.X, 0f, goal.Z))
                && Built(new Vector3(goal.X, goal.Height, goal.Z)))
            {
                int entry = 0;
                for (int i = points.Count - 2; i > 0; i--)
                    if (points[i].Mode != 0 || NavSearch.Distance(points[i].X, points[i].Z, goal.X, goal.Z) > LocalMeters) { entry = points[i].Mode != 0 ? Math.Min(points.Count - 2, i + 1) : i; break; }
                float goalY = Destination.Value.y > 0.1f ? Destination.Value.y : goal.Height;
                var s = points[entry];
                _local.Add((new LocalPlan(s.X, s.Height, s.Z, goal.X, goalY, goal.Z, LocalMeters + 8f, Settings.WalkSpeed.Value, maxSlope, Floors, StepClear), false, Time.unscaledTime));
            }
            if (_local.Count > 0) _localFor = route;
        }

        // one slice of the base planner, and the splice when a job is done. the goal side job runs on the route as the start side left it
        private static void LocalTick()
        {
            if (_local.Count == 0) return;
            if (_localFor == null || _localFor != _display || _localFor.Cancelled) { _local.Clear(); _localFor = null; return; }
            var (plan, atStart, started) = _local[0];
            if (!plan.Step(Settings.RouteBudget.Value)) return;
            _local.RemoveAt(0);
            var points = _localFor.Points;
            if (plan.Found && plan.Path.Count >= 2 && points.Count >= 2)
            {
                var finer = new System.Collections.Generic.List<NavPoint>(plan.Path.Count);
                foreach (var (x, y, z) in plan.Path) finer.Add(new NavPoint { X = x, Z = z, Height = y, Surface = NavSurface.Land, Mode = 0, Source = 1 });
                if (atStart)
                {
                    // the fine way ends at the coarse point it joins
                    int exit = NearestPoint(points, plan.Path[plan.Path.Count - 1].x, plan.Path[plan.Path.Count - 1].z, true);
                    finer[finer.Count - 1] = points[exit];
                    _localFor.Replace(0, exit, finer);
                }
                else
                {
                    int entry = NearestPoint(points, plan.Path[0].x, plan.Path[0].z, false);
                    finer[0] = points[entry];
                    _localFor.Replace(entry, points.Count - 1, finer);
                }
                LocalRefines++;
                Layers.Dirty();
            }
            if (Plugin.LogCounters.Value)
                Plugin.Log.LogInfo("route base planner " + (atStart ? "at the start" : "at the destination") + ", " + (plan.Found ? "way found" : "no way, coarse route kept") + ", "
                    + plan.CellsRead + " cells read, " + plan.Expanded + " nodes, " + ((Time.unscaledTime - started) * 1000f).ToString("F0") + " ms over frames");
        }

        private static int NearestPoint(System.Collections.Generic.List<NavPoint> points, float x, float z, bool first)
        {
            int best = first ? 0 : points.Count - 1; float d = float.MaxValue;
            for (int i = 0; i < points.Count; i++) { float dd = NavSearch.Distance(points[i].X, points[i].Z, x, z); if (dd < d) { d = dd; best = i; } }
            return best;
        }

        // the remembered world: loaded with the world from the cache, a snapshot of each terraformed or painted zone near you while it is loaded
        // (one zone a second, again when its terrain changes), saved at most every 30 s after a change
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainComp, uint> TerrainRevision = HarmonyLib.AccessTools.FieldRefAccess<TerrainComp, uint>("m_lastDataRevision");
        private static long _worldUid = long.MinValue;
        private static float _snapshotNext, _saveNext;
        private static bool _rememberedDirty;
        private const float SnapshotSeconds = 1f, SaveSeconds = 30f;
        private const int SnapshotReach = 2;

        private static string CachePath(long uid) { return System.IO.Path.Combine(Application.temporaryCachePath, "ValheimNavigator", "remembered_" + uid + ".bin"); }

        private static void RememberTick()
        {
            if (ZNet.instance == null || !Settings.LoadedTerrain.Value || !Settings.RememberedWorld.Value) return;
            long uid = ZNet.instance.GetWorldUID();
            if (uid != _worldUid)
            {
                _worldUid = uid; _rememberedDirty = false;
                try
                {
                    bool loaded = Remembered.Load(CachePath(uid));
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("remembered world " + (loaded ? "loaded, " + Remembered.BoxCount + " solids and " + Remembered.SnapshotCount + " terraformed zones" : "empty for this world"));
                }
                catch (Exception ex) { Remembered.Clear(); Plugin.Log.LogWarning("remembered world could not be read, starting empty: " + ex.Message); }
            }
            if (KnownWorld.BoxesChanged) { KnownWorld.BoxesChanged = false; _rememberedDirty = true; }
            if (Time.unscaledTime >= _snapshotNext && ZoneSystem.instance != null && Player.m_localPlayer != null)
            {
                _snapshotNext = Time.unscaledTime + SnapshotSeconds;
                var home = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
                for (int dz = -SnapshotReach; dz <= SnapshotReach; dz++)
                    for (int dx = -SnapshotReach; dx <= SnapshotReach; dx++)
                    {
                        var center = ZoneSystem.GetZonePos(new Vector2s(home.x + dx, home.y + dz));
                        long key = Remembered.ZoneOf(center.x, center.z);
                        if (!Settled(center.x, center.z, ZoneKey(center.x, center.z))) continue;
                        var terrain = TerrainComp.FindTerrainCompiler(center);
                        if (terrain == null) continue;
                        uint revision = TerrainRevision(terrain);
                        if (Remembered.HasSnapshot(key, revision)) continue;
                        TakeSnapshot(key, revision);
                        _rememberedDirty = true;
                        return;
                    }
            }
            if (_rememberedDirty && Time.unscaledTime >= _saveNext)
            {
                _saveNext = Time.unscaledTime + SaveSeconds; _rememberedDirty = false;
                try { Remembered.Save(CachePath(_worldUid)); }
                catch (Exception ex) { Plugin.Log.LogWarning("remembered world could not be saved: " + ex.Message); }
            }
        }

        // the zone's real ground and ground paint on the 2 m grid
        private static void TakeSnapshot(long key, uint revision)
        {
            var snapshot = new Remembered.Snapshot { Revision = revision };
            float water = ZoneSystem.instance.m_waterLevel;
            for (int j = 0; j < Remembered.Side; j++)
                for (int i = 0; i < Remembered.Side; i++)
                {
                    Remembered.GridPoint(key, i, j, out float x, out float z);
                    var point = new Vector3(x, 0f, z);
                    int k = j * Remembered.Side + i;
                    snapshot.Height[k] = Heightmap.GetHeight(point, out float h) ? h : WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : water;
                    var map = Heightmap.FindHeightmap(point);
                    snapshot.Ground[k] = map != null ? (byte)SpeedyPaths.FromPaint(map.GetPaintMask(point)) : (byte)0;
                }
            Remembered.Store(key, snapshot);
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("remembered the terrain of zone " + (int)(key >> 32) + ", " + (int)(uint)key + " (" + Remembered.SnapshotCount + " zones)");
        }

        internal static string ShipName;
        // BoatSource 2 found no ship within ShipMeters, so a built boat stands in
        internal static bool NoShipNear;
        // the player sailed and left a boat since Calculate was pressed
        internal static bool BoatUsed;
        // false for a ship from another mod with no speeds in the cfg, longship speeds stand in
        internal static bool ShipKnown = true;
        // routes at least this long use the abstraction's corridor. a corridor that failed is skipped until the destination changes
        private const float CorridorMeters = 1500f;
        private static bool _skipCorridor;
        // the wind the kept tree was built with, checked every WindCheckSeconds while a comfortable route sails
        private static float _windX, _windZ, _windNext;
        private static bool _windUsed, _moderUsed;
        // a weaker mean wind than this swings its direction easily and changes the sailing speed little, its turns start no new route (test 10)
        private const float WindCheckSeconds = 10f, WindTurnDegrees = 45f, WindTurnMinStrength = 0.15f;
        // the nearest known ship when it was beyond ShipMeters, 0 otherwise
        internal static float FarShipMeters;

        // the local player has Moder's power (the game's sailing power attribute)
        internal static bool ModerActive()
        {
            var player = Player.m_localPlayer;
            return player != null && player.GetSEMan() != null && player.GetSEMan().HaveStatusAttribute(StatusEffect.StatusAttribute.SailingPower);
        }
        // a longer unbroken swim is warned about. at 2 m/s and 5 stamina a second a new character has about 40 to 120 m (game code defaults)
        private const float LongSwim = 150f;

        // a compass name for a direction, z is north
        private static string Compass(float x, float z)
        {
            string[] names = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
            float degrees = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
            return names[((int)Mathf.Round(degrees / 45f) % 8 + 8) % 8];
        }

        private static string ShipLabel()
        {
            string name = ShipName ?? "";
            if (name.IndexOf("Raft", StringComparison.OrdinalIgnoreCase) >= 0) return "raft";
            if (name.IndexOf("Karve", StringComparison.OrdinalIgnoreCase) >= 0) return "karve";
            if (name.IndexOf("Ashlands", StringComparison.OrdinalIgnoreCase) >= 0) return "drakkar";
            if (name.IndexOf("Viking", StringComparison.OrdinalIgnoreCase) >= 0) return "longship";
            return "ship";
        }
        private static string _treeKey;
        private static bool _wasAboard;

        // every choice the kept tree depends on, a change starts a new tree
        private static string RouteKey()
        {
            return Comfortable + "|" + Settings.UseShip.Value + "|" + Settings.SailBias.Value + "|" + Settings.Danger.Value + "|" + Settings.MaxSlope.Value + "|" + Settings.SearchWeight.Value
                + "|" + Settings.UseWind.Value + "|" + Settings.RoutePortals.Value + "|" + Settings.CarryingOre.Value + "|" + Settings.ShipSpeedList.Value
                + "|" + Settings.BoatSource.Value + "|" + Settings.MoreBoats.Value + "|" + Settings.BuildSeconds.Value + "|" + Settings.ExtraBoatSeconds.Value + "|" + Settings.BuildShip.Value
                + "|" + Settings.LandmarkWeight.Value + "|" + Settings.CorridorWeight.Value + "|" + Abstraction.LandmarksReady + "|" + Settings.LoadedTerrain.Value + "|" + Settings.DetailedStructures.Value + "|" + Settings.SpeedyPaths.Value + "|" + Settings.MaxSwim.Value + "|" + Settings.JumpSlope.Value + "|" + Settings.JumpMeters.Value + "|" + Settings.MapMode.Value + "|" + Settings.ExplorerEdgeMeters.Value + "|" + Settings.ExplorerShared.Value;
        }

        internal static readonly string[] DangerNames = { "Ignore", "Avoid", "Never walk", "My progress" };
        private static readonly string[] Bosses = { "defeated_eikthyr", "defeated_gdking", "defeated_bonemass", "defeated_dragon", "defeated_goblinking", "defeated_queen", "defeated_fader" };
        private static readonly string[] BiomeNames = { "the ocean", "the Meadows", "the Black Forest", "the Swamp", "the Mountains", "the Plains", "the Mistlands", "the Ashlands", "the Deep North" };

        // the hardest biome your progress allows: the Black Forest after Eikthyr, one more biome per boss beaten
        private static int ProgressRank()
        {
            int rank = 1;
            if (ZoneSystem.instance == null) return rank;
            var keys = ZoneSystem.instance.GetGlobalKeys();
            for (int i = 0; i < Bosses.Length; i++) if (keys.Contains(Bosses[i])) rank = i + 2;
            return rank;
        }

        // biome rank by game progress, meadows 1 to deep north 8, the ocean 0
        internal static int Rank(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return 1;
                case Heightmap.Biome.BlackForest: return 2;
                case Heightmap.Biome.Swamp: return 3;
                case Heightmap.Biome.Mountain: return 4;
                case Heightmap.Biome.Plains: return 5;
                case Heightmap.Biome.Mistlands: return 6;
                case Heightmap.Biome.AshLands: return 7;
                case Heightmap.Biome.DeepNorth: return 8;
                default: return 0;
            }
        }

        // the ship you are aboard, otherwise the nearest known or loaded ship within ShipMeters. without one there, the nearest known ship
        // on land or sea explored by you or a cartography table (the user, 25 Oct)
        private static bool PickShip(Vector3 player, Ship aboard, out Vector3 position, out string prefab)
        {
            position = Vector3.zero; prefab = null; FarShipMeters = 0f; FarShipUsed = false;
            if (aboard != null) { position = aboard.transform.position; prefab = Utils.GetPrefabName(aboard.gameObject); return true; }
            float best = Settings.ShipMeters.Value, nearest = float.MaxValue, farBest = float.MaxValue;
            var farPosition = Vector3.zero;
            string farPrefab = null;
            foreach (var item in Ship.Instances)
            {
                var ship = item as Ship;
                if (ship == null) continue;
                float d = NavSearch.Distance(player.x, player.z, ship.transform.position.x, ship.transform.position.z);
                nearest = Math.Min(nearest, d);
                if (d < best) { best = d; position = ship.transform.position; prefab = Utils.GetPrefabName(ship.gameObject); }
                else if (d < farBest && ExplorerArea.OnExploredMap(ship.transform.position.x, ship.transform.position.z)) { farBest = d; farPosition = ship.transform.position; farPrefab = Utils.GetPrefabName(ship.gameObject); }
            }
            foreach (var mark in KnownWorld.Ships)
            {
                float d = NavSearch.Distance(player.x, player.z, mark.X, mark.Z);
                nearest = Math.Min(nearest, d);
                if (d < best) { best = d; position = new Vector3(mark.X, 0f, mark.Z); prefab = mark.Name; }
                else if (d < farBest && ExplorerArea.OnExploredMap(mark.X, mark.Z)) { farBest = d; farPosition = new Vector3(mark.X, 0f, mark.Z); farPrefab = mark.Name; }
            }
            if (prefab == null && farPrefab != null)
            {
                position = farPosition; prefab = farPrefab; FarShipUsed = true; FarShipMeters = farBest;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route uses a far ship, " + farPrefab + " at " + farPosition.x.ToString("F0") + ", " + farPosition.z.ToString("F0") + ", " + farBest.ToString("F0") + " m away, on the explored map");
                return true;
            }
            // a known ship was there but too far, the route text says why it was not used (test 13: a friend's boat at the map edge)
            if (prefab == null && nearest < float.MaxValue) FarShipMeters = nearest;
            return prefab != null;
        }

        // the route's ship lies beyond ShipMeters on the explored map
        internal static bool FarShipUsed;

        // a switch on the Route page changed, a running route is searched again with the new choice
        internal static void Recalculate()
        {
            if (_running && Destination.HasValue && !_waiting) Calculate(true);
        }

        internal static void Clear()
        {
            KnownWorld.CancelRoute();
            StopWorker();
            if (Search != null && !Search.Finished) Search.Cancel();
            Search = null; Destination = null; Problem = null; _waiting = false;
            _display = null; _running = false; _tree = null; _last = null;
            Name = "Pick a place in Find, or click the World map."; _world = null;
            Layers.Dirty();
        }

        internal static void Cancel()
        {
            _waiting = false; _running = false; KnownWorld.CancelRoute();
            StopWorker();
            if (Search != null && !Search.Finished) Search.Cancel();
            Problem = "Calculation canceled."; Layers.Dirty();
        }

        internal static void Tick()
        {
            if (_world != null && (WorldGenerator.instance != _world || Player.m_localPlayer == null)) { Clear(); return; }
            if (Destination.HasValue) Abstraction.Tick(Minimap.instance);
            RememberTick();
            LocalTick();
            if (_waiting)
            {
                if (KnownWorld.Busy) return;
                _waiting = false;
                Calculate(true);
            }
            if (_running && !_waiting && (Search == null || Idle(Search)) && Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + Settings.RouteRefresh.Value;
                _automatic = true;
                Follow();
                return;
            }
            if (Search == null) return;
            if (Working) return;
            // the background thread stopped on its own: the search finished (or failed), its result is taken over below
            bool fromWorker = _worker != null;
            if (fromWorker) { _worker = null; _workerSearch = null; }
            if (Search.Finished && !fromWorker) return;
            try
            {
                if (_workerError != null) { var error = _workerError; _workerError = null; throw new InvalidOperationException("background route search failed", error); }
                if (!Search.Finished)
                {
                    if (_threaded) { StartWorker(Search); return; }
                    Search.Step(Settings.RouteBudget.Value);
                }
                if (Search.Finished && Search.CorridorFailed)
                {
                    _skipCorridor = true;
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route corridor held no route, searching the whole map");
                    Calculate(true);
                    return;
                }
                if (Search.Finished)
                {
                    if (!Search.Cancelled && Search.Points.Count > 0) { _display = Search; _zonesSeen = LoadedZones(Search); StartLocal(Search); }
                    _next = Time.unscaledTime + Settings.RouteRefresh.Value;
                    Layers.Dirty();
                    if (!_reported && Plugin.LogCounters.Value)
                        Plugin.Log.LogInfo("route " + (Search.Reached ? "ready" : "partial") + ", " + Search.Length.ToString("F0") + " m, water " + Search.WaterLength.ToString("F0")
                            + " m, nodes " + Search.Expanded + ", samples " + Search.SampleCount + (fromWorker ? ", on a background thread" : ", worst slice " + Search.WorstSlice.ToString("F2") + " ms")
                            + ", " + Search.Seconds.ToString("F0") + " s, portal pairs " + Search.Options.Portals.Count + ", jumps " + Search.PortalJumps
                            + ", portal bound " + Search.Tree.PortalBound(Search.Tree.GoalX, Search.Tree.GoalZ, Search.Options.Boats).ToString("F0") + " s"
                            + ", loaded samples " + LoadedSamples + " (" + SolidSamples + " under a solid, " + BlockedSamples + " blocked, " + PathSamples + " on a path, " + RememberedSamples + " remembered, " + WallSteps + " wall steps, " + (LoadedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1") + " ms)");
                    _reported = true;
                }
            }
            catch (Exception ex)
            {
                Search.Cancel(); _running = false; Problem = "Route calculation failed. See the log.";
                Plugin.Log.LogWarning("route failed: " + ex);
            }
        }

        // every update the line behind the player is trimmed. a new search only runs off the route, after a teleport, when known sea hazards
        // changed or when the kept tree is older than the rebuild time. nothing else rescans the world's objects
        private static void Follow()
        {
            var at = Session.PlayerPosition();
            if (!at.HasValue || !Destination.HasValue) return;
            var p = at.Value;
            bool teleported = _last.HasValue && NavSearch.Distance(_last.Value.x, _last.Value.z, p.x, p.z) > Settings.TeleportMeters.Value;
            _last = p;
            // leaving the ship leaves it where it is, so the kept tree's boarding place is out of date
            bool left = _wasAboard && Aboard() == null;
            if (left) BoatUsed = true;
            if (KnownWorld.HazardVersion != _hazards || left || Time.unscaledTime - _treeBuilt >= Settings.RouteRebuildSeconds.Value) { Calculate(true); return; }
            if (!_wasAboard && Aboard() != null) { _wasAboard = true; Calculate(false); return; }
            // a wind that turned far from the one the route was planned with starts a new route
            if (_windUsed && Time.unscaledTime >= _windNext && _display != null && _display.SailLength > 0f)
            {
                _windNext = Time.unscaledTime + WindCheckSeconds;
                // Moder's power starting or running out changes every sailing speed
                if (ModerActive() != _moderUsed)
                {
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route searched again, Moder's power " + (_moderUsed ? "ended" : "started"));
                    Calculate(true);
                    return;
                }
                var wind = Wind.Mean(Mathf.Min(1800f, _display.SailLength / Mathf.Max(1f, _display.Options.SailSpeed)));
                if (!_moderUsed && wind.Intensity >= WindTurnMinStrength && wind.X * _windX + wind.Z * _windZ < Mathf.Cos(WindTurnDegrees * Mathf.Deg2Rad)) { Calculate(true); return; }
            }
            // a zone on the route that loaded after the route was searched may hold terraforming or objects the generated world lacks
            if (Settings.LoadedTerrain.Value && _display != null && _display.Finished && Time.unscaledTime >= _zoneNext && ZoneLoadedOnRoute(_display))
            {
                _zoneNext = Time.unscaledTime + ZoneRerouteSeconds; ZoneReroutes++;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route searched again, a zone on it loaded (" + ZoneReroutes + " so far)");
                Calculate(true);
                return;
            }
            // arrival: within the auto mark distance of the destination the place is marked done, the Find pin goes and the route ends
            if (Destination.HasValue && NavSearch.Distance(p.x, p.z, Destination.Value.x, Destination.Value.z) <= Plugin.AutoMarkMeters.Value)
            {
                string name = Name;
                var goal = Destination.Value;
                int marked = Settings.AutoMark.Value ? Session.MarkNear(goal.x, goal.z, Plugin.AutoMarkMeters.Value) : 0;
                Panel.Find?.Arrived(name, marked);
                Clear();
                Problem = "Arrived at " + name + "." + (marked > 0 ? " Marked done." : "");
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route arrived at " + name + ", " + marked + " marked done");
                return;
            }
            if (_display == null || teleported) { Calculate(false); return; }
            int version = _display.Version;
            float off = _display.Trim(p.x, p.z);
            if (_display.Version != version) { Trims++; Layers.Dirty(); }
            if (off > Settings.OffRouteMeters.Value) Calculate(false);
        }

        // one line for the find card, null while there is no route
        internal static string ShortSummary()
        {
            if (!Destination.HasValue) return null;
            if (_display == null) return Search != null && !Idle(Search) || _waiting ? "Route calculating" : null;
            string time = _display.Seconds < 60f ? _display.Seconds.ToString("F0") + " s" : (int)Math.Ceiling(_display.Seconds / 60f) + " min";
            return "Route " + _display.Length.ToString("N0") + " m, about " + time + (_display.Reached ? "" : ", partial") + (Search != null && !Idle(Search) ? ", updating" : "");
        }

        internal static string Summary()
        {
            if (Problem != null) return Problem;
            if (_waiting && !_automatic) return "Reading known sea hazards before routing.";
            if (Search == null) return "Choose a mode, then Calculate.";
            if (Search.Cancelled) return "Calculation canceled.";
            if (!Idle(Search)) return (_automatic ? "Updating route" : "Calculating") + ", " + Search.Expanded.ToString("N0") + " nodes checked.\nYou can close the map while it runs.";
            string text = Search.Reached ? "Route ready" : Search.Limited ? "Search limit reached. Partial route only" : "No route within the search area. Closest point found";
            string time = Search.Seconds < 60f ? Search.Seconds.ToString("F0") + " s" : (int)Math.Ceiling(Search.Seconds / 60f) + " min";
            float walk = Search.Length - Search.SailLength - Search.SwimLength;
            text += "\n" + Search.Length.ToString("N0") + " m, about " + time + ", climb " + Search.Climb.ToString("N0") + " m";
            text += "\nWalk " + walk.ToString("N0") + " m" + (Search.SailLength > 0f ? ", sail " + Search.SailLength.ToString("N0") + " m" : "") + (Search.SwimLength > 0f ? ", swim " + Search.SwimLength.ToString("N0") + " m" : "");
            if (!Search.Reached && Search.Points.Count > 0)
            {
                var end = Search.Points[Search.Points.Count - 1];
                text += "\nEndpoint x " + end.X.ToString("F0") + ", z " + end.Z.ToString("F0");
            }
            if (Search.Boards && Search.FirstBuilt) text += "\nBuild a " + ShipLabel() + " at the shore at x " + Search.BoardX.ToString("F0") + ", z " + Search.BoardZ.ToString("F0") + ".";
            else if (Search.Boards) text += "\nBoard your " + ShipLabel() + " at x " + Search.BoardX.ToString("F0") + ", z " + Search.BoardZ.ToString("F0") + ".";
            if (Search.BoatCount > 1) text += "\nUses " + Search.BoatCount + " boats, each later one built where the route reaches the sea again (More boats is on).";
            if (BoatUsed && Settings.BoatSource.Value == 1 && !Settings.MoreBoats.Value && !Search.Boards && Search.WaterLength > 100f) text += "\nThis trip's boat is used, the rest is on foot. More boats lets the route build another.";
            if (Search.Boards && NoShipNear) text += "\nNo ship of yours is known within " + Settings.ShipMeters.Value.ToString("N0") + " m, so the route builds one.";
            if (FarShipUsed && Search.Boards && !Search.FirstBuilt) text += "\nThe ship is " + FarShipMeters.ToString("N0") + " m away, beyond ShipMeters (" + Settings.ShipMeters.Value.ToString("N0") + " m), and known on your explored or shared map.";
            else if (FarShipMeters > 0f && !FarShipUsed && Search.WaterLength > 100f) text += "\nThe nearest known ship is " + FarShipMeters.ToString("N0") + " m away, beyond ShipMeters (" + Settings.ShipMeters.Value.ToString("N0") + " m), and not on your explored map, so it is not used.";
            if (Search.Options.Moder && Search.SailLength > 0f) text += "\nPlanned with Moder's power, the wind is behind the ship on every heading.";
            if (Search.Boards && !ShipKnown) text += "\n" + ShipName + " is from another mod, longship speeds are assumed (ShipSpeeds in the cfg).";
            if (Search.PortalJumps > 0) text += "\nUses " + Search.PortalJumps + (Search.PortalJumps == 1 ? " portal." : " portals.") + " Set Portals to Don't use when you carry ore.";
            if (Search.Options.Wind && Search.SailLength > 0f) text += "\nPlanned with wind from the " + Compass(-Search.Options.WindX, -Search.Options.WindZ) + ", " + (Search.Options.WindIntensity * 100f).ToString("F0") + " %.";
            if (Settings.LoadedTerrain.Value) text += Settings.DetailedStructures.Value ? "\nDetailed is a work in progress. Buildings are read and may fail inside them." : "\nDetailed is a work in progress. It reads the nearby terrain, rocks and trees, not buildings.";
            if (Search.LongestSwim > LongSwim) text += "\nA " + Search.LongestSwim.ToString("N0") + " m swim. Stamina may run out.";
            if (Search.DangerLength > 0f)
                text += Settings.Danger.Value == 3
                    ? "\nCareful, your gear may be too weak for " + BiomeNames[Math.Min(Search.DangerTop, BiomeNames.Length - 1)] + " (" + Search.DangerLength.ToString("N0") + " m)."
                    : "\nCrosses " + Search.DangerLength.ToString("N0") + " m of biomes harder than the destination.";
            if (Search.AshWaterLength > 0f) text += "\nMagenta legs are boiling Ashlands water. A suitable ship is required.";
            if (_running) text += "\nFollows you every " + Settings.RouteRefresh.Value.ToString("0.#") + " s and recalculates when you are " + Settings.OffRouteMeters.Value.ToString("F0") + " m off the route or teleport.";
            return text;
        }
    }

    internal sealed class NavigatorPage : Page
    {
        private TMP_Text _target, _status;
        private Ui.Switch _mode, _sail, _boat, _more, _detail, _ore;
        internal override string Title => "Route";

        internal override void Build(Transform parent)
        {
            _target = Ui.Label(parent, "Target", 0f, 0f, Panel.PageWidth, 55f, 18f, TextAlignmentOptions.TopLeft, Ui.Ink);
            _target.textWrappingMode = TextWrappingModes.Normal;
            _mode = Ui.Row(parent, "Mode", "Route mode", 0f, 58f, Panel.PageWidth, () => Navigator.Comfortable ? "Comfort" : "Shortest", () => Navigator.Comfortable,
                () => { Navigator.Comfortable = !Navigator.Comfortable; Navigator.Recalculate(); });
            // one route at a time, the switches choose which one, so the minimap never shows two lines
            _sail = Ui.Row(parent, "SailBias", "Foot or sail", 0f, 94f, Panel.PageWidth, () => Settings.SailBias.Value < 0.9f ? "More sail" : Settings.SailBias.Value > 1.1f ? "More walk" : "Balanced", () => Mathf.Abs(Settings.SailBias.Value - 1f) > 0.1f,
                () => { float[] steps = { 1f, 0.6f, 1.6f }; int i = Array.FindIndex(steps, s => Mathf.Abs(s - Settings.SailBias.Value) < 0.05f); Settings.SailBias.Value = steps[(i + 1) % steps.Length]; Navigator.Recalculate(); });
            // the boat: none, one built at the best shore (you can build one anywhere), or a walk to your nearest known ship
            _boat = Ui.Row(parent, "Boat", "Boat", 0f, 130f, Panel.PageWidth, () => !Settings.UseShip.Value ? "None" : Settings.BoatSource.Value == 2 ? "Nearest" : "Build", () => Settings.UseShip.Value,
                () =>
                {
                    if (!Settings.UseShip.Value) { Settings.UseShip.Value = true; Settings.BoatSource.Value = 1; }
                    else if (Settings.BoatSource.Value == 1) Settings.BoatSource.Value = 2;
                    else Settings.UseShip.Value = false;
                    Navigator.Recalculate();
                });
            // Fast reads only the generated world (0.7.8), Detailed reads the loaded area's real ground, objects and paths
            _detail = Ui.Row(parent, "RouteDetail", "Detail", 161f, 166f, 155f, () => Settings.LoadedTerrain.Value ? "Detailed" : "Fast", () => Settings.LoadedTerrain.Value,
                () => { Settings.LoadedTerrain.Value = !Settings.LoadedTerrain.Value; Navigator.Recalculate(); });
            _more = Ui.Row(parent, "MoreBoats", "More boats", 0f, 166f, 155f, () => Settings.MoreBoats.Value ? "Yes" : "No", () => Settings.MoreBoats.Value,
                () => { Settings.MoreBoats.Value = !Settings.MoreBoats.Value; Navigator.Recalculate(); });
            // portals on or off for this route, for example while carrying ore (CarryingOre in the cfg turns them off too)
            _ore = Ui.Row(parent, "UsePortals", "Portals", 0f, 202f, Panel.PageWidth, () => Settings.RoutePortals.Value && !Settings.CarryingOre.Value ? "Use" : "Don't use", () => Settings.RoutePortals.Value && !Settings.CarryingOre.Value,
                () => { bool on = Settings.RoutePortals.Value && !Settings.CarryingOre.Value; Settings.RoutePortals.Value = !on; if (!on) Settings.CarryingOre.Value = false; Navigator.Recalculate(); });
            TMP_Text unused;
            Ui.Tag(parent, "Calculate", "Calculate", 0f, 242f, 112f, 32f, 15f, TextAlignmentOptions.Midline, () => { Navigator.Start(); Show(); }, out unused);
            Ui.Tag(parent, "Cancel", "Cancel", 118f, 242f, 92f, 32f, 15f, TextAlignmentOptions.Midline, () => { Navigator.Cancel(); Show(); }, out unused);
            Ui.Tag(parent, "ClearRoute", "Clear", 216f, 242f, 100f, 32f, 15f, TextAlignmentOptions.Midline, () => { Navigator.Clear(); Show(); }, out unused);
            _status = Ui.Label(parent, "Status", 0f, 282f, Panel.PageWidth, 166f, 14f, TextAlignmentOptions.TopLeft, Ui.Ink);
            _status.textWrappingMode = TextWrappingModes.Normal;
            var legend = Ui.Label(parent, "Legend", 0f, 450f, Panel.PageWidth, 90f, 11f, TextAlignmentOptions.TopLeft, Ui.Dim);
            legend.textWrappingMode = TextWrappingModes.Normal;
            legend.text = "Line color: green flat, brown steep, dark blue sailing, light blue swimming or wading, magenta boiling Ashlands water, violet rings mark the portals to take. Boat: Build at the best shore, or walk to your Nearest ship.\nKnown sea rocks are avoided. Unknown spires, trees and buildings can block a route. Travel time is an estimate.";
        }

        internal override void Show() { _mode.Refresh(); _sail.Refresh(); _boat.Refresh(); _more.Refresh(); _detail.Refresh(); _ore.Refresh(); Frame(); }
        internal override void Frame() { _target.text = Navigator.Name; _status.text = Navigator.Summary(); }
    }
}
