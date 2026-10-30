using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // spawn protection as the game checks it. SpawnSystem.IsSpawnPointGood and CreatureSpawner.UpdateSpawner ask EffectArea.IsPointInsideArea,
    // a physics overlap with the colliders of PlayerBase effect areas. loaded areas are read live, so radii changed by Valheim Plus or other mods
    // are the real ones. the radius seen on a loaded object is remembered per prefab and used for objects of that prefab that are not loaded
    internal static class SpawnAreas
    {
        internal struct Area
        {
            internal float X, Z, Radius;
            // 3 loaded now, 2 remembered with a radius seen loaded, 1 remembered with the unmodified prefab radius
            internal byte State;
        }

        private const float ScanSeconds = 1f;
        private const float SaveSeconds = 5f;
        private static readonly FieldInfo AllAreas = typeof(EffectArea).GetField("s_allAreas", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        internal static readonly List<Area> Live = new List<Area>();
        private static readonly Dictionary<string, float> Learned = new Dictionary<string, float>();
        private static readonly Dictionary<string, Dictionary<int, int>> Counts = new Dictionary<string, Dictionary<int, int>>();
        // each object's own radius as last seen loaded, by its ZDO id
        private static readonly Dictionary<string, float> ObjectRadius = new Dictionary<string, float>();
        private static bool _loaded, _changed, _warned;
        private static float _nextScan, _nextSave;
        internal static int Version;

        private static string FilePath => Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "ValheimNavigator"), "spawn_protection_radius.txt");
        private static string ObjectFilePath => Path.Combine(Path.Combine(BepInEx.Paths.ConfigPath, "ValheimNavigator"), "spawn_protection_objects.txt");

        private static void Load()
        {
            _loaded = true;
            try
            {
                Read(FilePath, Learned);
                Read(ObjectFilePath, ObjectRadius);
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("spawn protection radii read, " + Learned.Count + " prefabs, " + ObjectRadius.Count + " objects");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("could not read the spawn protection radii: " + ex.Message); }
        }

        private static void Read(string path, Dictionary<string, float> into)
        {
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                int at = line.LastIndexOf('=');
                if (at > 0 && float.TryParse(line.Substring(at + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float radius))
                    into[line.Substring(0, at)] = radius;
            }
        }

        private static void Save()
        {
            _changed = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                Write(FilePath, Learned);
                Write(ObjectFilePath, ObjectRadius);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("could not save the spawn protection radii: " + ex.Message); }
        }

        private static void Write(string path, Dictionary<string, float> from)
        {
            var lines = new List<string>();
            foreach (var pair in from) lines.Add(pair.Key + "=" + pair.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            lines.Sort(StringComparer.Ordinal);
            File.WriteAllLines(path, lines.ToArray());
        }

        // the radius of a remembered object: its own as last seen loaded, else the one its prefab usually has, else the prefab's.
        // objects of one prefab can differ: Valheim Plus resizes in CraftingStation.Start only the first effect area of a station and only
        // when it is a PlayerBase one, and on a server its config arrives after the first stations started (ValheimPlus IL, 24 Oct)
        internal static float Radius(KnownMark mark, out bool learned)
        {
            if (!_loaded) Load();
            if (mark.Id != null && ObjectRadius.TryGetValue(mark.Id, out float own)) { learned = true; return own; }
            return Radius(mark.Name, mark.Radius, out learned);
        }

        // the radius for a remembered object of this prefab, the one seen loaded when there is one
        internal static float Radius(string prefab, float prefabRadius, out bool learned)
        {
            if (!_loaded) Load();
            learned = prefab != null && Learned.TryGetValue(prefab, out float radius);
            return learned ? Learned[prefab] : prefabRadius;
        }

        // the horizontal reach of an effect area collider in world meters
        internal static float RadiusOf(Collider collider)
        {
            var scale = collider.transform.lossyScale;
            float s = Math.Max(Math.Abs(scale.x), Math.Abs(scale.z));
            if (collider is SphereCollider sphere) return sphere.radius * s;
            if (collider is CapsuleCollider capsule) return capsule.radius * s;
            return Math.Max(collider.bounds.extents.x, collider.bounds.extents.z);
        }

        // reads the loaded PlayerBase areas once a second while the large map is open
        internal static void Scan()
        {
            if (!_loaded) Load();
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanSeconds;
            try
            {
                int signature = 0;
                Live.Clear();
                if (AllAreas != null && AllAreas.GetValue(null) is List<EffectArea> areas)
                    foreach (var area in areas)
                    {
                        if (area == null || (area.m_type & EffectArea.Type.PlayerBase) == 0) continue;
                        var collider = area.GetComponent<Collider>();
                        bool active = area.isActiveAndEnabled && collider != null && collider.enabled;
                        if (Plugin.LogCounters.Value) LogChange(area, active);
                        if (!active) continue;
                        float radius = RadiusOf(collider);
                        var center = collider.bounds.center;
                        Live.Add(new Area { X = center.x, Z = center.z, Radius = radius, State = 3 });
                        unchecked { signature = signature * 31 + center.x.GetHashCode() ^ center.z.GetHashCode() ^ radius.GetHashCode(); }
                        var view = area.GetComponentInParent<ZNetView>();
                        var zdo = view != null ? view.GetZDO() : null;
                        var prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
                        if (zdo != null)
                        {
                            string uid = zdo.m_uid.ToString();
                            if (!ObjectRadius.TryGetValue(uid, out float seen) || Math.Abs(seen - radius) > 0.05f) { ObjectRadius[uid] = radius; _changed = true; }
                        }
                        if (prefab != null)
                        {
                            if (!Counts.TryGetValue(prefab.name, out var counts)) { counts = new Dictionary<int, int>(); Counts[prefab.name] = counts; }
                            int key = Mathf.RoundToInt(radius * 10f);
                            counts.TryGetValue(key, out int n); counts[key] = n + 1;
                        }
                    }
                // one radius per prefab, the one most loaded objects of it have (the larger on a tie), so mixed radii do not flip the remembered value
                foreach (var pair in Counts)
                {
                    int bestKey = 0, bestCount = 0;
                    foreach (var count in pair.Value) if (count.Value > bestCount || count.Value == bestCount && count.Key > bestKey) { bestKey = count.Key; bestCount = count.Value; }
                    float radius = bestKey / 10f;
                    if (bestCount > 0 && (!Learned.TryGetValue(pair.Key, out float old) || Math.Abs(old - radius) > 0.05f))
                    {
                        Learned[pair.Key] = radius;
                        _changed = true;
                        if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("spawn protection radius of " + pair.Key + " is " + radius.ToString("F1") + " m, " + bestCount + " of " + pair.Value.Values.Sum() + " loaded have it" + (pair.Value.Count > 1 ? ", others " + string.Join(", ", pair.Value.Keys.Where(k => k != bestKey).Select(k => (k / 10f).ToString("F1") + " m")) : ""));
                    }
                }
                Counts.Clear();
                if (signature != _signature) { _signature = signature; Version++; }
                if (_changed && Time.unscaledTime >= _nextSave) { _nextSave = Time.unscaledTime + SaveSeconds; Save(); }
            }
            catch (Exception ex)
            {
                if (!_warned) Plugin.Log.LogWarning("spawn protection scan failed: " + ex);
                _warned = true;
            }
        }

        private static int _signature;
        private static readonly Dictionary<int, KeyValuePair<bool, float>> Seen = new Dictionary<int, KeyValuePair<bool, float>>();

        // one log line when a PlayerBase area is first seen, turns on or off, or changes its radius (Valheim Plus resizes some in CraftingStation.Start)
        private static void LogChange(EffectArea area, bool active)
        {
            int id = area.GetInstanceID();
            var areaCollider = area.GetComponent<Collider>();
            float size = areaCollider != null ? RadiusOf(areaCollider) : 0f;
            if (Seen.TryGetValue(id, out var was) && was.Key == active && Math.Abs(was.Value - size) < 0.05f) return;
            bool resized = Seen.ContainsKey(id) && was.Key == active;
            Seen[id] = new KeyValuePair<bool, float>(active, size);
            if (resized) { Plugin.Log.LogInfo("spawn protection area " + area.name + " at " + area.transform.position.x.ToString("F0") + ", " + area.transform.position.z.ToString("F0") + " radius changed " + was.Value.ToString("F1") + " to " + size.ToString("F1") + " m"); return; }
            var view = area.GetComponentInParent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            var prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            var fire = area.GetComponentInParent<Fireplace>();
            string state = fire == null ? "no fire" : "fire " + FireState(fire) + ", fuel " + (zdo != null ? zdo.GetFloat(ZDOVars.s_fuel, 0f).ToString("F1") : "?") + (fire.m_infiniteFuel ? ", infinite" : "");
            var p = area.transform.position;
            var collider = area.GetComponent<Collider>();
            string radius = collider != null ? ", radius " + RadiusOf(collider).ToString("F1") + " m" : "";
            Plugin.Log.LogInfo("spawn protection area " + (prefab != null ? prefab.name : area.name) + " at " + p.x.ToString("F0") + ", " + p.z.ToString("F0") + (active ? " active" : " inactive") + radius + ", " + state);
        }

        // IsBurning reads the fire's ZDO, which a fire being set up or removed does not have
        private static string FireState(Fireplace fire)
        {
            try { return fire.IsBurning() ? "burning" : "out"; }
            catch (NullReferenceException) { return "not set up"; }
        }

        // what protects a point, 0 nothing, otherwise the best state of the areas that cover it
        internal static int StateAt(float x, float z)
        {
            int best = 0;
            foreach (var area in Live) if (Inside(area.X, area.Z, area.Radius, x, z)) return 3;
            foreach (var mark in KnownWorld.Marks)
            {
                if (mark.Kind != 1) continue;
                float radius = Radius(mark, out bool learned);
                if (Inside(mark.X, mark.Z, radius, x, z)) best = Math.Max(best, learned ? 2 : 1);
            }
            return best;
        }

        private static bool Inside(float ax, float az, float radius, float x, float z)
        {
            float dx = x - ax, dz = z - az;
            return dx * dx + dz * dz <= radius * radius;
        }

        internal static string Describe(int state)
        {
            return state == 3 ? "Spawn-protected (loaded)" : state == 2 ? "Spawn-protected (remembered)" : state == 1 ? "Spawn-protected (prefab radius, not seen loaded yet)" : "";
        }
    }

    // the protected areas of the current view as one filled shape with a single outline, drawn into a small texture.
    // loaded areas are green, remembered ones paler, remembered ones with an unconfirmed radius sand colored
    internal static class SpawnLayer
    {
        private const int MaxPixels = 500000;
        private const float SettleSeconds = 0.08f;
        private static readonly Color32[] Fill = { new Color32(), new Color32(206, 190, 116, 48), new Color32(132, 178, 146, 52), new Color32(96, 186, 110, 72) };
        private static readonly Color32[] Edge = { new Color32(), new Color32(226, 208, 132, 175), new Color32(170, 214, 184, 185), new Color32(140, 235, 150, 235) };
        private static RectTransform _clip;
        private static RawImage _image;
        private static Texture2D _texture;
        private static Color32[] _pixels;
        private static byte[] _mask;
        private static Rect _shownUv, _lastUv;
        private static Vector2 _shownSize;
        private static int _w, _h, _shownVersion = -1, _shownKnown = -1;
        private static float _moved;
        private static bool _warned;
        internal static int Covered;

        internal static void Frame(Minimap map)
        {
            try
            {
                var image = map != null ? map.m_mapImageLarge : null;
                if (image == null || !Settings.SpawnSafe.Value)
                {
                    if (_clip != null && _clip.gameObject.activeSelf) _clip.gameObject.SetActive(false);
                    return;
                }
                if (_clip == null) Create(image);
                if (!_clip.gameObject.activeSelf) _clip.gameObject.SetActive(true);
                SpawnAreas.Scan();
                var uv = image.uvRect;
                var size = _clip.rect.size;
                if (uv != _lastUv) { _lastUv = uv; _moved = Time.unscaledTime; }
                bool settled = Time.unscaledTime - _moved >= SettleSeconds;
                if (settled && size.x >= 8f && size.y >= 8f && (uv != _shownUv || size != _shownSize || SpawnAreas.Version != _shownVersion || KnownWorld.Version != _shownKnown))
                    Draw(map, uv, size);
                Place(uv);
            }
            catch (Exception ex)
            {
                if (!_warned) Plugin.Log.LogWarning("spawn protection layer failed: " + ex);
                _warned = true;
            }
        }

        private static void Create(RawImage image)
        {
            var go = new GameObject("ValheimNavigator_SpawnAreas", typeof(RectTransform), typeof(RectMask2D));
            go.transform.SetParent(image.transform, false);
            _clip = (RectTransform)go.transform;
            Ui.Stretch(_clip, 0f);
            var markers = image.transform.Find("ValheimNavigator_Markers");
            if (markers != null) go.transform.SetSiblingIndex(markers.GetSiblingIndex());
            var picture = new GameObject("Picture", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            picture.transform.SetParent(go.transform, false);
            var rect = (RectTransform)picture.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            _image = picture.GetComponent<RawImage>();
            _image.raycastTarget = false;
            _image.enabled = false;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("spawn protection layer built");
        }

        private static void Place(Rect uv)
        {
            if (_texture == null || _shownUv.width <= 0f || uv.width <= 0f) { _image.enabled = false; return; }
            var rect = _clip.rect;
            var picture = (RectTransform)_image.transform;
            picture.anchoredPosition = new Vector2((_shownUv.x - uv.x) / uv.width * rect.width, (_shownUv.y - uv.y) / uv.height * rect.height);
            picture.sizeDelta = new Vector2(_shownUv.width / uv.width * rect.width, _shownUv.height / uv.height * rect.height);
            _image.enabled = true;
        }

        private static void Disc(float span, Rect uv, float x, float z, float radius, byte state)
        {
            float cx = ((x / span + 0.5f - uv.x) / uv.width) * _w, cy = ((z / span + 0.5f - uv.y) / uv.height) * _h;
            float rx = radius / span / uv.width * _w, ry = radius / span / uv.height * _h;
            if (cx + rx < 0f || cx - rx > _w || cy + ry < 0f || cy - ry > _h || rx <= 0f || ry <= 0f) return;
            int y0 = Math.Max(0, (int)(cy - ry)), y1 = Math.Min(_h - 1, (int)(cy + ry) + 1);
            for (int y = y0; y <= y1; y++)
            {
                float dy = (y + 0.5f - cy) / ry;
                if (dy * dy > 1f) continue;
                float half = rx * Mathf.Sqrt(1f - dy * dy);
                int x0 = Math.Max(0, Mathf.CeilToInt(cx - half - 0.5f)), x1 = Math.Min(_w - 1, Mathf.FloorToInt(cx + half - 0.5f));
                int o = y * _w;
                for (int px = x0; px <= x1; px++) if (_mask[o + px] < state) _mask[o + px] = state;
            }
        }

        private static void Draw(Minimap map, Rect uv, Vector2 size)
        {
            float scale = Mathf.Min(1f, Mathf.Sqrt(MaxPixels / (size.x * size.y)));
            _w = Math.Max(8, Mathf.RoundToInt(size.x * scale)); _h = Math.Max(8, Mathf.RoundToInt(size.y * scale));
            if (_mask == null || _mask.Length != _w * _h) { _mask = new byte[_w * _h]; _pixels = new Color32[_w * _h]; }
            else Array.Clear(_mask, 0, _mask.Length);
            float span = map.m_textureSize * map.m_pixelSize;
            foreach (var mark in KnownWorld.Marks)
            {
                if (mark.Kind != 1) continue;
                float radius = SpawnAreas.Radius(mark, out bool learned);
                Disc(span, uv, mark.X, mark.Z, radius, learned ? (byte)2 : (byte)1);
            }
            foreach (var area in SpawnAreas.Live) Disc(span, uv, area.X, area.Z, area.Radius, 3);
            int covered = 0;
            for (int y = 0; y < _h; y++)
                for (int x = 0, p = y * _w; x < _w; x++, p++)
                {
                    byte s = _mask[p];
                    if (s == 0) { _pixels[p] = new Color32(); continue; }
                    covered++;
                    bool edge = x > 0 && _mask[p - 1] < s || x < _w - 1 && _mask[p + 1] < s || y > 0 && _mask[p - _w] < s || y < _h - 1 && _mask[p + _w] < s;
                    _pixels[p] = edge ? Edge[s] : Fill[s];
                }
            if (_texture == null || _texture.width != _w || _texture.height != _h)
            {
                if (_texture != null) UnityEngine.Object.Destroy(_texture);
                _texture = new Texture2D(_w, _h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            _texture.SetPixelData(_pixels, 0);
            _texture.Apply(false);
            _image.texture = _texture;
            _shownUv = uv; _shownSize = size; _shownVersion = SpawnAreas.Version; _shownKnown = KnownWorld.Version;
            Covered = covered;
        }
    }
}
