using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // shared textures keep both maps in agreement. the picture is computed on a background thread from the height, biome and mask arrays
    // the sharp view keeps, then the explored state is applied. finished pictures are kept per world and settings in memory and in the game's
    // temporary folder, so a setting or a world seen before shows at once instead of being drawn again
    internal static class TerrainOverlay
    {
        private const int MaxSide = 1024;
        // steep ground is slate violet so it never reads as the red and yellow of boat clearance
        internal static readonly Color SteepColor = new Color(0.36f, 0.30f, 0.52f, 0.55f);
        internal static readonly Color SlopeColor = new Color(0.55f, 0.50f, 0.68f, 0.38f);
        // boiling ashlands water, the same magenta as the route's boiling water legs
        internal static readonly Color BoilingColor = new Color(0.69f, 0.32f, 0.55f, 1f);
        private static RawImage _terrain, _fog;
        private static Texture2D _source, _shade, _texture;
        private static int _side;

        // the game paints swamp with this color in its biome texture, swamp ground below the water line is not sea
        internal static bool Swamp(Color c) { return Math.Abs(c.r - 0xa3 / 255f) < 0.02f && Math.Abs(c.g - 0x72 / 255f) < 0.02f && Math.Abs(c.b - 0x58 / 255f) < 0.02f; }
        private static bool _dirty = true, _warned;
        private static string _key;
        internal static int Version;
        internal static Texture Texture => _texture;
        internal static Texture Shade => _shade;
        internal static bool Building => _job != null;
        internal static bool Ready(Minimap map) { return _source == MapAccess.Fog(map) && _shade != null && Version > 0 && _job == null; }
        internal static bool Active => Settings.TerrainView.Value != 0 || Settings.Contours.Value || Settings.Mist.Value || Settings.Shallows.Value || Settings.Steepness.Value || Settings.AshlandsHeat.Value;

        internal static void Dirty() { _dirty = true; DetailOverlay.Dirty(); }

        internal static string Key()
        {
            return Settings.TerrainView.Value + "/" + Settings.Contours.Value + "/" + Settings.Mist.Value + "/" + Settings.Shallows.Value + "/" + Settings.Steepness.Value
                + "/" + Settings.ShowUnexplored.Value + "/" + Settings.ContourMeters.Value + "/" + Settings.BoatDangerDepth.Value + "/" + Settings.BoatWarningDepth.Value + "/" + Settings.AshlandsHeat.Value + "/" + Settings.ContourAuto.Value + "/" + Settings.MaxSlope.Value;
        }

        // one picture being made on a background thread with copies of every setting it uses
        private sealed class Job
        {
            internal string Key, File;
            internal int Side, View;
            internal float Pixel, Water, ContourMeters, Danger, Warning, MaxSlope;
            internal bool Contours, Mist, Steep, Shallows, Heat, ShowUnexplored;
            internal Color32[] Full, Pixels, Shade;
            internal volatile bool Done;
            internal double Milliseconds;
            internal bool Loaded;
        }

        private static Job _job;
        // full pictures (before the explored state is applied) by world and key, the newest few in memory
        private static readonly Dictionary<string, Color32[]> Memory = new Dictionary<string, Color32[]>();
        private static readonly List<string> MemoryOrder = new List<string>();
        private const int MemoryPictures = 4, DiskPictures = 16;

        private static RawImage Layer(RawImage map, string name, int order)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(map.transform, false);
            Ui.Stretch((RectTransform)go.transform, 0f);
            go.transform.SetSiblingIndex(order);
            var image = go.GetComponent<RawImage>(); image.raycastTarget = false;
            return image;
        }

        // the cache folder in the game's temporary files, made on first use
        internal static string CacheFolder()
        {
            string folder = Path.Combine(Application.temporaryCachePath, "ValheimNavigator");
            Directory.CreateDirectory(folder);
            return folder;
        }

        // a fingerprint of the world's height map, so pictures of another world are never reused
        private static string World()
        {
            var height = DetailOverlay.HeightData;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < height.Length; i += 4099) hash = hash * 31 + height[i].GetHashCode();
                return height.Length + "_" + hash.ToString("x8");
            }
        }

        internal static void Frame(Minimap map)
        {
            try
            {
                if (map == null || map.m_mapImageLarge == null) return;
                if (_terrain == null)
                {
                    _terrain = Layer(map.m_mapImageLarge, "ValheimNavigator_Terrain", 0);
                    _fog = Layer(map.m_mapImageLarge, "ValheimNavigator_Shade", 1);
                    // a rebuilt map gets the kept pictures, a layer without a texture draws white (friend's test: white map while panning)
                    if (_texture != null) { _terrain.texture = _texture; _fog.texture = _shade; }
                }
                var source = MapAccess.Fog(map);
                MistClearings.Frame();
                string key = Key();
                if (source != _source || key != _key) _dirty = true;
                // the native map shows until the first build is done, an unbuilt texture would cover it in grey
                _terrain.gameObject.SetActive(Active && Version > 0 && !DetailOverlay.Covering);
                _fog.gameObject.SetActive(Settings.ShowUnexplored.Value && Version > 0);
                if (!Active && !Settings.ShowUnexplored.Value) return;
                _terrain.uvRect = _fog.uvRect = map.m_mapImageLarge.uvRect;
                _fog.color = new Color(0.13f, 0.12f, 0.10f, Settings.FogTint.Value);
                if (source == null || !DetailOverlay.EnsureCache(map)) return;
                DetailOverlay.RefreshFog(source);
                if (_dirty && _job == null && DetailOverlay.FogReady)
                {
                    _dirty = false; _source = source; _key = key;
                    Begin(map, key);
                }
                Finish(map);
            }
            catch (Exception ex)
            {
                _job = null;
                if (!_warned) Plugin.Log.LogWarning("terrain overlays unavailable: " + ex);
                _warned = true;
            }
        }

        private static void Begin(Minimap map, string key)
        {
            int side = Math.Min(MaxSide, DetailOverlay.DataSide);
            var job = new Job
            {
                Key = key, Side = side, View = Settings.TerrainView.Value, Pixel = map.m_pixelSize, Water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f,
                ContourMeters = Settings.ContourMeters.Value, Danger = Settings.BoatDangerDepth.Value, Warning = Math.Max(Settings.BoatWarningDepth.Value, Settings.BoatDangerDepth.Value),
                MaxSlope = Settings.MaxSlope.Value, Contours = Settings.Contours.Value, Mist = Settings.Mist.Value, Steep = Settings.Steepness.Value, Shallows = Settings.Shallows.Value,
                Heat = Settings.AshlandsHeat.Value, ShowUnexplored = Settings.ShowUnexplored.Value
            };
            // pictures with mist depend on loaded clearings, they are never kept
            string memoryKey = World() + "|" + key;
            if (!job.Mist)
            {
                job.File = Path.Combine(CacheFolder(), "terrain_" + memoryKey.GetStableHashCode().ToString("x8") + "_" + side + ".bin");
                if (Memory.TryGetValue(memoryKey, out var kept)) { job.Full = kept; job.Loaded = true; }
            }
            _job = job; _memoryKey = memoryKey;
            ThreadPool.QueueUserWorkItem(Run, job);
        }

        private static string _memoryKey;

        private static void Run(object state)
        {
            var job = (Job)state;
            var clock = Stopwatch.StartNew();
            try
            {
                int n = job.Side * job.Side;
                if (job.Full == null && job.File != null && File.Exists(job.File))
                {
                    var bytes = File.ReadAllBytes(job.File);
                    if (bytes.Length == n * 4)
                    {
                        job.Full = new Color32[n];
                        for (int i = 0, b = 0; i < n; i++, b += 4) job.Full[i] = new Color32(bytes[b], bytes[b + 1], bytes[b + 2], bytes[b + 3]);
                        job.Loaded = true;
                    }
                }
                if (job.Full == null)
                {
                    job.Full = new Color32[n];
                    for (int row = 0; row < job.Side; row++) BuildRow(job, row);
                    if (job.File != null) Save(job);
                }
                // the explored state is applied last, it changes as you explore while the full picture does not
                var fog = DetailOverlay.FogData;
                int side = DetailOverlay.DataSide;
                job.Pixels = new Color32[n]; job.Shade = new Color32[n];
                float edge = WorldGenerator.waterEdge * WorldGenerator.waterEdge;
                for (int row = 0; row < job.Side; row++)
                {
                    int sy = Math.Min(side - 1, row * side / job.Side);
                    float wz = (sy - side / 2f) * job.Pixel;
                    for (int x = 0; x < job.Side; x++)
                    {
                        int sx = Math.Min(side - 1, x * side / job.Side), index = row * job.Side + x;
                        // beyond the world's water edge the game shows open space, no shade either
                        float wx = (sx - side / 2f) * job.Pixel;
                        if (wx * wx + wz * wz > edge) continue;
                        bool unexplored = fog != null && fog[sy * side + sx] != 0;
                        job.Shade[index] = new Color32(255, 255, 255, unexplored ? (byte)255 : (byte)0);
                        job.Pixels[index] = !job.ShowUnexplored && unexplored ? new Color32() : job.Full[index];
                    }
                }
            }
            catch (Exception ex)
            {
                job.Pixels = null;
                if (!_warned) Plugin.Log.LogWarning("terrain overlay build failed: " + ex.Message);
                _warned = true;
            }
            job.Milliseconds = clock.Elapsed.TotalMilliseconds;
            job.Done = true;
        }

        // the picture is written once, the oldest files beyond the limit are removed. only this mod's own files are touched
        private static void Save(Job job)
        {
            var bytes = new byte[job.Full.Length * 4];
            for (int i = 0, b = 0; i < job.Full.Length; i++, b += 4) { var c = job.Full[i]; bytes[b] = c.r; bytes[b + 1] = c.g; bytes[b + 2] = c.b; bytes[b + 3] = c.a; }
            File.WriteAllBytes(job.File, bytes);
            var files = new DirectoryInfo(Path.GetDirectoryName(job.File)).GetFiles("terrain_*.bin");
            if (files.Length <= DiskPictures) return;
            Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = DiskPictures; i < files.Length; i++) files[i].Delete();
        }

        private static void Finish(Minimap map)
        {
            var job = _job;
            if (job == null || !job.Done) return;
            _job = null;
            if (job.Pixels == null) return;
            if (_texture == null || _texture.width != job.Side)
            {
                if (_texture != null) UnityEngine.Object.Destroy(_texture);
                if (_shade != null) UnityEngine.Object.Destroy(_shade);
                _side = job.Side;
                _texture = NewTexture(); _shade = NewTexture();
                _terrain.texture = _texture; _fog.texture = _shade;
            }
            _texture.SetPixels32(job.Pixels); _texture.Apply(false);
            _shade.SetPixels32(job.Shade); _shade.Apply(false);
            if (!job.Mist && _memoryKey != null && !Memory.ContainsKey(_memoryKey))
            {
                Memory[_memoryKey] = job.Full; MemoryOrder.Insert(0, _memoryKey);
                if (MemoryOrder.Count > MemoryPictures) { Memory.Remove(MemoryOrder[MemoryOrder.Count - 1]); MemoryOrder.RemoveAt(MemoryOrder.Count - 1); }
            }
            Version++;
            MapAccess.LargeShader(map).SetTexture("_FogTex", Reveal.Fog(map));
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("terrain overlays ready, " + job.Side + " px, view " + job.View + ", contours " + job.Contours + ", mist " + job.Mist + ", shallow water " + job.Shallows
                + ", " + job.Milliseconds.ToString("F0") + " ms on a background thread" + (job.Loaded ? ", from the cache" : ""));
        }

        private static Texture2D NewTexture()
        {
            return new Texture2D(_side, _side, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        }

        internal static Color32 DepthColor(float depth, float danger, float warning)
        {
            warning = Math.Max(warning, danger);
            return depth <= 0f || depth >= warning ? new Color32(0, 0, 0, 0) : depth < danger ? new Color32(196, 52, 40, 165) : new Color32(232, 206, 72, 135);
        }

        internal static Color Over(Color below, Color above)
        {
            float alpha = above.a + below.a * (1f - above.a);
            if (alpha <= 0f) return Color.clear;
            return new Color((above.r * above.a + below.r * below.a * (1f - above.a)) / alpha,
                (above.g * above.a + below.g * below.a * (1f - above.a)) / alpha,
                (above.b * above.a + below.b * below.a * (1f - above.a)) / alpha, alpha);
        }

        // the same drawing as before, from the kept arrays instead of the textures
        private static void BuildRow(Job job, int row)
        {
            var heights = DetailOverlay.HeightData; var biomes = DetailOverlay.BiomeData; var masks = DetailOverlay.MaskData;
            int width = DetailOverlay.DataSide, sy = Math.Min(width - 1, row * width / job.Side);
            int southY = Math.Max(0, sy - 1), northY = Math.Min(width - 1, sy + 1);
            float water = job.Water, pixel = job.Pixel;
            float wz = (sy - width / 2f) * pixel, edge = WorldGenerator.waterEdge * WorldGenerator.waterEdge;
            for (int x = 0; x < job.Side; x++)
            {
                int sx = Math.Min(width - 1, x * width / job.Side), index = row * job.Side + x, at = sy * width + sx;
                float wx = (sx - width / 2f) * pixel;
                // beyond the world's water edge the game shows open space, no overlay is drawn there
                if (wx * wx + wz * wz > edge) { job.Full[index] = new Color32(); continue; }
                Color color = Color.clear;
                float h = heights[at], depth = water - h;
                int left = Math.Max(0, sx - 1), right = Math.Min(width - 1, sx + 1);
                float slope = (heights[sy * width + right] - heights[sy * width + left]) / Math.Max(1f, (right - left) * pixel);
                float slopeZ = (heights[northY * width + sx] - heights[southY * width + sx]) / Math.Max(1f, (northY - southY) * pixel);
                float gradient = Mathf.Sqrt(slope * slope + slopeZ * slopeZ);
                float shade = Mathf.Clamp(0.92f - slope * 0.19f + slopeZ * 0.16f, 0.55f, 1.15f);
                var mask = masks[at];
                int biome = biomes[at];
                if (job.View == 2)
                {
                    color = depth > 0f ? Color.Lerp(new Color(0.32f, 0.45f, 0.48f), new Color(0.14f, 0.24f, 0.29f), Mathf.Clamp01(depth / 60f))
                        : Color.Lerp(new Color(0.48f, 0.49f, 0.40f), new Color(0.93f, 0.91f, 0.83f), Mathf.Pow(Mathf.Clamp01((h - water) / 220f), 0.8f)) * shade;
                    color.a = 0.97f;
                }
                else if (job.View == 1) { color = DetailOverlay.BiomeColor(biome); color.a = 0.86f; }
                if (job.Contours && depth <= 0f)
                {
                    float interval = job.ContourMeters, level = (h - water) / interval;
                    float gap = Math.Abs(level - (float)Math.Round(level)) * interval;
                    float thickness = Math.Min(interval * 0.2f, gradient * pixel * 0.55f);
                    if (gradient > 0.015f && level > 0.5f && gap < thickness)
                        color = Over(color, new Color(0.23f, 0.20f, 0.14f, (int)Math.Round(level) % 5 == 0 ? 0.65f : 0.35f));
                }
                if (job.Mist && mask.g > 12 && !MistClearings.Clear(wx, h + 1.8f, wz))
                    color = Over(color, new Color(0.67f, 0.64f, 0.70f, mask.g / 255f * 0.46f));
                if (job.Steep && depth <= 0f)
                {
                    float degrees = Mathf.Atan(gradient) * Mathf.Rad2Deg;
                    if (degrees > job.MaxSlope * 0.5f) color = Over(color, degrees > job.MaxSlope ? SteepColor : SlopeColor);
                }
                if (job.Shallows && biome != 2) color = Over(color, DepthColor(depth, job.Danger, job.Warning));
                // the game's mask blue holds the boiling sea gradient on water and the lava amount on ashlands ground
                float blue = mask.b / 255f;
                if (job.Heat && blue > 0.1f)
                    color = Over(color, depth > 0f ? new Color(BoilingColor.r, BoilingColor.g, BoilingColor.b, blue * 0.45f) : new Color(0.80f, 0.29f, 0.10f, blue * 0.62f));
                job.Full[index] = color;
            }
        }
    }
}
