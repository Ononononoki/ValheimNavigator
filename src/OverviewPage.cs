using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // a click on the overview, turned into a world position by the page
    internal sealed class OverviewClick : MonoBehaviour, IPointerClickHandler
    {
        internal Action<PointerEventData> Clicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            Clicked?.Invoke(eventData);
        }
    }

    // the overview tab. the whole world in the panel, with a dot for each place of the layers that are on, you and the picked result.
    // a click centers the large map there
    internal sealed class OverviewPage : Page
    {
        private const int Side = 316;
        private const int DotRadius = 1;
        private const int MarkRadius = 3;
        // the large map's shader takes its zoom, its pixel size (the game sets 200 divided by the zoom) and its center, here the whole world
        private const float Zoom = 1f;
        private const float PixelSizeBase = 200f;

        private RawImage _map;
        private RawImage _dots;
        private RawImage _terrain;
        private RawImage _fog, _shared;
        private int _overlayVersion = -1;
        private int _routeVersion = -1;
        private int _knownVersion = -1;
        private int _portalVersion = -1;
        private NavSearch _route;
        private Vector3? _picked;
        private Texture2D _dotTexture;
        private Color32[] _buffer;
        private Material _material;
        private TMP_Text _note;
        private bool _failed;

        internal override string Title => "World";

        internal override void Build(Transform parent)
        {
            _note = Ui.Label(parent, "Note", 0f, Side + 8f, Panel.PageWidth, 130f, 15f, TextAlignmentOptions.TopLeft, Ui.Dim);
            _note.textWrappingMode = TextWrappingModes.Normal;
            TMP_Text unused;
            Ui.Tag(parent, "Route", "Route to clicked point", 0f, 462f, Panel.PageWidth, 30f, 15f, TextAlignmentOptions.Midline, () =>
            {
                if (!_picked.HasValue) return;
                Navigator.SetTarget(_picked.Value, "World map point"); Panel.OpenRoute(); Navigator.Start();
            }, out unused);
            Ui.Tag(parent, "ViewControls", "Terrain and sailing controls", 0f, 500f, Panel.PageWidth, 30f, 14f, TextAlignmentOptions.Midline, Panel.OpenSettings, out unused);
        }

        internal override void Show()
        {
            if (_failed) return;
            var map = Minimap.instance;
            if (map == null) return;
            try
            {
                if (_map == null) Create(map);
                _material.SetTexture("_FogTex", Reveal.Fog(map));
                _material.SetFloat("_zoom", Zoom);
                _material.SetFloat("_pixelSize", PixelSizeBase / Zoom);
                _material.SetVector("_mapCenter", Vector4.zero);
                Draw(map);
                Frame();
            }
            catch (Exception ex)
            {
                _failed = true;
                _note.text = "The overview is not available.";
                Plugin.Log.LogWarning("could not build the overview: " + ex.Message);
            }
        }

        private void Create(Minimap map)
        {
            _material = new Material(MapAccess.LargeShader(map));
            _map = Layer("World", 0);
            _map.material = _material;
            _map.texture = map.m_mapImageLarge != null && map.m_mapImageLarge.texture != null ? map.m_mapImageLarge.texture : map.m_mapTexture;
            _map.uvRect = new Rect(0f, 0f, 1f, 1f);
            _map.gameObject.AddComponent<OverviewClick>().Clicked = Clicked;

            _dotTexture = new Texture2D(Side, Side, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _buffer = new Color32[Side * Side];
            _terrain = Layer("Terrain", 1); _terrain.raycastTarget = false;
            _fog = Layer("Shade", 2); _fog.raycastTarget = false;
            _shared = Layer("SharedShade", 3); _shared.raycastTarget = false;
            _dots = Layer("Dots", 4);
            _dots.texture = _dotTexture;
            _dots.raycastTarget = false;
        }

        private RawImage Layer(string name, int order)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            Ui.Place(go, Root.transform, 0f, 0f, Side, Side);
            go.transform.SetSiblingIndex(order);
            return go.GetComponent<RawImage>();
        }

        // a pixel of the dot texture for a world position, y grows to the north
        private static bool Pixel(Minimap map, float x, float z, out int px, out int py)
        {
            float size = map.m_textureSize;
            px = (int)((x / map.m_pixelSize + size / 2f) / size * Side);
            py = (int)((z / map.m_pixelSize + size / 2f) / size * Side);
            return px >= 0 && py >= 0 && px < Side && py < Side;
        }

        private void Blob(int cx, int cy, int radius, Color32 color)
        {
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(Side - 1, cy + radius); y++)
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(Side - 1, cx + radius); x++)
                    _buffer[y * Side + x] = color;
        }

        private void Draw(Minimap map)
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            int dots = 0;
            if (Session.Ready)
                foreach (var kind in Session.Kinds.Values)
                {
                    if (!Settings.KindOn(kind)) continue;
                    Color32 color = Ui.DotColor(kind.Category);
                    Color32 faded = new Color32(color.r, color.g, color.b, 70);
                    foreach (var e in kind.Items)
                        if ((Settings.SpawnFilter.Value == 0 || e.Placed == (Settings.SpawnFilter.Value == 1)) && ExplorerArea.Visible(e.X, e.Z) && Pixel(map, e.X, e.Z, out int px, out int py)) { Blob(px, py, DotRadius, e.Done ? faded : color); dots++; }
                }
            var last = Session.Last;
            if (Settings.SearchPins.Value && MapAccess.Pins(map) != null)
                foreach (var pin in MapAccess.Pins(map))
                    if (pin.m_save && Pixel(map, pin.m_pos.x, pin.m_pos.z, out int px, out int py)) Blob(px, py, 1, new Color32(229, 221, 197, 230));
            if (KnownWorld.Enabled)
                foreach (var mark in KnownWorld.Marks)
                {
                    if (!KnownWorld.Drawn(mark)) continue;
                    if (Pixel(map, mark.X, mark.Z, out int mx, out int my))
                        Blob(mx, my, 1, KnownWorld.Color(mark));
                }
            if (Settings.PortalsOn.Value && Settings.PortalLines.Value)
            {
                Portals.Refresh(Settings.PortalSeconds.Value);
                var tags = new System.Collections.Generic.Dictionary<string, PortalInfo>();
                foreach (var portal in Portals.List)
                {
                    if (!Pixel(map, portal.X, portal.Z, out int px, out int py)) continue;
                    if (!string.IsNullOrEmpty(portal.Tag) && tags.TryGetValue(portal.Tag, out var other) && Pixel(map, other.X, other.Z, out int ox, out int oy))
                    {
                        int length = Math.Max(1, Math.Max(Math.Abs(px - ox), Math.Abs(py - oy)));
                        for (int step = 0; step <= length; step++) if (step % 6 < 3) Blob(ox + (px - ox) * step / length, oy + (py - oy) * step / length, 0, new Color32(174, 157, 177, 88));
                    }
                    if (!string.IsNullOrEmpty(portal.Tag)) tags[portal.Tag] = portal;
                    Blob(px, py, 1, new Color32(174, 157, 177, 58));
                }
            }
            var route = Navigator.Visible;
            if (route != null && route.Finished && !route.Cancelled)
                for (int i = 1; i < route.Points.Count; i++)
                {
                    var a = route.Points[i - 1]; var b = route.Points[i];
                    if (b.Mode == 2) continue; // portal jumps are not drawn as lines
                    if (!Pixel(map, a.X, a.Z, out int ax, out int ay) || !Pixel(map, b.X, b.Z, out int bx, out int by)) continue;
                    int count = Math.Max(1, Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)));
                    Color32 color = Layers.RouteColor(a, b);
                    for (int step = 0; step <= count; step++) Blob(ax + (bx - ax) * step / count, ay + (by - ay) * step / count, 0, color);
                }
            if (last != null && Pixel(map, last.Entry.X, last.Entry.Z, out int lx, out int ly)) Blob(lx, ly, MarkRadius, new Color32(255, 200, 40, 255));
            var at = Session.PlayerPosition();
            if (at != null && Pixel(map, at.Value.x, at.Value.z, out int sx, out int sy)) Blob(sx, sy, MarkRadius, new Color32(255, 255, 255, 255));
            _dotTexture.SetPixelData(_buffer, 0);
            _dotTexture.Apply(false);
            _note.text = (dots == 0 ? "Switch categories on in the Layers tab to see their places here. " : dots + " places shown. ")
                + "White is you, gold is the picked result. Click the map to center the large map there.";
        }

        private void Clicked(PointerEventData data)
        {
            var map = Minimap.instance;
            if (map == null || _map == null) return;
            var rect = (RectTransform)_map.transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, data.position, data.pressEventCamera, out var local)) return;
            float u = local.x / Side, v = (local.y + Side) / Side;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return;
            float size = map.m_textureSize;
            _picked = new Vector3((u * size - size / 2f) * map.m_pixelSize, 0f, (v * size - size / 2f) * map.m_pixelSize);
            map.ShowPointOnMap(_picked.Value);
            _note.text = "Picked x " + _picked.Value.x.ToString("F0") + ", z " + _picked.Value.z.ToString("F0") + ". Press Route below.";
        }

        internal override void Frame()
        {
            if (_terrain == null) return;
            _terrain.texture = TerrainOverlay.Texture;
            _terrain.gameObject.SetActive(TerrainOverlay.Active);
            _fog.texture = TerrainOverlay.Shade;
            _fog.color = TerrainOverlay.ShadeColor;
            _fog.gameObject.SetActive(Settings.UnexploredShown);
            _shared.texture = TerrainOverlay.SharedShade;
            _shared.color = TerrainOverlay.SharedColor;
            _shared.gameObject.SetActive(TerrainOverlay.SharedShown && (TerrainOverlay.Active || Settings.UnexploredShown) && TerrainOverlay.SharedShade != null);
            _material.SetTexture("_FogTex", Reveal.Fog(Minimap.instance));
            // the copied material keeps the game's live shared fade, as the large map does
            var live = MapAccess.LargeShader(Minimap.instance);
            if (live != null && live.HasProperty("_SharedFade")) _material.SetFloat("_SharedFade", live.GetFloat("_SharedFade"));
            int version = Navigator.Visible != null ? Navigator.Visible.Version : -1;
            if (_overlayVersion != TerrainOverlay.Version || _routeVersion != version || _knownVersion != KnownWorld.Version || _portalVersion != Portals.Version || _route != Navigator.Visible)
            {
                _overlayVersion = TerrainOverlay.Version; _routeVersion = version; _knownVersion = KnownWorld.Version; _portalVersion = Portals.Version; _route = Navigator.Visible; Draw(Minimap.instance);
            }
        }
    }
}
