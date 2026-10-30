using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using UnityEngine;

namespace ValheimNavigator
{
    // one 256 m tile of the fine terrain: 128 x 128 points 2 m apart from its south west corner, row 0 in the south.
    // heights in 2 cm steps, the biome as its bit number, the game's map mask (forest, mist, lava or ocean gradient) as bytes
    internal sealed class FineTile
    {
        internal const int Cells = 128;
        internal const int Bytes = Cells * Cells * 6;
        internal readonly short[] Height = new short[Cells * Cells];
        internal readonly byte[] Biome = new byte[Cells * Cells];
        internal readonly byte[] Mask = new byte[Cells * Cells * 3];
        internal int X, Z;
    }

    // the ground of new zones exactly as the game builds it (HeightmapBuilder.Build): each 64 m zone blends the heights of the biomes at
    // its four corners at the point with smoothstep weights. GetHeight and the map texture use only the point's own biome
    internal sealed class FineBuilder
    {
        // zone corners a tile touches on each side
        private const int Corners = FineTerrain.TileMeters / FineTerrain.ZoneMeters + 2;
        private readonly WorldGenerator _world;
        private readonly Func<float, float, float, Heightmap.Biome, Color> _mask;
        private readonly Heightmap.Biome[] _corner = new Heightmap.Biome[Corners * Corners];
        private readonly float[] _weight = new float[FineTerrain.ZoneMeters / FineTerrain.CellMeters];

        internal FineBuilder(WorldGenerator world, Func<float, float, float, Heightmap.Biome, Color> mask)
        {
            _world = world; _mask = mask;
            // the game's weight of a point i meters into its zone, DUtils.SmoothStep(0, 1, i / 64)
            for (int i = 0; i < _weight.Length; i++) _weight[i] = DUtils.SmoothStep(0f, 1f, (float)((double)(i * FineTerrain.CellMeters) / FineTerrain.ZoneMeters));
        }

        // false when cancelled part way
        internal bool Build(int tx, int tz, FineTile tile, Func<bool> cancelled)
        {
            const int n = FineTile.Cells, zone = FineTerrain.ZoneMeters, half = zone / 2;
            tile.X = tx; tile.Z = tz;
            int ox = tx * FineTerrain.TileMeters, oz = tz * FineTerrain.TileMeters;
            int zx0 = FloorDiv(ox + half, zone), zz0 = FloorDiv(oz + half, zone);
            for (int j = 0; j < Corners; j++)
                for (int i = 0; i < Corners; i++)
                    _corner[j * Corners + i] = _world.GetBiome((zx0 + i) * zone - half, (zz0 + j) * zone - half, 0.02f, false);
            for (int j = 0; j < n; j++)
            {
                if (cancelled()) return false;
                int wz = oz + j * FineTerrain.CellMeters, cz = FloorDiv(wz + half, zone);
                float tzw = _weight[(wz + half - cz * zone) / FineTerrain.CellMeters];
                int row = (cz - zz0) * Corners;
                for (int i = 0; i < n; i++)
                {
                    int wx = ox + i * FineTerrain.CellMeters, cx = FloorDiv(wx + half, zone);
                    float txw = _weight[(wx + half - cx * zone) / FineTerrain.CellMeters];
                    int c = row + cx - zx0;
                    float h = Height(_corner[c], _corner[c + 1], _corner[c + Corners], _corner[c + Corners + 1], wx, wz, txw, tzw);
                    var biome = _world.GetBiome(wx, wz, 0.02f, false);
                    var mask = _mask(wx, wz, h, biome);
                    int k = j * n + i;
                    tile.Height[k] = FineTerrain.Quantize(h);
                    tile.Biome[k] = FineTerrain.BiomeByte(biome);
                    tile.Mask[k * 3] = Byte(mask.r); tile.Mask[k * 3 + 1] = Byte(mask.g); tile.Mask[k * 3 + 2] = Byte(mask.b);
                }
            }
            return true;
        }

