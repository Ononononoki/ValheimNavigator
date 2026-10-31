using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ValheimNavigator
{
    // the route graph of the whole world (RouteGraph.cs zones), built on background threads nearest to you and to the asked route first, kept
    // on disk per world in tiles of 4 x 4 zones, read back on demand with the farthest tiles let go. a zone whose ground changed (terraforming,
    // visited ground, location shaping, a guest's copy of the host's) is built again with its eight neighbours, which read 20 m into it. runs
    // whenever a world is loaded, with or without the detailed terrain map
    internal static class RouteStore
    {
        internal const int TileZones = 4, TileMeters = TileZones * ZoneGraph.Meters;
        // the tile grid covers the world disc, tile index -Half to Half - 1 on each axis
        private const int Half = 42, Side = Half * 2;
        private const int FileVersion = 1;
        private const int MemoryTiles = 1200;
        private const float StatusSeconds = 1f, CheckSeconds = 2f, FullCheckSeconds = 30f;
        // a zone's ground data must stay the same this long before its zones are built again: visited ground fills in over many seconds
        // as you walk, each step moved the hash and rebuilt the zone again (30 Oct: about 1000 to 1500 rebuilds a session)
        private const float SettleSeconds = 20f;
        private static readonly Dictionary<long, (ulong hash, double since)> Pending = new Dictionary<long, (ulong, double)>();
        private static readonly System.Diagnostics.Stopwatch _settleClock = System.Diagnostics.Stopwatch.StartNew();
        private const float WorldEdge = 10500f;

        private enum State : byte { Unknown, Built, Working, Outside }

        private sealed class Tile { internal ZoneGraph[] Zones = new ZoneGraph[TileZones * TileZones]; internal long Used; }

        private static readonly object Gate = new object();
        private static readonly State[] States = new State[Side * Side];
        private static readonly Dictionary<int, Tile> Kept = new Dictionary<int, Tile>();
        private static readonly Dictionary<long, ulong> Signatures = new Dictionary<long, ulong>();
        private static readonly Dictionary<int, int> Repairs = new Dictionary<int, int>();
        private static readonly Dictionary<long, ulong> Own = new Dictionary<long, ulong>();

        private static WorldGenerator _world;
        private static volatile bool _opened;
        private static int _generation;
        private static int _threads;
        private static string _folder, _rulesKey;
        private static float _water;
        private static ZoneGraph.Rules _rules;
        private static volatile float _focusX, _focusZ;
        private static volatile bool _wanted;
        // while the game is paused or its menu is open nobody plays: BoostThreads more threads build the route map then
        private const int BoostThreads = 2;
        private static volatile bool _boost;
        private static float _wantX0, _wantZ0, _wantX1, _wantZ1;
        private static long _clockUse;
        private static bool _ownTerrain;

        // counters, under Gate
        private static int _inside, _built, _fresh, _diskReads, _repaired, _zonesBuilt, _failed;
        private static double _buildMilliseconds;
        private static bool _complete, _warned;
        private static System.Diagnostics.Stopwatch _clock;
        private static float _nextStatus;

        internal static string Status { get; private set; }
        internal static float Progress { get; private set; } = -1f;
        internal static ZoneGraph.Rules Rules => _rules;
        internal static bool Running => _world != null;

        // every frame on the game's thread: start and stop with the world, follow the player
        internal static void Tick()
        {
            var world = WorldGenerator.instance;
            bool want = Plugin.Enabled.Value && world != null && ZoneSystem.instance != null && !(ZNet.instance != null && ZNet.instance.IsDedicated());
            // changed route rules (the walking and sailing settings) start the route map again, checked once a second
            bool rulesChanged = _world != null && Time.unscaledTime >= _nextStatus && RulesKey() != _rulesKey;
            if (_world != null && (!want || world != _world || _threads != Settings.FineThreads.Value || rulesChanged)) Stop();
            if (_world == null && want && Player.m_localPlayer != null) Start(world, RulesKey());
            if (_world == null) { Status = null; Progress = -1f; return; }
            if (Player.m_localPlayer != null) { var p = Player.m_localPlayer.transform.position; _focusX = p.x; _focusZ = p.z; }
            _boost = Time.timeScale < 0.01f || Menu.IsVisible();
            // without the detailed terrain map nothing else reads the terraforming and the location shaping, the routes need both
            _ownTerrain = FineTerrain.Folder == null;
            if (_ownTerrain) { TerrainEdits.Tick(); LocationShaping.Tick(); }
            if (Time.unscaledTime >= _nextStatus) { _nextStatus = Time.unscaledTime + StatusSeconds; UpdateStatus(); }
        }

        // the walking and sailing rules the zones are built with, from the settings
        internal static NavOptions Options(bool comfort)
        {
            float maxSlope = Settings.MaxSlope.Value;
            return new NavOptions
            {
                MaxSlope = Mathf.Tan(maxSlope * Mathf.Deg2Rad), JumpSlope = Mathf.Tan(Mathf.Max(maxSlope, Settings.JumpSlope.Value) * Mathf.Deg2Rad), JumpMeters = Settings.JumpMeters.Value,
                WalkSpeed = Settings.WalkSpeed.Value, SwimSpeed = Settings.SwimSpeed.Value, SwimRisk = Settings.SwimRisk.Value, WadeDepth = Settings.WadeDepth.Value,
                MaxSwim = Settings.MaxSwim.Value, SailSpeed = Settings.SailSpeed.Value, ShipDraft = Settings.ShipDraft.Value, SlopePenalty = Settings.SlopePenalty.Value,
                Comfortable = comfort, WaterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f
            };
        }

        private static string RulesKey()
        {
            var o = Options(false);
            return string.Join(" ", new[] { o.MaxSlope, o.JumpSlope, o.JumpMeters, o.WalkSpeed, o.SwimSpeed, o.SwimRisk, o.WadeDepth, o.MaxSwim, o.SailSpeed, o.ShipDraft, o.SlopePenalty, o.WaterLevel }
                .Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static void Start(WorldGenerator world, string rulesKey)
        {
            _world = world;
            _threads = Math.Max(1, Settings.FineThreads.Value);
            _rulesKey = rulesKey;
            _water = ZoneSystem.instance.m_waterLevel;
            var shortest = Options(false); var comfort = Options(true);
            _rules = new ZoneGraph.Rules { Shortest = new NavTerrain(Point, shortest), Comfort = new NavTerrain(Point, comfort), Water = _water, Draft = shortest.ShipDraft, MaxSwim = shortest.MaxSwim, Wade = shortest.WadeDepth };
            _opened = false;
            int generation = ++_generation;
            lock (Gate)
            {
                Kept.Clear(); Signatures.Clear(); Repairs.Clear(); Own.Clear(); Pending.Clear();
                _inside = _built = _fresh = _diskReads = _repaired = _zonesBuilt = _failed = 0;
                _buildMilliseconds = 0; _complete = false; _wanted = false;
                for (int tz = -Half; tz < Half; tz++)
                    for (int tx = -Half; tx < Half; tx++)
                    {
                        bool inside = false;
                        for (int k = 0; k < TileZones * TileZones && !inside; k++) inside = Inside(tx * TileZones + k % TileZones, tz * TileZones + k / TileZones);
                        States[Index(tx, tz)] = inside ? State.Unknown : State.Outside;
                        if (inside) _inside++;
                    }
            }
            var w = world.m_world;
            string name = w != null ? w.m_name : "world";
            int seed = w != null ? w.m_seed : 0, version = w != null ? w.m_worldGenVersion : 0;
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            _folder = Path.Combine(BepInEx.Paths.CachePath, Plugin.ModName, name + "_" + seed, "routes");
            string key = FileVersion + " " + ZoneGraph.Version + " " + seed + " " + version + " " + global::Version.GetVersionString(false) + " " + rulesKey;
            _clock = System.Diagnostics.Stopwatch.StartNew();
            int threads = _threads;
            new Thread(() => Open(generation, key, threads)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator route map" }.Start();
        }

        internal static void Stop()
        {
            if (_world == null) return;
            lock (Gate) _generation++;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route map stopped, " + Summary());
            lock (Gate) { Kept.Clear(); Signatures.Clear(); Repairs.Clear(); Own.Clear(); Pending.Clear(); }
            // the detailed terrain map clears these itself when it runs
            if (_ownTerrain && FineTerrain.Folder == null) { TerrainEdits.Clear(true); LocationShaping.Clear(); }
            _world = null; _rules = null; Status = null; Progress = -1f;
        }

        // the zone's center lies inside the world's edge
        private static bool Inside(int zx, int zz) { float x = zx * ZoneGraph.Meters, z = zz * ZoneGraph.Meters; return x * x + z * z < WorldEdge * WorldEdge; }

        private static int Index(int tx, int tz) => (tz + Half) * Side + tx + Half;
        private static int TileOf(int zone) => FineBuilder.FloorDiv(zone, TileZones);
        private static long Key(int zx, int zz) => TerrainEdits.Key(zx, zz);

        // the ground point the zones and the routes read (x and z even meters), any thread
        internal static NavPoint Sample(int x, int z)
        {
            var world = _world ?? WorldGenerator.instance;
            var biome = world.GetBiome(x, z);
            float lava = 0f;
            if (biome == Heightmap.Biome.AshLands) { world.GetBiomeHeight(biome, x, z, out Color mask, false, true); lava = mask.a; }
            float height = RouteGround.Height(world, x, z);
            var surface = NavSearch.Classify(height, _water, biome == Heightmap.Biome.AshLands, lava, WorldGenerator.GetAshlandsOceanGradient(x, z));
            return new NavPoint { X = x, Z = z, Height = height, Surface = surface, Rank = (byte)Navigator.Rank(biome) };
        }

        private static NavPoint Point(float x, float z) => Sample(Mathf.RoundToInt(x), Mathf.RoundToInt(z));

        // open sea: deeper than 10 m on a 16 m grid over the zone and 64 m around it, so the zone is built from flat deep water without
        // reading the ground point by point
        private static bool Deep(int zx, int zz)
        {
            const int step = 16, margin = 64;
            float limit = _water - 10f;
            int x0 = ZoneGraph.OriginX(zx) - margin, z0 = ZoneGraph.OriginZ(zz) - margin, span = ZoneGraph.Meters + 2 * margin;
            for (int z = 0; z <= span; z += step)
                for (int x = 0; x <= span; x += step)
                    if (_world.GetHeight(x0 + x, z0 + z) > limit) return false;
            return true;
        }

        // a tile's ground points with the ring its zones read around them, each read once: neighbouring zones of the tile share their border
        // and ring points (a zone alone reads 2.6 times its own points)
        internal sealed class TileGround
        {
            private readonly int _x0, _z0, _n;
            private readonly NavPoint[] _points;
            private readonly bool[] _read;

            internal TileGround(int tx, int tz, ZoneGraph.Rules rules)
            {
                int ring = ZoneGraph.Ring(rules);
                _n = TileZones * ZoneGraph.Size + 2 * ring;
                _x0 = ZoneGraph.OriginX(tx * TileZones) - ring * ZoneGraph.Step; _z0 = ZoneGraph.OriginZ(tz * TileZones) - ring * ZoneGraph.Step;
                _points = new NavPoint[_n * _n]; _read = new bool[_n * _n];
            }

            internal NavPoint Get(int x, int z)
            {
                int i = (x - _x0) / ZoneGraph.Step, j = (z - _z0) / ZoneGraph.Step;
                if (x < _x0 || z < _z0 || i >= _n || j >= _n) return Sample(x, z);
                int k = j * _n + i;
                if (!_read[k]) { _points[k] = Sample(x, z); _read[k] = true; }
                return _points[k];
            }
        }

        private static NavPoint DeepSample(int x, int z) =>
            new NavPoint { X = x, Z = z, Height = _water - 10f, Surface = WorldGenerator.GetAshlandsOceanGradient(x, z) >= 0f ? NavSurface.AshWater : NavSurface.Water };

        // ---- the zones for a route query (any thread) ----

        // the zone's graph, read from disk when its tile was let go, null when it is not built (yet) or outside the world
        internal static ZoneGraph Zone(int zx, int zz)
        {
            int tx = TileOf(zx), tz = TileOf(zz);
            if (tx < -Half || tz < -Half || tx >= Half || tz >= Half) return null;
            int k = Index(tx, tz), slot = (zz - tz * TileZones) * TileZones + zx - tx * TileZones;
            int generation;
            lock (Gate)
            {
                if (Kept.TryGetValue(k, out var tile)) { tile.Used = ++_clockUse; return tile.Zones[slot]; }
                if (States[k] != State.Built) return null;
                generation = _generation;
            }
            var read = ReadTile(tx, tz);
            lock (Gate)
            {
                if (generation != _generation || read == null) return null;
                if (Kept.TryGetValue(k, out var other)) return other.Zones[slot];
                _diskReads++;
                Keep(k, read);
                return read.Zones[slot];
            }
        }

        // every tile around the box (the start, the goal, 512 m more) is built: the route can be searched on the graph
        internal static bool Ready(float x0, float z0, float x1, float z1, out int missing)
        {
            missing = 0;
            if (!_opened) { missing = -1; return false; }
            int ax = TileOf(RouteQuery.ZoneOf(Math.Min(x0, x1) - 512f)), bx = TileOf(RouteQuery.ZoneOf(Math.Max(x0, x1) + 512f));
            int az = TileOf(RouteQuery.ZoneOf(Math.Min(z0, z1) - 512f)), bz = TileOf(RouteQuery.ZoneOf(Math.Max(z0, z1) + 512f));
            lock (Gate)
                for (int tz = Math.Max(-Half, az); tz <= Math.Min(Half - 1, bz); tz++)
                    for (int tx = Math.Max(-Half, ax); tx <= Math.Min(Half - 1, bx); tx++)
                    {
                        var s = States[Index(tx, tz)];
                        if (s == State.Unknown || s == State.Working) missing++;
                    }
            return missing == 0;
        }

        // a route wants this box: its tiles are built before the rest of the world
        internal static void Want(float x0, float z0, float x1, float z1)
        {
            lock (Gate) { _wantX0 = Math.Min(x0, x1) - 512f; _wantZ0 = Math.Min(z0, z1) - 512f; _wantX1 = Math.Max(x0, x1) + 512f; _wantZ1 = Math.Max(z0, z1) + 512f; _wanted = true; }
        }

        // under Gate: keeps the tile and lets go of the least used ones over the limit, they stay on disk
        private static void Keep(int k, Tile tile)
        {
            tile.Used = ++_clockUse;
            Kept[k] = tile;
            while (Kept.Count > MemoryTiles)
            {
                int old = -1; long oldest = long.MaxValue;
                foreach (var pair in Kept) if (pair.Value.Used < oldest) { oldest = pair.Value.Used; old = pair.Key; }
                Kept.Remove(old);
            }
        }

        // ---- the background work ----

        private static void Open(int generation, string key, int threads)
        {
            int onDisk = 0;
            try
            {
                Directory.CreateDirectory(_folder);
                string keyFile = Path.Combine(_folder, "key.txt");
                if (!File.Exists(keyFile) || File.ReadAllText(keyFile).Trim() != key)
                {
                    // another world version, game version, file format or route rule: the old zones no longer match
                    foreach (var file in Directory.GetFiles(_folder)) File.Delete(file);
                    File.WriteAllText(keyFile, key);
                }
                foreach (var file in Directory.GetFiles(_folder, "*.tmp")) File.Delete(file);
                foreach (var file in Directory.GetFiles(_folder, "r_*.bin"))
                {
                    if (generation != _generation) return;
                    var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                    if (parts.Length != 3 || !int.TryParse(parts[1], out int tx) || !int.TryParse(parts[2], out int tz) || tx < -Half || tz < -Half || tx >= Half || tz >= Half) continue;
                    // the signatures of every zone on disk, so a zone changed since it was built is found. the tile itself is read again on demand
                    var tile = ReadTile(tx, tz);
                    if (tile == null) continue;
                    lock (Gate)
                    {
                        if (generation != _generation) return;
                        int k = Index(tx, tz);
                        if (States[k] != State.Unknown) continue;
                        States[k] = State.Built; _built++; onDisk++;
                        foreach (var z in tile.Zones) if (z != null) Signatures[Key(z.Zx, z.Zz)] = z.Signature;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("route map cache folder could not be read, zones are built again and may not be kept: " + ex.Message);
            }
            if (generation != _generation) return;
            _opened = true;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route map started, " + onDisk + " tiles on disk, " + _inside + " tiles in the world, " + threads + " threads");
            for (int i = 0; i < threads + BoostThreads; i++)
            {
                bool boost = i >= threads;
                new Thread(() => Work(generation, boost)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator route map " + i }.Start();
            }
            Check(generation);
        }

        private static void Work(int generation, bool boost)
        {
            while (generation == _generation)
            {
                if (boost && !_boost) { Thread.Sleep(500); continue; }
                int k, mask;
                lock (Gate) k = Pick(out mask);
                if (k < 0) { Thread.Sleep(250); continue; }
                int tx = k % Side - Half, tz = k / Side - Half;
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    // a repair starts from the tile as it is, a new tile from nothing
                    var tile = mask != 0 ? (Peek(k) ?? ReadTile(tx, tz) ?? new Tile()) : new Tile();
                    var copy = new Tile(); Array.Copy(tile.Zones, copy.Zones, copy.Zones.Length);
                    int count = 0;
                    var signatures = new ulong[copy.Zones.Length];
                    var ground = new TileGround(tx, tz, _rules);
                    for (int s = 0; s < copy.Zones.Length; s++)
                    {
                        if (mask != 0 && (mask >> s & 1) == 0) continue;
                        int zx = tx * TileZones + s % TileZones, zz = tz * TileZones + s / TileZones;
                        if (!Inside(zx, zz)) { copy.Zones[s] = null; continue; }
                        // the signature is taken before the ground is read, so a change while building is seen by the next check
                        ulong signature = Signature(zx, zz);
                        var g = ZoneGraph.Build(zx, zz, Deep(zx, zz) ? (Func<int, int, NavPoint>)DeepSample : ground.Get, _rules);
                        g.Signature = signature; signatures[s] = signature;
                        copy.Zones[s] = g; count++;
                        if (generation != _generation) return;
                    }
                    double ms = clock.Elapsed.TotalMilliseconds;
                    bool written = WriteTile(tx, tz, copy);
                    lock (Gate)
                    {
                        if (generation != _generation) return;
                        if (mask == 0) { States[k] = State.Built; _built++; _fresh++; }
                        else _repaired += count;
                        _zonesBuilt += count; _buildMilliseconds += ms;
                        if (!written) _failed++;
                        for (int s = 0; s < copy.Zones.Length; s++) if (copy.Zones[s] != null && (mask == 0 || (mask >> s & 1) != 0)) Signatures[Key(copy.Zones[s].Zx, copy.Zones[s].Zz)] = signatures[s];
                        if (Kept.ContainsKey(k) || mask == 0) Keep(k, copy);
                    }
                }
                catch (Exception ex)
                {
                    lock (Gate)
                    {
                        if (generation != _generation) return;
                        if (mask == 0) { States[k] = State.Built; _built++; }
                        _failed++;
                    }
                    if (!_warned) { _warned = true; Plugin.Log.LogWarning("route map tile " + tx + ", " + tz + " failed: " + ex); }
                }
            }
        }

        private static Tile Peek(int k) { lock (Gate) return Kept.TryGetValue(k, out var t) ? t : null; }

        // a repair first (nearest), else the tile still to build nearest to you or inside the box a route wants. under Gate
        private static int Pick(out int mask)
        {
            mask = 0;
            if (!_opened) return -1;
            float fx = _focusX, fz = _focusZ;
            int best = -1; float bestDistance = float.MaxValue;
            foreach (var pair in Repairs)
            {
                float d = Distance(pair.Key, fx, fz);
                if (d < bestDistance) { bestDistance = d; best = pair.Key; }
            }
            if (best >= 0) { mask = Repairs[best]; Repairs.Remove(best); return best; }
            for (int k = 0; k < States.Length; k++)
            {
                if (States[k] != State.Unknown) continue;
                float d = Distance(k, fx, fz);
                if (_wanted)
                {
                    float cx = (k % Side - Half + 0.5f) * TileMeters - ZoneGraph.Meters / 2f, cz = (k / Side - Half + 0.5f) * TileMeters - ZoneGraph.Meters / 2f;
                    float bx = Math.Max(_wantX0 - cx, Math.Max(0f, cx - _wantX1)), bz = Math.Max(_wantZ0 - cz, Math.Max(0f, cz - _wantZ1));
                    d = Math.Min(d, (float)Math.Sqrt(bx * bx + bz * bz));
                }
                if (d < bestDistance) { bestDistance = d; best = k; }
            }
            if (best >= 0) States[best] = State.Working;
            return best;
        }

        private static float Distance(int k, float fx, float fz)
        {
            float cx = (k % Side - Half + 0.5f) * TileMeters - ZoneGraph.Meters / 2f, cz = (k / Side - Half + 0.5f) * TileMeters - ZoneGraph.Meters / 2f;
            return (float)Math.Sqrt((cx - fx) * (cx - fx) + (cz - fz) * (cz - fz));
        }

        // ---- changed ground ----

        // the zone's own ground data: its terraforming and visited ground, and the location modifiers reaching it, 0 for none
        private static ulong OwnHash(int zx, int zz)
        {
            ulong a = TerrainEdits.ZoneHash(zx, zz), b = LocationShaping.ZoneHash(zx, zz);
            return a == 0UL && b == 0UL ? 0UL : Mix(Mix(1469598103934665603UL, a), b);
        }

        private static ulong Mix(ulong h, ulong v) { unchecked { for (int i = 0; i < 8; i++) { h = (h ^ (v & 0xFF)) * 1099511628211UL; v >>= 8; } } return h; }

        // a zone's signature: the own data of the zone and its eight neighbours (the swim limit and the border steps read into them)
        internal static ulong Signature(Func<int, int, ulong> own, int zx, int zz)
        {
            ulong h = 1469598103934665603UL;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) h = Mix(h, own(zx + dx, zz + dz));
            return h;
        }

        private static ulong Signature(int zx, int zz) => Signature(OwnHash, zx, zz);

        // the own data hash as the last check found it
        private static ulong Cached(int zx, int zz) { lock (Gate) return Own.TryGetValue(Key(zx, zz), out ulong v) ? v : 0UL; }

        // the zones a change in these zones touches: each one and its eight neighbours
        internal static HashSet<long> Touched(IEnumerable<long> changed)
        {
            var touched = new HashSet<long>();
            foreach (long key in changed)
            {
                int zx = (int)(key >> 32), zz = (int)(uint)key;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) touched.Add(Key(zx + dx, zz + dz));
            }
            return touched;
        }

        // on the route map's thread: when the terraforming, the visited ground or the location shaping moved, the zones with ground data are
        // hashed, a changed one and its neighbours are compared with their built signature, and the ones that differ are built again
        private static void Check(int generation)
        {
            int editVersion = int.MinValue; ulong shaping = ulong.MaxValue;
            var full = System.Diagnostics.Stopwatch.StartNew();
            while (generation == _generation)
            {
                Thread.Sleep((int)(CheckSeconds * 1000f));
                if (generation != _generation) return;
                if (TerrainEdits.Version == editVersion && LocationShaping.Signature == shaping && full.Elapsed.TotalSeconds < FullCheckSeconds && Pending.Count == 0) continue;
                editVersion = TerrainEdits.Version; shaping = LocationShaping.Signature; full.Reset(); full.Start();
                try
                {
                    var keys = new HashSet<long>(TerrainEdits.DataKeys());
                    keys.UnionWith(LocationShaping.ShapedKeys());
                    var changed = new List<long>();
                    lock (Gate) foreach (long key in Own.Keys) if (!keys.Contains(key)) changed.Add(key);
                    foreach (long key in keys)
                    {
                        ulong own = OwnHash((int)(key >> 32), (int)(uint)key);
                        lock (Gate)
                        {
                            if (Own.TryGetValue(key, out ulong old) && old == own) { Pending.Remove(key); continue; }
                            // a new hash waits until it has stayed the same for SettleSeconds
                            if (!Pending.TryGetValue(key, out var seen) || seen.hash != own) { Pending[key] = (own, _settleClock.Elapsed.TotalSeconds); continue; }
                            if (_settleClock.Elapsed.TotalSeconds - seen.since < SettleSeconds) continue;
                            Pending.Remove(key); Own[key] = own;
                        }
                        changed.Add(key);
                    }
                    lock (Gate) foreach (long key in changed) if (!keys.Contains(key)) Own.Remove(key);
                    if (changed.Count == 0) continue;
                    int marked = 0;
                    foreach (long key in Touched(changed))
                    {
                        int zx = (int)(key >> 32), zz = (int)(uint)key;
                        ulong now = Signature(Cached, zx, zz);
                        lock (Gate)
                        {
                            if (generation != _generation) return;
                            if (!Signatures.TryGetValue(key, out ulong built) || built == now) continue;
                            int tx = TileOf(zx), tz = TileOf(zz), k = Index(tx, tz);
                            Repairs.TryGetValue(k, out int mask);
                            Repairs[k] = mask | 1 << ((zz - tz * TileZones) * TileZones + zx - tx * TileZones);
                            marked++;
                        }
                    }
                    if (marked > 0 && Plugin.LogCounters.Value) Plugin.Log.LogInfo("route map: " + changed.Count + " zones with changed ground, " + marked + " zones to build again");
                }
                catch (Exception ex) { if (!_warned) { _warned = true; Plugin.Log.LogWarning("route map change check failed: " + ex.Message); } }
            }
        }

        // ---- tile files ----

        private static string TilePath(int tx, int tz) => Path.Combine(_folder, "r_" + tx + "_" + tz + ".bin");

        internal static byte[] Encode(ZoneGraph[] zones)
        {
            using (var memory = new MemoryStream())
            {
                using (var deflate = new DeflateStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new BinaryWriter(deflate))
                {
                    w.Write(FileVersion);
                    w.Write(zones.Length);
                    for (int s = 0; s < zones.Length; s++)
                    {
                        w.Write(zones[s] != null);
                        if (zones[s] != null) zones[s].Write(w);
                    }
                }
                return memory.ToArray();
            }
        }

        internal static ZoneGraph[] Decode(byte[] file)
        {
            using (var memory = new MemoryStream(file))
            using (var deflate = new DeflateStream(memory, CompressionMode.Decompress))
            using (var r = new BinaryReader(deflate))
            {
                if (r.ReadInt32() != FileVersion) throw new InvalidDataException("route tile version");
                int count = r.ReadInt32();
                if (count != TileZones * TileZones) throw new InvalidDataException("route tile size");
                var zones = new ZoneGraph[count];
                for (int s = 0; s < count; s++) if (r.ReadBoolean()) zones[s] = ZoneGraph.Read(r);
                return zones;
            }
        }

        private static bool WriteTile(int tx, int tz, Tile tile)
        {
            try
            {
                var bytes = Encode(tile.Zones);
                string path = TilePath(tx, tz), temporary = path + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                return true;
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("route map tile could not be written, it is built again next time: " + ex.Message); }
                return false;
            }
        }

        private static Tile ReadTile(int tx, int tz)
        {
            try
            {
                string path = TilePath(tx, tz);
                if (!File.Exists(path)) return null;
                var zones = Decode(File.ReadAllBytes(path));
                for (int s = 0; s < zones.Length; s++)
                    if (zones[s] != null && (zones[s].Zx != tx * TileZones + s % TileZones || zones[s].Zz != tz * TileZones + s / TileZones)) return null;
                var tile = new Tile(); tile.Zones = zones;
                return tile;
            }
            catch (Exception) { return null; }
        }

        // ---- status ----

        private static void UpdateStatus()
        {
            int built, inside; bool complete;
            lock (Gate) { built = _built; inside = _inside; complete = _complete; }
            if (!_opened) { Status = "Route map: reading"; Progress = 0f; return; }
            float progress = inside > 0 ? (float)built / inside : 1f;
            Progress = progress;
            Status = progress >= 1f ? null : "Route map: " + Mathf.FloorToInt(progress * 100f) + " %";
            if (progress >= 1f && !complete)
            {
                lock (Gate) _complete = true;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route map complete, " + Summary());
            }
        }

        private static string Summary()
        {
            lock (Gate)
                return _built + " of " + _inside + " tiles, " + _fresh + " built now, " + _zonesBuilt + " zones built in " + (_zonesBuilt > 0 ? (_buildMilliseconds / _zonesBuilt).ToString("F1") : "0") + " ms a zone, "
                    + _diskReads + " tiles read from disk, " + _repaired + " zones built again for changed ground, " + _failed + " failed, " + Kept.Count + " tiles in memory, "
                    + (_clock != null ? _clock.Elapsed.TotalSeconds.ToString("F0") : "0") + " s";
        }
    }
}
