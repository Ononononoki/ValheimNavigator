using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        private static RawImage _terrain, _fog, _shared;
        // the shade over unexplored land and, apart, over land only a cartography table explored, so the game's live shared fade applies
        private static Texture2D _source, _shade, _sharedShade, _texture;
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
                + "/" + Settings.UnexploredShown + "/" + Settings.ContourMeters.Value + "/" + Settings.BoatDangerDepth.Value + "/" + Settings.BoatWarningDepth.Value + "/" + Settings.AshlandsHeat.Value + "/" + Settings.ContourAuto.Value + "/" + Settings.MaxSlope.Value + "/" + SharedDrawn;
        }

        // the settings the full picture depends on. the explored state (map mode, Show unexplored, cartography land) and the mist are applied
        // after it, so switching those reuses the kept full picture and redoes only that step (test 15: 72 to 82 ms on every mode switch,
        // test 16: still 44 to 107 ms with Mist on, the mist was part of the full picture then)
        internal static string FullKey()
        {
            return Settings.TerrainView.Value + "/" + Settings.Contours.Value + "/" + Settings.Shallows.Value + "/" + Settings.Steepness.Value
                + "/" + Settings.ContourMeters.Value + "/" + Settings.BoatDangerDepth.Value + "/" + Settings.BoatWarningDepth.Value + "/" + Settings.AshlandsHeat.Value + "/" + Settings.MaxSlope.Value;
        }

        // one picture being made on a background thread with copies of every setting it uses
        private sealed class Job
        {
            internal string Key, File;
            internal int Side, View;
            internal float Pixel, Water, ContourMeters, Danger, Warning, MaxSlope;
            internal bool Contours, Mist, Steep, Shallows, Heat, ShowUnexplored, Shared;
            internal Color32[] Full, Pixels, Shade, SharedShade, Clouds;
            // the shade pictures depend only on the fog, the clouds and the size: unchanged since the last upload, they are not drawn again
            internal string ShadeKey;
            internal bool ShadeSame;
            internal int CloudSide;
            internal volatile bool Done;
            internal double Milliseconds, FogMilliseconds;
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
        internal static string World()
        {
            var height = DetailOverlay.HeightData;
            unchecked
            {
                int hash = 17;
                for (int i = 0; i < height.Length; i += 4099) hash = hash * 31 + height[i].GetHashCode();
                return height.Length + "_" + hash.ToString("x8");
            }
        }

        private static RawImage _miniTerrain, _miniFog, _miniShared;
        private static float _nextMiniBuild;
        private const float MiniBuildSeconds = 1f;

        // the minimap shows the same pictures as the large map (cfg Overview.MinimapTerrain). while the large map is closed the pictures are
        // kept up to date once a second, so a new world or a changed view reaches the minimap too
        internal static void MiniFrame(Minimap map)
        {
            try
            {
                var small = map != null ? map.m_mapImageSmall : null;
                if (small == null) return;
                bool on = Settings.MinimapTerrain.Value && map.m_mode == Minimap.MapMode.Small;
                if (on && Time.unscaledTime >= _nextMiniBuild && (Active || Settings.UnexploredShown))
                {
                    _nextMiniBuild = Time.unscaledTime + MiniBuildSeconds;
                    Frame(map);
                }
                if (_miniTerrain == null)
                {
                    if (!on) return;
                    _miniTerrain = Layer(small, "ValheimNavigator_MiniTerrain", 0);
                    _miniFog = Layer(small, "ValheimNavigator_MiniShade", 1);
                    _miniShared = Layer(small, "ValheimNavigator_MiniSharedShade", 2);
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("minimap view layers built on " + small.name + ", " + small.transform.childCount + " children");
                }
                bool terrain = on && Active && Version > 0 && _texture != null && !DetailOverlay.MiniCovering, shade = on && Settings.UnexploredShown && Version > 0 && _shade != null;
                bool shared = on && SharedShown && (Active || Settings.UnexploredShown) && Version > 0 && _sharedShade != null;
                if (Plugin.LogCounters.Value && map.m_mode == Minimap.MapMode.Small)
                {
                    // once per change while the minimap is up: what it shows and why
                    string state = !Settings.MinimapTerrain.Value ? "hidden, MinimapTerrain off"
                        : Version == 0 ? "waiting for the first picture" : "view " + Settings.TerrainView.Value + " " + (Active && _texture != null ? "shown" : "off (native view, no overlay)") + (Settings.Detail.Value ? " sharp" : " coarse") + ", shade " + shade + ", cartography shade " + shared;
                    if (state != _miniState) { _miniState = state; Plugin.Log.LogInfo("minimap view " + state + ", uv " + small.uvRect.width.ToString("F3")); }
                }
                if (_miniTerrain.gameObject.activeSelf != terrain) _miniTerrain.gameObject.SetActive(terrain);
                if (_miniFog.gameObject.activeSelf != shade) _miniFog.gameObject.SetActive(shade);
                if (_miniShared.gameObject.activeSelf != shared) _miniShared.gameObject.SetActive(shared);
                if (!terrain && !shade && !shared) return;
                ReadFade(map);
                _miniTerrain.texture = _texture; _miniFog.texture = _shade; _miniShared.texture = _sharedShade;
                _miniTerrain.uvRect = _miniFog.uvRect = _miniShared.uvRect = small.uvRect;
                _miniFog.color = ShadeColor; _miniShared.color = SharedColor;
            }
            catch (Exception ex)
            {
                if (!_miniWarned) Plugin.Log.LogWarning("minimap terrain unavailable: " + ex.Message);
                _miniWarned = true;
            }
        }

        private static bool _miniWarned;
        private static string _miniState;

        internal static void Frame(Minimap map)
        {
            try
            {
                if (map == null || map.m_mapImageLarge == null) return;
                if (_terrain == null)
                {
                    _terrain = Layer(map.m_mapImageLarge, "ValheimNavigator_Terrain", 0);
                    _fog = Layer(map.m_mapImageLarge, "ValheimNavigator_Shade", 1);
                    _shared = Layer(map.m_mapImageLarge, "ValheimNavigator_SharedShade", 2);
                    // a rebuilt map gets the kept pictures, a layer without a texture draws white (friend's test: white map while panning)
                    if (_texture != null) { _terrain.texture = _texture; _fog.texture = _shade; _shared.texture = _sharedShade; }
                }
                var source = MapAccess.Fog(map);
                MistClearings.Frame();
                ReadFade(map);
                string key = Key();
                if (source != _source || key != _key) _dirty = true;
                // the native map shows until the first build is done, an unbuilt texture would cover it in grey
                _terrain.gameObject.SetActive(Active && Version > 0 && !DetailOverlay.Covering);
                _fog.gameObject.SetActive(Settings.UnexploredShown && Version > 0);
                _shared.gameObject.SetActive(SharedShown && (Active || Settings.UnexploredShown) && Version > 0);
                if (!Active && !Settings.UnexploredShown) return;
                _terrain.uvRect = _fog.uvRect = _shared.uvRect = map.m_mapImageLarge.uvRect;
                _fog.color = ShadeColor; _shared.color = SharedColor;
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
                Heat = Settings.AshlandsHeat.Value, ShowUnexplored = Settings.UnexploredShown, Shared = SharedDrawn
            };
            // the full picture holds no mist (it depends on loaded clearings and goes on in the explored step), so it is always kept
            string memoryKey = World() + "|" + FullKey();
            job.File = Path.Combine(CacheFolder(), "terrain_" + memoryKey.GetStableHashCode().ToString("x8") + "_" + side + ".bin");
            if (Memory.TryGetValue(memoryKey, out var kept)) { job.Full = kept; job.Loaded = true; }
            if (Settings.FogClouds.Value) ReadClouds(map);
            job.Clouds = Settings.FogClouds.Value ? _clouds : null; job.CloudSide = _cloudSide;
            job.ShadeKey = DetailOverlay.FogWrites + "|" + (job.Clouds != null) + "|" + side + "|" + World();
            job.ShadeSame = job.ShadeKey == _shadeKey && _shade != null && _shade.width == side;
            // the arrays of the last picture are reused, its texture already holds a copy
            job.Pixels = _sparePixels; job.Shade = _spareShade; job.SharedShade = _spareShared;
            _sparePixels = _spareShade = _spareShared = null;
            _job = job; _memoryKey = memoryKey;
            ThreadPool.QueueUserWorkItem(Run, job);
        }

        private static string _memoryKey, _shadeKey;
        internal static int ShadeUploads;
        private static Color32[] _sparePixels, _spareShade, _spareShared;

        // the game's own map clouds (the large map material's fog layer texture), its fog color and how far it fades shared exploration,
        // read once so the unexplored shade looks like the game's fog
        private static Color32[] _clouds;
        private static int _cloudSide;
        private static float _sharedFade = 0.5f;
        private static Color _fogColor = new Color(0.13f, 0.12f, 0.10f, 1f);
        private static bool _cloudsRead;
        // one cloud tile spans this many map texels (about 3 km), close to the game's look at a middle zoom
        private const float CloudTexels = 256f;

        private static void ReadClouds(Minimap map)
        {
            if (_cloudsRead) return;
            _cloudsRead = true;
            try
            {
                var material = MapAccess.LargeShader(map);
                if (material == null) return;
                if (material.HasProperty("_SharedFade")) _sharedFade = Mathf.Clamp01(material.GetFloat("_SharedFade"));
                if (material.HasProperty("_FogColor")) { var c = material.GetColor("_FogColor"); _fogColor = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f); }
                var source = material.HasProperty("_FogLayerTex") ? material.GetTexture("_FogLayerTex") : null;
                if (source == null && material.HasProperty("_CloudTex")) source = material.GetTexture("_CloudTex");
                if (source == null) return;
                // the texture may not be readable, so it is drawn into a small render texture and read back once
                int size = Math.Min(512, Math.Max(16, Math.Min(source.width, source.height)));
                var target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
                var active = RenderTexture.active;
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var copy = new Texture2D(size, size, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                copy.Apply(false);
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                _clouds = copy.GetPixels32();
                _cloudSide = size;
                UnityEngine.Object.Destroy(copy);
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("map clouds read, " + source.name + " " + source.width + "x" + source.height + ", shared fade " + _sharedFade.ToString("F2") + ", fog color " + _fogColor);
            }
            catch (Exception ex) { _clouds = null; Plugin.Log.LogWarning("the game's map clouds could not be read, the plain shade stays: " + ex.Message); }
        }

        // the shade's strength: 0 is no shade, 0.5 a clear fog with the terrain visible through it (test 16: 0.85 was too dark)
        internal static float Strength => Settings.FogTint.Value <= 0f ? 0f : Mathf.Min(0.95f, Settings.FogTint.Value * 1.3f);
        // land only a cartography table explored keeps this share of the shade while the game shows the shared map fully
        private const float SharedTint = 0.35f;
        // the game's fog color with the clouds, else a plain dark shade, lit by the game's live sun and ambient light
        private static Color Tint
        {
            get
            {
                var c = Settings.FogClouds.Value && _clouds != null ? _fogColor : new Color(0.13f, 0.12f, 0.10f, 1f);
                return new Color(c.r * _light.r, c.g * _light.g, c.b * _light.b, 1f);
            }
        }

        // the map's fog color never changes in the game's code (only _SharedFade is set), the day and night look of the map comes from
        // EnvMan's global light (SetEnv writes _AmbientColor and _SunColor). before the first value it stays white
        private static Color _light = Color.white;
        private static int _ambientId = -1, _sunId = -1;
        private static float _loggedLight = -1f;

        private static void ReadLight()
        {
            if (_ambientId < 0) { _ambientId = Shader.PropertyToID("_AmbientColor"); _sunId = Shader.PropertyToID("_SunColor"); }
            Color ambient = Shader.GetGlobalColor(_ambientId), sun = Shader.GetGlobalColor(_sunId);
            var light = new Color(Mathf.Clamp01(ambient.r + sun.r), Mathf.Clamp01(ambient.g + sun.g), Mathf.Clamp01(ambient.b + sun.b), 1f);
            // no light set yet (the menu, a loading world) keeps the plain color
            if (Mathf.Max(light.r, Mathf.Max(light.g, light.b)) < 0.05f) light = Color.white;
            _light = light;
            float level = (light.r + light.g + light.b) / 3f;
            if (Plugin.LogCounters.Value && Math.Abs(level - _loggedLight) >= 0.2f)
            {
                _loggedLight = level;
                var tint = Tint;
                Plugin.Log.LogInfo("map shade light, ambient " + ambient + ", sun " + sun + ", shade color RGB(" + tint.r.ToString("F2") + ", " + tint.g.ToString("F2") + ", " + tint.b.ToString("F2") + ")");
            }
        }
        internal static Color ShadeColor { get { var c = Tint; c.a = Strength; return c; } }
        // the game fades shared land in and out with the Cartography Table button, the layer follows its live value
        internal static Color SharedColor { get { var c = Tint; c.a = Strength * Mathf.Lerp(1f, SharedTint, _sharedFade); return c; } }
        // with the unexplored shade, and over cartography land when our own view is drawn there (the native view keeps the game's tint)
        internal static bool SharedShown => Settings.UnexploredShown || SharedDrawn && Settings.TerrainView.Value != 0;
        // cartography land gets our view while ExplorerShared is on and the game shows the table's map (its Cartography Table switch), so
        // switching the table map off takes our view, contours included, off that land too (test 16)
        internal static bool SharedDrawn => Settings.ExplorerShared.Value && _sharedFade >= 0.5f;
        private static int _sharedLogged = -1;
        internal static Texture SharedShade => _sharedShade;

        private static int _fadeFrame = -1, _fadeId = -1;
        private static float _loggedFade = -1f;

        // _SharedFade is set by Minimap.Update every frame it changes (the game's only write to the map material), read once a frame
        private static void ReadFade(Minimap map)
        {
            if (_fadeFrame == Time.frameCount) return;
            _fadeFrame = Time.frameCount;
            ReadLight();
            if (_fadeId < 0) _fadeId = Shader.PropertyToID("_SharedFade");
            var material = MapAccess.LargeShader(map);
            if (material == null || !material.HasProperty(_fadeId)) return;
            _sharedFade = Mathf.Clamp01(material.GetFloat(_fadeId));
            int drawn = SharedDrawn ? 1 : 0;
            if (drawn != _sharedLogged)
            {
                _sharedLogged = drawn;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("cartography land " + (drawn == 1 ? "drawn in our view" : "left to the game's fog (table map off or ExplorerShared off)"));
            }
            float end = _sharedFade <= 0.001f ? 0f : _sharedFade >= 0.999f ? 1f : -1f;
            if (end >= 0f && end != _loggedFade)
            {
                _loggedFade = end;
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("shared map fade " + end.ToString("F0") + ", cartography shade alpha " + SharedColor.a.ToString("F2") + ", unexplored shade alpha " + Strength.ToString("F2"));
            }
        }
        // the disk cache is read into one kept buffer, only one picture is made at a time
        private static byte[] _readBuffer;

        private static void Run(object state)
        {
            var job = (Job)state;
            var clock = Stopwatch.StartNew();
            try
            {
                int n = job.Side * job.Side;
                if (job.Full == null && job.File != null && File.Exists(job.File) && new FileInfo(job.File).Length == n * 4L)
                {
                    if (_readBuffer == null || _readBuffer.Length != n * 4) _readBuffer = new byte[n * 4];
                    var bytes = _readBuffer;
                    int read = 0;
                    using (var stream = File.OpenRead(job.File))
                        while (read < bytes.Length) { int got = stream.Read(bytes, read, bytes.Length - read); if (got <= 0) break; read += got; }
                    if (read == n * 4)
                    {
                        job.Full = new Color32[n];
                        for (int i = 0, b = 0; i < n; i++, b += 4) job.Full[i] = new Color32(bytes[b], bytes[b + 1], bytes[b + 2], bytes[b + 3]);
                        job.Loaded = true;
                    }
                }
                if (job.Full == null)
                {
                    job.Full = new Color32[n];
                    // each row writes only its own pixels, so rows are shared out over a few threads
                    Parallel.For(0, job.Side, new ParallelOptions { MaxDegreeOfParallelism = DetailOverlay.Threads }, row => BuildRow(job, row));
                    if (job.File != null) Save(job);
                }
                double fogStart = clock.Elapsed.TotalMilliseconds;
                // the explored state is applied last, it changes as you explore while the full picture does not
                var fog = DetailOverlay.FogData;
                int side = DetailOverlay.DataSide;
                // pixels beyond the world's edge are skipped below, so reused arrays start cleared
                if (job.Pixels == null || job.Pixels.Length != n) job.Pixels = new Color32[n]; else Array.Clear(job.Pixels, 0, n);
                if (!job.ShadeSame)
                {
                    if (job.Shade == null || job.Shade.Length != n) job.Shade = new Color32[n]; else Array.Clear(job.Shade, 0, n);
                    if (job.SharedShade == null || job.SharedShade.Length != n) job.SharedShade = new Color32[n]; else Array.Clear(job.SharedShade, 0, n);
                }
                float edge = WorldGenerator.waterEdge * WorldGenerator.waterEdge;
                var heights = DetailOverlay.HeightData; var masks = DetailOverlay.MaskData;
                // each row writes only its own pixels, so rows are shared out like the full picture's
                Parallel.For(0, job.Side, new ParallelOptions { MaxDegreeOfParallelism = DetailOverlay.Threads }, row =>
                {
                    int sy = Math.Min(side - 1, row * side / job.Side);
                    float wz = (sy - side / 2f) * job.Pixel;
                    for (int x = 0; x < job.Side; x++)
                    {
                        int sx = Math.Min(side - 1, x * side / job.Side), index = row * job.Side + x;
                        // beyond the world's water edge the game shows open space, no shade either
                        float wx = (sx - side / 2f) * job.Pixel;
                        if (wx * wx + wz * wz > edge) continue;
                        byte fogState = fog != null ? fog[sy * side + sx] : (byte)0;
                        // clear texels stay white, so the filtered edge of the shade does not darken
                        if (!job.ShadeSame) job.Shade[index] = job.SharedShade[index] = new Color32(255, 255, 255, 0);
                        if (fogState != 0 && !job.ShadeSame)
                        {
                            // the game's cloud texture tiled over the world. land only others explored goes into its own picture, its layer
                            // follows the game's live shared fade
                            byte light = 255;
                            if (job.Clouds != null)
                            {
                                var cloud = job.Clouds[(int)(sy / CloudTexels * job.CloudSide) % job.CloudSide * job.CloudSide + (int)(sx / CloudTexels * job.CloudSide) % job.CloudSide];
                                light = (byte)Math.Max(cloud.r, Math.Max(cloud.g, cloud.b));
                            }
                            if (fogState == 2) job.SharedShade[index] = new Color32(light, light, light, 255);
                            else job.Shade[index] = new Color32(light, light, light, 255);
                        }
                        // land a cartography table explored shows the view too when ExplorerShared is on
                        if (!job.ShowUnexplored && !ExplorerArea.Explored(fogState, job.Shared)) { job.Pixels[index] = new Color32(); continue; }
                        var pixel = job.Full[index];
                        // natural Mistlands mist over the kept picture, thinned near the loaded clearing sources
                        int at = sy * side + sx;
                        if (job.Mist && masks[at].g > 12 && !MistClearings.Clear(wx, heights[at] + 1.8f, wz))
                            pixel = Over(pixel, new Color(0.67f, 0.64f, 0.70f, masks[at].g / 255f * 0.46f));
                        job.Pixels[index] = pixel;
                    }
                });
                job.FogMilliseconds = clock.Elapsed.TotalMilliseconds - fogStart;
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
                if (_sharedShade != null) UnityEngine.Object.Destroy(_sharedShade);
                _side = job.Side;
                _texture = NewTexture(); _shade = NewTexture(); _sharedShade = NewTexture();
                _terrain.texture = _texture; _fog.texture = _shade; _shared.texture = _sharedShade;
            }
            // the pictures' bytes go to the textures as they are (RGBA32), without SetPixels32's per-pixel path
            _texture.SetPixelData(job.Pixels, 0); _texture.Apply(false);
            if (!job.ShadeSame)
            {
                _shade.SetPixelData(job.Shade, 0); _shade.Apply(false);
                _sharedShade.SetPixelData(job.SharedShade, 0); _sharedShade.Apply(false);
                _shadeKey = job.ShadeKey; ShadeUploads++;
            }
            _sparePixels = job.Pixels; _spareShade = job.Shade; _spareShared = job.SharedShade;
            if (_memoryKey != null && !Memory.ContainsKey(_memoryKey))
            {
                Memory[_memoryKey] = job.Full; MemoryOrder.Insert(0, _memoryKey);
                if (MemoryOrder.Count > MemoryPictures) { Memory.Remove(MemoryOrder[MemoryOrder.Count - 1]); MemoryOrder.RemoveAt(MemoryOrder.Count - 1); }
            }
            Version++;
            MapAccess.LargeShader(map).SetTexture("_FogTex", Reveal.Fog(map));
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("terrain overlays ready, " + job.Side + " px, view " + job.View + ", contours " + job.Contours + ", mist " + job.Mist + ", shallow water " + job.Shallows
                + ", " + job.Milliseconds.ToString("F0") + " ms on a background thread (explored state " + job.FogMilliseconds.ToString("F0") + " ms)" + (job.Loaded ? ", from the cache" : "") + (job.ShadeSame ? ", shade unchanged" : ""));
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
            float water = job.Water, pixel = job.Pixel, mpp = pixel * width / job.Side;
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
                // Simplified was drawn only by the sharp view, so the minimap showed the game's map under it (test 15)
                else if (job.View == 3) color = DetailOverlay.SimplifiedTexel(biome, h, water, slope, slopeZ, mpp, mask.r / 255f, mask.b / 255f, wx, wz);
                if (job.Contours && depth <= 0f)
                {
                    float interval = job.ContourMeters, level = (h - water) / interval;
                    float gap = Math.Abs(level - (float)Math.Round(level)) * interval;
                    float thickness = Math.Min(interval * 0.2f, gradient * pixel * 0.55f);
                    if (gradient > 0.015f && level > 0.5f && gap < thickness)
                        color = Over(color, new Color(0.23f, 0.20f, 0.14f, (int)Math.Round(level) % 5 == 0 ? 0.65f : 0.35f));
                }
                if (job.Steep && depth <= 0f)
                {
                    float degrees = Mathf.Atan(gradient) * Mathf.Rad2Deg;
                    if (degrees > job.MaxSlope * 0.5f) color = Over(color, degrees > job.MaxSlope ? SteepColor : SlopeColor);
                }
                if (job.Shallows && biome != 2) color = Over(color, DepthColor(depth, job.Danger, job.Warning));
                // the game's mask blue holds the boiling sea gradient on water and the lava amount on ashlands ground
                float blue = mask.b / 255f;
                // Simplified paints lava into the ground itself, as the sharp view does
                if (job.Heat && blue > 0.1f && (depth > 0f || job.View != 3))
                    color = Over(color, depth > 0f ? new Color(BoilingColor.r, BoilingColor.g, BoilingColor.b, blue * 0.45f) : new Color(0.80f, 0.29f, 0.10f, blue * 0.62f));
                job.Full[index] = color;
            }
        }
    }
}
