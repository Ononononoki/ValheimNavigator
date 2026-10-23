using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the small box a click on a map marker opens. the panel's tab and the current destination stay until one of its buttons is used
    internal static class MarkerMenu
    {
        private const float Width = 300f;
        private const float TextHeight = 52f;
        private const float ButtonHeight = 30f;
        private const float Pad = 6f;
        private const float Offset = 12f;
        private const float MatchMeters = 1f;

        private static GameObject _root;
        private static TMP_Text _text;
        private static Button _find;
        private static TMP_Text _goText;
        private static Entry _entry;
        private static KnownMark _known;
        private static bool _current;
        private static Func<Minimap, Vector3, Vector3> _toWorld;
        private static Func<Minimap, float> _radius;
        private static Func<Minimap, Vector3, float, bool, Minimap.PinData> _closest;
        private static bool _nativeFailed;

        internal static bool Open => _root != null && _root.activeSelf;
        internal static Transform Root => _root != null ? _root.transform : null;

        internal static void ApplyLook()
        {
            if (_root != null) Ui.Window(_root.GetComponent<Image>(), 0.95f);
        }
        internal static bool Current => _current;

        internal static void Hide()
        {
            if (_root != null && _root.activeSelf) _root.SetActive(false);
            _entry = null; _known = null; _current = false;
        }

        // the game's own left click crosses off a saved pin near the mouse, that click is left to the game
        internal static bool SavedPinUnderMouse(Minimap map)
        {
            if (_nativeFailed) return false;
            try
            {
                if (_closest == null)
                {
                    _toWorld = AccessTools.MethodDelegate<Func<Minimap, Vector3, Vector3>>(AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint", new[] { typeof(Vector3) }));
                    _radius = AccessTools.MethodDelegate<Func<Minimap, float>>(AccessTools.PropertyGetter(typeof(Minimap), "PinInteractRadius"));
                    _closest = AccessTools.MethodDelegate<Func<Minimap, Vector3, float, bool, Minimap.PinData>>(AccessTools.Method(typeof(Minimap), "GetClosestPin", new[] { typeof(Vector3), typeof(float), typeof(bool) }));
                }
                return _closest(map, _toWorld(map, Input.mousePosition), _radius(map), true) != null;
            }
            catch (Exception ex)
            {
                _nativeFailed = true;
                Plugin.Log.LogWarning("saved pin check unavailable: " + ex.Message);
                return false;
            }
        }

        private static Vector3 PositionOf(Entry entry, KnownMark known)
        {
            return entry != null ? new Vector3(entry.X, entry.Y, entry.Z) : new Vector3(known.X, 0f, known.Z);
        }

        private static string NameOf(Entry entry, KnownMark known)
        {
            if (entry != null) return entry.Kind + (entry.Possible ? " (possible site)" : "") + (entry.Done ? " (done)" : "");
            return known.Kind == 1 ? "Spawn-protected area" : known.Name;
        }

        // true when the place is what the find card, the pin and the route already point at
        internal static bool IsDestination(Vector3 at)
        {
            Vector3? target = Panel.Find != null ? Panel.Find.Target : null;
            if (!target.HasValue) target = Navigator.Destination;
            if (!target.HasValue) return false;
            float dx = target.Value.x - at.x, dz = target.Value.z - at.z;
            return dx * dx + dz * dz <= MatchMeters * MatchMeters;
        }

        internal static void Show(Minimap map, Entry entry, KnownMark known, Camera camera)
        {
            var root = map.m_largeRoot != null ? (RectTransform)map.m_largeRoot.transform : null;
            if (root == null || entry == null && known == null) return;
            if (_root == null) Build(root);
            _entry = entry; _known = known;
            var at = PositionOf(entry, known);
            _current = IsDestination(at);
            string text = (_current ? "Current destination\n" : "") + NameOf(entry, known);
            var player = Session.PlayerPosition();
            if (player.HasValue)
            {
                float dx = at.x - player.Value.x, dz = at.z - player.Value.z;
                text += "\n" + Nearest.Distance(dx, dz).ToString("F0") + " m " + Nearest.Direction(dx, dz);
            }
            _text.text = text;
            _goText.text = _current ? "Route" : "Go here";
            _find.gameObject.SetActive(!_current);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, Input.mousePosition, camera, out var p)) return;
            var rect = (RectTransform)_root.transform;
            float height = rect.sizeDelta.y;
            float x = Mathf.Clamp(p.x - root.rect.xMin + Offset, 0f, root.rect.width - Width);
            float y = Mathf.Clamp(p.y - root.rect.yMin - Offset, height, root.rect.height);
            rect.anchoredPosition = new Vector2(x, y);
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("marker menu, " + NameOf(entry, known) + (_current ? ", current destination" : ""));
        }

        private static void Build(RectTransform root)
        {
            _root = new GameObject("ValheimNavigator_MarkerMenu", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)_root.transform;
            rect.SetParent(root, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(Width, Pad * 3f + TextHeight + ButtonHeight);
            Ui.Window(_root.GetComponent<Image>(), 0.95f);
            _text = Ui.Label(_root.transform, "Text", Pad + 4f, Pad, Width - Pad * 2f - 8f, TextHeight, 16f, TextAlignmentOptions.TopLeft, Ui.Ink);
            _text.textWrappingMode = TextWrappingModes.Normal;
            float y = Pad * 2f + TextHeight, w = (Width - Pad * 4f) / 3f;
            Ui.Tag(_root.transform, "Go", "Go here", Pad, y, w, ButtonHeight, 15f, TextAlignmentOptions.Midline, Go, out _goText);
            TMP_Text unused;
            _find = Ui.Tag(_root.transform, "Find", "Open in Find", Pad * 2f + w, y, w, ButtonHeight, 14f, TextAlignmentOptions.Midline, OpenInFind, out unused);
            Ui.Tag(_root.transform, "Close", "Close", Pad * 3f + w * 2f, y, w, ButtonHeight, 15f, TextAlignmentOptions.Midline, Hide, out unused);
            _root.SetActive(false);
        }

        // the clicked place becomes the destination and a route is calculated, the panel keeps its tab
        private static void Go()
        {
            var entry = _entry; var known = _known; bool current = _current;
            Hide();
            if (entry == null && known == null) return;
            if (!current)
            {
                Panel.EnsureFind();
                if (Panel.Find == null) return;
                if (entry != null) Panel.Find.PickEntry(entry); else Panel.Find.PickKnown(known);
            }
            else if (!Navigator.Destination.HasValue) Navigator.SetTarget(PositionOf(entry, known), NameOf(entry, known));
            Navigator.Start();
        }

        private static void OpenInFind()
        {
            var entry = _entry; var known = _known;
            Hide();
            if (entry != null) Panel.Pick(entry);
            else if (known != null) Panel.PickKnown(known);
        }
    }
}
