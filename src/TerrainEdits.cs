using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace ValheimNavigator
{
    // your terraforming and ground paint over the fine terrain, read from the world data the game saves for every edited zone (the zone's
    // _TerrainCompiler object, ZDOVars.s_TCData). this works for zones that are not loaded, and on the host for the whole world. a zone is
    // read again only when its data revision changed. the idea to read this record and the paint classes follow LivingMap by tbsj1ga (MIT),
    // the format and the height rule are the game's own (TerrainComp.Save, Load and ApplyToHeightmap)
    internal static class TerrainEdits
    {
        // the game's vertex grid of a zone is 65 x 65 points 1 m apart, the fine terrain uses every second one
        private const int Vertices = 65, Points = 33;
        private const float MaxDelta = 8f;
        private const int ZdosPerFrame = 4000, SlotsPerFrame = 40000;
        private const float PassSeconds = 10f, CheckSeconds = 1f;
        private const int ChecksPerSession = 24;

        internal const byte PaintNone = 0, PaintDirt = 1, PaintCultivated = 2, PaintPaved = 3, PaintCleared = 4;

        // one zone's edits at the fine points, row 0 in the south. height in 2 cm steps
        internal sealed class Zone
        {
            internal readonly short[] Height = new short[Points * Points];
            internal readonly byte[] Paint = new byte[Points * Points];
            internal int Changed, Painted;
        }

        private static readonly int CompilerPrefab = "_TerrainCompiler".GetStableHashCode();
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> Sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly object Gate = new object();
        private static readonly Dictionary<long, Zone> Zones = new Dictionary<long, Zone>();
        private static readonly Dictionary<ZDOID, uint> Revisions = new Dictionary<ZDOID, uint>();
        private static readonly List<KeyValuePair<long, byte[]>> Found = new List<KeyValuePair<long, byte[]>>();
        private static readonly HashSet<long> Checked = new HashSet<long>();
        // a guest's copy of the host's edits and visited ground (MapShare). a zone the guest read itself (its edits or its visited ground)
        // wins over the host's copy of that zone
        private static Dictionary<long, Zone> _sharedZones = new Dictionary<long, Zone>();
        private static Dictionary<long, short[]> _sharedVisited = new Dictionary<long, short[]>();

        // the host's zones for a guest (null clears them)
        internal static void SetShared(Dictionary<long, Zone> zones, Dictionary<long, short[]> visited)
        {
            lock (Gate) { _sharedZones = zones ?? new Dictionary<long, Zone>(); _sharedVisited = visited ?? new Dictionary<long, short[]>(); }
            Version++;
        }

        // the host's own zones for MapShare to send. the zone objects and height arrays are replaced, never changed, so they can be encoded
        // on another thread
        internal static void Snapshot(List<KeyValuePair<long, Zone>> zones, List<KeyValuePair<long, short[]>> visited)
        {
            lock (Gate)
            {
                foreach (var pair in Zones) zones.Add(pair);
                foreach (var pair in Visited) visited.Add(pair);
            }
        }

        // the zone's visited ground: the guest's own, else the host's when the guest has no edits of its own there (caller holds Gate)
        private static bool VisitedLocked(long key, out short[] heights)
        {
            if (Visited.TryGetValue(key, out heights)) return true;
            return !Zones.ContainsKey(key) && _sharedVisited.TryGetValue(key, out heights);
        }

        // the zone of a point has edits or visited ground, the guest's own or the host's (routes check such ground every 2 m)
        internal static bool HasData(float x, float z)
        {
            long key = Key(Mathf.FloorToInt((x + 32f) / 64f), Mathf.FloorToInt((z + 32f) / 64f));
            lock (Gate) return Zones.ContainsKey(key) || Visited.ContainsKey(key) || _sharedZones.ContainsKey(key) || _sharedVisited.ContainsKey(key);
        }

        // a hash of the heights a zone's route ground reads from here (the visited ground and edits that win for it), 0 when it has none.
        // the route store rebuilds a zone whose hash changed
        internal static ulong ZoneHash(int zx, int zz)
        {
            long key = Key(zx, zz);
            short[] visited; Zone zone;
            lock (Gate) { VisitedLocked(key, out visited); ZoneLocked(key, out zone); }
            if (visited == null && zone == null) return 0UL;
            ulong h = 1469598103934665603UL;
            unchecked
            {
                if (visited != null) foreach (short v in visited) h = (h ^ (ushort)v) * 1099511628211UL;
                h = (h ^ 0xFFUL) * 1099511628211UL;
                if (zone != null) foreach (short v in zone.Height) h = (h ^ (ushort)v) * 1099511628211UL;
            }
            return h;
        }

        // every zone with edits or visited ground, the guest's own or the host's
        internal static List<long> DataKeys()
        {
            lock (Gate)
            {
                var keys = new HashSet<long>(Zones.Keys);
                keys.UnionWith(Visited.Keys); keys.UnionWith(_sharedZones.Keys); keys.UnionWith(_sharedVisited.Keys);
                return new List<long>(keys);
            }
        }

        private static bool ZoneLocked(long key, out Zone zone) => Zones.TryGetValue(key, out zone) || !Visited.ContainsKey(key) && _sharedZones.TryGetValue(key, out zone);

        private static ZDOMan _source;
        private static int _slot, _inSlot, _compilers, _passes, _failed;
        private static float _nextPass, _nextCheck;
        private static Task<List<KeyValuePair<long, Zone>>> _parsing;
        private static System.Diagnostics.Stopwatch _passClock;
        private static double _scanMilliseconds;
        private static bool _warned;

        internal static long Key(int zx, int zz) => (long)zx << 32 ^ (uint)zz;

        // every frame on the game's thread while the fine terrain runs: a sliced walk over the world's objects, the records that changed are
        // parsed on a background task
        internal static void Tick()
        {
            var man = ZDOMan.instance;
            if (man != _source) { Clear(false); _source = man; }
            if (man == null) return;
            try
            {
                if (Time.unscaledTime >= _nextVisit) { _nextVisit = Time.unscaledTime + VisitSeconds; ReadLoaded(); }
                if (_parsing != null)
                {
                    if (!_parsing.IsCompleted) return;
                    Merge(_parsing);
                    _parsing = null;
                }
                // the check compares only after the first pass has read the edits
                if (Plugin.LogCounters.Value && _passes > 0 && Time.unscaledTime >= _nextCheck) { _nextCheck = Time.unscaledTime + CheckSeconds; CheckLoaded(); }
                if (_slot < 0 && Time.unscaledTime < _nextPass) return;
                if (_slot < 0) { _slot = 0; _inSlot = 0; _compilers = 0; _scanMilliseconds = 0; _passClock = System.Diagnostics.Stopwatch.StartNew(); }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var sectors = Sectors(man);
                int seen = 0, slots = 0;
                while (_slot < sectors.Length && seen < ZdosPerFrame && slots < SlotsPerFrame)
                {
                    var list = sectors[_slot];
                    if (list == null || _inSlot >= list.Count) { _slot++; _inSlot = 0; slots++; continue; }
                    var zdo = list[_inSlot++];
                    seen++;
                    if (zdo == null || zdo.GetPrefab() != CompilerPrefab) continue;
                    _compilers++;
                    uint revision = zdo.DataRevision;
                    if (Revisions.TryGetValue(zdo.m_uid, out uint was) && was == revision) continue;
                    Revisions[zdo.m_uid] = revision;
                    byte[] data = zdo.GetByteArray(ZDOVars.s_TCData, null);
                    if (data == null) continue;
                    var p = zdo.GetPosition();
                    Found.Add(new KeyValuePair<long, byte[]>(Key(Mathf.FloorToInt((p.x + 32f) / 64f), Mathf.FloorToInt((p.z + 32f) / 64f)), data));
                }
                _scanMilliseconds += clock.Elapsed.TotalMilliseconds;
                if (_slot < sectors.Length) return;
                _slot = -1; _passes++;
                _nextPass = Time.unscaledTime + PassSeconds;
                if (Found.Count == 0) return;
                var work = new List<KeyValuePair<long, byte[]>>(Found);
                Found.Clear();
                _parsing = Task.Run(() => ParseAll(work));
            }
            catch (Exception ex)
            {
                _slot = -1; _nextPass = Time.unscaledTime + PassSeconds;
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("terrain edits could not be read: " + ex.Message); }
            }
        }

        // all: also the visited zones (the world is left), otherwise only what the world data walk found
        internal static void Clear(bool all)
        {
            lock (Gate) { Zones.Clear(); _sharedZones = new Dictionary<long, Zone>(); _sharedVisited = new Dictionary<long, short[]>(); if (all) { Visited.Clear(); _visitedDirty = false; } }
            Revisions.Clear(); Found.Clear(); Checked.Clear();
            _slot = -1; _nextPass = 0f; _parsing = null; _source = null; _passes = 0;
        }

        // the real ground of zones you visited, read from the loaded terrain every few seconds. it holds what the edit record does not: the
        // leveling of locations and some pieces (TerrainModifier, applied before the edits in Heightmap.ApplyModifiers). it comes first, before
        // the generated ground plus the edits, and is kept on disk per world
        private static readonly Dictionary<long, short[]> Visited = new Dictionary<long, short[]>();
        private static readonly short[] Scratch = new short[Points * Points];
        private const float VisitSeconds = 5f, VisitSaveSeconds = 120f;
        private const int VisitedVersion = 1;
        private static float _nextVisit, _nextVisitSave;
        private static bool _visitedDirty;
        private static int _visitedChanged;
        private static Task _visitedSaving;

        internal static int VisitedCount { get { lock (Gate) return Visited.Count; } }

        private static void ReadLoaded()
        {
            int changed = 0;
            foreach (var map in Heightmap.GetAllHeightmaps())
            {
                if (map == null || map.IsDistantLod || map.m_width != Vertices - 1 || map.m_scale != 1f || map.HaveQueuedRebuild()) continue;
                var c = map.transform.position;
                int zx = Mathf.RoundToInt(c.x / 64f), zz = Mathf.RoundToInt(c.z / 64f);
                if (Math.Abs(c.x - zx * 64f) > 0.01f || Math.Abs(c.z - zz * 64f) > 0.01f) continue;
                try
                {
                    for (int j = 0; j < Points; j++)
                        for (int i = 0; i < Points; i++)
                            Scratch[j * Points + i] = FineTerrain.Quantize(map.GetHeight(i * 2, j * 2));
                }
                catch (Exception) { continue; }
                long key = Key(zx, zz);
                lock (Gate)
                {
                    if (Visited.TryGetValue(key, out var old) && Same(old, Scratch)) continue;
                    Visited[key] = (short[])Scratch.Clone();
                }
                changed++;
            }
            if (changed == 0) return;
            _visitedDirty = true; _visitedChanged += changed; Version++;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("visited ground: " + changed + " loaded zones read or changed, " + VisitedCount + " kept");
            if (Time.unscaledTime >= _nextVisitSave && (_visitedSaving == null || _visitedSaving.IsCompleted) && FineTerrain.Folder != null)
            {
                _nextVisitSave = Time.unscaledTime + VisitSaveSeconds;
                string folder = FineTerrain.Folder;
                _visitedSaving = Task.Run(() => SaveVisited(folder));
            }
        }

        private static bool Same(short[] a, short[] b)
        {
            for (int k = 0; k < a.Length; k++) if (a[k] != b[k]) return false;
            return true;
        }

        // grows whenever edits or visited ground change, so a view drawn from them knows to draw again
        internal static int Version;

        // one zone's visited ground and edits (either can be null), read once per zone by the map view
        internal static void ZoneData(int zx, int zz, out short[] visited, out Zone edits)
        {
            long key = Key(zx, zz);
            lock (Gate) { VisitedLocked(key, out visited); ZoneLocked(key, out edits); }
        }

        // the best known ground of a fine point: the visited ground, else the generated ground shaped around placed locations plus the edits.
        // false when neither is known
        internal static bool Ground(int x, int z, out float height)
        {
            if (VisitedAt(x, z, out height)) return true;
            if (!FineTerrain.TryBase(x, z, out height)) return false;
            height = LocationShaping.Shape(x, z, height);
            if (At(x, z, out float delta, out _)) height += delta;
            return true;
        }

        // something the ground is drawn from changed elsewhere (the location shaping), the views draw again
        internal static void Changed() { Version++; }

        // the ground of a fine point in a visited zone
        internal static bool VisitedAt(int x, int z, out float height)
        {
            height = 0f;
            int zx = FineBuilder.FloorDiv(x + 32, 64), zz = FineBuilder.FloorDiv(z + 32, 64);
            short[] heights;
            lock (Gate) if (!VisitedLocked(Key(zx, zz), out heights)) return false;
            height = heights[((z + 32 - zz * 64) >> 1) * Points + ((x + 32 - zx * 64) >> 1)] * FineTerrain.HeightStep;
            return true;
        }

        // "NVVG", version, count, then per zone its x, z and 33 x 33 heights, deflated after the mark and version
        internal static void SaveVisited(string folder)
        {
            if (folder == null) return;
            byte[] raw;
            int count;
            lock (Gate)
            {
                if (!_visitedDirty) return;
                count = Visited.Count;
                raw = new byte[4 + count * (8 + Points * Points * 2)];
                int o = 0;
                Write(raw, ref o, count);
                foreach (var pair in Visited)
                {
                    Write(raw, ref o, (int)(pair.Key >> 32)); Write(raw, ref o, (int)pair.Key);
                    Buffer.BlockCopy(pair.Value, 0, raw, o, Points * Points * 2); o += Points * Points * 2;
                }
                _visitedDirty = false;
            }
            try
            {
                string path = System.IO.Path.Combine(folder, "visited.bin"), temporary = path + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".tmp";
                using (var file = new System.IO.FileStream(temporary, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                {
                    file.Write(new[] { (byte)'N', (byte)'V', (byte)'V', (byte)'G', (byte)VisitedVersion }, 0, 5);
                    using (var deflate = new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionMode.Compress)) deflate.Write(raw, 0, raw.Length);
                }
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(temporary, path);
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("visited ground saved, " + count + " zones");
            }
            catch (Exception ex)
            {
                lock (Gate) _visitedDirty = true;
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("visited ground could not be saved: " + ex.Message); }
            }
        }

        internal static void LoadVisited(string folder)
        {
            string path = System.IO.Path.Combine(folder, "visited.bin");
            if (!System.IO.File.Exists(path)) return;
            try
            {
                var file = System.IO.File.ReadAllBytes(path);
                if (file.Length < 5 || file[0] != 'N' || file[1] != 'V' || file[2] != 'V' || file[3] != 'G' || file[4] != VisitedVersion) return;
                var raw = new System.IO.MemoryStream();
                using (var deflate = new System.IO.Compression.DeflateStream(new System.IO.MemoryStream(file, 5, file.Length - 5), System.IO.Compression.CompressionMode.Decompress)) deflate.CopyTo(raw);
                var bytes = raw.ToArray();
                int o = 0, count = BitConverter.ToInt32(bytes, o); o += 4;
                if (bytes.Length != 4 + count * (8 + Points * Points * 2)) return;
                lock (Gate)
                    for (int n = 0; n < count; n++)
                    {
                        int zx = BitConverter.ToInt32(bytes, o), zz = BitConverter.ToInt32(bytes, o + 4); o += 8;
                        var heights = new short[Points * Points];
                        Buffer.BlockCopy(bytes, o, heights, 0, heights.Length * 2); o += heights.Length * 2;
                        if (!Visited.ContainsKey(Key(zx, zz))) Visited[Key(zx, zz)] = heights;
                    }
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; Plugin.Log.LogWarning("visited ground could not be read, it is read again where you go: " + ex.Message); }
            }
        }

        private static void Write(byte[] to, ref int o, int value)
        {
            to[o++] = (byte)value; to[o++] = (byte)(value >> 8); to[o++] = (byte)(value >> 16); to[o++] = (byte)(value >> 24);
        }

        private static List<KeyValuePair<long, Zone>> ParseAll(List<KeyValuePair<long, byte[]>> work)
        {
            var result = new List<KeyValuePair<long, Zone>>(work.Count);
            foreach (var item in work)
            {
                try { result.Add(new KeyValuePair<long, Zone>(item.Key, Parse(Utils.Decompress(item.Value)))); }
                catch (Exception) { result.Add(new KeyValuePair<long, Zone>(item.Key, null)); }
            }
            return result;
        }

        // the record as TerrainComp.Save writes it: version, operations, last point, last radius, the height flags (each set one with its level
        // and smooth delta), the paint flags (each set one with its color). the game's ground is base + clamp(level + smooth, -8, 8)
        internal static Zone Parse(byte[] raw)
        {
            var package = new ZPackage(raw);
            package.ReadInt(); package.ReadInt(); package.ReadVector3(); package.ReadSingle();
            int heights = package.ReadInt();
            if (heights != Vertices * Vertices) return null;
            var zone = new Zone();
            for (int k = 0; k < heights; k++)
            {
                if (!package.ReadBool()) continue;
                float delta = package.ReadSingle() + package.ReadSingle();
                int x = k % Vertices, z = k / Vertices;
                if ((x & 1) != 0 || (z & 1) != 0 || delta == 0f) continue;
                zone.Height[(z >> 1) * Points + (x >> 1)] = FineTerrain.Quantize(Mathf.Clamp(delta, -MaxDelta, MaxDelta));
                zone.Changed++;
            }
            int paints = package.ReadInt();
            if (paints != Vertices * Vertices) return zone;
            for (int k = 0; k < paints; k++)
            {
                if (!package.ReadBool()) continue;
                float r = package.ReadSingle(), g = package.ReadSingle(), b = package.ReadSingle(), a = package.ReadSingle();
                int x = k % Vertices, z = k / Vertices;
                if ((x & 1) != 0 || (z & 1) != 0) continue;
                // the game's paint colors: dirt red, cultivated green, paved blue, low alpha where vegetation was cleared, deep snow white
                // (Heightmap.m_paintMaskDeepSnow, the deep north's own snow, not a path)
                bool snow = r > 0.5f && g > 0.5f && b > 0.5f;
                byte kind = snow ? PaintNone : b > 0.5f ? PaintPaved : r > 0.5f ? PaintDirt : g > 0.5f ? PaintCultivated : a < 0.5f ? PaintCleared : PaintNone;
                if (kind == PaintNone) continue;
                zone.Paint[(z >> 1) * Points + (x >> 1)] = kind;
                zone.Painted++;
            }
            return zone;
        }

        private static void Merge(Task<List<KeyValuePair<long, Zone>>> task)
        {
            int read = 0, changed = 0, painted = 0;
            var kinds = new int[5];
            lock (Gate)
                foreach (var item in task.Result)
                {
                    if (item.Value == null) { _failed++; continue; }
                    Zones[item.Key] = item.Value;
                    Checked.Remove(item.Key);
                    read++; changed += item.Value.Changed; painted += item.Value.Painted;
                    if (Plugin.LogCounters.Value) foreach (byte p in item.Value.Paint) kinds[p]++;
                }
            Version++;
            if (Plugin.LogCounters.Value)
                Plugin.Log.LogInfo("terrain edits pass " + _passes + ": " + read + " zones read again (" + changed + " raised or lowered points, " + painted + " painted points at 2 m: dirt "
                    + kinds[PaintDirt] + ", cultivated " + kinds[PaintCultivated] + ", paved " + kinds[PaintPaved] + ", cleared " + kinds[PaintCleared] + "), "
                    + _compilers + " edited zones known, " + Zones.Count + " kept, " + _failed + " unreadable, walk " + _scanMilliseconds.ToString("F1") + " ms over "
                    + (_passClock != null ? _passClock.Elapsed.TotalSeconds.ToString("F1") : "?") + " s");
        }

        // the edit at a fine point (x and z even meters), false when the zone has none
        internal static bool At(int x, int z, out float delta, out byte paint)
        {
            delta = 0f; paint = PaintNone;
            int zx = FineBuilder.FloorDiv(x + 32, 64), zz = FineBuilder.FloorDiv(z + 32, 64);
            Zone zone;
            lock (Gate) if (!ZoneLocked(Key(zx, zz), out zone)) return false;
            int k = ((z + 32 - zz * 64) >> 1) * Points + ((x + 32 - zx * 64) >> 1);
            delta = zone.Height[k] * FineTerrain.HeightStep;
            paint = zone.Paint[k];
            return true;
        }

        // proof in game (LogCounters): for a loaded zone near you, the fine ground plus the edits against the game's real ground at every
        // fine point. once per zone, a few zones a session
        private static void CheckLoaded()
        {
            if (Checked.Count >= ChecksPerSession || Player.m_localPlayer == null || !LocationShaping.Ready) return;
            var p = Player.m_localPlayer.transform.position;
            int zx = Mathf.FloorToInt((p.x + 32f) / 64f), zz = Mathf.FloorToInt((p.z + 32f) / 64f);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    long key = Key(zx + dx, zz + dz);
                    if (Checked.Contains(key)) continue;
                    if (Check(zx + dx, zz + dz)) { Checked.Add(key); return; }
                }
        }

        private static bool Check(int zx, int zz)
        {
            int x0 = zx * 64 - 32, z0 = zz * 64 - 32, points = 0, off = 0, edited = 0;
            float worst = 0f, worstX = 0f, worstZ = 0f;
            for (int j = 0; j < Points; j++)
                for (int i = 0; i < Points; i++)
                {
                    int x = x0 + i * 2, z = z0 + j * 2;
                    if (!FineTerrain.TryBase(x, z, out float ground)) return false;
                    if (!Heightmap.GetHeight(new Vector3(x, 0f, z), out float real)) return false;
                    ground = LocationShaping.Shape(x, z, ground);
                    if (At(x, z, out float delta, out _)) { ground += delta; if (delta != 0f) edited++; }
                    float d = Math.Abs(real - ground);
                    if (d > 0.05f) off++;
                    if (d > worst) { worst = d; worstX = x; worstZ = z; }
                    points++;
                }
            Plugin.Log.LogInfo("fine terrain check at zone " + zx + ", " + zz + ": " + points + " points against the loaded ground, " + edited + " edited, worst "
                + (worst * 100f).ToString("F1") + " cm at " + worstX.ToString("F0") + ", " + worstZ.ToString("F0") + ", " + off + " off by more than 5 cm (generated ground, location shaping and edits)"
                + (VisitedAt(x0, z0, out _) ? ", the visited ground is kept for this zone" : ""));
            return true;
        }
    }
}
