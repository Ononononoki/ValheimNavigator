using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ValheimNavigator
{
    // the ground shaping the game does where it places a location (its TerrainModifier components: flattened plateaus, smoothed slopes),
    // so the generated ground matches the real one before your edits are added. the modifiers of each location kind are read once from the
    // game's own location prefabs (one at a time, loaded in the background) and kept in a file per game version. the placed locations come
    // from the world's LocationProxy objects, with their exact position and turn. the math follows Heightmap.LevelTerrain and SmoothTerrain2
    internal static class LocationShaping
    {
        private const float TickSeconds = 1f, ProxySeconds = 60f;
        private const int ProxyStepsPerTick = 8;
        private const string ProxyPrefab = "LocationProxy";

        // one modifier of a location kind, in the location's own space
        private struct Kind
        {
            internal int Sort;
            internal Vector3 Local;
            internal bool Level, Square, Smooth;
            internal float LevelRadius, LevelOffset, SmoothRadius, Power;
        }

        // one placed modifier in the world: its vertex, the height it shapes to, and its reach
        internal struct Mod
        {
            internal int Cx, Cz, Sort;
            internal float Y, LevelRadius, SmoothRadius, Power, Order;
            internal bool Level, Square, Smooth;
        }

        private static Dictionary<string, Kind[]> _kinds;
        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        private static readonly Queue<ZoneSystem.ZoneLocation> Pending = new Queue<ZoneSystem.ZoneLocation>();
        private static ZoneSystem.ZoneLocation _loading;
        private static string _file, _gameVersion;
        private static bool _kindsChanged, _kindsReady;
        private static readonly List<ZDO> Proxies = new List<ZDO>();
        private static int _proxyIndex = -1;
        private static float _nextTick, _nextProxies;
        private static ulong _lastKey;
        // the shaping by 64 m zone, replaced whole when it changes, read by background threads
        private static volatile Dictionary<long, Mod[]> _byZone;
        private static bool _warned;

        internal static int Count { get; private set; }
        // the shaping is built for this world, or it failed and the plain generated ground is used
        internal static bool Ready => _byZone != null || _warned;

        internal static void Clear() { _byZone = null; Proxies.Clear(); _proxyIndex = -1; _lastKey = 0; Count = 0; _nextProxies = 0f; _shared = null; _sharedChanged = false; }

        // one placed location as the host sends it: the stable hash of its prefab name (ZDOVars.s_location), its position and its turn in degrees
        internal struct Placed { internal int Location; internal float X, Y, Z, Angle; }

        // a guest's copy of the host's placed locations (MapShare). a location the guest's own world data has wins
        private static Placed[] _shared;
        private static bool _sharedChanged;

        internal static void SetShared(Placed[] placed) { _shared = placed; _sharedChanged = true; }

        // the host's placed locations for MapShare to send, false while a proxy walk is filling the list
        internal static bool Snapshot(List<Placed> placed)
        {
            if (_proxyIndex >= 0) return false;
            foreach (var zdo in Proxies)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                var p = zdo.GetPosition();
                placed.Add(new Placed { Location = zdo.GetInt(ZDOVars.s_location, 0), X = p.x, Y = p.y, Z = p.z, Angle = zdo.GetRotation().eulerAngles.y });
            }
            return true;
        }

        // changes when the placed locations or their kinds change
        internal static ulong Signature => _lastKey;

        // every frame from the fine terrain's tick
        internal static void Tick()
        {
            if (ZoneSystem.instance == null || ZDOMan.instance == null) return;
            try
            {
                if (_loading != null) Read();
                if (Time.unscaledTime < _nextTick) return;
                _nextTick = Time.unscaledTime + TickSeconds;
                if (_kinds == null) Open();
                if (_loading == null && Pending.Count > 0) Next();
                if (Pending.Count == 0 && _loading == null && !_kindsReady) { _kindsReady = true; if (_kindsChanged) Save(); }
                if (!_kindsReady) return;
                if (_sharedChanged && _proxyIndex < 0 && _nextProxies > 0f) { _sharedChanged = false; Build(); }
                if (_proxyIndex < 0 && Time.unscaledTime >= _nextProxies) { Proxies.Clear(); _proxyIndex = 0; }
                if (_proxyIndex >= 0)
                    for (int i = 0; i < ProxyStepsPerTick; i++)
                        if (ZDOMan.instance.GetAllZDOsWithPrefabIterative(ProxyPrefab, Proxies, ref _proxyIndex)) { _proxyIndex = -1; _nextProxies = Time.unscaledTime + ProxySeconds; Build(); break; }
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("location ground shaping unavailable, the map uses the plain generated ground there: " + ex.Message); }
                _loading = null; Pending.Clear(); _kindsReady = true;
            }
        }

        // the cached kinds for this game version, and every location kind still to read from its prefab
        private static void Open()
        {
            _kinds = new Dictionary<string, Kind[]>();
            _gameVersion = global::Version.GetVersionString(false);
            _file = Path.Combine(BepInEx.Paths.CachePath, Plugin.ModName, "location_terrain.txt");
            try
            {
                if (File.Exists(_file))
                {
                    var lines = File.ReadAllLines(_file);
                    if (lines.Length > 0 && lines[0] == "version " + _gameVersion)
                        for (int i = 1; i < lines.Length; i++) Parse(lines[i]);
                    else _kinds.Clear();
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("location ground shaping file could not be read, it is made again: " + ex.Message); _kinds.Clear(); }
            foreach (var location in ZoneSystem.instance.m_locations)
            {
                if (location == null || string.IsNullOrEmpty(location.m_prefabName)) continue;
                // a proxy keeps its location as the stable hash of the prefab name (ZDOVars.s_location)
                Names[location.m_prefabName.GetStableHashCode()] = location.m_prefabName;
                if (location.m_prefab.IsValid && !string.IsNullOrEmpty(location.m_prefab.Name)) Names[location.m_prefab.Name.GetStableHashCode()] = location.m_prefabName;
                if (location.m_enable && !_kinds.ContainsKey(location.m_prefabName) && location.m_prefab.IsValid) Pending.Enqueue(location);
            }
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("location ground shaping: " + _kinds.Count + " location kinds from the file, " + Pending.Count + " to read from the game");
        }

        private static void Next()
        {
            _loading = Pending.Dequeue();
            _loading.m_prefab.LoadAsync();
        }

        // the loaded prefab's modifiers, then the prefab let go again
        private static void Read()
        {
            var location = _loading;
            if (!location.m_prefab.IsLoaded) return;
            _loading = null;
            var prefab = location.m_prefab.Asset;
            var list = new List<Kind>();
            if (prefab != null)
                foreach (var m in prefab.GetComponentsInChildren<TerrainModifier>(true))
                {
                    // shaping the game writes into the terrain compiler is in the saved edits already
                    if (m.m_useTerrainCompiler || m.m_playerModifiction || !m.m_level && !m.m_smooth) continue;
                    list.Add(new Kind { Sort = m.m_sortOrder, Local = prefab.transform.InverseTransformPoint(m.transform.position), Level = m.m_level, Square = m.m_square,
                        Smooth = m.m_smooth, LevelRadius = m.m_levelRadius, LevelOffset = m.m_levelOffset, SmoothRadius = m.m_smoothRadius, Power = m.m_smoothPower });
                }
            location.m_prefab.Release();
            _kinds[location.m_prefabName] = list.ToArray();
            _kindsChanged = true;
        }

        private static void Parse(string line)
        {
            var parts = line.Split('|');
            if (parts.Length < 1 || parts[0].Length == 0) return;
            var list = new List<Kind>();
            for (int i = 1; i < parts.Length; i++)
            {
                var f = parts[i].Split(';');
                if (f.Length != 11) return;
                float F(int k) => float.Parse(f[k], CultureInfo.InvariantCulture);
                list.Add(new Kind { Sort = int.Parse(f[0], CultureInfo.InvariantCulture), Local = new Vector3(F(1), F(2), F(3)), Level = f[4] == "1", LevelRadius = F(5), LevelOffset = F(6),
                    Square = f[7] == "1", Smooth = f[8] == "1", SmoothRadius = F(9), Power = F(10) });
            }
            _kinds[parts[0]] = list.ToArray();
        }

        private static void Save()
        {
            try
            {
                var text = new System.Text.StringBuilder("version " + _gameVersion + "\n");
                var inv = CultureInfo.InvariantCulture;
                foreach (var pair in _kinds)
                {
                    text.Append(pair.Key);
                    foreach (var k in pair.Value)
                        text.Append('|').Append(k.Sort.ToString(inv)).Append(';').Append(k.Local.x.ToString("R", inv)).Append(';').Append(k.Local.y.ToString("R", inv)).Append(';').Append(k.Local.z.ToString("R", inv))
                            .Append(';').Append(k.Level ? '1' : '0').Append(';').Append(k.LevelRadius.ToString("R", inv)).Append(';').Append(k.LevelOffset.ToString("R", inv)).Append(';').Append(k.Square ? '1' : '0')
                            .Append(';').Append(k.Smooth ? '1' : '0').Append(';').Append(k.SmoothRadius.ToString("R", inv)).Append(';').Append(k.Power.ToString("R", inv));
                    text.Append('\n');
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.WriteAllText(_file + ".tmp", text.ToString());
                if (File.Exists(_file)) File.Delete(_file);
                File.Move(_file + ".tmp", _file);
                _kindsChanged = false;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("location ground shaping: " + _kinds.Count + " location kinds read and kept in " + _file);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("location ground shaping file could not be written, it is read again next time: " + ex.Message); }
        }

        // the placed modifiers of every location proxy, in the game's order (sort order, then distance from the world's middle), by zone
        private static void Build()
        {
            var mods = new List<Mod>();
            ulong key = 0;
            var own = new HashSet<long>();
            void Place(int location, Vector3 position, Quaternion rotation)
            {
                if (!Names.TryGetValue(location, out var name) || !_kinds.TryGetValue(name, out var kinds) || kinds.Length == 0) return;
                unchecked { key += (ulong)name.GetStableHashCode() * 0x9E3779B97F4A7C15UL ^ (ulong)(long)Mathf.RoundToInt(position.x * 4f) * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(long)Mathf.RoundToInt(position.z * 4f); }
                foreach (var k in kinds)
                {
                    var at = position + rotation * k.Local;
                    mods.Add(new Mod { Cx = Mathf.FloorToInt(at.x + 0.5f), Cz = Mathf.FloorToInt(at.z + 0.5f), Sort = k.Sort, Y = at.y + k.LevelOffset, Level = k.Level, Square = k.Square,
                        LevelRadius = k.LevelRadius, Smooth = k.Smooth, SmoothRadius = k.SmoothRadius, Power = k.Power, Order = at.x * at.x + at.y * at.y + at.z * at.z });
                }
            }
            long Spot(float x, float z) => ((long)Mathf.RoundToInt(x * 4f) << 32) ^ (uint)Mathf.RoundToInt(z * 4f);
            foreach (var zdo in Proxies)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                var position = zdo.GetPosition();
                own.Add(Spot(position.x, position.z));
                Place(zdo.GetInt(ZDOVars.s_location, 0), position, zdo.GetRotation());
            }
            var shared = _shared;
            if (shared != null)
                foreach (var s in shared)
                    if (!own.Contains(Spot(s.X, s.Z))) Place(s.Location, new Vector3(s.X, s.Y, s.Z), Quaternion.Euler(0f, s.Angle, 0f));
            if (key == _lastKey && _byZone != null) return;
            _lastKey = key;
            mods.Sort((a, b) => a.Sort != b.Sort ? a.Sort.CompareTo(b.Sort) : a.Order.CompareTo(b.Order));
            var byZone = new Dictionary<long, List<Mod>>();
            foreach (var m in mods)
            {
                float reach = Math.Max(m.Level ? m.LevelRadius * (m.Square ? 1.4143f : 1f) : 0f, m.Smooth ? m.SmoothRadius : 0f) + 1f;
                int z0 = FineBuilder.FloorDiv((int)Math.Floor(m.Cz - reach) + 32, 64), z1 = FineBuilder.FloorDiv((int)Math.Ceiling(m.Cz + reach) + 32, 64);
                int x0 = FineBuilder.FloorDiv((int)Math.Floor(m.Cx - reach) + 32, 64), x1 = FineBuilder.FloorDiv((int)Math.Ceiling(m.Cx + reach) + 32, 64);
                for (int zz = z0; zz <= z1; zz++)
                    for (int zx = x0; zx <= x1; zx++)
                    {
                        long k = ((long)zx << 32) ^ (uint)zz;
                        if (!byZone.TryGetValue(k, out var list)) byZone[k] = list = new List<Mod>();
                        list.Add(m);
                    }
            }
            var done = new Dictionary<long, Mod[]>(byZone.Count);
            foreach (var pair in byZone) done[pair.Key] = pair.Value.ToArray();
            _byZone = done;
            Count = mods.Count;
            TerrainEdits.Changed();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("location ground shaping: " + Proxies.Count + " placed locations" + (shared != null ? " and " + shared.Length + " from the host" : "")
                + ", " + mods.Count + " ground modifiers over " + done.Count + " zones");
        }

        // a hash of the modifiers reaching a zone, 0 when none (the route store rebuilds a zone whose hash changed)
        internal static ulong ZoneHash(int zx, int zz)
        {
            var mods = ZoneMods(zx, zz);
            if (mods == null) return 0UL;
            ulong h = 1469598103934665603UL;
            unchecked
            {
                void Mix(int v) { h = (h ^ (uint)v) * 1099511628211UL; }
                foreach (var m in mods)
                {
                    Mix(m.Cx); Mix(m.Cz); Mix(m.Sort); Mix(BitConverter.ToInt32(BitConverter.GetBytes(m.Y), 0)); Mix(BitConverter.ToInt32(BitConverter.GetBytes(m.LevelRadius), 0));
                    Mix(BitConverter.ToInt32(BitConverter.GetBytes(m.SmoothRadius), 0)); Mix(BitConverter.ToInt32(BitConverter.GetBytes(m.Power), 0)); Mix((m.Level ? 1 : 0) | (m.Square ? 2 : 0) | (m.Smooth ? 4 : 0));
                }
            }
            return h;
        }

        // every zone some modifier reaches
        internal static List<long> ShapedKeys() { var byZone = _byZone; return byZone != null ? new List<long>(byZone.Keys) : new List<long>(); }

        // the modifiers reaching one 64 m zone, null when none (any thread)
        internal static Mod[] ZoneMods(int zx, int zz)
        {
            var byZone = _byZone;
            return byZone != null && byZone.TryGetValue(((long)zx << 32) ^ (uint)zz, out var mods) ? mods : null;
        }

        // the generated ground of a fine point shaped like the game shapes it around placed locations (any thread)
        internal static float Shape(int x, int z, float height)
        {
            var mods = ZoneMods(FineBuilder.FloorDiv(x + 32, 64), FineBuilder.FloorDiv(z + 32, 64));
            return mods != null ? Apply(mods, x, z, height) : height;
        }

        // the game's own steps for one vertex: level sets the height inside the radius (or the axis aligned square of its whole meters), smooth
        // moves it toward the height by 1 - (distance / radius) ^ power
        internal static float Apply(Mod[] mods, int x, int z, float height)
        {
            foreach (var m in mods)
            {
                float dx = x - m.Cx, dz = z - m.Cz;
                if (m.Level)
                {
                    int reach = Mathf.CeilToInt(m.LevelRadius);
                    bool inside = m.Square ? Math.Abs(dx) <= reach && Math.Abs(dz) <= reach : Math.Sqrt(dx * dx + dz * dz) <= m.LevelRadius;
                    if (inside) height = m.Y;
                }
                if (m.Smooth)
                {
                    float d = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (d <= m.SmoothRadius && m.SmoothRadius > 0f)
                    {
                        float t = d / m.SmoothRadius;
                        t = m.Power == 3f ? t * t * t : Mathf.Pow(t, m.Power);
                        height = DUtils.Lerp(height, m.Y, 1f - t);
                    }
                }
            }
            return height;
        }
    }
}
