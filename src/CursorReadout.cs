using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    internal static class CursorReadout
    {
        private static TMP_Text _text;
        private static float _next;

        internal static void Frame(Minimap map, RectTransform rect, Camera camera)
        {
            var local = Vector2.zero;
            bool show = Settings.CursorTerrain.Value && !Panel.PointerOver && RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, Input.mousePosition, camera, out local) && rect.rect.Contains(local);
            if (!show) { if (_text != null) _text.transform.parent.gameObject.SetActive(false); return; }
            if (_text == null)
            {
                var go = new GameObject("ValheimNavigator_Cursor", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                var root = (RectTransform)map.m_largeRoot.transform;
                // one row above the game's own key hints at the bottom of the map
                Ui.Place(go, root, 14f, root.rect.height - 80f, 640f, 30f);
                go.GetComponent<Image>().color = new Color(0.06f, 0.05f, 0.04f, 0.72f);
                go.GetComponent<Image>().raycastTarget = false;
                _text = Ui.Label(go.transform, "Readout", 8f, 0f, 624f, 30f, 16f, TextAlignmentOptions.MidlineLeft, Ui.Ink);
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("map cursor terrain readout created");
            }
            _text.transform.parent.gameObject.SetActive(true);
            _text.gameObject.SetActive(true);
            // the readout starts right of the open panel so the panel never covers it
            var box = (RectTransform)_text.transform.parent;
            float left = 14f + Panel.RightEdge;
            if (Mathf.Abs(box.anchoredPosition.x - left) > 0.5f) box.anchoredPosition = new Vector2(left, box.anchoredPosition.y);
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.1f;
            var uv = map.m_mapImageLarge.uvRect;
            float u = uv.x + (local.x - rect.rect.xMin) / rect.rect.width * uv.width;
            float v = uv.y + (local.y - rect.rect.yMin) / rect.rect.height * uv.height;
            float span = map.m_textureSize * map.m_pixelSize;
            var position = new Vector3((u - 0.5f) * span, 0f, (v - 0.5f) * span);
            string text = "x " + position.x.ToString("F0") + ", z " + position.z.ToString("F0");
            var height = MapAccess.Height(map);
            var fog = MapAccess.Fog(map);
            if (position.x * position.x + position.z * position.z > WorldGenerator.waterEdge * WorldGenerator.waterEdge) text += "    Outside the world";
            else if (!Settings.ShowUnexplored.Value && fog != null && fog.GetPixelBilinear(u, v).r > 0.5f) text += "    Unexplored";
            else if (height != null)
            {
                float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                float h = height.GetPixelBilinear(u, v).r;
                text += h >= water ? "    Ground " + (h - water).ToString("F1") + " m above sea" : "    Water depth " + (water - h).ToString("F1") + " m";
            }
            if (Settings.SpawnSafe.Value)
            {
                int state = SpawnAreas.StateAt(position.x, position.z);
                if (state > 0) text += "    " + SpawnAreas.Describe(state);
            }
            _text.text = text;
        }
    }
}