        // only every step-th point of a tile, the same ground and mask as Build, into a side x side grid (the overview of a deep water tile)
        internal void Coarse(int tx, int tz, int step, short[] height, byte[] mask)
        {
            const int n = FineTile.Cells, zone = FineTerrain.ZoneMeters, half = zone / 2;
            int side = n / step;
            int ox = tx * FineTerrain.TileMeters, oz = tz * FineTerrain.TileMeters;
            int zx0 = FloorDiv(ox + half, zone), zz0 = FloorDiv(oz + half, zone);
            for (int j = 0; j < Corners; j++)
                for (int i = 0; i < Corners; i++)
                    _corner[j * Corners + i] = _world.GetBiome((zx0 + i) * zone - half, (zz0 + j) * zone - half, 0.02f, false);
            for (int j = 0; j < side; j++)
            {
                int wz = oz + j * step * FineTerrain.CellMeters, cz = FloorDiv(wz + half, zone);
                float tzw = _weight[(wz + half - cz * zone) / FineTerrain.CellMeters];
                int row = (cz - zz0) * Corners;
                for (int i = 0; i < side; i++)
                {
                    int wx = ox + i * step * FineTerrain.CellMeters, cx = FloorDiv(wx + half, zone);
                    float txw = _weight[(wx + half - cx * zone) / FineTerrain.CellMeters];
                    int c = row + cx - zx0;
                    float h = Height(_corner[c], _corner[c + 1], _corner[c + Corners], _corner[c + Corners + 1], wx, wz, txw, tzw);
                    var m = _mask(wx, wz, h, _world.GetBiome(wx, wz, 0.02f, false));
                    int k = j * side + i;
                    height[k] = FineTerrain.Quantize(h);
                    mask[k * 3] = Byte(m.r); mask[k * 3 + 1] = Byte(m.g); mask[k * 3 + 2] = Byte(m.b);
                }
            }
        }

        // the corner biomes in the game's order: (x, z), (x + 64, z), (x, z + 64), (x + 64, z + 64). a biome met twice is sampled once
        private float Height(Heightmap.Biome b0, Heightmap.Biome b1, Heightmap.Biome b2, Heightmap.Biome b3, float x, float z, float tx, float tz) => Blend(_world, b0, b1, b2, b3, x, z, tx, tz);

        private static float Blend(WorldGenerator world, Heightmap.Biome b0, Heightmap.Biome b1, Heightmap.Biome b2, Heightmap.Biome b3, float x, float z, float tx, float tz)
        {
            Color unused;
            float h0 = world.GetBiomeHeight(b0, x, z, out unused, false, true);
            if (b2 == b0 && b1 == b0 && b3 == b0) return h0;
            float h1 = b1 == b0 ? h0 : world.GetBiomeHeight(b1, x, z, out unused, false, true);
            float h2 = b2 == b0 ? h0 : b2 == b1 ? h1 : world.GetBiomeHeight(b2, x, z, out unused, false, true);
            float h3 = b3 == b0 ? h0 : b3 == b1 ? h1 : b3 == b2 ? h2 : world.GetBiomeHeight(b3, x, z, out unused, false, true);
            return DUtils.Lerp(DUtils.Lerp(h0, h1, tx), DUtils.Lerp(h2, h3, tx), tz);
        }

        // the ground of one fine point (x and z even meters) exactly as Build makes it, for a point whose tile is not in memory (routes). the
        // zone's corner biomes are kept per thread in a small table, neighbour points share them
        [ThreadStatic] private static long[] _cornerKeys;
        [ThreadStatic] private static Heightmap.Biome[] _cornerBiomes;
        private const int CornerSlots = 1024;

        internal static float PointHeight(WorldGenerator world, int x, int z)
        {
            const int zone = FineTerrain.ZoneMeters, half = zone / 2;
            int cx = FloorDiv(x + half, zone), cz = FloorDiv(z + half, zone);
            if (_cornerKeys == null) { _cornerKeys = new long[CornerSlots]; _cornerBiomes = new Heightmap.Biome[CornerSlots * 4]; for (int i = 0; i < CornerSlots; i++) _cornerKeys[i] = long.MinValue; }
            long key = (long)cx << 32 ^ (uint)cz;
            int slot = (int)((uint)(cx * 73856093 ^ cz * 19349663) % CornerSlots);
            if (_cornerKeys[slot] != key)
            {
                _cornerKeys[slot] = key;
                _cornerBiomes[slot * 4] = world.GetBiome(cx * zone - half, cz * zone - half, 0.02f, false);
                _cornerBiomes[slot * 4 + 1] = world.GetBiome((cx + 1) * zone - half, cz * zone - half, 0.02f, false);
                _cornerBiomes[slot * 4 + 2] = world.GetBiome(cx * zone - half, (cz + 1) * zone - half, 0.02f, false);
                _cornerBiomes[slot * 4 + 3] = world.GetBiome((cx + 1) * zone - half, (cz + 1) * zone - half, 0.02f, false);
            }
            float tx = DUtils.SmoothStep(0f, 1f, (float)((double)(x + half - cx * zone) / zone)), tz = DUtils.SmoothStep(0f, 1f, (float)((double)(z + half - cz * zone) / zone));
            return Blend(world, _cornerBiomes[slot * 4], _cornerBiomes[slot * 4 + 1], _cornerBiomes[slot * 4 + 2], _cornerBiomes[slot * 4 + 3], x, z, tx, tz);
        }

        private static byte Byte(float v) => (byte)(Mathf.Clamp01(v) * 255f + 0.5f);

        internal static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }

