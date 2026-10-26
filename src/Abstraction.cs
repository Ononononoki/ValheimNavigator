using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ValheimNavigator
{
    // Dynamic Terrain Abstraction (Sturtevant, Sigurdson, Taylor, Gibson, AIIDE 2019, doi:10.1609/aiide.v15i1.5228) used as a corridor.
    // the world is cut into sectors of 8 by 8 cells of 24 m, cells of one terrain type that connect inside a sector form a region. a long route
    // is first planned over regions (thousands instead of hundreds of thousands of cells), then the normal search runs only inside those regions
    // widened by 4 cells. RouteBench audit on the user's world (22 Oct): routes at most 7.2 percent longer than exact A*, 3.5 to 19 times less work.
    // it is built once per world on a background thread from the height, biome and mask arrays the sharp view keeps
    internal static class Abstraction
    {
        internal const float Cell = 24f, Origin = -10512f, Sample = 6f;
        internal const int SectorCells = 8, Margin = 4;
        internal static readonly int N = (int)(-2f * Origin / Cell) + 1;
        private static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 }, Dz = { 0, 1, 0, -1, 1, 1, -1, -1 };

        private sealed class Region { internal int Type, Rep, Count; internal double SumX, SumZ; internal readonly Dictionary<int, float> Links = new Dictionary<int, float>(); }

        private static float[] _height;
        private static byte[] _biome;
        private static Color32[] _mask;
        private static int _side;
        private static float _pixel, _water, _maxSlope;
        private static int[] _regionOf;
        private static List<Region> _regions;
        private static List<int>[] _cellsOf;
        private static volatile int _state; // 0 none, 1 building, 2 ready, 3 failed
        private static WorldGenerator _world;
        internal static bool Ready => _state == 2;
        internal static string Problem;

        // starts the background build when the map data is in memory, again when the world or the slide limit changed
        internal static void Tick(Minimap map)
        {
            if (map == null || WorldGenerator.instance == null) return;
            float maxSlope = Mathf.Tan(Mathf.Max(Settings.MaxSlope.Value, Settings.JumpMeters.Value > 0f ? Settings.JumpSlope.Value : 0f) * Mathf.Deg2Rad);
            if (_state != 1 && (_world != WorldGenerator.instance || Math.Abs(maxSlope - _maxSlope) > 0.0001f)) { _state = 0; _footMarks = _sailMarks = null; }
            // landmark tables built with other speeds are no lower bound, they are built again
            if (_state == 2 && !_landmarksBuilding && !_landmarksFailed && Time.unscaledTime >= _nextKeyCheck && (_nextKeyCheck = Time.unscaledTime + 2f) > 0f && _landmarkKey != LandmarkKey())
            {
                _footMarks = _sailMarks = null; _landmarksBuilding = true;
                new Thread(BuildLandmarks) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator route landmarks" }.Start();
            }
            if (_state != 0) return;
            if (!DetailOverlay.EnsureCache(map)) return;
            _world = WorldGenerator.instance; _maxSlope = maxSlope;
            _height = DetailOverlay.HeightData; _biome = DetailOverlay.BiomeData; _mask = DetailOverlay.MaskData; _side = DetailOverlay.DataSide;
            _pixel = map.m_pixelSize; _water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            // the cache folder and the world's fingerprint are read here, on the game's thread
            try { _cacheFolder = TerrainOverlay.CacheFolder(); _worldKey = TerrainOverlay.World() + "_" + _pixel.ToString("R") + "_" + _water.ToString("R"); }
            catch (Exception ex) { _cacheFolder = null; Plugin.Log.LogWarning("route tables are not kept on disk: " + ex.Message); }
            _state = 1; _landmarksBuilding = true;
            new Thread(Build) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator route abstraction" }.Start();
        }

        private static float X(int c) { return Origin + (c % N) * Cell; }
        private static float Z(int c) { return Origin + (c / N) * Cell; }
        internal static int CellAt(float x, float z)
        {
            int ix = (int)Math.Round((x - Origin) / Cell), iz = (int)Math.Round((z - Origin) / Cell);
            return ix < 0 || iz < 0 || ix >= N || iz >= N ? -1 : iz * N + ix;
        }

        private static int Neighbour(int c, int d)
        {
            int x = c % N + Dx[d], z = c / N + Dz[d];
            return x < 0 || z < 0 || x >= N || z >= N ? -1 : z * N + x;
        }

        // bilinear height of the 12 m texture and its nearest biome texel, the same reading as RouteBench
        private static float Height(float x, float z, out int texel)
        {
            float tx = x / _pixel + _side / 2f, ty = z / _pixel + _side / 2f;
            int x0 = Math.Max(0, Math.Min(_side - 2, (int)Math.Floor(tx))), y0 = Math.Max(0, Math.Min(_side - 2, (int)Math.Floor(ty)));
            float fx = Math.Max(0f, Math.Min(1f, tx - x0)), fy = Math.Max(0f, Math.Min(1f, ty - y0));
            int i = y0 * _side + x0;
            texel = Math.Max(0, Math.Min(_side * _side - 1, (int)(ty + 0.5f) * _side + (int)(tx + 0.5f)));
            return (_height[i] * (1 - fx) + _height[i + 1] * fx) * (1 - fy) + (_height[i + _side] * (1 - fx) + _height[i + _side + 1] * fx) * fy;
        }

        private static bool Lava(int texel) { return _biome[texel] == 5 && _mask[texel].b > 25; }

        // the sharp view's biome palette index to a progress rank, white texels are mountains, deep north or open ocean
        private static int Rank(int texel, float x, float z, float h)
        {
            switch (_biome[texel])
            {
                case 0: return 1;
                case 1: return 2;
                case 2: return 3;
                case 3: return 5;
                case 4: return 6;
                case 5: return 7;
                case 6: return h < _water ? 0 : x * x + (z + 4000f) * (z + 4000f) > 144e6f ? 8 : 4;
                default: return 0;
            }
        }

        // terrain types: rank 0 to 8 on land, 9 wading water, 10 swimming water, 11 boiling Ashlands water, -1 lava or outside
        private static int TypeOf(int c)
        {
            float x = X(c), z = Z(c);
            if (x * x + z * z > 10500f * 10500f) return -1;
            float h = Height(x, z, out int texel);
            // water first: the mask's blue is the boiling sea on water and lava only on land, as in the route's own reading
            if (h < _water) return Lava(texel) ? 11 : _water - h >= 2f ? 10 : 9;
            if (Lava(texel)) return -1;
            return Rank(texel, x, z, h);
        }

        // the same walking check as the route: lava blocks, ground steeper than the slide limit blocks, fully wet steps are fine
        private static bool Passable(int a, int b)
        {
            float x0 = X(a), z0 = Z(a), x1 = X(b), z1 = Z(b), len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            int steps = (int)Math.Ceiling(len / Sample);
            float step = len / steps, before = Height(x0, z0, out int texel);
            if (before >= _water && Lava(texel)) return false;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps, next = Height(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t, out texel);
                bool wb = before < _water, wn = next < _water;
                if (!wn && Lava(texel)) return false;
                if (!(wb && wn) && Math.Abs((wn ? _water : next) - (wb ? _water : before)) / step > _maxSlope) return false;
                before = next;
            }
            return true;
        }

        private static void Build()
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                if (LoadRegions(clock)) { BuildLandmarks(); return; }
                var type = new int[N * N];
                // each cell's type reads only the map arrays, so cells are shared out over a few threads
                System.Threading.Tasks.Parallel.For(0, N * N, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = DetailOverlay.Threads }, c => type[c] = TypeOf(c));
                var regionOf = new int[N * N];
                for (int c = 0; c < regionOf.Length; c++) regionOf[c] = -1;
                var regions = new List<Region>();
                var queue = new Queue<int>();
                for (int c = 0; c < N * N; c++)
                {
                    if (type[c] < 0 || regionOf[c] >= 0) continue;
                    int sx = (c % N) / SectorCells, sz = (c / N) / SectorCells, id = regions.Count;
                    var region = new Region { Type = type[c] };
                    regions.Add(region);
                    regionOf[c] = id; queue.Enqueue(c);
                    while (queue.Count > 0)
                    {
                        int a = queue.Dequeue();
                        region.Count++; region.SumX += X(a); region.SumZ += Z(a);
                        for (int d = 0; d < 8; d++)
                        {
                            int j = Neighbour(a, d);
                            if (j < 0 || regionOf[j] >= 0 || type[j] != region.Type || (j % N) / SectorCells != sx || (j / N) / SectorCells != sz || !Passable(a, j)) continue;
                            regionOf[j] = id; queue.Enqueue(j);
                        }
                    }
                }
                var best = new float[regions.Count];
                for (int r = 0; r < best.Length; r++) best[r] = float.MaxValue;
                var cellsOf = new List<int>[regions.Count];
                for (int r = 0; r < cellsOf.Length; r++) cellsOf[r] = new List<int>();
                for (int c = 0; c < N * N; c++)
                {
                    int r = regionOf[c];
                    if (r < 0) continue;
                    cellsOf[r].Add(c);
                    var region = regions[r];
                    float dx = X(c) - (float)(region.SumX / region.Count), dz = Z(c) - (float)(region.SumZ / region.Count), d = dx * dx + dz * dz;
                    if (d < best[r]) { best[r] = d; region.Rep = c; }
                }
                for (int c = 0; c < N * N; c++)
                {
                    int r = regionOf[c];
                    if (r < 0) continue;
                    for (int d = 0; d < 8; d++)
                    {
                        int j = Neighbour(c, d);
                        if (j < 0) continue;
                        int q = regionOf[j];
                        if (q < 0 || q == r || regions[r].Links.ContainsKey(q) || !Passable(c, j)) continue;
                        float length = Distance(regions[r].Rep, regions[q].Rep);
                        regions[r].Links[q] = length; regions[q].Links[r] = length;
                    }
                }
                _regionOf = regionOf; _regions = regions; _cellsOf = cellsOf;
                _state = 2;
                Problem = null;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route abstraction ready, " + regions.Count + " regions, " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms on a background thread");
                SaveRegions();
                BuildLandmarks();
            }
            catch (Exception ex)
            {
                Problem = ex.Message; _state = 3; _landmarksBuilding = false;
                Plugin.Log.LogWarning("route abstraction failed: " + ex);
            }
        }

        // landmark tables (ALT, Goldberg and Harrelson 2005): lower-bound travel times from LandmarkCount cells to every cell. the 12 m map
        // texture is not the exact terrain, so no cliff or lava is trusted as a wall: land costs its flat walking time, water its wading time,
        // and swimming time only where the texture is 1 m deeper than the wade depth. the sailing tables may also sail any water at the fastest
        // known ship under the strongest sail bias. RouteBench without walls, 4 landmarks: weight 1.2 worst 1.9 percent with half the work of
        // weight 1.5 (22 Oct). times are whole seconds rounded down, 16 bit, about 12 MB for both sets
        internal const int LandmarkCount = 4;
        private const float MinSailBias = 0.6f, WadePenalty = 1.3f, SnapMeters = 17f;
        private static ushort[][] _footMarks, _sailMarks;
        // the slowest of land 0, wading 1, swimming 2 around each cell, for the slack of a point's snap to its cell
        private static byte[] _rateClass;
        private static float _walkRate, _wadeRate, _swimRate, _sailRate;
        private static volatile bool _landmarksBuilding;
        private static string _landmarkKey;
        // the speeds behind the tables are compared every 2 s, not every frame
        private static float _nextKeyCheck;
        internal static float LandmarkSailSpeed;
        internal static bool LandmarksReady => _footMarks != null && _sailMarks != null;
        // for the route log: on, building or off (test 7: the tables never came on in game and nothing said why)
        internal static string LandmarkState => LandmarksReady ? "on" : _landmarksBuilding ? "building" : _landmarksFailed ? "failed" : "off";
        private static bool _landmarksFailed;
        private static void Stage(System.Diagnostics.Stopwatch clock, string what)
        {
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route landmarks: " + what + ", " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
        }

        private static float FastestShip()
        {
            float fastest = 9.5f;
            foreach (var entry in (Settings.ShipSpeedList.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var speeds = entry.Split('=');
                if (speeds.Length != 2) continue;
                foreach (var part in speeds[1].Split('/'))
                    if (float.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) fastest = Math.Max(fastest, v);
            }
            return Math.Max(fastest, Settings.SailSpeed.Value);
        }

        private static string LandmarkKey()
        {
            return Settings.WalkSpeed.Value + "|" + Settings.WadeDepth.Value + "|" + Settings.SwimSpeed.Value + "|" + Settings.SwimRisk.Value + "|" + FastestShip();
        }

        private static void BuildLandmarks()
        {
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var world = _world;
                string key = LandmarkKey();
                Stage(clock, "building");
                float wade = Settings.WadeDepth.Value;
                _walkRate = 1f / Settings.WalkSpeed.Value; _wadeRate = WadePenalty / Settings.WalkSpeed.Value; _swimRate = Settings.SwimRisk.Value / Settings.SwimSpeed.Value;
                float sailSpeed = FastestShip() / MinSailBias;
                _sailRate = 1f / sailSpeed;
                if (LoadLandmarks(key, world, clock)) return;
                // lower-bound edge times, each edge once: directions 0, 1, 4 and 5, the other four are their reverses
                int[] half = { 0, 1, 4, 5 };
                var foot = new float[N * N * 4]; var sail = new float[N * N * 4];
                var rate = new byte[N * N];
                // each cell writes only its own edges, so cells are shared out over a few threads (3.2 s on one thread in test 15)
                System.Threading.Tasks.Parallel.For(0, N * N, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = DetailOverlay.Threads }, c =>
                {
                    float x0 = X(c), z0 = Z(c);
                    float h0 = Height(x0, z0, out _), d0 = _water - h0;
                    rate[c] = (byte)(d0 >= wade + 1f ? 2 : d0 >= 1f ? 1 : 0);
                    for (int k = 0; k < 4; k++)
                    {
                        int d = half[k], j = Neighbour(c, d);
                        float x1 = X(Math.Max(0, j)), z1 = Z(Math.Max(0, j));
                        if (j < 0 || x0 * x0 + z0 * z0 > 10500f * 10500f || x1 * x1 + z1 * z1 > 10500f * 10500f) { foot[c * 4 + k] = sail[c * 4 + k] = float.PositiveInfinity; continue; }
                        float len = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
                        int steps = (int)Math.Ceiling(len / Sample);
                        float step = len / steps, before = h0, f = 0f, s = 0f;
                        for (int i = 1; i <= steps; i++)
                        {
                            float t = (float)i / steps, next = Height(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t, out _);
                            float depth = _water - Math.Max(before, next);
                            float r = depth >= wade + 1f ? _swimRate : depth >= 1f ? _wadeRate : _walkRate;
                            f += step * r;
                            // a meter of margin, so a coast the texture draws a little high still counts as sailable
                            s += before < _water + 1f && next < _water + 1f ? step * Math.Min(r, _sailRate) : step * r;
                            before = next;
                        }
                        foot[c * 4 + k] = f; sail[c * 4 + k] = s;
                    }
                });
                var slow = new byte[N * N];
                for (int c = 0; c < N * N; c++)
                {
                    byte m = rate[c];
                    for (int d = 0; d < 8; d++) { int j = Neighbour(c, d); if (j >= 0 && rate[j] > m) m = rate[j]; }
                    slow[c] = m;
                }
                Stage(clock, "edges ready");
                // the foot and sailing sets do not depend on each other and are placed side by side. inside a set each landmark waits for the
                // ones before it (farthest-point placement)
                ushort[][] footMarks = null, sailMarks = null;
                System.Threading.Tasks.Parallel.Invoke(() => footMarks = Place(foot), () => sailMarks = Place(sail));
                foot = null;
                Stage(clock, "foot and sailing tables ready");
                // a world left while building keeps no tables of the old one
                if (world != _world) { Stage(clock, "world changed, tables dropped"); return; }
                _rateClass = slow; LandmarkSailSpeed = sailSpeed; _landmarkKey = key;
                _footMarks = footMarks; _sailMarks = sailMarks;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route landmarks ready, " + LandmarkCount + " on foot and " + LandmarkCount + " sailing, " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms on a background thread");
                SaveLandmarks(key);
            }
            catch (Exception ex)
            {
                _footMarks = _sailMarks = null;
                Plugin.Log.LogWarning("route landmarks failed, not tried again this session: " + ex);
                _landmarksFailed = true;
            }
            finally { _landmarksBuilding = false; }
        }

        // the regions and the landmark tables are kept per world in the game's temporary folder (about 6 MB and 13 MB), so a world seen
        // before has them at once. the file holds its full key and is read only when the key, every length and every index fit, else the
        // tables are built as before. the newest few files of each kind are kept
        private static string _cacheFolder, _worldKey;
        private const int KeptFiles = 4;
        private const string RegionsFormat = "VNA1", LandmarksFormat = "VNL1";
        private static bool _cacheWarned;
        // regions and landmark sets read from disk this session, for the checks and the log
        internal static int CacheReads;

        private static string CacheFile(string kind, string key)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char ch in key) h = (h ^ ch) * 16777619;
                return System.IO.Path.Combine(_cacheFolder, kind + "_" + h.ToString("x8") + ".bin");
            }
        }

        private static string RegionsKey() { return RegionsFormat + "|" + _worldKey + "|" + _maxSlope.ToString("R") + "|" + N; }

        private static void CacheWarning(string what, Exception ex)
        {
            if (_cacheWarned) return;
            _cacheWarned = true;
            Plugin.Log.LogWarning("route tables cache, " + what + ": " + ex.Message);
        }

        // written next to the final name first, so a game closed mid-write leaves no half file under it
        private static void Write(string path, Action<System.IO.BinaryWriter> write)
        {
            string temp = path + ".tmp";
            using (var stream = new System.IO.BinaryWriter(System.IO.File.Create(temp))) write(stream);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(temp, path);
            var files = new System.IO.DirectoryInfo(_cacheFolder).GetFiles(System.IO.Path.GetFileName(path).Split('_')[0] + "_*.bin");
            if (files.Length <= KeptFiles) return;
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeptFiles; i < files.Length; i++) files[i].Delete();
        }

        private static void SaveRegions()
        {
            if (_cacheFolder == null || _worldKey == null) return;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string key = RegionsKey();
                Write(CacheFile("regions", key), w =>
                {
                    w.Write(key); w.Write(N); w.Write(_regions.Count);
                    foreach (int r in _regionOf) w.Write(r);
                    foreach (var region in _regions)
                    {
                        w.Write(region.Type); w.Write(region.Rep); w.Write(region.Count); w.Write(region.SumX); w.Write(region.SumZ); w.Write(region.Links.Count);
                        foreach (var link in region.Links) { w.Write(link.Key); w.Write(link.Value); }
                    }
                });
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route abstraction kept on disk, " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
            }
            catch (Exception ex) { CacheWarning("the regions were not written", ex); }
        }

        private static bool LoadRegions(System.Diagnostics.Stopwatch clock)
        {
            if (_cacheFolder == null || _worldKey == null) return false;
            string key = RegionsKey(), path = CacheFile("regions", key);
            if (!System.IO.File.Exists(path)) return false;
            try
            {
                int[] regionOf; List<Region> regions;
                using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
                {
                    if (r.ReadString() != key || r.ReadInt32() != N) return false;
                    int count = r.ReadInt32();
                    if (count < 0 || count > N * N) return false;
                    regionOf = new int[N * N];
                    for (int c = 0; c < regionOf.Length; c++) { int v = r.ReadInt32(); if (v < -1 || v >= count) return false; regionOf[c] = v; }
                    regions = new List<Region>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var region = new Region { Type = r.ReadInt32(), Rep = r.ReadInt32(), Count = r.ReadInt32(), SumX = r.ReadDouble(), SumZ = r.ReadDouble() };
                        if (region.Rep < 0 || region.Rep >= N * N) return false;
                        int links = r.ReadInt32();
                        for (int k = 0; k < links; k++) { int q = r.ReadInt32(); float length = r.ReadSingle(); if (q < 0 || q >= count) return false; region.Links[q] = length; }
                        regions.Add(region);
                    }
                }
                // the cells of each region in the order the build gives them
                var cellsOf = new List<int>[regions.Count];
                for (int i = 0; i < cellsOf.Length; i++) cellsOf[i] = new List<int>();
                for (int c = 0; c < regionOf.Length; c++) if (regionOf[c] >= 0) cellsOf[regionOf[c]].Add(c);
                _regionOf = regionOf; _regions = regions; _cellsOf = cellsOf;
                _state = 2; Problem = null; CacheReads++;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route abstraction read from the disk cache, " + regions.Count + " regions, " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
                return true;
            }
            catch (Exception ex) { CacheWarning("the regions file could not be read, they are built again", ex); return false; }
        }

        private static string LandmarksKey(string key) { return LandmarksFormat + "|" + _worldKey + "|" + key + "|" + N + "|" + LandmarkCount + "|" + MinSailBias.ToString("R") + "|" + WadePenalty.ToString("R"); }

        private static void SaveLandmarks(string key)
        {
            if (_cacheFolder == null || _worldKey == null) return;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string full = LandmarksKey(key);
                var foot = _footMarks; var sail = _sailMarks; var slow = _rateClass;
                Write(CacheFile("landmarks", full), w =>
                {
                    w.Write(full); w.Write(N); w.Write(LandmarkSailSpeed); w.Write(foot.Length); w.Write(sail.Length);
                    w.Write(slow);
                    var bytes = new byte[N * N * 2];
                    foreach (var mark in foot) { Buffer.BlockCopy(mark, 0, bytes, 0, bytes.Length); w.Write(bytes); }
                    foreach (var mark in sail) { Buffer.BlockCopy(mark, 0, bytes, 0, bytes.Length); w.Write(bytes); }
                });
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route landmarks kept on disk, " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
            }
            catch (Exception ex) { CacheWarning("the landmarks were not written", ex); }
        }

        private static bool LoadLandmarks(string key, WorldGenerator world, System.Diagnostics.Stopwatch clock)
        {
            if (_cacheFolder == null || _worldKey == null) return false;
            string full = LandmarksKey(key), path = CacheFile("landmarks", full);
            if (!System.IO.File.Exists(path)) return false;
            try
            {
                float sailSpeed; byte[] slow; ushort[][] foot, sail;
                using (var r = new System.IO.BinaryReader(System.IO.File.OpenRead(path)))
                {
                    if (r.ReadString() != full || r.ReadInt32() != N) return false;
                    sailSpeed = r.ReadSingle();
                    int footCount = r.ReadInt32(), sailCount = r.ReadInt32();
                    if (footCount < 1 || footCount > LandmarkCount || sailCount < 1 || sailCount > LandmarkCount) return false;
                    slow = r.ReadBytes(N * N);
                    if (slow.Length != N * N) return false;
                    ushort[] Mark()
                    {
                        var bytes = r.ReadBytes(N * N * 2);
                        if (bytes.Length != N * N * 2) throw new System.IO.EndOfStreamException("short landmark table");
                        var mark = new ushort[N * N];
                        Buffer.BlockCopy(bytes, 0, mark, 0, bytes.Length);
                        return mark;
                    }
                    foot = new ushort[footCount][]; for (int i = 0; i < footCount; i++) foot[i] = Mark();
                    sail = new ushort[sailCount][]; for (int i = 0; i < sailCount; i++) sail[i] = Mark();
                }
                if (world != _world) return false;
                _rateClass = slow; LandmarkSailSpeed = sailSpeed; _landmarkKey = key;
                _footMarks = foot; _sailMarks = sail; CacheReads++;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route landmarks read from the disk cache, " + clock.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
                return true;
            }
            catch (Exception ex) { CacheWarning("the landmarks file could not be read, they are built again", ex); return false; }
        }

        // the edge time from cell c in direction d, reading the stored half of the directions
        private static float EdgeTime(float[] edges, int c, int d, int j)
        {
            switch (d)
            {
                case 0: return edges[c * 4];
                case 1: return edges[c * 4 + 1];
                case 4: return edges[c * 4 + 2];
                case 5: return edges[c * 4 + 3];
                case 2: return edges[j * 4];
                case 3: return edges[j * 4 + 1];
                case 6: return edges[j * 4 + 2];
                default: return edges[j * 4 + 3];
            }
        }

        // the landmark search's queue: each cell at most once, moved up when its time drops (decrease-key), so its size is fixed. the lazy queue
        // of 0.7.10 kept every improvement and ran out of memory in the game (test 8). each set has its own queue, the two sets run side by side
        private static float[] Dijkstra(float[] edges, int source, int[] queue, int[] slot)
        {
            var dist = new float[N * N];
            for (int i = 0; i < dist.Length; i++) dist[i] = float.PositiveInfinity;
            for (int i = 0; i < slot.Length; i++) slot[i] = -1;
            int size = 0;
            void Up(int i)
            {
                int c = queue[i]; float f = dist[c];
                while (i > 0) { int up = (i - 1) / 2; if (dist[queue[up]] <= f) break; queue[i] = queue[up]; slot[queue[i]] = i; i = up; }
                queue[i] = c; slot[c] = i;
            }
            dist[source] = 0f; queue[0] = source; slot[source] = 0; size = 1;
            while (size > 0)
            {
                int c = queue[0];
                slot[c] = -2;
                size--;
                if (size > 0)
                {
                    int last = queue[size]; float lf = dist[last]; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1;
                        if (l >= size) break;
                        if (l + 1 < size && dist[queue[l + 1]] < dist[queue[l]]) l++;
                        if (lf <= dist[queue[l]]) break;
                        queue[i] = queue[l]; slot[queue[i]] = i; i = l;
                    }
                    queue[i] = last; slot[last] = i;
                }
                float f = dist[c];
                for (int d = 0; d < 8; d++)
                {
                    int j = Neighbour(c, d);
                    if (j < 0 || slot[j] == -2) continue;
                    float w = EdgeTime(edges, c, d, j);
                    if (float.IsInfinity(w) || f + w >= dist[j]) continue;
                    dist[j] = f + w;
                    if (slot[j] < 0) { queue[size] = j; slot[j] = size; size++; }
                    Up(slot[j]);
                }
            }
            return dist;
        }

        // farthest-point placement from the world's center: each landmark is the cell farthest from the center and the landmarks so far
        private static ushort[][] Place(float[] edges)
        {
            var marks = new ushort[LandmarkCount][];
            int[] queue = new int[N * N], slot = new int[N * N];
            var nearest = Dijkstra(edges, CellAt(0f, 0f), queue, slot);
            for (int k = 0; k < LandmarkCount; k++)
            {
                int far = -1; float best = 0f;
                for (int c = 0; c < nearest.Length; c++) if (!float.IsInfinity(nearest[c]) && nearest[c] > best) { best = nearest[c]; far = c; }
                if (far < 0) { Array.Resize(ref marks, k); break; }
                var dist = Dijkstra(edges, far, queue, slot);
                var mark = new ushort[N * N];
                for (int c = 0; c < N * N; c++)
                {
                    mark[c] = float.IsInfinity(dist[c]) ? ushort.MaxValue : (ushort)Math.Min(ushort.MaxValue - 1, Math.Floor(dist[c]));
                    nearest[c] = Math.Min(nearest[c], dist[c]);
                }
                marks[k] = mark;
            }
            return marks;
        }

        // a lower bound on the time from one point to another: the largest landmark difference, less a second for the rounding and the snap
        // of both points to their cells at the slowest rate around them. 0 when the tables are not ready
        internal static float LandmarkBound(float fx, float fz, float tx, float tz, bool sail)
        {
            var marks = sail ? _sailMarks : _footMarks;
            var slow = _rateClass;
            if (marks == null || slow == null) return 0f;
            int a = CellAt(fx, fz), b = CellAt(tx, tz);
            if (a < 0 || b < 0) return 0f;
            int h = 0;
            foreach (var mark in marks)
            {
                int x = mark[a], y = mark[b];
                if (x == ushort.MaxValue || y == ushort.MaxValue) continue;
                h = Math.Max(h, Math.Abs(x - y));
            }
            return Math.Max(0f, h - 1f - SnapMeters * (SlackRate(slow[a]) + SlackRate(slow[b])));
        }

        private static float SlackRate(byte rate) { return rate == 2 ? _swimRate : rate == 1 ? _wadeRate : _walkRate; }

        private static float Distance(int a, int b) { float dx = X(b) - X(a), dz = Z(b) - Z(a); return (float)Math.Sqrt(dx * dx + dz * dz); }

        // seconds per meter of a terrain type with the route's options, sailing deep water when sail is set
        private static float Rate(NavOptions o, int type, bool sail)
        {
            if (type == 11) return sail ? (o.HeatProof ? o.SailBias / o.SailSpeed : float.PositiveInfinity) : o.SwimRisk / o.SwimSpeed * o.BoilingPenalty;
            if (type == 10) return sail ? o.SailBias / o.SailSpeed : o.SwimRisk / o.SwimSpeed;
            if (type == 9) return o.WadePenalty / o.WalkSpeed;
            if (type > o.DangerRank) return o.DangerBlock ? float.PositiveInfinity : o.DangerPenalty / o.WalkSpeed;
            return 1f / o.WalkSpeed;
        }

        private static bool Wet(int type) { return type >= 9; }
        private static bool Sailable(NavOptions o, int type) { return type == 9 || type == 10 || type == 11 && o.HeatProof; }

        // the abstract route between two cells over (region, mode) states, null when none. the modes are the route search's: 0 on foot before
        // any boat, 1 sailing, 2 on foot after landing. the boat is boarded in the ship's region or built where land meets sailable water, and
        // another one only with MoreBoats. a sea plan that sails, walks over land and sails on with the same boat let the corridor miss the
        // real boat route (test 6, raft route 78 percent slower than exact). without boats only mode 0 is searched. portal pairs add links
        // between their regions on foot
        // free plans the old way, sailing and walking mixed freely in one mode. it has no boat rule but finds the coasts the fine search can use
        // where the rule-bound plan picks one it cannot (home to Infested Mine with a built boat, 2.1 times exact with the rule-bound plan alone)
        private static List<int> Path(NavOptions o, int from, int to, bool boats, Dictionary<int, List<int>> portals, byte startMode = 0, bool free = false)
        {
            int start = from >= 0 ? _regionOf[from] : -1, goal = to >= 0 ? _regionOf[to] : -1;
            if (start < 0 || goal < 0) return null;
            int ship = -1;
            if (boats && o.HasShip) { int c = CellAt(o.ShipX, o.ShipZ); ship = c >= 0 ? _regionOf[c] : -1; }
            float fastest = boats ? Math.Max(o.WalkSpeed, o.Fastest) : o.WalkSpeed;
            // arrays stamped per search and a binary heap of (priority, state). the first version's dictionaries and sorted set cost 3 to 7 ms
            // of the game's frame per new route (RouteBench audit, 22 Oct)
            int count = _regions.Count * 3;
            if (_g == null || _g.Length < count) { _g = new float[count]; _parent = new int[count]; _seen = new int[count]; _closed = new int[count]; }
            int stamp = ++_stamp;
            _heap.Clear();
            float gx = X(to), gz = Z(to);
            int first = start * 3 + (boats && !free ? startMode : 0);
            _g[first] = 0f; _parent[first] = -1; _seen[first] = stamp;
            Push(0f, first);
            int reached = -1;
            while (_heap.Count > 0)
            {
                var (f, s) = Pop();
                if (_closed[s] == stamp) continue;
                _closed[s] = stamp;
                int r = s / 3, m = s % 3, typeR = _regions[r].Type;
                if (r == goal && m != 1) { reached = s; break; }
                foreach (var link in _regions[r].Links)
                {
                    int q = link.Key, typeQ = _regions[q].Type;
                    float half = link.Value * 0.5f;
                    if (free) Visit(s, q * 3, half * (Rate(o, typeR, boats) + Rate(o, typeQ, boats)), stamp, gx, gz, fastest);
                    else if (m != 1)
                    {
                        Visit(s, q * 3 + m, half * (Rate(o, typeR, false) + Rate(o, typeQ, false)), stamp, gx, gz, fastest);
                        // a boat built where this land meets sailable water
                        bool build = m == 0 && o.BuildBoat || m == 2 && o.MoreBoats;
                        if (boats && build && !Wet(typeR) && Sailable(o, typeQ)) Visit(s, q * 3 + 1, half * (Rate(o, typeR, false) + o.SailBias / o.SailSpeed) + o.BuildSeconds, stamp, gx, gz, fastest);
                    }
                    else
                    {
                        if (Sailable(o, typeR) && Sailable(o, typeQ)) Visit(s, q * 3 + 1, half * 2f * o.SailBias / o.SailSpeed, stamp, gx, gz, fastest);
                        // landing onto land
                        if (!Wet(typeQ)) Visit(s, q * 3 + 2, half * (o.SailBias / o.SailSpeed + Rate(o, typeQ, false)) + o.SwitchSeconds, stamp, gx, gz, fastest);
                    }
                }
                if (m == 0 && r == ship && !free) Visit(s, r * 3 + 1, o.SwitchSeconds, stamp, gx, gz, fastest);
                if (m != 1 && portals != null && portals.TryGetValue(r, out var jumps)) foreach (int q in jumps) Visit(s, q * 3 + m, o.PortalSeconds, stamp, gx, gz, fastest);
            }
            if (reached < 0) return null;
            var chain = new List<int>();
            for (int s = reached; s >= 0; s = _parent[s]) chain.Add(s / 3);
            return chain;
        }

        private static float[] _g;
        private static int[] _parent, _seen, _closed;
        private static int _stamp;
        private static readonly List<(float f, int r)> _heap = new List<(float, int)>();

        private static void Visit(int r, int q, float cost, int stamp, float gx, float gz, float fastest)
        {
            if (float.IsInfinity(cost) || _closed[q] == stamp) return;
            float ng = _g[r] + cost;
            if (_seen[q] == stamp && ng >= _g[q]) return;
            _g[q] = ng; _parent[q] = r; _seen[q] = stamp;
            int rep = _regions[q / 3].Rep;
            float dx = X(rep) - gx, dz = Z(rep) - gz;
            Push(ng + (float)Math.Sqrt(dx * dx + dz * dz) / fastest, q);
        }

        private static void Push(float f, int r)
        {
            _heap.Add((f, r));
            int i = _heap.Count - 1;
            while (i > 0) { int up = (i - 1) / 2; if (_heap[up].f <= _heap[i].f) break; (_heap[up], _heap[i]) = (_heap[i], _heap[up]); i = up; }
        }

        private static (float f, int r) Pop()
        {
            var top = _heap[0]; var last = _heap[_heap.Count - 1]; _heap.RemoveAt(_heap.Count - 1);
            if (_heap.Count > 0)
            {
                _heap[0] = last; int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, rr = l + 1, m = i;
                    if (l < _heap.Count && _heap[l].f < _heap[m].f) m = l;
                    if (rr < _heap.Count && _heap[rr].f < _heap[m].f) m = rr;
                    if (m == i) break;
                    (_heap[m], _heap[i]) = (_heap[i], _heap[m]); i = m;
                }
            }
            return top;
        }

        // a destination on lava or a cliff top no route can stand on moves to the nearest walkable cell within 8 cells. false when none is near
        internal static bool Snap(ref float x, ref float z)
        {
            if (_state != 2) return true;
            int c = CellAt(x, z);
            if (c < 0) return false;
            if (_regionOf[c] >= 0) return true;
            int cx = c % N, cz = c / N, best = -1; float bestD = float.MaxValue;
            for (int dz = -8; dz <= 8; dz++)
                for (int dx = -8; dx <= 8; dx++)
                {
                    int xx = cx + dx, zz = cz + dz;
                    if (xx < 0 || zz < 0 || xx >= N || zz >= N) continue;
                    int j = zz * N + xx;
                    float d = dx * dx + dz * dz;
                    if (_regionOf[j] >= 0 && d < bestD) { bestD = d; best = j; }
                }
            if (best < 0) return false;
            x = X(best); z = Z(best);
            return true;
        }

        // the cells a long route may use: its abstract routes on foot, and to the ship and on by sea, widened by the margin. null when unknown.
        // noWay is set when the abstraction is ready and holds no route at all, so a whole-map search would only waste time
        internal static bool[] Corridor(NavOptions o, float sx, float sz, float gx, float gz, byte startMode, out int regionCount, out bool noWay)
        {
            regionCount = 0; noWay = false;
            if (_state != 2) return null;
            int start = CellAt(sx, sz), goal = CellAt(gx, gz);
            Dictionary<int, List<int>> portals = null;
            foreach (var p in o.Portals)
            {
                int a = CellAt(p[0], p[1]), b = CellAt(p[2], p[3]);
                if (a < 0 || b < 0 || _regionOf[a] < 0 || _regionOf[b] < 0) continue;
                if (portals == null) portals = new Dictionary<int, List<int>>();
                if (!portals.TryGetValue(_regionOf[a], out var la)) portals[_regionOf[a]] = la = new List<int>();
                if (!portals.TryGetValue(_regionOf[b], out var lb)) portals[_regionOf[b]] = lb = new List<int>();
                la.Add(_regionOf[b]); lb.Add(_regionOf[a]);
            }
            var regions = new HashSet<int>();
            if (startMode == 0)
            {
                var walk = Path(o, start, goal, false, portals);
                if (walk != null) regions.UnionWith(walk);
            }
            if (o.Boats)
            {
                // the best route with the boat rules: to the ship or a shore, one sailing leg, on by foot
                var byBoat = Path(o, start, goal, true, portals, startMode);
                if (byBoat != null) regions.UnionWith(byBoat);
                var mixed = Path(o, start, goal, true, portals, startMode, true);
                if (mixed != null) regions.UnionWith(mixed);
            }
            if (regions.Count == 0) { noWay = start >= 0 && goal >= 0 && _regionOf[start] >= 0 && _regionOf[goal] >= 0; return null; }
            regionCount = regions.Count;
            var allowed = new bool[N * N];
            foreach (int r in regions)
                foreach (int c in _cellsOf[r])
                {
                    int x0 = c % N, z0 = c / N;
                    for (int dz = -Margin; dz <= Margin; dz++)
                        for (int dx = -Margin; dx <= Margin; dx++)
                        {
                            int x = x0 + dx, z = z0 + dz;
                            if (x >= 0 && z >= 0 && x < N && z < N) allowed[z * N + x] = true;
                        }
                }
            // portal ends are always allowed, the jump lands outside the walked regions
            foreach (var p in o.Portals) { MarkAround(allowed, p[0], p[1]); MarkAround(allowed, p[2], p[3]); }
            return allowed;
        }

        // every cell within meters of a point is allowed
        internal static void Allow(bool[] allowed, float x, float z, float meters)
        {
            int c = CellAt(x, z);
            if (c < 0) return;
            int x0 = c % N, z0 = c / N, r = (int)Math.Ceiling(meters / Cell);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int xx = x0 + dx, zz = z0 + dz;
                    if (xx >= 0 && zz >= 0 && xx < N && zz < N && dx * dx + dz * dz <= r * r) allowed[zz * N + xx] = true;
                }
        }

        private static void MarkAround(bool[] allowed, float x, float z)
        {
            int c = CellAt(x, z);
            if (c < 0) return;
            int x0 = c % N, z0 = c / N;
            for (int dz = -Margin; dz <= Margin; dz++)
                for (int dx = -Margin; dx <= Margin; dx++)
                {
                    int xx = x0 + dx, zz = z0 + dz;
                    if (xx >= 0 && zz >= 0 && xx < N && zz < N) allowed[zz * N + xx] = true;
                }
        }

        internal static bool Inside(bool[] allowed, float x, float z)
        {
            int c = CellAt(x, z);
            return c >= 0 && allowed[c];
        }
    }
}
