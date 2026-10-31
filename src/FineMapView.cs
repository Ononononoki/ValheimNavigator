using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the detailed map: a layer over the large map and the minimap, drawn by Navigator's copy of the game's map shader
    // (shaders/NavigatorFineMap.shader, in the embedded bundle) from a window of 2 m ground around the view. the game's own colors,
    // patterns, fog and live sun come from the game's map material every frame. where the window has no data, and when zoomed out past
    // 6 m a texel, the game's own map shows
    internal static class FineMapView
    {
        private const string BundleResource = "ValheimNavigator.navigator_finemap";
        private const int Side = 1024;
        // 24 m a texel covers the whole world in one window
        private const int MaxTexel = 24;
        private const float WindowFactor = 1.5f;
        private const float RedrawSeconds = 1f;
        private const string LivingMapRoot = "LivingMap detail";
        // the game's terrain texture layers for dirt paths, tilled soil and paving (Heightmap material _DiffuseArrayTex), made small and blurred
        // (the game's array has one full level with point filtering, so it would shimmer on the map), meters a tile, the paint's soft edge
        private static readonly int[] GroundLayers = { 3, 10, 12 };
        private static readonly string[] GroundNames = { "_GroundDirt", "_GroundSoil", "_GroundStone" };
        internal const int GroundPixels = 16;
        private const float GroundTile = 14f, PaintSoft = 1f;
        private const float GroundLookSeconds = 2f;
        private static RenderTexture[] _ground;
        private static float _nextGroundLook;

        private static readonly string[] Textures = { "_MainTex", "_BackgroundTex", "_FogLayerTex", "_FogTex", "_WaterTex", "_lavaTex", "_MountainTex", "_CloudTex", "_ForestTex" };
        private static readonly string[] Colors = { "_ForestColor", "_WaterColor", "_WaterColorDeep", "_WaterColorAshlands", "_WaterColorAshlandsDeep", "_lightColor", "_ambientLightColor", "_lavaColor1", "_lavaColor2" };
        private static readonly string[] Floats = { "_pixelSize", "_zoom", "_normalWidth", "_normalIntensity", "_SharedFade" };
        private static readonly string[] Vectors = { "_mapCenter", "_CloudOffset" };

        private static Shader _shader;
        private static bool _loaded;

        // one map picture (the large map or the minimap) with its layer and its window of 2 m data
        private sealed class View
        {
            internal string Name;
            internal RawImage Image;
            internal Material Material;
            internal Texture2D Height, Mask, Paint;
            internal int OriginX, OriginZ, Texel;
            internal int KeptVersion = -1, EditVersion = -1;
            internal bool LavaEdge;
            internal float NextDraw;
            internal Task<Window> Drawing;
            internal bool Logged;
        }

        // one filled window, made on a background task
        private sealed class Window
        {
            internal int OriginX, OriginZ, Texel, Covered;
            internal float[] Heights;
            internal Color32[] Masks, Paints;
            internal double Milliseconds;
        }

        private static View _large, _small;

        internal static bool Wanted => Settings.FineTerrain.Value && Settings.DetailedView && FineTerrain.Status != null && Shader != null;

        private static Shader Shader
        {
            get
            {
                if (_loaded) return _shader;
                _loaded = true;
                try
                {
                    using (var stream = typeof(FineMapView).Assembly.GetManifestResourceStream(BundleResource))
                    {
                        if (stream != null)
                        {
                            var bytes = new byte[stream.Length];
                            int read = 0;
                            while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                            var bundle = AssetBundle.LoadFromMemory(bytes);
                            if (bundle != null) { _shader = bundle.LoadAsset<Shader>("NavigatorFineMap"); bundle.Unload(false); }
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("the detailed map shader did not load: " + ex.Message); }
                if (_shader != null && !_shader.isSupported) _shader = null;
                if (_shader == null) Plugin.Log.LogWarning("the detailed map is off, its shader is not available on this computer");
                else if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("detailed map shader loaded");
                return _shader;
            }
        }

        // every frame after the minimap's own update
        internal static void Frame(Minimap map)
        {
            try
            {
                Draw(map, map.m_mapImageLarge, MapAccess.LargeShader(map), ref _large, "large map", map.m_mode == Minimap.MapMode.Large);
                Draw(map, map.m_mapImageSmall, map.m_mapImageSmall != null ? map.m_mapImageSmall.material : null, ref _small, "minimap", map.m_mode == Minimap.MapMode.Small && Settings.MinimapTerrain.Value);
            }
            catch (Exception ex)
            {
                if (_large != null && !_large.Logged) { _large.Logged = true; Plugin.Log.LogWarning("the detailed map could not be drawn: " + ex); }
                Hide(_large); Hide(_small);
            }
        }

        private static void Hide(View view) { if (view != null && view.Image != null && view.Image.gameObject.activeSelf) view.Image.gameObject.SetActive(false); }

        private static void Draw(Minimap map, RawImage game, Material gameMaterial, ref View view, string name, bool shown)
        {
            if (game == null || gameMaterial == null || !shown || !Wanted) { Hide(view); return; }
            var rect = game.uvRect;
            float world = map.m_textureSize * map.m_pixelSize;
            // the window covers one and a half times the visible width, in whole 2 m steps, the texel grows when zoomed out up to the whole
            // world (the 8 m overview fills what the kept 2 m tiles do not cover)
            int texel = Math.Min(MaxTexel, Math.Max(FineTerrain.CellMeters, (int)Math.Ceiling(rect.width * world * WindowFactor / Side / FineTerrain.CellMeters) * FineTerrain.CellMeters));
            if (view == null) view = Create(game, name);
            float cx = (rect.center.x - 0.5f) * world, cz = (rect.center.y - 0.5f) * world;
            float span = Side * texel;

            if (view.Drawing != null && view.Drawing.IsCompleted)
            {
                Upload(view, view.Drawing.Result);
                view.Drawing = null;
            }
            // drawn again before the view reaches the window's edge
            bool moved = view.Texel != texel || Math.Abs(cx - (view.OriginX + span / 2f)) > span / 6f || Math.Abs(cz - (view.OriginZ + span / 2f)) > span / 6f;
            bool edge = Settings.LavaEdge.Value;
            bool newer = view.KeptVersion != FineTerrain.KeptVersion || view.EditVersion != TerrainEdits.Version || view.LavaEdge != edge;
            if (view.Drawing == null && (moved || newer && Time.unscaledTime >= view.NextDraw))
            {
                view.NextDraw = Time.unscaledTime + RedrawSeconds;
                view.KeptVersion = FineTerrain.KeptVersion; view.EditVersion = TerrainEdits.Version; view.LavaEdge = edge;
                // texel centers on whole multiples of the texel (even meters), so they are the fine points themselves
                int ox = (int)Math.Floor((cx - span / 2f) / texel) * texel, oz = (int)Math.Floor((cz - span / 2f) / texel) * texel;
                view.Drawing = Task.Run(() => Fill(ox, oz, texel, edge));
            }

            var image = view.Image;
            if (!image.gameObject.activeSelf) image.gameObject.SetActive(view.Texel > 0);
            if (view.Texel == 0) return;
            // the game keeps its map colors on the material (Minimap.Start sets _MainTex to m_mapTexture), the picture's own texture is
            // empty, and a RawImage passes its texture as _MainTex (white when empty). the live material texture also carries LivingMap's bases
            var colors = gameMaterial.GetTexture("_MainTex") ?? game.texture ?? map.m_mapTexture;
            image.texture = colors;
            image.uvRect = rect;
            Order(game, image);

            var m = view.Material;
            foreach (var p in Textures) if (gameMaterial.HasProperty(p)) m.SetTexture(p, p == "_MainTex" ? colors : gameMaterial.GetTexture(p));
            foreach (var p in Colors) if (gameMaterial.HasProperty(p)) m.SetColor(p, gameMaterial.GetColor(p));
            foreach (var p in Floats) if (gameMaterial.HasProperty(p)) m.SetFloat(p, gameMaterial.GetFloat(p));
            foreach (var p in Vectors) if (gameMaterial.HasProperty(p)) m.SetVector(p, gameMaterial.GetVector(p));
            m.SetFloat("_World", world);
            var ground = GroundTextures();
            if (ground != null) for (int i = 0; i < GroundNames.Length; i++) m.SetTexture(GroundNames[i], ground[i]);
            m.SetFloat("_GroundReady", ground != null ? 1f : 0f);
            m.SetFloat("_GroundTile", GroundTile);
            m.SetFloat("_PaintSoft", PaintSoft);
            // the slope step grows with the texel when zoomed out, its strength follows in the shader, so the shading keeps the game's look
            m.SetFloat("_NormalStep", Math.Max(FineTerrain.CellMeters * 2f, view.Texel));
            // texel i holds the point origin + i * texel, its center, so the texture's edge is half a texel before it
            m.SetVector("_Window", new Vector4(view.OriginX - view.Texel / 2f, view.OriginZ - view.Texel / 2f, Side * view.Texel, 0f));
        }

        // small blurred copies of the game's own ground textures, made once from any loaded ground piece's material (looked for every few
        // seconds until found, made again if the graphics device lost them)
        private static RenderTexture[] GroundTextures()
        {
            if (_ground != null && _ground[0] != null && _ground[0].IsCreated()) return _ground;
            if (Time.unscaledTime < _nextGroundLook) return null;
            _nextGroundLook = Time.unscaledTime + GroundLookSeconds;
            foreach (var heightmap in Heightmap.GetAllHeightmaps())
            {
                var renderer = heightmap != null ? heightmap.GetComponent<MeshRenderer>() : null;
                var material = renderer != null ? renderer.sharedMaterial : null;
                if (material == null || !material.HasProperty("_DiffuseArrayTex") || !(material.GetTexture("_DiffuseArrayTex") is Texture2DArray array) || array.depth <= GroundLayers[2]) continue;
                _ground = Shrink(array, GroundLayers);
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("detailed map ground textures from the game's terrain, " + array.name + " " + array.width + " px, layers " + string.Join(", ", GroundLayers) + " as " + GroundPixels + " px blurred copies");
                break;
            }
            return _ground;
        }

        // each layer halved step by step with bilinear sampling (the array's own point filter set back right after) down to GroundPixels,
        // into a texture with mips that repeats
        internal static RenderTexture[] Shrink(Texture2DArray array, int[] layers)
        {
            var result = new RenderTexture[layers.Length];
            var filter = array.filterMode;
            array.filterMode = FilterMode.Bilinear;
            try
            {
                for (int i = 0; i < layers.Length; i++)
                {
                    int size = array.width / 2;
                    var step = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    step.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(array, step, layers[i], 0);
                    while (size / 2 > GroundPixels)
                    {
                        size /= 2;
                        var next = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        next.filterMode = FilterMode.Bilinear;
                        Graphics.Blit(step, next);
                        RenderTexture.ReleaseTemporary(step);
                        step = next;
                    }
                    var final = new RenderTexture(GroundPixels, GroundPixels, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                    { useMipMap = true, autoGenerateMips = true, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, name = "ValheimNavigator ground " + layers[i] };
                    final.Create();
                    Graphics.Blit(step, final);
                    RenderTexture.ReleaseTemporary(step);
                    result[i] = final;
                }
            }
            finally { array.filterMode = filter; }
            return result;
        }

        private static View Create(RawImage game, string name)
        {
            var go = new GameObject("ValheimNavigator_DetailedMap", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            go.transform.SetParent(game.transform, false);
            Ui.Stretch((RectTransform)go.transform, 0f);
            go.transform.SetSiblingIndex(0);
            var image = go.GetComponent<RawImage>();
            image.raycastTarget = false;
            var view = new View { Name = name, Image = image, Material = new Material(Shader) { name = "ValheimNavigator detailed map" } };
            image.material = view.Material;
            // the mask texture decodes like the game's own mask texture
            var gameMask = MapAccess.LargeShader(Minimap.instance)?.GetTexture("_MaskTex");
            bool srgb = gameMask != null && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(gameMask.graphicsFormat);
            view.Height = new Texture2D(Side, Side, TextureFormat.RFloat, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "ValheimNavigator fine height" };
            view.Mask = new Texture2D(Side, Side, TextureFormat.RGBA32, false, !srgb) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "ValheimNavigator fine mask" };
            view.Material.SetTexture("_FineHeight", view.Height);
            view.Material.SetTexture("_FineMask", view.Mask);
            // your ground paint at 2 m: r dirt, g cultivated, b paved, a cleared
            view.Paint = new Texture2D(Side, Side, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "ValheimNavigator fine paint" };
            view.Material.SetTexture("_FinePaint", view.Paint);
            go.SetActive(false);
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("detailed map layer made on the " + name + ", mask " + (srgb ? "srgb" : "linear") + " like the game's");
            return view;
        }

        // the layer sits under Navigator's own layers. LivingMap's close-up layer goes under it (LivingMapMode 0) or stays over it (1)
        private static void Order(RawImage game, RawImage layer)
        {
            var parent = game.transform;
            if (layer.transform.GetSiblingIndex() != 0) layer.transform.SetSiblingIndex(0);
            if (Settings.LivingMapMode.Value != 0) return;
            var living = parent.Find(LivingMapRoot);
            if (living != null && living.GetSiblingIndex() != 0) living.SetSiblingIndex(0);
        }

        // the 2 m ground, mask and ground paint of the window: the visited ground first, else the generated ground plus your edits. alpha 0
        // where no tile is in memory
        // the Ashlands' biome byte in the fine tiles (FineTerrain.BiomeByte: the biome's bit number)
        private const byte AshlandsBit = 6;

        private static Window Fill(int ox, int oz, int texel, bool edge)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var w = new Window { OriginX = ox, OriginZ = oz, Texel = texel, Heights = new float[Side * Side], Masks = new Color32[Side * Side], Paints = new Color32[Side * Side] };
            FineTile tile = null; int tileX = int.MinValue, tileZ = int.MinValue;
            short[] visited = null; TerrainEdits.Zone edits = null; LocationShaping.Mod[] shaping = null; int zoneX = int.MinValue, zoneZ = int.MinValue;
            const int t = FineTerrain.TileMeters, c = FineTerrain.CellMeters, n = FineTile.Cells;
            for (int j = 0; j < Side; j++)
            {
                int z = oz + j * texel;
                int tz = FineBuilder.FloorDiv(z, t), zz = FineBuilder.FloorDiv(z + 32, 64);
                for (int i = 0; i < Side; i++)
                {
                    int x = ox + i * texel;
                    int tx = FineBuilder.FloorDiv(x, t);
                    if (tx != tileX || tz != tileZ) { tileX = tx; tileZ = tz; tile = FineTerrain.Tile(tx, tz); }
                    int k = j * Side + i;
                    // the kept 2 m tile, else the 8 m overview (zoomed out or far from you), else nothing (the game's map shows)
                    short ground; byte r, g, b;
                    if (tile != null)
                    {
                        int cell = (z - tz * t) / c * n + (x - tx * t) / c;
                        ground = tile.Height[cell]; r = tile.Mask[cell * 3]; g = tile.Mask[cell * 3 + 1]; b = tile.Mask[cell * 3 + 2];
                    }
                    else if (FineTerrain.Overview(x, z, out ground, out int o))
                    {
                        r = FineTerrain.OverviewMask[o * 3]; g = FineTerrain.OverviewMask[o * 3 + 1]; b = FineTerrain.OverviewMask[o * 3 + 2];
                    }
                    else continue;
                    int zx = FineBuilder.FloorDiv(x + 32, 64);
                    if (zx != zoneX || zz != zoneZ) { zoneX = zx; zoneZ = zz; TerrainEdits.ZoneData(zx, zz, out visited, out edits); shaping = LocationShaping.ZoneMods(zx, zz); }
                    int point = ((z + 32 - zz * 64) >> 1) * 33 + ((x + 32 - zx * 64) >> 1);
                    // the visited ground as loaded, else the generated ground shaped around placed locations plus your edits
                    float h = visited != null ? visited[point] * FineTerrain.HeightStep
                        : (shaping != null ? LocationShaping.Apply(shaping, x, z, ground * FineTerrain.HeightStep) : ground * FineTerrain.HeightStep) + (edits != null ? edits.Height[point] * FineTerrain.HeightStep : 0f);
                    w.Heights[k] = h;
                    // the lava where it burns (LavaEdge), on the Ashlands ground only: the mask blue is the boiling sea's gradient on water
                    if (edge && b > 0 && h >= 30f && (tile == null || tile.Biome[(z - tz * t) / c * n + (x - tx * t) / c] == AshlandsBit)) b = (byte)(TerrainOverlay.LavaDrawn(b / 255f) * 255f + 0.5f);
                    w.Masks[k] = new Color32(r, g, b, 255);
                    byte paint = edits != null ? edits.Paint[point] : TerrainEdits.PaintNone;
                    if (paint != TerrainEdits.PaintNone)
                        w.Paints[k] = new Color32(paint == TerrainEdits.PaintDirt ? (byte)255 : (byte)0, paint == TerrainEdits.PaintCultivated ? (byte)255 : (byte)0,
                            paint == TerrainEdits.PaintPaved ? (byte)255 : (byte)0, paint == TerrainEdits.PaintCleared ? (byte)255 : (byte)0);
                    w.Covered++;
                }
            }
            w.Milliseconds = clock.Elapsed.TotalMilliseconds;
            return w;
        }

        private static void Upload(View view, Window w)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            view.Height.SetPixelData(w.Heights, 0);
            view.Height.Apply(false);
            view.Mask.SetPixelData(w.Masks, 0);
            view.Mask.Apply(false);
            view.Paint.SetPixelData(w.Paints, 0);
            view.Paint.Apply(false);
            view.OriginX = w.OriginX; view.OriginZ = w.OriginZ; view.Texel = w.Texel;
            if (Plugin.LogCounters.Value)
                Plugin.Log.LogInfo("detailed map window on the " + view.Name + ": " + Side * w.Texel + " m at " + w.Texel + " m a texel around " + (w.OriginX + Side * w.Texel / 2) + ", " + (w.OriginZ + Side * w.Texel / 2)
                    + ", " + (100.0 * w.Covered / (Side * Side)).ToString("F0") + " % covered, filled in " + w.Milliseconds.ToString("F0") + " ms on a background thread, uploaded in " + clock.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
        }
    }
}
