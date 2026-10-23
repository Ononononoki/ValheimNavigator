using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // one tab of the panel, built once and shown again each time the panel opens or its tab is picked
    internal abstract class Page
    {
        internal GameObject Root;
        internal bool Built;

        internal abstract string Title { get; }

        internal abstract void Build(Transform parent);

        // the panel opens, the tab is picked or the refresh time has passed
        internal abstract void Show();
        internal virtual void Frame() { }
    }

    // the panel on the game's large map, built once with uGUI in the game's font. a title row, a row of tabs and the page of the picked tab
    internal static class Panel
    {
        internal const float Width = 340f;
        internal const float Height = 640f;
        internal const float PageWidth = 316f;
        internal const float PageHeight = 540f;
        private const float PageLeft = 12f;
        private const float PageTop = 84f;
        private const float TabTop = 46f;
        private const float TabHeight = 30f;

        private static bool _failed;
        private static bool _wasLarge;
        private static bool _panelOn;
        private static GameObject _root;
        private static GameObject _toggle;
        private static float _refresh;
        private static int _tab;
        private static readonly List<Page> Pages = new List<Page>();
        private static readonly List<UnityEngine.UI.Button> TabButtons = new List<UnityEngine.UI.Button>();
        private static readonly List<Image> Plates = new List<Image>();

        internal static FindPage Find;
        internal static double WorstPageMilliseconds;

        private static int _pointerFrame = -1;
        private static bool _pointerOver;

        private static bool _wasShown;

        private static bool Shift()
        {
            return UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
        }

        // the area the open panel covers, in the local space of the given rect. false when the panel is not shown.
        // the marker layer draws nothing under the panel
        internal static bool CoveredArea(RectTransform space, out Rect area)
        {
            area = default;
            if (_root == null || !_root.activeInHierarchy) return false;
            var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(space, _root.transform);
            area = new Rect(bounds.min.x, bounds.min.y, bounds.size.x, bounds.size.y);
            return true;
        }

        // true while the mouse is on the open panel. the map's wheel zoom is held back then, so the wheel scrolls the lists
        internal static bool PointerOver
        {
            get
            {
                if (_pointerFrame == Time.frameCount) return _pointerOver;
                _pointerFrame = Time.frameCount;
                _pointerOver = false;
                if (_root != null && _root.activeInHierarchy)
                {
                    var canvas = _root.GetComponentInParent<Canvas>();
                    var camera = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                    _pointerOver = RectTransformUtility.RectangleContainsScreenPoint((RectTransform)_root.transform, UnityEngine.Input.mousePosition, camera);
                }
                return _pointerOver;
            }
        }

        // every frame after the minimap's own update. the panel opens with the map only when shift is held
        internal static void Tick(Minimap map)
        {
            if (_failed || !Plugin.Enabled.Value) return;
            try
            {
                MiniRoute.Frame(map);
                if (Settings.SpawnSafe.Value) SpawnAreas.Scan();
                bool large = map.m_mode == Minimap.MapMode.Large;
                if (large && !_wasLarge) Opened(map);
                if (!large && _wasLarge) Closed(map);
                _wasLarge = large;
                if (!large && _root == null) return;
                if (_root == null) Build(map);

                bool show = large && _panelOn;
                if (show && !_root.activeSelf) Select(_tab);
                if (_root.activeSelf != show) _root.SetActive(show);
                if (show != _wasShown)
                {
                    _wasShown = show;
                    Layers.Dirty();
                }
                bool button = large && !_panelOn;
                if (_toggle.activeSelf != button) { _toggle.SetActive(button); if (button) _toggle.transform.SetAsLastSibling(); }
                if (show) Fit(map);
                Ui.Typing = show && Find != null && Find.Typing;
                if (large) Layers.Frame(map);
                if (large) SpawnLayer.Frame(map);
                if (large) TerrainOverlay.Frame(map);
                if (large) DetailOverlay.Frame(map);

                if (show)
                {
                    Pages[_tab].Frame();
                    _refresh += Time.unscaledDeltaTime;
                    if (_refresh >= Settings.RefreshSeconds.Value) Select(_tab);
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Ui.Typing = false;
                Plugin.Log.LogError("panel switched off: " + e);
            }
        }

        private static void Opened(Minimap map)
        {
            _panelOn = Shift();
            Session.EnsureFresh();
            Reveal.Apply(map);
            Layers.Dirty();
        }

        private static void Closed(Minimap map)
        {
            Layers.Close();
            Ui.Typing = false;
        }

        // a switch of the settings or layers tab or a done mark changed what the map should show
        internal static void LayersChanged()
        {
            // terrain pictures follow their own settings key, so marker and layer changes no longer redraw the terrain
            Layers.Dirty();
        }

        // the panel is drawn at its design size and scaled, by default to the height of the map window
        private static float _scale = -1f;
        internal static float Scale => _scale;
        // how far the open panel reaches into the map from the left, 0 when it is closed
        internal static float RightEdge => _root != null && _root.activeInHierarchy && _scale > 0f ? 12f + Width * _scale : 0f;

        private static void Fit(Minimap map)
        {
            var image = map.m_mapImageLarge != null ? (RectTransform)map.m_mapImageLarge.transform : null;
            float available = image != null ? image.rect.height * image.lossyScale.y / Math.Max(0.0001f, _root.transform.parent.lossyScale.y) : Height;
            float scale = Settings.PanelScale.Value > 0f ? Settings.PanelScale.Value : Math.Max(0.6f, (available - 2f * FitMargin) / Height);
            if (Math.Abs(scale - _scale) < 0.001f) return;
            _scale = scale;
            _root.transform.localScale = new Vector3(scale, scale, 1f);
            Layers.Dirty();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("panel scale " + scale.ToString("F2") + (Settings.PanelScale.Value > 0f ? "" : ", fitted to the map height"));
        }

        private const float FitMargin = 10f;

        internal static void ApplyStyle()
        {
            if (_root == null) return;
            Ui.Window(_root.GetComponent<Image>(), Settings.Opacity.Value);
            foreach (var plate in Plates) Ui.Plate(plate);
            _scale = -1f;
            foreach (var scroll in _root.GetComponentsInChildren<ScrollRect>(true)) scroll.scrollSensitivity = Settings.ScrollSpeed.Value;
            Ui.ApplySounds(_root.transform);
            Ui.ApplySounds(MarkerMenu.Root);
            Ui.ApplyLook(_root.transform);
            Ui.ApplyLook(MarkerMenu.Root);
            MarkerMenu.ApplyLook();
            if (Pages.Count > 0 && Pages[_tab].Built) Select(_tab);
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("panel style, opacity " + Settings.Opacity.Value + ", wheel " + Settings.ScrollSpeed.Value);
        }

        internal static void OpenRoute()
        {
            _panelOn = true;
            Select(4);
        }

        internal static void OpenSettings() { Select(2); ((SettingsPage)Pages[2]).FocusTerrain(); }

        internal static void Pick(Entry entry)
        {
            _panelOn = true;
            Select(0);
            _root.SetActive(true);
            Find.PickEntry(entry);
        }

        internal static void PickKnown(KnownMark mark)
        {
            _panelOn = true;
            Select(0);
            _root.SetActive(true);
            Find.PickKnown(mark);
        }

        // the find tab holds the destination, it is built without switching to it when a map marker becomes the destination
        internal static void EnsureFind()
        {
            if (_root == null || Pages.Count == 0 || Pages[0].Built) return;
            Pages[0].Build(Pages[0].Root.transform);
            Pages[0].Built = true;
        }

        internal static void Close()
        {
            _panelOn = false;
        }

        private static void Select(int tab)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool built = !Pages[tab].Built;
            if (built) { Pages[tab].Build(Pages[tab].Root.transform); Pages[tab].Built = true; }
            _tab = tab;
            _refresh = 0f;
            for (int i = 0; i < Pages.Count; i++)
            {
                Pages[i].Root.SetActive(i == tab);
                Ui.Tint(TabButtons[i], i == tab ? Ui.Chosen : Ui.Face);
            }
            Pages[tab].Show();
            WorstPageMilliseconds = Math.Max(WorstPageMilliseconds, clock.Elapsed.TotalMilliseconds);
            if (built && Plugin.LogCounters.Value) Plugin.Log.LogInfo("page built, " + Pages[tab].Title + ", " + clock.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
        }

        private static void Build(Minimap map)
        {
            Ui.Font = map.m_biomeNameLarge != null ? map.m_biomeNameLarge.font : null;
            Ui.FindClickSound(map.m_largeRoot != null ? map.m_largeRoot.transform : null);
            Ui.FindGameLook();
            Pages.Clear();
            TabButtons.Clear();

            _root = new GameObject("ValheimNavigator_Panel", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)_root.transform;
            rect.SetParent(map.m_largeRoot.transform, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(12f, 0f);
            rect.sizeDelta = new Vector2(Width, Height);
            Ui.Window(_root.GetComponent<Image>(), Settings.Opacity.Value);
            var panel = _root.transform;
            TMP_Text unused;

            Ui.Label(panel, "Title", 12f, 8f, 250f, 30f, 22f, TextAlignmentOptions.MidlineLeft, Ui.Accent).text = "Valheim Navigator";
            // one dark plate behind the page area, the pages draw on top of it
            var plateGo = new GameObject("Plate", typeof(RectTransform), typeof(Image));
            Ui.Place(plateGo, panel, PageLeft - 4f, PageTop - 4f, PageWidth + 8f, PageHeight + 2f);
            var plate = plateGo.GetComponent<Image>();
            plate.raycastTarget = false;
            Ui.FindPlate();
            Ui.Plate(plate);
            Plates.Clear(); Plates.Add(plate);
            Ui.Tag(panel, "Close", "X", 300f, 8f, 30f, 30f, 18f, TextAlignmentOptions.Midline, Close, out unused);

            Find = new FindPage();
            Pages.Add(Find);
            Pages.Add(new LayersPage());
            Pages.Add(new SettingsPage());
            Pages.Add(new OverviewPage());
            Pages.Add(new NavigatorPage());

            float tabWidth = (PageWidth - (Pages.Count - 1f) * 4f) / Pages.Count;
            for (int i = 0; i < Pages.Count; i++)
            {
                int tab = i;
                TabButtons.Add(Ui.Tag(panel, "Tab" + i, Pages[i].Title, PageLeft + i * (tabWidth + 4f), TabTop, tabWidth, TabHeight, 12f, TextAlignmentOptions.Midline, () => Select(tab), out unused));
                var page = new GameObject("Page" + i, typeof(RectTransform));
                Ui.Place(page, panel, PageLeft, PageTop, PageWidth, PageHeight);
                Pages[i].Root = page;
                page.SetActive(false);
            }
            _root.SetActive(false);

            // the button sits in the top left corner of the map window, above the map's own layers
            _toggle = Ui.Tag(map.m_mapImageLarge != null ? map.m_mapImageLarge.transform : map.m_largeRoot.transform, "ValheimNavigator_Toggle", "Navigator", 0f, 0f, 132f, 34f, 18f, TextAlignmentOptions.Midline, () => { _panelOn = true; }, out unused).gameObject;
            var toggle = (RectTransform)_toggle.transform;
            toggle.anchorMin = toggle.anchorMax = toggle.pivot = new Vector2(0f, 1f);
            toggle.anchoredPosition = new Vector2(10f, -10f);
            _toggle.SetActive(false);
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("panel built on the large map, font " + (Ui.Font != null ? Ui.Font.name : "none") + ", opacity " + Settings.Opacity.Value + ", wheel " + Settings.ScrollSpeed.Value);
        }
    }
}
