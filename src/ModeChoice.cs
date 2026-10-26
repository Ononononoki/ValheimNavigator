using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the first large map opening asks how Navigator should see the world: Explorer keeps search, markers and routes to explored land and
    // what lies close to it or to the player, Cartographer knows the whole world. the choice is kept in the cfg (02 - Map mode.MapMode) and
    // can be changed in Settings
    internal static class ModeChoice
    {
        private const float Width = 560f, Height = 250f;
        private static GameObject _root;

        internal static string Name(int mode) { return mode == 1 ? "Explorer" : mode == 2 ? "Cartographer" : "Ask"; }

        // every frame while the large map is open
        internal static void Frame(Minimap map)
        {
            bool ask = Settings.MapMode.Value == 0;
            if (!ask) { if (_root != null && _root.activeSelf) _root.SetActive(false); return; }
            if (_root == null) Build(map);
            if (!_root.activeSelf) { _root.SetActive(true); _root.transform.SetAsLastSibling(); }
        }

        private static void Build(Minimap map)
        {
            _root = new GameObject("ValheimNavigator_ModeChoice", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)_root.transform;
            rect.SetParent(map.m_largeRoot.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(Width, Height);
            Ui.Window(_root.GetComponent<Image>(), Mathf.Max(0.9f, Settings.Opacity.Value));
            var panel = _root.transform;
            Ui.Label(panel, "Title", 16f, 12f, Width - 32f, 30f, 22f, TextAlignmentOptions.MidlineLeft, Ui.Accent).text = "How should Valheim Navigator see the world?";
            var note = Ui.Label(panel, "Note", 16f, 48f, Width - 32f, 110f, 16f, TextAlignmentOptions.TopLeft, Ui.Ink);
            note.textWrappingMode = TextWrappingModes.Normal;
            note.overflowMode = TextOverflowModes.Overflow;
            note.text = "Explorer: search, map markers and routes cover the land you have explored and places close to it or to you, even a little into the fog. The rest stays hidden.\n"
                + "Cartographer: the whole world is known, also what lies in the fog.\nYou can change it later in Settings.";
            TMP_Text unused;
            float w = (Width - 48f) / 2f;
            Ui.Tag(panel, "Explorer", "Explorer", 16f, Height - 62f, w, 44f, 18f, TextAlignmentOptions.Midline, () => Choose(1), out unused);
            Ui.Tag(panel, "Cartographer", "Cartographer", 32f + w, Height - 62f, w, 44f, 18f, TextAlignmentOptions.Midline, () => Choose(2), out unused);
            Ui.ApplySounds(panel);
            Ui.ApplyLook(panel);
            _root.SetActive(false);
        }

        internal static void Choose(int mode)
        {
            Settings.MapMode.Value = mode;
            if (_root != null) _root.SetActive(false);
            Applied();
        }

        // everything that reads the mode is drawn again
        internal static void Applied()
        {
            Reveal.Apply(Minimap.instance);
            DetailOverlay.Dirty();
            Panel.LayersChanged();
            Navigator.Recalculate();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("map mode " + Name(Settings.MapMode.Value));
        }
    }
}