    // the fine terrain map: 2 m tiles from the game's world generator, built on background threads nearest to you (or to the large map's
    // view) first and kept on disk per world. tiles deeper than 5 m under water everywhere are not built. step 1: the data only, the
    // map views and routes still read the game's 12 m map
    internal static class FineTerrain
    {
        internal const int TileMeters = 256, ZoneMeters = 64, CellMeters = 2;
        internal const float HeightStep = 0.02f;
        private const float DeepWater = 5f;
        // the tile grid covers the world disc, tile index -Half to Half - 1 on each axis
        private const int Half = 42, Side = Half * 2;
        private const int FileVersion = 1;
        private const int AroundTiles = 4;
        private const float StatusSeconds = 1f, LogSeconds = 30f;

        private enum State : byte { Unknown, Water, Built, Working, Outside }

        private static readonly object Gate = new object(), SaveGate = new object();
        private static readonly State[] States = new State[Side * Side];
        private static readonly Dictionary<int, FineTile> Kept = new Dictionary<int, FineTile>();
        private static readonly HashSet<int> Loading = new HashSet<int>();

        private static WorldGenerator _world;
        private static volatile bool _opened;
        private static int _generation;
        private static int _threads;
        private static long _limit;
        private static float _water;
        private static string _folder;
        private static volatile float _focusX, _focusZ;

        // counters, under Gate
        private static int _inside, _decided, _built, _fresh, _waterTiles, _diskReads, _newWater, _failedWrites;
        private static double _buildMilliseconds;
        private static System.Diagnostics.Stopwatch _clock;
        private static bool _complete, _writeWarned, _maskFailed;

        private static float _nextStatus, _nextLog;
        private static float _progress = -1f;
        private static string _status;

        // the menu line, null while there is nothing to show
        internal static string Status => _status;
        // this world's cache folder while the fine terrain runs
        internal static string Folder => _world != null ? _folder : null;
        internal const string OffStatus = "Detailed terrain map is off, the regular map is shown";
        internal const string OffNote = "DetailedTerrain is off in the cfg (section 09 - Performance), so the map uses the game's regular 12 m data. Set it to true for the 2 m map. Files built earlier stay in BepInEx\\cache\\ValheimNavigator until you delete that folder.";
        // 0 to 1 while building, -1 when there is nothing to show
        internal static float Progress => _progress;

