using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the large map's current view drawn at up to screen resolution. heights are catmull-rom and slopes cubic b-spline samples of the game's
    // height texture, so shores, hillshade and contour lines stay smooth and continuous at any zoom (the method of the earlier map tool).
    // a new view is drawn on a background thread once panning or zooming settles, the last finished picture stays in place meanwhile
    internal static class DetailOverlay
    {
        // about half of a 1440p map view, the bilinear upscale keeps lines smooth and the redraw takes half the work
        private const int MaxPixels = 600000;
        private const float SettleSeconds = 0.15f;
        private const float MinorWidth = 0.9f, MajorWidth = 1.4f, MinorAlpha = 0.55f, MajorAlpha = 0.8f;
        private const float LightLength = 1.9899749f, FlatDot = 1.4f / LightLength;
        private static readonly Color ContourColor = new Color(0.13f, 0.10f, 0.07f, 1f);
        // the colors the game paints its biome texture with, in the order of the palettes below
        private static readonly Color32[] GameBiome =
        {
            new Color32(0x92, 0xa7, 0x5c, 255), new Color32(0x6b, 0x74, 0x3f, 255), new Color32(0xa3, 0x72, 0x58, 255), new Color32(0xe7, 0xab, 0x78, 255),
            new Color32(0x33, 0x33, 0x33, 255), new Color32(0x7b, 0x20, 0x20, 255), new Color32(0xff, 0xff, 0xff, 255)
        };
        // meadows, black forest, swamp, plains, mistlands, ashlands, mountain, other
        private static readonly Color[] Land = { C(138, 160, 84), C(84, 99, 55), C(118, 96, 70), C(214, 176, 112), C(88, 84, 100), C(100, 42, 36), C(226, 230, 234), C(150, 150, 150) };
        private static readonly Color[] Flat = { C(146, 167, 92), C(92, 104, 56), C(163, 114, 88), C(231, 171, 120), C(78, 76, 88), C(123, 32, 32), C(236, 238, 240), C(150, 150, 150) };
        private static readonly Color North = C(212, 226, 240), Sand = C(206, 192, 146), Lava = C(255, 122, 44);

        private static Color C(int r, int g, int b) { return new Color(r / 255f, g / 255f, b / 255f, 1f); }

        private static RectTransform _clip;
        private static RawImage _image;
        private static Texture2D _texture;
        // the game's textures read once into plain arrays, a few rows a frame
        private static Texture2D _cacheSource;
        private static float[] _height;
        private static byte[] _biome;
        private static Color32[] _mask;
        private static int _side, _cacheRow;
        private static bool _cached;
        // the picture shown
        private static Rect _shownUv, _lastUv;
        private static Vector2 _shownSize, _lastSize;
        private static string _shownKey;
        private static bool _dirty = true, _warned;
        private static float _moved, _interval = 20f;
        internal static float Interval => _interval;
        internal static bool Covering;
        internal static int Renders;

        // explored areas or mist clearings changed, kept pictures are out of date
        internal static void Dirty() { _dirty = true; Recent.Clear(); }

        private static void Hide()
        {
            Covering = false;
            if (_clip != null && _clip.gameObject.activeSelf) _clip.gameObject.SetActive(false);
        }

        internal static void Frame(Minimap map)
        {
            try
            {
                var image = map != null ? map.m_mapImageLarge : null;
                if (image == null || !Settings.Detail.Value || !TerrainOverlay.Active) { Hide(); return; }
                if (_clip == null) Create(image);
                if (!_clip.gameObject.activeSelf) _clip.gameObject.SetActive(true);
                var uv = image.uvRect;
                if (!EnsureCache(map)) { Place(uv); return; }
                var size = _clip.rect.size;
                string key = TerrainOverlay.Key();
                if (uv != _lastUv || size != _lastSize) { _lastUv = uv; _lastSize = size; _moved = Time.unscaledTime; }
                bool settled = Time.unscaledTime - _moved >= SettleSeconds;
                bool stale = _dirty || key != _shownKey || uv != _shownUv || size != _shownSize;
                if (!Settings.ShowUnexplored.Value) RefreshFog(MapAccess.Fog(map));
                bool jobCurrent = _job != null && Matches(_job, uv, size, key) && !_dirty;
                if (settled && stale && !jobCurrent && size.x >= 8f && size.y >= 8f) Start(map, uv, size, key);
                Collect();
                Place(uv);
            }
            catch (Exception ex)
            {
                Covering = false;
                if (!_warned) Plugin.Log.LogWarning("detailed map view unavailable: " + ex);
                _warned = true;
            }
        }

        private static void Create(RawImage image)
        {
            var go = new GameObject("ValheimNavigator_Detail", typeof(RectTransform), typeof(RectMask2D));
            go.transform.SetParent(image.transform, false);
            _clip = (RectTransform)go.transform;
            Ui.Stretch(_clip, 0f);
            go.transform.SetSiblingIndex(Math.Min(1, image.transform.childCount - 1));
            var picture = new GameObject("Picture", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            picture.transform.SetParent(go.transform, false);
            var rect = (RectTransform)picture.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            _image = picture.GetComponent<RawImage>();
            _image.raycastTarget = false;
            _image.enabled = false;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("detailed map view layer built");
        }

        // the finished picture keeps its place on the world while the map is panned or zoomed
        private static void Place(Rect uv)
        {
            if (_texture == null || _shownUv.width <= 0f || uv.width <= 0f || uv.height <= 0f)
            {
                Covering = false;
                if (_image != null) _image.enabled = false;
                return;
            }
            var rect = _clip.rect;
            var picture = (RectTransform)_image.transform;
            picture.anchoredPosition = new Vector2((_shownUv.x - uv.x) / uv.width * rect.width, (_shownUv.y - uv.y) / uv.height * rect.height);
            picture.sizeDelta = new Vector2(_shownUv.width / uv.width * rect.width, _shownUv.height / uv.height * rect.height);
            _image.enabled = true;
            float slack = uv.width * 0.002f;
            Covering = _shownKey == TerrainOverlay.Key() && _shownUv.xMin <= uv.xMin + slack && _shownUv.yMin <= uv.yMin + slack && _shownUv.xMax >= uv.xMax - slack && _shownUv.yMax >= uv.yMax - slack;
        }

        private static int Classify(Color32 c)
        {
            for (int i = 0; i < GameBiome.Length; i++)
                if (Math.Abs(c.r - GameBiome[i].r) < 4 && Math.Abs(c.g - GameBiome[i].g) < 4 && Math.Abs(c.b - GameBiome[i].b) < 4) return i;
            return 7;
        }

        // the cached game textures, shared with the route abstraction once complete
        internal static bool DataReady => _cached;
        internal static float[] HeightData => _height;
        internal static byte[] BiomeData => _biome;
        internal static Color32[] MaskData => _mask;
        internal static int DataSide => _side;

        internal static bool EnsureCache(Minimap map)
        {
            var height = MapAccess.Height(map);
            var biome = map.m_mapTexture;
            if (height == null || biome == null) return false;
            if (height != _cacheSource)
            {
                _cacheSource = height; _side = height.width; _cacheRow = 0; _cached = false;
                int n = _side * _side;
                if (_height == null || _height.Length != n) { _height = new float[n]; _biome = new byte[n]; _mask = new Color32[n]; }
            }
            if (_cached) return true;
            var mask = MapAccess.Mask(map);
            var clock = Stopwatch.StartNew();
            // the textures are read in place where their format is known (test 11: GetPixels made a new array per row, 200 MB at the first open)
            bool rawHeight = height.format == TextureFormat.RFloat;
            var heightData = rawHeight ? height.GetPixelData<float>(0) : default;
            int biomeStride = Layout(biome.format, out int br, out int bg, out int bb, out _);
            if (biome.width != _side) biomeStride = 0;
            var biomeData = biomeStride > 0 ? biome.GetPixelData<byte>(0) : default;
            int maskStride = mask != null ? Layout(mask.format, out int mr, out int mg, out int mb, out int ma) : Layout(TextureFormat.RFloat, out mr, out mg, out mb, out ma);
            if (mask == null || mask.width != _side) maskStride = 0;
            var maskData = maskStride > 0 ? mask.GetPixelData<byte>(0) : default;
            while (_cacheRow < _side && clock.Elapsed.TotalMilliseconds < Settings.DetailMilliseconds.Value)
            {
                int o = _cacheRow * _side;
                if (rawHeight) for (int x = 0; x < _side; x++) _height[o + x] = heightData[o + x];
                else { var h = height.GetPixels(0, _cacheRow, _side, 1); for (int x = 0; x < _side; x++) _height[o + x] = h[x].r; }
                if (biomeStride > 0)
                    for (int x = 0; x < _side; x++) { int s = (o + x) * biomeStride; _biome[o + x] = (byte)Classify(new Color32(biomeData[s + br], biomeData[s + bg], biomeData[s + bb], 255)); }
                else if (biome.width == _side) { var b = biome.GetPixels(0, _cacheRow, _side, 1); for (int x = 0; x < _side; x++) _biome[o + x] = (byte)Classify(b[x]); }
                if (maskStride > 0)
                    for (int x = 0; x < _side; x++) { int s = (o + x) * maskStride; _mask[o + x] = new Color32(maskData[s + mr], maskData[s + mg], maskData[s + mb], ma >= 0 ? maskData[s + ma] : (byte)255); }
                else if (mask != null && mask.width == _side) { var m = mask.GetPixels(0, _cacheRow, _side, 1); for (int x = 0; x < _side; x++) _mask[o + x] = m[x]; }
                _cacheRow++;
            }
            if (_cacheRow < _side) return false;
            _cached = true;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("detailed map view data ready, " + _side + " px");
            return true;
        }

        private static void SplineWeights(float t, float[] o, int at)
        {
            // catmull-rom weights for the value, cubic b-spline weights and b-spline slope weights
            float t2 = t * t, t3 = t2 * t, s = 1f - t;
            o[at] = 0.5f * (2f * t2 - t - t3); o[at + 1] = 0.5f * (2f - 5f * t2 + 3f * t3); o[at + 2] = 0.5f * (t + 4f * t2 - 3f * t3); o[at + 3] = 0.5f * (t3 - t2);
            o[at + 4] = s * s * s / 6f; o[at + 5] = (3f * t3 - 6f * t2 + 4f) / 6f; o[at + 6] = (3f * t2 + 3f * t + 1f - 3f * t3) / 6f; o[at + 7] = t3 / 6f;
            o[at + 8] = -0.5f * s * s; o[at + 9] = 0.5f * (3f * t2 - 4f * t); o[at + 10] = 0.5f * (1f + 2f * t - 3f * t2); o[at + 11] = 0.5f * t2;
        }

        private static int Clamp(int i) { return i < 0 ? 0 : i >= _side ? _side - 1 : i; }

        // one picture being drawn on a background thread. every value it needs is copied in, so the game's frame never waits for it
        private sealed class Job
        {
            internal Rect Uv; internal Vector2 Size; internal string Key;
            internal int W, H, View;
            internal float Interval, Mpp, Pixel, Water, Danger, Warning, MaxSlope;
            internal bool Contours, Mist, Steep, Shallows, Heat, Fogged;
            internal int[] Cols; internal float[] ColWeights, Wx, Fx;
            internal Color32[] Pixels;
            internal volatile bool Done, Cancel;
            internal double Milliseconds;
        }

        private static Job _job;
        // the last pictures drawn, a view seen again is shown at once
        private const int RecentViews = 6;
        private static readonly List<Job> Recent = new List<Job>();
        // explored state of the game's fog texture, one byte a texel, refreshed a few rows a frame
        private static byte[] _fog;
        private static int _fogRow;
        private static bool _fogFull;
        private static Texture2D _fogSource;

        private static int _fogFrame = -1;
        internal static byte[] FogData => _fog;
        internal static bool FogReady => _fogFull;
        // the game's palette color of a biome index, for the biome view
        internal static Color BiomeColor(int index) { return index < GameBiome.Length ? (Color)GameBiome[index] : new Color(0.20f, 0.35f, 0.45f, 1f); }

        // once a frame at most, both map layers call it
        internal static void RefreshFog(Texture2D fog)
        {
            if (_fogFrame == Time.frameCount) return;
            _fogFrame = Time.frameCount;
            if (fog == null || fog.width != _side || fog.height != _side) { _fogFull = false; return; }
            if (_fog == null || _fog.Length != _side * _side || fog != _fogSource) { _fog = new byte[_side * _side]; _fogRow = 0; _fogFull = false; _fogSource = fog; }
            int rows = _fogFull ? 32 : 256;
            // read in place where the format is known: GetPixels made a 32 KB array per row, about 1 MB a frame while the map was open (test 11)
            int stride = Layout(fog.format, out int red, out _, out _, out _);
            var data = stride > 0 ? fog.GetPixelData<byte>(0) : default;
            for (int i = 0; i < rows; i++)
            {
                int o = _fogRow * _side;
                if (stride > 0) for (int x = 0; x < _side; x++) _fog[o + x] = data[(o + x) * stride + red] > 127 ? (byte)1 : (byte)0;
                else { var row = fog.GetPixels(0, _fogRow, _side, 1); for (int x = 0; x < _side; x++) _fog[o + x] = row[x].r > 0.5f ? (byte)1 : (byte)0; }
                if (++_fogRow >= _side) { _fogRow = 0; _fogFull = true; }
            }
        }

        // bytes per texel and the byte offsets of red, green, blue and alpha (-1 without alpha) of 8-bit formats, 0 for any other format
        internal static int Layout(TextureFormat format, out int r, out int g, out int b, out int a)
        {
            switch (format)
            {
                case TextureFormat.RGBA32: r = 0; g = 1; b = 2; a = 3; return 4;
                case TextureFormat.ARGB32: a = 0; r = 1; g = 2; b = 3; return 4;
                case TextureFormat.BGRA32: b = 0; g = 1; r = 2; a = 3; return 4;
                case TextureFormat.RGB24: r = 0; g = 1; b = 2; a = -1; return 3;
                case TextureFormat.R8: r = g = b = 0; a = -1; return 1;
                default: r = g = b = 0; a = -1; return 0;
            }
        }

        private static bool Matches(Job job, Rect uv, Vector2 size, string key) { return job.Uv == uv && job.Size == size && job.Key == key; }

        private static void Start(Minimap map, Rect uv, Vector2 size, string key)
        {
            var recent = Recent.Find(j => Matches(j, uv, size, key));
            if (recent != null) { Show(recent); _dirty = false; return; }
            if (!Settings.ShowUnexplored.Value && !_fogFull) return;
            if (_job != null) _job.Cancel = true;
            float scale = Mathf.Min(1f, Mathf.Sqrt(MaxPixels / (size.x * size.y)));
            var job = new Job { Uv = uv, Size = size, Key = key, W = Math.Max(8, Mathf.RoundToInt(size.x * scale)), H = Math.Max(8, Mathf.RoundToInt(size.y * scale)) };
            job.Pixels = new Color32[job.W * job.H];
            job.Cols = new int[job.W * 4]; job.ColWeights = new float[job.W * 12]; job.Wx = new float[job.W]; job.Fx = new float[job.W];
            job.Pixel = map.m_pixelSize;
            for (int px = 0; px < job.W; px++)
            {
                float tx = (uv.x + (px + 0.5f) / job.W * uv.width) * _side;
                int c0 = Mathf.FloorToInt(tx);
                for (int k = 0; k < 4; k++) job.Cols[px * 4 + k] = Clamp(c0 - 1 + k);
                SplineWeights(tx - c0, job.ColWeights, px * 12);
                job.Wx[px] = (tx - _side / 2f) * job.Pixel;
                job.Fx[px] = tx - c0;
            }
            job.Mpp = uv.width * _side * job.Pixel / job.W;
            // contours get finer when zoomed in, like the earlier map tool: 5 m from 0.9 screen pixels a meter, 10 m from 0.35
            float screenPixelsPerMeter = size.x / (uv.width * _side * job.Pixel), interval = Settings.ContourMeters.Value;
            job.Interval = !Settings.ContourAuto.Value ? interval : screenPixelsPerMeter >= 0.9f ? Math.Min(interval, 5f) : screenPixelsPerMeter >= 0.35f ? Math.Min(interval, 10f) : interval;
            job.Water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            job.View = Settings.TerrainView.Value; job.Contours = Settings.Contours.Value; job.Mist = Settings.Mist.Value; job.Steep = Settings.Steepness.Value;
            job.Shallows = Settings.Shallows.Value; job.Heat = Settings.AshlandsHeat.Value; job.Fogged = !Settings.ShowUnexplored.Value;
            job.Danger = Settings.BoatDangerDepth.Value; job.Warning = Math.Max(Settings.BoatWarningDepth.Value, job.Danger); job.MaxSlope = Settings.MaxSlope.Value;
            _job = job; _dirty = false;
            ThreadPool.QueueUserWorkItem(Run, job);
        }

        private static void Run(object state)
        {
            var job = (Job)state;
            var clock = Stopwatch.StartNew();
            try
            {
                var rowWeights = new float[12];
                var rowOffsets = new int[4];
                for (int py = 0; py < job.H && !job.Cancel; py++) Row(job, py, rowWeights, rowOffsets);
            }
            catch (Exception ex) { job.Cancel = true; if (!_warned) Plugin.Log.LogWarning("detailed map view row failed: " + ex.Message); _warned = true; }
            job.Milliseconds = clock.Elapsed.TotalMilliseconds;
            job.Done = true;
        }

        // the finished picture goes into the texture on the game's thread
        private static void Collect()
        {
            var job = _job;
            if (job == null || !job.Done) return;
            _job = null;
            if (job.Cancel) return;
            Show(job);
            Recent.Insert(0, job);
            if (Recent.Count > RecentViews) Recent.RemoveAt(Recent.Count - 1);
            Renders++;
            if (Plugin.LogCounters.Value && (Renders <= 3 || Renders % 20 == 0))
                Plugin.Log.LogInfo("detailed map view drawn, " + job.W + "x" + job.H + ", " + job.Milliseconds.ToString("F0") + " ms on a background thread, render " + Renders);
        }

        private static void Show(Job job)
        {
            if (_texture == null || _texture.width != job.W || _texture.height != job.H)
            {
                if (_texture != null) UnityEngine.Object.Destroy(_texture);
                _texture = new Texture2D(job.W, job.H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            _texture.SetPixels32(job.Pixels);
            _texture.Apply(false);
            _image.texture = _texture;
            _shownUv = job.Uv; _shownSize = job.Size; _shownKey = job.Key; _interval = job.Interval;
        }

        private static float Shade(float gx, float gz, float ex)
        {
            gx *= ex; gz *= ex;
            float dot = (gx + 1.4f - gz) / (Mathf.Sqrt(gx * gx + 1f + gz * gz) * LightLength);
            return Mathf.Clamp(dot / FlatDot, 0.42f, 1.32f);
        }

        private static Color Mix(Color a, Color b, float t) { return new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t); }

        private static void Row(Job job, int py, float[] rowWeights, int[] rowOffsets)
        {
            float pixel = job.Pixel, water = job.Water;
            float ty = (job.Uv.y + (py + 0.5f) / job.H * job.Uv.height) * _side;
            int r0 = Mathf.FloorToInt(ty), rn = Clamp(r0), rn1 = Clamp(r0 + 1);
            float fy = ty - r0;
            SplineWeights(fy, rowWeights, 0);
            for (int k = 0; k < 4; k++) rowOffsets[k] = Clamp(r0 - 1 + k) * _side;
            float wz = (ty - _side / 2f) * pixel, edge = WorldGenerator.waterEdge * WorldGenerator.waterEdge;
            var fog = job.Fogged ? _fog : null;
            int view = job.View;
            float interval = job.Interval, danger = job.Danger, warning = job.Warning, maxSlope = job.MaxSlope;
            int w = job.W;
            for (int px = 0, p = py * w; px < w; px++, p++)
            {
                float wx = job.Wx[px];
                int cn = job.Cols[px * 4 + 1], cn1 = job.Cols[px * 4 + 2];
                if (wx * wx + wz * wz > edge || fog != null && fog[rn * _side + cn] != 0) { job.Pixels[p] = new Color32(); continue; }
                int k = px * 12;
                float h = 0f, hu = 0f, hr = 0f;
                for (int r = 0; r < 4; r++)
                {
                    int o = rowOffsets[r];
                    float a0 = _height[o + job.Cols[px * 4]], a1 = _height[o + cn], a2 = _height[o + cn1], a3 = _height[o + job.Cols[px * 4 + 3]];
                    h += rowWeights[r] * (job.ColWeights[k] * a0 + job.ColWeights[k + 1] * a1 + job.ColWeights[k + 2] * a2 + job.ColWeights[k + 3] * a3);
                    hu += rowWeights[4 + r] * (job.ColWeights[k + 8] * a0 + job.ColWeights[k + 9] * a1 + job.ColWeights[k + 10] * a2 + job.ColWeights[k + 11] * a3);
                    hr += rowWeights[8 + r] * (job.ColWeights[k + 4] * a0 + job.ColWeights[k + 5] * a1 + job.ColWeights[k + 6] * a2 + job.ColWeights[k + 7] * a3);
                }
                float gx = hu / pixel, gz = hr / pixel, slope = Mathf.Sqrt(gx * gx + gz * gz);
                float gpx = Mathf.Max(0.001f, slope * job.Mpp), hh = h - water, depth = water - h;
                int i00 = rn * _side + cn;
                int b = _biome[i00];
                float fx = job.Fx[px];
                Color32 m00 = _mask[i00], m01 = _mask[rn * _side + cn1], m10 = _mask[rn1 * _side + cn], m11 = _mask[rn1 * _side + cn1];
                float blue = ((m00.b * (1f - fx) + m01.b * fx) * (1f - fy) + (m10.b * (1f - fx) + m11.b * fx) * fy) / 255f;
                Color color = Color.clear;
                if (view != 0)
                {
                    bool north = b == 6 && wx * wx + (wz + 4000f) * (wz + 4000f) > 144e6f;
                    float land = Mathf.Clamp01((h - (b == 2 ? water - 2.2f : water)) / gpx + 0.5f);
                    Color ground;
                    if (view == 2)
                    {
                        float v = (58f + 197f * Mathf.Pow(Mathf.Clamp01(hh / 220f), 0.8f)) / 255f * Shade(gx, gz, 2.2f);
                        ground = new Color(v * 0.97f, v, v * 0.95f, 1f);
                    }
                    else
                    {
                        // biome colors and forest blend with the same b-spline weights as the slopes, so borders are smooth curves.
                        // the game paints open ocean white like mountains, such texels take the middle texel's biome
                        var palette = view == 1 ? Flat : Land;
                        float gr = 0f, gg = 0f, gb = 0f, forest = 0f;
                        for (int r = 0; r < 4; r++)
                        {
                            int o = rowOffsets[r];
                            for (int c = 0; c < 4; c++)
                            {
                                int index = o + job.Cols[px * 4 + c];
                                float wgt = rowWeights[4 + r] * job.ColWeights[k + 4 + c];
                                int bi = _biome[index];
                                if (bi == 6 && _height[index] < water) bi = b;
                                var pc = palette[bi];
                                gr += pc.r * wgt; gg += pc.g * wgt; gb += pc.b * wgt;
                                forest += _mask[index].r * wgt;
                            }
                        }
                        ground = new Color(gr, gg, gb, 1f);
                        forest /= 255f;
                        if (north) ground = view == 1 ? C(206, 222, 240) : North;
                        if (view == 3)
                        {
                            if (b <= 3 && forest > 0f) { float f = 1f - 0.2f * forest; ground.r *= f * 0.94f; ground.g *= f; ground.b *= f * 0.88f; }
                            if (b != 2 && b != 5 && b != 6) ground = Mix(ground, Sand, 0.45f * Mathf.Clamp01((1.2f - hh) / gpx + 0.5f));
                            float s = Shade(gx, gz, 2.6f);
                            ground.r *= s; ground.g *= s; ground.b *= s;
                            if (b == 5 && blue > 0f) ground = Mix(ground, Lava, Math.Min(1f, blue * 1.15f));
                        }
                    }
                    Color sea;
                    if (view == 2) { float e = Mathf.Clamp01(depth / 60f); sea = C((int)(26 - 14 * e), (int)(64 - 36 * e), (int)(104 - 46 * e)); }
                    else if (view == 1) sea = C(36, 74, 98);
                    else
                    {
                        float e = Mathf.Pow(Mathf.Clamp01(depth / 45f), 0.55f);
                        sea = b == 2 ? new Color((76 - 22 * e) / 255f, (84 - 24 * e) / 255f, (60 - 12 * e) / 255f, 1f)
                            : b == 5 ? new Color((60 - 30 * e) / 255f, (40 - 22 * e) / 255f, (44 - 18 * e) / 255f, 1f)
                            : new Color((78 - 60 * e) / 255f, (132 - 92 * e) / 255f, (142 - 80 * e) / 255f, 1f);
                    }
                    color = Mix(sea, ground, land);
                    color.a = view == 1 ? 0.92f : 1f;
                }
                if (job.Contours && hh > 0f)
                {
                    int level = Mathf.RoundToInt(hh / interval);
                    if (level > 0)
                    {
                        bool major = level % 5 == 0;
                        float gap = (major ? 5f * interval : interval) / gpx;
                        float a = Mathf.Clamp01((major ? MajorWidth : MinorWidth) - Math.Abs(hh - level * interval) / gpx) * (major ? MajorAlpha : MinorAlpha) * Mathf.Clamp01((gap - 2.5f) / 3f);
                        if (a > 0f) color = TerrainOverlay.Over(color, new Color(ContourColor.r, ContourColor.g, ContourColor.b, a));
                    }
                }
                if (job.Mist && m00.g > 12 && !MistClearings.Clear(wx, h + 1.8f, wz))
                    color = TerrainOverlay.Over(color, new Color(0.67f, 0.64f, 0.70f, m00.g / 255f * 0.46f));
                if (job.Steep && hh > 0f)
                {
                    float degrees = Mathf.Atan(slope) * Mathf.Rad2Deg;
                    if (degrees > maxSlope * 0.5f) color = TerrainOverlay.Over(color, degrees > maxSlope ? TerrainOverlay.SteepColor : TerrainOverlay.SlopeColor);
                }
                // swamp ground under the water line is wading ground, not sea
                if (job.Shallows && b != 2 && depth > -gpx && depth < warning + gpx)
                {
                    float e0 = Mathf.Clamp01(depth / gpx + 0.5f), e1 = Mathf.Clamp01((depth - danger) / gpx + 0.5f), e2 = Mathf.Clamp01((depth - warning) / gpx + 0.5f);
                    float red = (e0 - e1) * 0.65f, yellow = (e1 - e2) * 0.53f;
                    if (red > 0f) color = TerrainOverlay.Over(color, new Color(196 / 255f, 52 / 255f, 40 / 255f, red));
                    if (yellow > 0f) color = TerrainOverlay.Over(color, new Color(232 / 255f, 206 / 255f, 72 / 255f, yellow));
                }
                if (job.Heat && blue > 0.1f)
                {
                    if (depth > 0f) color = TerrainOverlay.Over(color, new Color(TerrainOverlay.BoilingColor.r, TerrainOverlay.BoilingColor.g, TerrainOverlay.BoilingColor.b, blue * 0.45f));
                    else if (view != 3 && b == 5) color = TerrainOverlay.Over(color, new Color(0.80f, 0.29f, 0.10f, blue * 0.62f));
                }
                job.Pixels[p] = color;
            }
        }
    }
}