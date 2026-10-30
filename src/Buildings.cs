using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // buildings on the large map and the minimap (Structures: 0 none, 1 placed by players, 2 every structure): the building pieces on the
    // ground in explored land, read from the world data the game keeps for all objects (so pieces far away show too), drawn top down as
    // their own footprints, turned as placed and colored by material, only the highest layer where pieces lie over each other, with a soft
    // edge around each building. the idea of drawing pieces from the world data, colored by material, follows LivingMap by tbsj1ga
    // (MIT), the code here is Navigator's own
    internal static class Buildings
    {
        private const float PassSeconds = 10f;
        private const double SliceMilliseconds = 1.0;
        // location interiors (crypts, caves, the tower insides) sit far above the world at the place of their entrance, they are not on the map
        private const float SkyHeight = 1000f;
        // only pieces at most this many meters above the generated ground under them (edits and location leveling move it a few meters only)
        private const float GroundReach = 100f;
        // a piece whose footprint is longer than this on a side is left out (a model or collider reaching far past the piece)
        private const float MaxFootprint = 32f;
        // a footprint smaller than this on screen is drawn at this size, so a base stays visible zoomed out
        private const float MinPixels = 1.2f;
        // past this many meters a screen pixel the layer is hidden (the whole world view)
        private const float HideMetersPerPixel = 16f;
        // the soft edge around a building fades out over this many pixels from this strength, drawn from this many pixels a meter
        private const float OutlinePixels = 1.2f, OutlineFrom = 0.5f;
        private const byte EdgeAlpha = 140;
        // fills are moved this far toward the map's paper color, so buildings sit in the map
        private const float Muted = 0.2f;
        private static readonly Color Paper = new Color(0.76f, 0.68f, 0.53f);
        // a quad is four vertices and a soft edge eight, a UI mesh holds up to 65000
        private const int MaxQuads = 16000, MaxRings = 8000;
        private const float CoverPixels = 3f;

        private sealed class Shape
        {
            internal float MinX, MaxX, MinZ, MaxZ, Top;
            internal Color32 Color, Edge;
            internal string Name;
        }

        internal struct Piece
        {
            internal float X, Z, Cos, Sin, Top;
            internal float MinX, MaxX, MinZ, MaxZ;
            internal Color32 Color, Edge;
        }

        internal struct Quad { internal Vector3 A, B, C, D; internal Color32 Color; }

        // a soft edge: the inner corners at the fill's border in Color, the outer corners clear
        internal struct Ring { internal Vector3 A, B, C, D, OuterA, OuterB, OuterC, OuterD; internal Color32 Color; }

        private static readonly Dictionary<int, Shape> Shapes = new Dictionary<int, Shape>();
        // the generated ground height by 16 m cell, for the world it was read from
        private static readonly Dictionary<long, float> Grounds = new Dictionary<long, float>();
        private static WorldGenerator _groundWorld;
        private static System.Reflection.FieldInfo _sectorsField;
        private static List<ZDO>[] _sectors;
        private static int _sector = -1, _inSector;
        // the pass fills _found, a finished pass that differs takes the place of _pieces (the two arrays take turns)
        private static Piece[] _found = new Piece[1024], _pieces = new Piece[1024];
        private static float[] _keys = new float[1024];
        private static int _foundCount, _count, _version, _passes, _sky, _high, _over, _mode = -1, _inShared, _sharedTaken;
        private static ulong _hash, _lastHash;
        private static float _nextPass;
        private static bool _warned, _sizesLogged, _newKind;
        private static Layer _large, _small;
        // a guest's copy of the host's player pieces (MapShare), and the pieces this pass read from the guest's own world data, which win
        private static SharedPiece[] _shared;
        private static readonly HashSet<ulong> Local = new HashSet<ulong>();

        internal static int Count => _count;

        // one player piece as the host sends it, the turn in radians
        internal struct SharedPiece { internal int Prefab; internal float X, Y, Z, Angle; }

        // the host's pieces for a guest (null clears them), read by the next pass
        internal static void SetShared(SharedPiece[] pieces) { _shared = pieces; _nextPass = 0f; }

        // a building piece Navigator draws (MapShare's host walk asks)
        internal static bool IsPiece(int prefab) => ShapeOf(prefab) != null;

        // the same piece read twice (from the guest's world data and from the host) has the same key
        internal static ulong PieceKey(int prefab, Vector3 position)
        {
            unchecked
            {
                return (ulong)prefab * 0x9E3779B97F4A7C15UL ^ (ulong)(long)Mathf.RoundToInt(position.x * 4f) * 0xC2B2AE3D27D4EB4FUL
                    ^ (ulong)(long)Mathf.RoundToInt(position.z * 4f) * 0x165667B19E3779F9UL ^ (ulong)(long)Mathf.RoundToInt(position.y * 4f) * 0x27D4EB2F165667C5UL;
            }
        }

        // every frame after the minimap's own update
        internal static void Frame(Minimap map)
        {
            try
            {
                bool large = map.m_mode == Minimap.MapMode.Large, small = map.m_mode == Minimap.MapMode.Small && Settings.MinimapTerrain.Value;
                int mode = Settings.Structures.Value;
                if (mode != _mode) { _mode = mode; _sector = -1; _nextPass = 0f; _count = 0; _lastHash = 0; _version++; }
                bool want = mode != 0 && ZDOMan.instance != null && ZNetScene.instance != null;
                if (want && (large || small)) Scan(mode);
                Show(ref _large, map, map.m_mapImageLarge, map.m_pinRootLarge, "large map", want && large);
                Show(ref _small, map, map.m_mapImageSmall, map.m_pinRootSmall, "minimap", want && small);
            }
            catch (Exception ex)
            {
                if (!_warned) Plugin.Log.LogWarning("buildings on the map unavailable: " + ex);
                _warned = true;
                if (_large != null) _large.Root.SetActive(false);
                if (_small != null) _small.Root.SetActive(false);
            }
        }

        // a pass over the game's object sectors every few seconds, a slice of it each frame inside the time budget, no copy of the lists
        private static void Scan(int mode)
        {
            if (_sector < 0)
            {
                if (Time.unscaledTime < _nextPass) return;
                if (_sectorsField == null) _sectorsField = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsBySector");
                _sectors = _sectorsField?.GetValue(ZDOMan.instance) as List<ZDO>[];
                if (_sectors == null) { _nextPass = Time.unscaledTime + PassSeconds; return; }
                _sector = 0; _inSector = 0; _inShared = 0; _sharedTaken = 0; _foundCount = 0; _hash = 0; _sky = _high = _over = 0;
                Local.Clear();
            }
            int limit = mode == 2 ? Settings.AllStructureLimit.Value : Settings.StructureLimit.Value;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int steps = 0;
            // the clock is also read after a new kind of object, its first footprint reads the prefab's models
            while (_sector < _sectors.Length)
            {
                if ((++steps & 63) == 0 || _newKind) { _newKind = false; if (clock.Elapsed.TotalMilliseconds > SliceMilliseconds) return; }
                var list = _sectors[_sector];
                if (list == null || _inSector >= list.Count) { _sector++; _inSector = 0; continue; }
                Take(list[_inSector++], mode, limit);
            }
            // then the host's pieces a guest got, where the guest's own world data has none
            var shared = _shared;
            while (shared != null && _inShared < shared.Length)
            {
                if ((++steps & 63) == 0 || _newKind) { _newKind = false; if (clock.Elapsed.TotalMilliseconds > SliceMilliseconds) return; }
                TakeShared(shared[_inShared++], limit);
            }
            // the same pieces as the last pass (an order free sum of each piece's key): no sort, no redraw
            _passes++;
            if (_hash != _lastHash || _foundCount != _count)
            {
                Array.Sort(_keys, _found, 0, _foundCount);
                var done = _found; _found = _pieces; _pieces = done;
                _count = _foundCount; _lastHash = _hash; _version++;
                if (_found.Length < _pieces.Length) _found = new Piece[_pieces.Length];
            }
            _sector = -1;
            _nextPass = Time.unscaledTime + PassSeconds;
            if (Plugin.LogCounters.Value && (_passes == 1 || _passes % 30 == 0))
                Plugin.Log.LogInfo("buildings on the map (" + (mode == 2 ? "all structures" : "placed by players") + "): " + _count + " pieces in explored land, " + _sky + " inside locations left out, "
                    + _high + " more than " + GroundReach.ToString("F0") + " m above the ground left out, " + _over + " over the limit of " + limit + ", " + Shapes.Count + " kinds of objects seen, pass " + _passes
                    + (shared != null ? ", " + _sharedTaken + " of " + shared.Length + " pieces from the host" : ""));
            if (Plugin.LogCounters.Value && !_sizesLogged) { _sizesLogged = true; LogLargest(); }
        }

        private static void Take(ZDO zdo, int mode, int limit)
        {
            if (zdo == null || !zdo.IsValid()) return;
            int prefab = zdo.GetPrefab();
            var shape = ShapeOf(prefab);
            if (shape == null) return;
            var position = zdo.GetPosition();
            if (position.y > SkyHeight) { _sky++; return; }
            // a piece a player placed carries its creator (Piece.IsPlacedByPlayer), the world's own structures do not
            if (mode == 1 && zdo.GetLong(ZDOVars.s_creator, 0L) == 0L) return;
            if (_shared != null) Local.Add(PieceKey(prefab, position));
            Add(prefab, shape, position, zdo.GetRotation().eulerAngles.y * Mathf.Deg2Rad, limit);
        }

        private static void TakeShared(SharedPiece piece, int limit)
        {
            var shape = ShapeOf(piece.Prefab);
            if (shape == null) return;
            var position = new Vector3(piece.X, piece.Y, piece.Z);
            if (position.y > SkyHeight || Local.Contains(PieceKey(piece.Prefab, position))) return;
            int before = _foundCount;
            Add(piece.Prefab, shape, position, piece.Angle, limit);
            if (_foundCount > before) _sharedTaken++;
        }

        private static void Add(int prefab, Shape shape, Vector3 position, float angle, int limit)
        {
            if (!ExplorerArea.NearExplored(position.x, position.z, 0f)) return;
            if (position.y - Ground(position.x, position.z) > GroundReach) { _high++; return; }
            if (_foundCount >= limit) { _over++; return; }
            if (_foundCount == _found.Length) { Array.Resize(ref _found, _found.Length * 2); Array.Resize(ref _keys, _found.Length); }
            if (_keys.Length < _found.Length) Array.Resize(ref _keys, _found.Length);
            float top = position.y + shape.Top;
            _found[_foundCount] = new Piece { X = position.x, Z = position.z, Cos = Mathf.Cos(angle), Sin = Mathf.Sin(angle), Top = top,
                MinX = shape.MinX, MaxX = shape.MaxX, MinZ = shape.MinZ, MaxZ = shape.MaxZ, Color = shape.Color, Edge = shape.Edge };
            _keys[_foundCount] = top;
            _foundCount++;
            unchecked
            {
                ulong key = (ulong)prefab * 0x9E3779B97F4A7C15UL ^ (ulong)(long)Mathf.RoundToInt(position.x * 4f) * 0xC2B2AE3D27D4EB4FUL
                    ^ (ulong)(long)Mathf.RoundToInt(position.z * 4f) * 0x165667B19E3779F9UL ^ (ulong)(long)Mathf.RoundToInt(top * 4f) * 0x27D4EB2F165667C5UL
                    ^ (ulong)(long)Mathf.RoundToInt(angle * 100f) * 0xFF51AFD7ED558CCDUL;
                _hash += key ^ (key >> 29);
            }
        }

        // the generated ground under a point, read once per 16 m cell (pieces lie close together)
        private static float Ground(float x, float z)
        {
            var world = WorldGenerator.instance;
            if (world == null) return float.MinValue;
            if (world != _groundWorld) { Grounds.Clear(); _groundWorld = world; }
            int cx = Mathf.FloorToInt(x / 16f), cz = Mathf.FloorToInt(z / 16f);
            long key = ((long)cx << 32) ^ (uint)cz;
            if (!Grounds.TryGetValue(key, out float ground)) Grounds[key] = ground = world.GetHeight(cx * 16f + 8f, cz * 16f + 8f);
            return ground;
        }

        // the footprint of a building piece in its own space, from the models it shows (its colliders when it shows none). null for anything
        // that is not a building piece, or whose footprint is too large to be the piece
        private static Shape ShapeOf(int hash)
        {
            if (Shapes.TryGetValue(hash, out var known)) return known;
            _newKind = true;
            Shape shape = null;
            var prefab = ZNetScene.instance.GetPrefab(hash);
            var wear = prefab != null ? prefab.GetComponent<WearNTear>() : null;
            if (wear != null && prefab.GetComponent<global::Piece>() != null && prefab.GetComponent<Ship>() == null && prefab.GetComponent<Vagon>() == null)
            {
                var root = prefab.transform.worldToLocalMatrix;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue, top = float.MinValue;
                void Add(Transform t, Bounds local)
                {
                    var m = root * t.localToWorldMatrix;
                    for (int i = 0; i < 8; i++)
                    {
                        var p = m.MultiplyPoint3x4(new Vector3(i % 2 == 0 ? local.min.x : local.max.x, (i / 2) % 2 == 0 ? local.min.y : local.max.y, i / 4 == 0 ? local.min.z : local.max.z));
                        minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z); top = Mathf.Max(top, p.y);
                    }
                }
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer.enabled || !Shown(renderer.transform, prefab.transform)) continue;
                    if (renderer is MeshRenderer)
                    {
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null) Add(renderer.transform, filter.sharedMesh.bounds);
                    }
                    else if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null) Add(renderer.transform, skinned.sharedMesh.bounds);
                }
                if (minX == float.MaxValue)
                    foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
                    {
                        if (collider.isTrigger) continue;
                        if (collider is BoxCollider box) Add(collider.transform, new Bounds(box.center, box.size));
                        else if (collider is MeshCollider mesh && mesh.sharedMesh != null) Add(collider.transform, mesh.sharedMesh.bounds);
                        else if (collider is SphereCollider sphere) Add(collider.transform, new Bounds(sphere.center, Vector3.one * sphere.radius * 2f));
                        else if (collider is CapsuleCollider capsule) Add(collider.transform, new Bounds(capsule.center, new Vector3(capsule.radius * 2f, capsule.height, capsule.radius * 2f)));
                    }
                if (maxX - minX > MaxFootprint || maxZ - minZ > MaxFootprint)
                {
                    if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("buildings on the map: " + prefab.name + " left out, its footprint is " + (maxX - minX).ToString("F1") + "x" + (maxZ - minZ).ToString("F1") + " m");
                }
                else if (maxX - minX >= 0.2f && maxZ - minZ >= 0.2f)
                {
                    var color = ColorOf(wear.m_materialType, prefab.name);
                    shape = new Shape { MinX = minX, MaxX = maxX, MinZ = minZ, MaxZ = maxZ, Top = top, Color = color, Edge = EdgeOf(color), Name = prefab.name };
                }
            }
            Shapes[hash] = shape;
            return shape;
        }

        // a part switched on up to the prefab's root (a mod's prefab may sit under an inactive holder, so the root's own parents are not asked)
        private static bool Shown(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }

        // the largest footprints seen, to find a piece whose colliders reach far past what it shows
        private static void LogLargest()
        {
            var sizes = new List<Shape>();
            foreach (var s in Shapes.Values) if (s != null) sizes.Add(s);
            sizes.Sort((a, b) => ((b.MaxX - b.MinX) * (b.MaxZ - b.MinZ)).CompareTo((a.MaxX - a.MinX) * (a.MaxZ - a.MinZ)));
            var text = new System.Text.StringBuilder("buildings on the map, largest footprints:");
            for (int i = 0; i < Math.Min(8, sizes.Count); i++) text.Append(' ').Append(sizes[i].Name).Append(' ').Append((sizes[i].MaxX - sizes[i].MinX).ToString("F1")).Append('x').Append((sizes[i].MaxZ - sizes[i].MinZ).ToString("F1")).Append(" m,");
            Plugin.Log.LogInfo(text.ToString().TrimEnd(','));
        }

        // muted colors that sit with the game's map paper. roofs darker, floors a little lighter, black marble dark
        private static Color32 ColorOf(WearNTear.MaterialType material, string name)
        {
            Color c;
            string lower = name.ToLowerInvariant();
            switch (material)
            {
                case WearNTear.MaterialType.Stone: c = new Color(0.58f, 0.58f, 0.56f); break;
                case WearNTear.MaterialType.Marble: c = lower.Contains("blackmarble") ? new Color(0.21f, 0.21f, 0.23f) : new Color(0.86f, 0.85f, 0.82f); break;
                case WearNTear.MaterialType.Iron: c = new Color(0.32f, 0.32f, 0.34f); break;
                case WearNTear.MaterialType.HardWood: c = new Color(0.47f, 0.33f, 0.21f); break;
                case WearNTear.MaterialType.Ashstone: c = new Color(0.36f, 0.34f, 0.34f); break;
                case WearNTear.MaterialType.Ancient: c = new Color(0.45f, 0.38f, 0.30f); break;
                case WearNTear.MaterialType.Ice: c = new Color(0.76f, 0.85f, 0.92f); break;
                case WearNTear.MaterialType.Timberwood: c = new Color(0.55f, 0.40f, 0.26f); break;
                default: c = new Color(0.62f, 0.46f, 0.30f); break;
            }
            if (lower.Contains("roof")) c = material == WearNTear.MaterialType.Wood || material == WearNTear.MaterialType.HardWood || material == WearNTear.MaterialType.Timberwood
                ? new Color(0.40f, 0.22f, 0.15f) : c * 0.75f;
            else if (lower.Contains("floor")) c = Color.Lerp(c, Color.white, 0.12f);
            c = Color.Lerp(c, Paper, Muted);
            c.a = 1f;
            return c;
        }

        // the soft edge: light around dark materials (black marble, iron), dark around the rest
        private static Color32 EdgeOf(Color32 fill)
        {
            float light = (0.299f * fill.r + 0.587f * fill.g + 0.114f * fill.b) / 255f;
            return light < 0.33f ? new Color32(196, 192, 182, EdgeAlpha) : new Color32(52, 42, 34, EdgeAlpha);
        }

        // one layer over one map picture, under Navigator's markers and the game's pins: the soft edges, then the fills over them
        private sealed class Layer
        {
            internal GameObject Root;
            internal RectTransform Rect;
            internal BuildingGraphic Fill, Edge;
            internal readonly List<Quad> Fills = new List<Quad>();
            internal readonly List<Ring> Edges = new List<Ring>();
            internal Rect LastUv;
            internal Vector2 LastSize, Shift;
            internal int LastVersion = -1;
        }

        // the layer is drawn this far past each side of the view (a share of its size), and slid instead of drawn again while the view
        // moved less than SlideReach without zooming (the minimap follows the player every frame)
        private const float SlideMargin = 0.3f, SlideReach = 0.25f;

        private static void Show(ref Layer layer, Minimap map, RawImage image, RectTransform pins, string name, bool shown)
        {
            if (image == null) return;
            if (layer == null)
            {
                if (!shown) return;
                layer = Create(image, name);
            }
            if (layer.Root.activeSelf != shown) layer.Root.SetActive(shown);
            if (!shown) return;
            Order(image.transform, layer.Root.transform, pins);
            var uv = image.uvRect;
            var rect = layer.Rect.rect;
            if (rect.size == layer.LastSize && layer.LastVersion == _version && uv.width == layer.LastUv.width && uv.height == layer.LastUv.height && uv.width > 0f && uv.height > 0f)
            {
                float dx = (uv.x - layer.LastUv.x) / uv.width, dy = (uv.y - layer.LastUv.y) / uv.height;
                if (Math.Abs(dx) <= SlideReach && Math.Abs(dy) <= SlideReach)
                {
                    var shift = new Vector2(-dx * rect.width, -dy * rect.height);
                    if (shift != layer.Shift) Slide(layer, shift);
                    return;
                }
            }
            layer.LastUv = uv; layer.LastSize = rect.size; layer.LastVersion = _version;
            float span = map.m_textureSize * map.m_pixelSize;
            float pixels = uv.width * span > 0f ? rect.width / (uv.width * span) : 0f;
            if (pixels > 0f && 1f / pixels <= HideMetersPerPixel)
            {
                float m = SlideMargin;
                var wide = new Rect(rect.xMin - m * rect.width, rect.yMin - m * rect.height, rect.width * (1f + 2f * m), rect.height * (1f + 2f * m));
                var wideUv = new Rect(uv.x - m * uv.width, uv.y - m * uv.height, uv.width * (1f + 2f * m), uv.height * (1f + 2f * m));
                Select(_pieces, _count, wide, wideUv, span, pixels, layer.Fills, layer.Edges);
            }
            else { layer.Fills.Clear(); layer.Edges.Clear(); }
            Slide(layer, Vector2.zero);
            layer.Fill.SetVerticesDirty(); layer.Edge.SetVerticesDirty();
        }

        private static void Slide(Layer layer, Vector2 shift)
        {
            layer.Shift = shift;
            ((RectTransform)layer.Fill.transform).anchoredPosition = shift;
            ((RectTransform)layer.Edge.transform).anchoredPosition = shift;
        }

        // only the highest layer: from the highest piece down, a piece is kept where a screen cell wholly inside it (its middle cell when it is
        // smaller) is still open, then it covers those cells. the kept pieces come out lowest first, with their soft edges
        private static bool[] _covered;
        internal static int Select(Piece[] pieces, int count, Rect r, Rect uv, float span, float pixels, List<Quad> fills, List<Ring> edges)
        {
            fills.Clear(); edges.Clear();
            if (pieces == null || count == 0 || span <= 0f || pixels <= 0f) return 0;
            float s = pixels, min = MinPixels / 2f, grow = pixels >= OutlineFrom ? OutlinePixels / pixels : 0f;
            float u0 = uv.x * span - span / 2f, v0 = uv.y * span - span / 2f, uw = uv.width * span, vh0 = uv.height * span;
            float margin = 20f / s;
            int gw = Mathf.CeilToInt(r.width / CoverPixels) + 1, gh = Mathf.CeilToInt(r.height / CoverPixels) + 1;
            if (_covered == null || _covered.Length < gw * gh) _covered = new bool[gw * gh];
            else Array.Clear(_covered, 0, gw * gh);
            for (int i = count - 1; i >= 0 && fills.Count < MaxQuads; i--)
            {
                var p = pieces[i];
                if (p.X < u0 - margin || p.X > u0 + uw + margin || p.Z < v0 - margin || p.Z > v0 + vh0 + margin) continue;
                float cx = r.xMin + (p.X - u0) / uw * r.width, cy = r.yMin + (p.Z - v0) / vh0 * r.height;
                float x0 = p.MinX * s, x1 = p.MaxX * s, z0 = p.MinZ * s, z1 = p.MaxZ * s;
                if (x1 - x0 < MinPixels) { float m = (x0 + x1) / 2f; x0 = m - min; x1 = m + min; }
                if (z1 - z0 < MinPixels) { float m = (z0 + z1) / 2f; z0 = m - min; z1 = m + min; }
                var q = new Quad { A = Corner(cx, cy, x0, z0, p), B = Corner(cx, cy, x0, z1, p), C = Corner(cx, cy, x1, z1, p), D = Corner(cx, cy, x1, z0, p), Color = p.Color };
                float left = Math.Min(Math.Min(q.A.x, q.B.x), Math.Min(q.C.x, q.D.x)) - r.xMin, right = Math.Max(Math.Max(q.A.x, q.B.x), Math.Max(q.C.x, q.D.x)) - r.xMin;
                float bottom = Math.Min(Math.Min(q.A.y, q.B.y), Math.Min(q.C.y, q.D.y)) - r.yMin, top = Math.Max(Math.Max(q.A.y, q.B.y), Math.Max(q.C.y, q.D.y)) - r.yMin;
                int ia = Mathf.CeilToInt(left / CoverPixels), ib = Mathf.FloorToInt(right / CoverPixels) - 1;
                int ic = Mathf.CeilToInt(bottom / CoverPixels), id = Mathf.FloorToInt(top / CoverPixels) - 1;
                if (ia > ib) ia = ib = (int)((left + right) / 2f / CoverPixels);
                if (ic > id) ic = id = (int)((bottom + top) / 2f / CoverPixels);
                bool open = false;
                for (int y = Math.Max(ic, 0); y <= Math.Min(id, gh - 1) && !open; y++)
                    for (int x = Math.Max(ia, 0); x <= Math.Min(ib, gw - 1); x++) if (!_covered[y * gw + x]) { open = true; break; }
                if (!open) continue;
                fills.Add(q);
                if (grow > 0f && edges.Count < MaxRings)
                {
                    float g = grow * s;
                    edges.Add(new Ring { A = q.A, B = q.B, C = q.C, D = q.D, OuterA = Corner(cx, cy, x0 - g, z0 - g, p), OuterB = Corner(cx, cy, x0 - g, z1 + g, p),
                        OuterC = Corner(cx, cy, x1 + g, z1 + g, p), OuterD = Corner(cx, cy, x1 + g, z0 - g, p), Color = p.Edge });
                }
                for (int y = Math.Max(ic, 0); y <= Math.Min(id, gh - 1); y++)
                    for (int x = Math.Max(ia, 0); x <= Math.Min(ib, gw - 1); x++) _covered[y * gw + x] = true;
            }
            fills.Reverse(); edges.Reverse();
            return fills.Count;
        }

        // a footprint corner turned by the piece's rotation (unity's y turn: x' = x cos + z sin, z' = -x sin + z cos)
        internal static Vector3 Corner(float cx, float cy, float x, float z, Piece p) => new Vector3(cx + x * p.Cos + z * p.Sin, cy - x * p.Sin + z * p.Cos);

        private static Layer Create(RawImage image, string name)
        {
            // the canvas renderer is added here, a graphic from a plugin dll does not get it from its RequireComponent
            var go = new GameObject("ValheimNavigator_Buildings", typeof(RectTransform), typeof(RectMask2D));
            go.transform.SetParent(image.transform, false);
            var rect = (RectTransform)go.transform;
            Ui.Stretch(rect, 0f);
            var layer = new Layer { Root = go, Rect = rect };
            layer.Edge = Part(go, "Outline", null, layer.Edges);
            layer.Fill = Part(go, "Fill", layer.Fills, null);
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("buildings layer built on the " + name);
            return layer;
        }

        private static BuildingGraphic Part(GameObject root, string name, List<Quad> quads, List<Ring> rings)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(root.transform, false);
            Ui.Stretch((RectTransform)go.transform, 0f);
            var graphic = go.AddComponent<BuildingGraphic>();
            graphic.raycastTarget = false;
            graphic.Quads = quads;
            graphic.Rings = rings;
            return graphic;
        }

        // just under the lowest of Navigator's markers, spawn areas, minimap route and the game's pins, above the map views
        private static void Order(Transform parent, Transform layer, RectTransform pins)
        {
            int target = int.MaxValue;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == layer) continue;
                if (child == pins || child.name == "ValheimNavigator_Markers" || child.name == "ValheimNavigator_SpawnAreas" || child.name == "ValheimNavigator_MiniRoute") { target = i; break; }
            }
            if (target == int.MaxValue) return;
            int index = layer.GetSiblingIndex();
            if (index > target) layer.SetSiblingIndex(target);
        }

        internal sealed class BuildingGraphic : MaskableGraphic
        {
            internal List<Quad> Quads;
            internal List<Ring> Rings;

            protected override void OnPopulateMesh(VertexHelper vh)
            {
                vh.Clear();
                var uv = Vector2.zero;
                if (Quads != null)
                    foreach (var q in Quads)
                    {
                        int n = vh.currentVertCount;
                        vh.AddVert(q.A, q.Color, uv); vh.AddVert(q.B, q.Color, uv); vh.AddVert(q.C, q.Color, uv); vh.AddVert(q.D, q.Color, uv);
                        vh.AddTriangle(n, n + 1, n + 2);
                        vh.AddTriangle(n, n + 2, n + 3);
                    }
                if (Rings != null)
                    foreach (var e in Rings)
                    {
                        // four bands between the fill's border and the clear outer corners
                        int n = vh.currentVertCount;
                        var clear = new Color32(e.Color.r, e.Color.g, e.Color.b, 0);
                        vh.AddVert(e.A, e.Color, uv); vh.AddVert(e.B, e.Color, uv); vh.AddVert(e.C, e.Color, uv); vh.AddVert(e.D, e.Color, uv);
                        vh.AddVert(e.OuterA, clear, uv); vh.AddVert(e.OuterB, clear, uv); vh.AddVert(e.OuterC, clear, uv); vh.AddVert(e.OuterD, clear, uv);
                        for (int i = 0; i < 4; i++)
                        {
                            int j = (i + 1) % 4;
                            vh.AddTriangle(n + i, n + j, n + 4 + j);
                            vh.AddTriangle(n + i, n + 4 + j, n + 4 + i);
                        }
                    }
            }
        }
    }
}