        internal static short Quantize(float h) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(h / HeightStep)));

        internal static byte BiomeByte(Heightmap.Biome biome)
        {
            int b = (int)biome, bit = 0;
            while (b != 0) { bit++; b >>= 1; }
            return (byte)bit;
        }

        // every frame on the game's thread: start and stop with the world, follow the player or the large map's view, the menu line
        internal static void Tick()
        {
            var world = WorldGenerator.instance;
            // a player is needed to start, not to keep running (death and respawn leave the build alone)
            bool want = Plugin.Enabled.Value && Settings.FineTerrain.Value && !_maskFailed && world != null && Minimap.instance != null;
            if (_world != null && (!want || world != _world || _threads != Settings.FineThreads.Value)) Stop();
            if (_world == null && want && Player.m_localPlayer != null) Start(world);
            if (_world == null)
            {
                // switched off in the cfg: the map keeps the regular map and the panel says why
                _status = Plugin.Enabled.Value && !Settings.FineTerrain.Value && world != null ? OffStatus : null;
                _progress = -1f;
                return;
            }
            _limit = (long)Settings.FineMemoryMB.Value * 1024L * 1024L;
            Focus();
            TerrainEdits.Tick();
            LocationShaping.Tick();
            if (Time.unscaledTime >= _nextStatus) { _nextStatus = Time.unscaledTime + StatusSeconds; UpdateStatus(); }
        }

        private static void Focus()
        {
            var map = Minimap.instance;
            var image = map.m_mapImageLarge;
            if (map.m_mode == Minimap.MapMode.Large && image != null)
            {
                var center = image.uvRect.center;
                float size = map.m_textureSize;
                _focusX = (center.x * size - size / 2f) * map.m_pixelSize;
                _focusZ = (center.y * size - size / 2f) * map.m_pixelSize;
                return;
            }
            if (Player.m_localPlayer == null) return;
            var player = Player.m_localPlayer.transform.position;
            _focusX = player.x; _focusZ = player.z;
        }

        private static void Start(WorldGenerator world)
        {
            _maskOf = AccessMask(Minimap.instance);
            if (_maskOf == null) { _maskFailed = true; _status = null; return; }
            _world = world;
            _threads = Settings.FineThreads.Value;
            _water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            _opened = false;
            int generation = ++_generation;
            if (OverviewHeight == null) { OverviewHeight = new short[OverviewSide * OverviewSide]; OverviewMask = new byte[OverviewSide * OverviewSide * 3]; }
            _overviewReady = new bool[Side * Side];
            _overviewTiles = 0;
            lock (Gate)
            {
                Kept.Clear(); Loading.Clear();
                _inside = _decided = _built = _fresh = _waterTiles = _diskReads = _newWater = _failedWrites = 0;
                _buildMilliseconds = 0; _complete = false;
                float edge = WorldGenerator.waterEdge;
                for (int tz = -Half; tz < Half; tz++)
                    for (int tx = -Half; tx < Half; tx++)
                    {
                        // the tile's point nearest the world's center
                        float nx = Mathf.Clamp(0f, tx * TileMeters, (tx + 1) * TileMeters), nz = Mathf.Clamp(0f, tz * TileMeters, (tz + 1) * TileMeters);
                        bool inside = nx * nx + nz * nz < edge * edge;
                        States[Index(tx, tz)] = inside ? State.Unknown : State.Outside;
                        if (inside) _inside++;
                    }
            }
            var w = world.m_world;
            string name = w != null ? w.m_name : "world";
            int seed = w != null ? w.m_seed : 0, version = w != null ? w.m_worldGenVersion : 0;
            foreach (char bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
            _folder = Path.Combine(BepInEx.Paths.CachePath, Plugin.ModName, name + "_" + seed, "fine");
            string key = FileVersion + " " + seed + " " + version + " " + global::Version.GetVersionString(false);
            _clock = System.Diagnostics.Stopwatch.StartNew();
            _nextLog = Time.unscaledTime + LogSeconds;
            int threads = _threads; var mask = _maskOf;
            new Thread(() => Open(generation, key, world, threads, mask)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator fine terrain" }.Start();
        }

        private static Func<float, float, float, Heightmap.Biome, Color> _maskOf;

        // the game's own map mask (Minimap.GetMaskColor reads only the generator and fixed colors, so it runs on any thread)
        private static Func<float, float, float, Heightmap.Biome, Color> AccessMask(Minimap map)
        {
            try
            {
                var open = HarmonyLib.AccessTools.MethodDelegate<Func<Minimap, float, float, float, Heightmap.Biome, Color>>(
                    HarmonyLib.AccessTools.Method(typeof(Minimap), "GetMaskColor", new[] { typeof(float), typeof(float), typeof(float), typeof(Heightmap.Biome) }));
                return (x, z, h, b) => open(map, x, z, h, b);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("the detailed terrain map is off, the game's map mask could not be read: " + ex.Message);
                return null;
            }
        }

        // the workers see the new generation within one row of a tile and leave without writing, so nothing waits for them here
        internal static void Stop()
        {
            if (_world == null) return;
            lock (Gate) _generation++;
            SaveWater();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("fine terrain stopped, " + Summary());
            lock (Gate) { Kept.Clear(); Loading.Clear(); }
            TerrainEdits.SaveVisited(_folder);
            TerrainEdits.Clear(true);
            LocationShaping.Clear();
            _world = null; _status = null; _progress = -1f;
        }

        private static int Index(int tx, int tz) => (tz + Half) * Side + tx + Half;

        // the whole world at 8 m (every fourth 2 m point), filled from each tile when it is built or read, so the detailed map can be drawn
        // zoomed out without keeping every tile in memory (about 36 MB). a tile's cells are written once by one worker, then only read
        internal const int OverviewMeters = 8, OverviewPerTile = TileMeters / OverviewMeters, OverviewSide = Side * OverviewPerTile;
        internal static short[] OverviewHeight;
        internal static byte[] OverviewMask;
        private static bool[] _overviewReady;
        private static int _overviewTiles;

        // the overview's ground and mask at a world point, false where no tile is built (deep water, outside, not yet)
        internal static bool Overview(int x, int z, out short height, out int cell)
        {
            height = 0; cell = -1;
            var ready = _overviewReady;
            if (ready == null) return false;
            int ox = FineBuilder.FloorDiv(x, OverviewMeters) + Half * OverviewPerTile, oz = FineBuilder.FloorDiv(z, OverviewMeters) + Half * OverviewPerTile;
            if (ox < 0 || oz < 0 || ox >= OverviewSide || oz >= OverviewSide || !ready[oz / OverviewPerTile * Side + ox / OverviewPerTile]) return false;
            cell = oz * OverviewSide + ox;
            height = OverviewHeight[cell];
            return true;
        }

        private static void FillOverview(int k, FineTile tile)
        {
            const int step = OverviewMeters / CellMeters;
            int tx = k % Side, tz = k / Side;
            for (int j = 0; j < OverviewPerTile; j++)
                for (int i = 0; i < OverviewPerTile; i++)
                {
                    int c = j * step * FineTile.Cells + i * step, o = (tz * OverviewPerTile + j) * OverviewSide + tx * OverviewPerTile + i;
                    OverviewHeight[o] = tile.Height[c];
                    OverviewMask[o * 3] = tile.Mask[c * 3]; OverviewMask[o * 3 + 1] = tile.Mask[c * 3 + 1]; OverviewMask[o * 3 + 2] = tile.Mask[c * 3 + 2];
                }
            _overviewReady[k] = true;
            Interlocked.Increment(ref _overviewTiles);
            Interlocked.Increment(ref KeptVersion);
        }

        // a deep water tile is never built at 2 m, its overview comes straight from the generator at 8 m, so the detailed map covers the
        // sea too and the game's own map does not show through in tile sized blocks
        private static void FillWaterOverview(int generation, int k, int tx, int tz, FineBuilder builder, Scratch scratch)
        {
            builder.Coarse(tx, tz, OverviewMeters / CellMeters, scratch.WaterHeight, scratch.WaterMask);
            lock (Gate)
            {
                Loading.Remove(k);
                if (generation != _generation || _overviewReady[k]) return;
            }
            int side = OverviewPerTile, x0 = (tx + Half) * side, z0 = (tz + Half) * side;
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    int c = j * side + i, o = (z0 + j) * OverviewSide + x0 + i;
                    OverviewHeight[o] = scratch.WaterHeight[c];
                    OverviewMask[o * 3] = scratch.WaterMask[c * 3]; OverviewMask[o * 3 + 1] = scratch.WaterMask[c * 3 + 1]; OverviewMask[o * 3 + 2] = scratch.WaterMask[c * 3 + 2];
                }
            _overviewReady[k] = true;
            Interlocked.Increment(ref _overviewTiles);
            Interlocked.Increment(ref KeptVersion);
        }

        // grows whenever a tile comes into memory or into the overview, so a view drawn from them knows to draw again
        internal static int KeptVersion;

        // the kept tile (tiles are not changed after they are built or read, so it can be read without the lock), null when not in memory
        internal static FineTile Tile(int tx, int tz)
        {
            if (tx < -Half || tz < -Half || tx >= Half || tz >= Half) return null;
            lock (Gate) return Kept.TryGetValue(Index(tx, tz), out var tile) ? tile : null;
        }

        // the generated ground at a fine point (x and z even meters) when its tile is in memory
        internal static bool TryBase(int x, int z, out float height)
        {
            height = 0f;
            int tx = FineBuilder.FloorDiv(x, TileMeters), tz = FineBuilder.FloorDiv(z, TileMeters);
            if (tx < -Half || tz < -Half || tx >= Half || tz >= Half) return false;
            FineTile tile;
            lock (Gate) if (!Kept.TryGetValue(Index(tx, tz), out tile)) return false;
            height = tile.Height[(z - tz * TileMeters) / CellMeters * FineTile.Cells + (x - tx * TileMeters) / CellMeters] * HeightStep;
            return true;
        }

        // the cache folder is checked and read on a background thread, then the workers start
        private static void Open(int generation, string key, WorldGenerator world, int threads, Func<float, float, float, Heightmap.Biome, Color> mask)
        {
            int onDisk = 0, water = 0;
            try
            {
                Directory.CreateDirectory(_folder);
                string keyFile = Path.Combine(_folder, "key.txt");
                if (!File.Exists(keyFile) || File.ReadAllText(keyFile).Trim() != key)
                {
                    // another world version, game version or file format: the old tiles no longer match
                    foreach (var file in Directory.GetFiles(_folder)) File.Delete(file);
                    File.WriteAllText(keyFile, key);
                }
                foreach (var file in Directory.GetFiles(_folder, "*.tile.*.tmp")) File.Delete(file);
                var found = new List<int>();
                foreach (var file in Directory.GetFiles(_folder, "*.tile"))
                {
                    var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int tx) && int.TryParse(parts[1], out int tz) && Math.Abs(tx + 0.5f) < Half && Math.Abs(tz + 0.5f) < Half)
                        found.Add(Index(tx, tz));
                }
                if (generation == _generation) TerrainEdits.LoadVisited(_folder);
                string waterFile = Path.Combine(_folder, "water.bin");
                byte[] known = File.Exists(waterFile) ? File.ReadAllBytes(waterFile) : null;
                lock (Gate)
                {
                    if (generation != _generation) return;
                    foreach (int k in found) if (States[k] == State.Unknown) { States[k] = State.Built; _built++; _decided++; onDisk++; }
                    if (known != null && known.Length == States.Length)
                        for (int k = 0; k < known.Length; k++)
                            if (known[k] == 1 && States[k] == State.Unknown) { States[k] = State.Water; _waterTiles++; _decided++; water++; }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("fine terrain cache folder could not be read, tiles are built again and may not be kept: " + ex.Message);
            }
            if (generation != _generation) return;
            _opened = true;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("fine terrain started, " + onDisk + " tiles on disk, " + water + " water tiles known, " + _inside + " tiles in the world, " + TerrainEdits.VisitedCount + " visited zones kept, " + threads + " threads");
            for (int i = 0; i < threads; i++)
            {
                var builder = new FineBuilder(world, mask);
                new Thread(() => Work(generation, builder)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "ValheimNavigator fine terrain " + i }.Start();
            }
        }

        private sealed class Scratch
        {
            internal readonly FineTile Tile = new FineTile();
            internal readonly byte[] Raw = new byte[FineTile.Bytes];
            internal readonly MemoryStream Output = new MemoryStream(FineTile.Bytes / 2);
            internal readonly short[] WaterHeight = new short[OverviewPerTile * OverviewPerTile];
            internal readonly byte[] WaterMask = new byte[OverviewPerTile * OverviewPerTile * 3];
        }

        private static void Work(int generation, FineBuilder builder)
        {
            var scratch = new Scratch();
            Func<bool> cancelled = () => generation != _generation;
            while (!cancelled())
            {
                int k; bool load, keep, water;
                lock (Gate) k = Pick(out load, out keep, out water);
                if (k < 0) { Thread.Sleep(250); continue; }
                int tx = k % Side - Half, tz = k / Side - Half;
                try
                {
                    if (water) { FillWaterOverview(generation, k, tx, tz, builder, scratch); continue; }
                    if (load) { Load(generation, k, tx, tz, scratch, keep); continue; }
                    if (Deep(tx, tz))
                    {
                        lock (Gate) { if (generation != _generation) return; States[k] = State.Water; _waterTiles++; _decided++; _newWater++; }
                        if (_newWater >= 64) SaveWater();
                        FillWaterOverview(generation, k, tx, tz, builder, scratch);
                        continue;
                    }
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var tile = keep ? new FineTile() : scratch.Tile;
                    if (!builder.Build(tx, tz, tile, cancelled)) return;
                    double ms = clock.Elapsed.TotalMilliseconds;
                    if (cancelled()) return;
                    FillOverview(k, tile);
                    bool written = Write(tile, scratch);
                    lock (Gate)
                    {
                        if (generation != _generation) return;
                        States[k] = State.Built; _built++; _fresh++; _decided++; _buildMilliseconds += ms;
                        if (!written) _failedWrites++;
                        if (keep) Keep(k, tile);
                    }
                }
                catch (Exception ex)
                {
                    // a tile that cannot be read is built again, one that cannot be built is left out until the next start
                    lock (Gate)
                    {
                        if (generation != _generation) return;
                        if (water) { Loading.Remove(k); _overviewReady[k] = true; }
                        else if (load) { Loading.Remove(k); States[k] = State.Unknown; _built--; _decided--; }
                        else { States[k] = State.Built; _decided++; _failedWrites++; }
                    }
                    if (!_writeWarned) { _writeWarned = true; Plugin.Log.LogWarning("fine terrain tile " + tx + ", " + tz + " failed: " + ex.Message); }
                }
            }
        }

        // a tile on disk read only for the overview waits behind everything else
        private const float OverviewOnlyPenalty = 1000f;

        // the nearest tile still to decide, a tile on disk that belongs in memory now, or one the overview still lacks (a tile on disk, or a
        // deep water tile drawn from the generator). under Gate
        private static int Pick(out bool load, out bool keep, out bool water)
        {
            load = keep = water = false;
            if (!_opened) return -1;
            float fx = _focusX / TileMeters - 0.5f, fz = _focusZ / TileMeters - 0.5f;
            float farthest = -1f;
            foreach (var pair in Kept) farthest = Math.Max(farthest, Distance(pair.Key, fx, fz));
            bool room = Bytes() + FineTile.Bytes <= _limit;
            int best = -1; float bestDistance = float.MaxValue, bestRank = float.MaxValue; bool bestLoad = false, bestWater = false;
            for (int k = 0; k < States.Length; k++)
            {
                var state = States[k];
                if (state == State.Water)
                {
                    if (_overviewReady[k] || Loading.Contains(k)) continue;
                    float waterRank = Distance(k, fx, fz) + OverviewOnlyPenalty;
                    if (waterRank >= bestRank) continue;
                    best = k; bestDistance = waterRank - OverviewOnlyPenalty; bestRank = waterRank; bestLoad = false; bestWater = true;
                    continue;
                }
                if (state != State.Unknown && (state != State.Built || Kept.ContainsKey(k) || Loading.Contains(k))) continue;
                float d = Distance(k, fx, fz);
                if (d >= bestRank) continue;
                bool wanted = room || d < farthest - 1f;
                float rank = d;
                if (state == State.Built && !wanted)
                {
                    if (_overviewReady[k]) continue;
                    rank = d + OverviewOnlyPenalty;
                    if (rank >= bestRank) continue;
                }
                best = k; bestDistance = d; bestRank = rank; bestLoad = state == State.Built; bestWater = false;
            }
            if (best < 0) return -1;
            if (bestWater) { water = true; Loading.Add(best); return best; }
            load = bestLoad;
            keep = room || bestDistance < farthest - 1f;
            if (load) Loading.Add(best); else States[best] = State.Working;
            return best;
        }

        private static float Distance(int k, float fx, float fz)
        {
            float dx = k % Side - Half - fx, dz = k / Side - Half - fz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static long Bytes() => (long)Kept.Count * FineTile.Bytes;

        // under Gate: keeps the tile and lets go of the farthest ones over the memory limit, they stay on disk
        private static void Keep(int k, FineTile tile)
        {
            Kept[k] = tile;
            KeptVersion++;
            float fx = _focusX / TileMeters - 0.5f, fz = _focusZ / TileMeters - 0.5f;
            while (Bytes() > _limit && Kept.Count > 1)
            {
                int far = -1; float farDistance = -1f;
                foreach (var pair in Kept) { float d = Distance(pair.Key, fx, fz); if (d > farDistance) { farDistance = d; far = pair.Key; } }
                Kept.Remove(far);
            }
        }

        // deeper than DeepWater everywhere: the plain generator height on a 16 m grid over the tile and the 64 m around it (the ground
        // blend reaches one zone), with a margin for peaks between samples
        private static bool Deep(int tx, int tz)
        {
            const int step = 16, margin = ZoneMeters;
            float limit = _water - DeepWater - 3f;
            int x0 = tx * TileMeters - margin, z0 = tz * TileMeters - margin, span = TileMeters + 2 * margin;
            for (int z = 0; z <= span; z += step)
                for (int x = 0; x <= span; x += step)
                    if (_world.GetHeight(x0 + x, z0 + z) > limit) return false;
            return true;
        }

        private static string TilePath(int tx, int tz) => Path.Combine(_folder, tx + "_" + tz + ".tile");

        private static bool Write(FineTile tile, Scratch scratch)
        {
            try
            {
                int length = Encode(tile, scratch.Raw, scratch.Output);
                string path = TilePath(tile.X, tile.Z), temporary = path + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write)) file.Write(scratch.Output.GetBuffer(), 0, length);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                return true;
            }
            catch (Exception ex)
            {
                if (!_writeWarned) { _writeWarned = true; Plugin.Log.LogWarning("fine terrain tile could not be written, it is built again next time: " + ex.Message); }
                return false;
            }
        }

        // keep: the tile stays in memory, otherwise it is read only for the overview
        private static void Load(int generation, int k, int tx, int tz, Scratch scratch, bool keep)
        {
            var tile = keep ? new FineTile() : scratch.Tile;
            bool ok = Decode(File.ReadAllBytes(TilePath(tx, tz)), tile, scratch.Raw) && tile.X == tx && tile.Z == tz;
            if (ok && generation == _generation && !_overviewReady[k]) FillOverview(k, tile);
            lock (Gate)
            {
                if (generation != _generation) return;
                Loading.Remove(k);
                if (!ok) { States[k] = State.Unknown; _built--; _decided--; return; }
                _diskReads++;
                if (keep) Keep(k, tile);
            }
        }

        private static void SaveWater()
        {
            lock (SaveGate)
            {
                if (_folder == null) return;
                var bytes = new byte[States.Length];
                lock (Gate)
                {
                    for (int k = 0; k < bytes.Length; k++) bytes[k] = States[k] == State.Water ? (byte)1 : (byte)0;
                    _newWater = 0;
                }
                try
                {
                    string path = Path.Combine(_folder, "water.bin"), temporary = path + ".tmp";
                    File.WriteAllBytes(temporary, bytes);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(temporary, path);
                }
                catch (Exception ex)
                {
                    if (!_writeWarned) { _writeWarned = true; Plugin.Log.LogWarning("fine terrain water list could not be written: " + ex.Message); }
                }
            }
        }

        // the file: "NVFT", version, compressed flag, cells, tile x and z, raw length, then the heights as row deltas, the biomes and the
        // masks, deflated. without the game's native deflate the raw bytes are written
        internal static int Encode(FineTile tile, byte[] raw, MemoryStream output)
        {
            const int n = FineTile.Cells;
            int o = 0;
            for (int j = 0; j < n; j++)
            {
                short last = 0;
                for (int i = 0; i < n; i++)
                {
                    short v = tile.Height[j * n + i], d = (short)(v - last);
                    last = v;
                    raw[o++] = (byte)d; raw[o++] = (byte)(d >> 8);
                }
            }
            Buffer.BlockCopy(tile.Biome, 0, raw, o, n * n); o += n * n;
            Buffer.BlockCopy(tile.Mask, 0, raw, o, n * n * 3); o += n * n * 3;
            bool compressed = true;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                output.SetLength(0);
                var writer = new BinaryWriter(output);
                writer.Write((byte)'N'); writer.Write((byte)'V'); writer.Write((byte)'F'); writer.Write((byte)'T');
                writer.Write((byte)FileVersion); writer.Write(compressed ? (byte)1 : (byte)0); writer.Write((short)n);
                writer.Write(tile.X); writer.Write(tile.Z); writer.Write(o);
                writer.Flush();
                if (!compressed) { output.Write(raw, 0, o); break; }
                try
                {
                    using (var deflate = new DeflateStream(output, CompressionMode.Compress, true)) deflate.Write(raw, 0, o);
                    break;
                }
                catch (Exception) { compressed = false; }
            }
            return (int)output.Length;
        }

        internal static bool Decode(byte[] file, FineTile tile, byte[] raw)
        {
            const int n = FineTile.Cells, header = 20;
            if (file.Length < header || file[0] != 'N' || file[1] != 'V' || file[2] != 'F' || file[3] != 'T' || file[4] != FileVersion || BitConverter.ToInt16(file, 6) != n) return false;
            bool compressed = file[5] == 1;
            tile.X = BitConverter.ToInt32(file, 8); tile.Z = BitConverter.ToInt32(file, 12);
            int length = BitConverter.ToInt32(file, 16);
            if (length != FineTile.Bytes) return false;
            if (compressed)
            {
                try
                {
                    using (var deflate = new DeflateStream(new MemoryStream(file, header, file.Length - header), CompressionMode.Decompress))
                    {
                        int read = 0, got;
                        while (read < length && (got = deflate.Read(raw, read, length - read)) > 0) read += got;
                        if (read != length) return false;
                    }
                }
                catch (InvalidDataException) { return false; }
            }
            else
            {
                if (file.Length - header != length) return false;
                Buffer.BlockCopy(file, header, raw, 0, length);
            }
            int o = 0;
            for (int j = 0; j < n; j++)
            {
                short last = 0;
                for (int i = 0; i < n; i++)
                {
                    last = (short)(last + (short)(raw[o] | raw[o + 1] << 8));
                    o += 2;
                    tile.Height[j * n + i] = last;
                }
            }
            Buffer.BlockCopy(raw, o, tile.Biome, 0, n * n); o += n * n;
            Buffer.BlockCopy(raw, o, tile.Mask, 0, n * n * 3);
            return true;
        }

        // the menu line and its log line, once a second
        private static void UpdateStatus()
        {
            int decided, inside, around = 0, aroundReady = 0;
            bool complete;
            lock (Gate)
            {
                decided = _decided; inside = _inside;
                complete = decided >= inside;
                int px = Mathf.FloorToInt(_focusX / TileMeters), pz = Mathf.FloorToInt(_focusZ / TileMeters);
                if (Player.m_localPlayer != null) { var p = Player.m_localPlayer.transform.position; px = Mathf.FloorToInt(p.x / TileMeters); pz = Mathf.FloorToInt(p.z / TileMeters); }
                for (int tz = pz - AroundTiles; tz <= pz + AroundTiles; tz++)
                    for (int tx = px - AroundTiles; tx <= px + AroundTiles; tx++)
                    {
                        if (tx < -Half || tz < -Half || tx >= Half || tz >= Half) continue;
                        var state = States[Index(tx, tz)];
                        if (state == State.Outside) continue;
                        around++;
                        if (state == State.Water || state == State.Built) aroundReady++;
                    }
            }
            if (!_opened) { _status = "Detailed terrain map: reading the cache"; _progress = 0f; return; }
            if (complete)
            {
                _status = "Detailed terrain map ready"; _progress = 1f;
                if (!_complete)
                {
                    _complete = true;
                    SaveWater();
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("fine terrain complete in " + _clock.Elapsed.TotalSeconds.ToString("F0") + " s, " + Summary());
                }
                return;
            }
            _progress = inside > 0 ? (float)decided / inside : 0f;
            _status = "Detailed terrain map: " + Mathf.RoundToInt(_progress * 100f) + " %, " + (aroundReady >= around ? "the area around you is ready" : "building the area around you");
            if (Plugin.LogCounters.Value && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + LogSeconds;
                Plugin.Log.LogInfo("fine terrain " + Mathf.RoundToInt(_progress * 100f) + " % after " + _clock.Elapsed.TotalSeconds.ToString("F0") + " s, " + Summary());
            }
        }

        private static string Summary()
        {
            lock (Gate)
            {
                return _decided + " of " + _inside + " tiles decided, " + _built + " built or on disk (" + _fresh + " built now), " + _waterTiles + " deep water, "
                    + (_fresh > 0 ? (_buildMilliseconds / _fresh).ToString("F0") + " ms a built tile, " : "")
                    + _diskReads + " read from disk, " + Kept.Count + " kept (" + (Bytes() / (1024 * 1024)) + " MB), " + _overviewTiles + " in the 8 m overview" + (_failedWrites > 0 ? ", " + _failedWrites + " not written" : "");
            }
        }
    }
}
