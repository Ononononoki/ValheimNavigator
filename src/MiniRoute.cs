using System;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the route on the small map in the corner, the same path and colors as on the large map, drawn again only when the view or the route changes
    internal static class MiniRoute
    {
        private const float LineWidth = 2.5f;
        private const int MaxSegments = 6000;

        private static MarkerGraphic _graphic;
        private static RectTransform _rect;
        private static Rect _lastUv;
        private static Vector2 _lastSize;
        private static NavSearch _lastRoute;
        private static int _lastVersion;
        private static bool _warned;
        internal static int Segments;

        internal static void Frame(Minimap map)
        {
            try
            {
                var image = map != null ? map.m_mapImageSmall : null;
                if (image == null) return;
                var route = Navigator.Visible;
                bool show = Plugin.Enabled.Value && Settings.MiniRoute.Value && map.m_mode == Minimap.MapMode.Small && route != null && route.Finished && !route.Cancelled;
                if (_graphic == null)
                {
                    if (!show) return;
                    Create(image);
                }
                if (_graphic.gameObject.activeSelf != show) _graphic.gameObject.SetActive(show);
                if (!show) { _lastRoute = null; return; }
                var uv = image.uvRect;
                var rect = _rect.rect;
                if (route == _lastRoute && route.Version == _lastVersion && uv == _lastUv && rect.size == _lastSize) return;
                _lastRoute = route; _lastVersion = route.Version; _lastUv = uv; _lastSize = rect.size;
                Draw(map, rect, uv, route);
            }
            catch (Exception ex)
            {
                if (!_warned) Plugin.Log.LogWarning("minimap route unavailable: " + ex);
                _warned = true;
            }
        }

        private static void Create(RawImage image)
        {
            // the canvas renderer is added here, a graphic from a plugin dll does not get it from its RequireComponent
            var go = new GameObject("ValheimNavigator_MiniRoute", typeof(RectTransform), typeof(CanvasRenderer), typeof(RectMask2D));
            go.transform.SetParent(image.transform, false);
            _rect = (RectTransform)go.transform;
            Ui.Stretch(_rect, 0f);
            _graphic = go.AddComponent<MarkerGraphic>();
            _graphic.raycastTarget = false;
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("route layer built on the minimap");
        }

        private static void Draw(Minimap map, Rect rect, Rect uv, NavSearch route)
        {
            var segs = _graphic.Segs;
            segs.Clear(); _graphic.Dots.Clear(); _graphic.Symbols.Clear();
            float span = map.m_textureSize * map.m_pixelSize;
            for (int i = 1; i < route.Points.Count && segs.Count < MaxSegments; i++)
            {
                var a = route.Points[i - 1]; var b = route.Points[i];
                float x0 = rect.xMin + ((a.X / span + 0.5f - uv.x) / uv.width) * rect.width;
                float y0 = rect.yMin + ((a.Z / span + 0.5f - uv.y) / uv.height) * rect.height;
                // a portal jump draws no line, only a violet ring at the portal to walk into
                if (b.Mode == 2)
                {
                    if (rect.Contains(new Vector2(x0, y0))) _graphic.Dots.Add(new Dot { X = x0, Y = y0, Radius = 5f, Color = new Color32(170, 120, 210, 235) });
                    continue;
                }
                float x1 = rect.xMin + ((b.X / span + 0.5f - uv.x) / uv.width) * rect.width;
                float y1 = rect.yMin + ((b.Z / span + 0.5f - uv.y) / uv.height) * rect.height;
                if (!Layers.Clip(rect, ref x0, ref y0, ref x1, ref y1)) continue;
                segs.Add(new Seg { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Width = LineWidth, Color = Layers.RouteColor(a, b) });
            }
            Segments = segs.Count;
            _graphic.Refresh();
        }
    }
}
