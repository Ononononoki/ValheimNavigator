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

    // shows a note while the pointer is on the graphic it sits on
    internal sealed class HoverNote : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        internal GameObject Note;
        internal Func<bool> When;

        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData) { if (Note != null && (When == null || When())) { Note.SetActive(true); Note.transform.SetAsLastSibling(); } }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData) { if (Note != null) Note.SetActive(false); }

        private void OnDisable() { if (Note != null) Note.SetActive(false); }
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
        // the detailed terrain map's progress line under the pages, its bar fills behind the text
        private const float FineTop = PageTop + PageHeight - 1f, FineHeight = 16f;
        private static TMP_Text _fineText;
        private static RectTransform _fineBar;
        private static GameObject _fineNote;
        // the note box of the bottom line, also opened from the detailed map switch on the settings page
        internal static GameObject FineNote => _fineNote;
        private static TMP_Text _fineNoteText;
        private static string _fineShown;
        private const string LivingMapNote = "LivingMap is installed too. Both mods draw on the large map, so where a Navigator map view is drawn it covers LivingMap's bases and roads.";
        private static bool? _livingMap;

        // LivingMap by tbsj1ga, found once when the panel is built (every plugin has loaded by then)
        private static bool LivingMapInstalled
        {
            get
            {
                if (_livingMap.HasValue) return _livingMap.Value;
                _livingMap = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("j1ga.livingmap");
                if (_livingMap.Value) Plugin.Log.LogInfo("LivingMap is installed, the panel's bottom line explains how the two maps overlap");
                return _livingMap.Value;
            }
        }

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
            bool timed = Plugin.LogCounters.Value;
            if (timed) _mark = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                TerrainOverlay.MiniFrame(map); if (timed) Mark(0);
                DetailOverlay.MiniFrame(map); if (timed) Mark(1);
                FineMapView.Frame(map); if (timed) Mark(2);
                Buildings.Frame(map); if (timed) Mark(3);
                MiniRoute.Frame(map); if (timed) Mark(4);
                if (Settings.SpawnSafe.Value) SpawnAreas.Scan();
                if (timed) Mark(5);
                bool large = map.m_mode == Minimap.MapMode.Large;
                _timedLarge = large;
                if (large && !_wasLarge) Opened(map);
                if (!large && _wasLarge) Closed(map);
                _wasLarge = large;
                if (timed) Mark(6);
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
                if (timed) Mark(7);
                if (large) ModeChoice.Frame(map);
                if (timed) Mark(8);
                if (large) Layers.Frame(map);
                if (timed) Mark(9);
                if (large) SpawnLayer.Frame(map);
                if (timed) Mark(10);
                if (large) TerrainOverlay.Frame(map);
                if (timed) Mark(11);
                if (large) DetailOverlay.Frame(map);
                if (timed) Mark(12);

                if (show)
                {
                    FineFrame();
                    Pages[_tab].Frame();
                    _refresh += Time.unscaledDeltaTime;
                    if (_refresh >= Settings.RefreshSeconds.Value) Select(_tab);
                }
                if (timed) Mark(13);
            }
            catch (Exception e)
            {
                _failed = true;
                Ui.Typing = false;
                Plugin.Log.LogError("panel switched off: " + e);
            }
            finally
            {
                if (timed) EndTick();
            }
        }

        // LogCounters: the time of each part of the frame, a frame over SlowTickMs writes its parts, a summary every SummarySeconds
        private static readonly string[] PartNames = { "minimap terrain view", "minimap detail view", "detailed map", "buildings", "minimap route", "spawn areas",
            "map open or close", "panel and layout", "mode choice", "markers and layers", "spawn layer", "large map terrain view", "large map detail view", "panel page" };
        private static readonly long[] PartNow = new long[PartNames.Length], PartSum = new long[PartNames.Length], PartMax = new long[PartNames.Length];
        private const double SlowTickMs = 15.0, PartShownMs = 0.5;
        private const float SummarySeconds = 60f;
        private const int SlowLogLimit = 80;
        private static long _mark;
        private static int _timedFrames, _slowFrames, _slowLogs;
        private static float _nextSummary;
        private static bool _timedLarge;

        private static void Mark(int part)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            PartNow[part] += now - _mark;
            _mark = now;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static void EndTick()
        {
            long total = 0;
            for (int i = 0; i < PartNow.Length; i++)
            {
                total += PartNow[i]; PartSum[i] += PartNow[i];
                if (PartNow[i] > PartMax[i]) PartMax[i] = PartNow[i];
            }
            _timedFrames++;
            if (Ms(total) >= SlowTickMs)
            {
                _slowFrames++;
                if (_slowLogs < SlowLogLimit)
                {
                    _slowLogs++;
                    var text = new System.Text.StringBuilder("slow map frame ").Append(DateTime.Now.ToString("HH:mm:ss")).Append(' ').Append(Ms(total).ToString("F1"))
                        .Append(" ms (").Append(_timedLarge ? "large map" : "minimap").Append("):");
                    for (int i = 0; i < PartNow.Length; i++) if (Ms(PartNow[i]) >= PartShownMs) text.Append(' ').Append(PartNames[i]).Append(' ').Append(Ms(PartNow[i]).ToString("F1")).Append(',');
                    Plugin.Log.LogInfo(text.ToString().TrimEnd(','));
                }
            }
            Array.Clear(PartNow, 0, PartNow.Length);
            if (Time.unscaledTime < _nextSummary) return;
            if (_nextSummary > 0f && _timedFrames > 0)
            {
                var text = new System.Text.StringBuilder("map frame parts over ").Append(_timedFrames).Append(" frames, ").Append(_slowFrames).Append(" over ")
                    .Append(SlowTickMs.ToString("F0")).Append(" ms (mean / longest ms):");
                for (int i = 0; i < PartSum.Length; i++)
                    if (PartMax[i] > 0) text.Append(' ').Append(PartNames[i]).Append(' ').Append((Ms(PartSum[i]) / _timedFrames).ToString("F3")).Append(" / ").Append(Ms(PartMax[i]).ToString("F1")).Append(',');
                Plugin.Log.LogInfo(text.ToString().TrimEnd(','));
            }
            Array.Clear(PartSum, 0, PartSum.Length); Array.Clear(PartMax, 0, PartMax.Length);
            _timedFrames = _slowFrames = 0;
            _nextSummary = Time.unscaledTime + SummarySeconds;
        }

        private static void FineFrame()
        {
            string fine = FineTerrain.Status, route = RouteStore.Status;
            string status = route == null ? fine : fine == null ? route : fine + ", " + route.ToLowerInvariant();
            if (status == _fineShown) return;
            _fineShown = status;
            bool on = status != null;
            // the bar follows the detailed terrain map while it builds, else the route map
            float progress = FineTerrain.Progress >= 0f && FineTerrain.Progress < 1f ? FineTerrain.Progress : RouteStore.Progress;
            _fineText.gameObject.SetActive(on);
            _fineBar.gameObject.SetActive(on && progress >= 0f && progress < 1f);
            // the hover note explains the switched off map and LivingMap next to Navigator, the line takes the pointer only then
            string note = fine == FineTerrain.OffStatus ? FineTerrain.OffNote : null;
            if (LivingMapInstalled) note = (note != null ? note + "\n" : "") + LivingMapNote;
            _fineText.raycastTarget = note != null;
            if (note == null) _fineNote.SetActive(false);
            else
            {
                // the note box grows with its text and sits right above the line
                _fineNoteText.text = note;
                float height = Mathf.Ceil(_fineNoteText.GetPreferredValues(note, PageWidth - 12f, 0f).y) + 2f;
                Ui.Place(_fineNote, _fineNote.transform.parent, PageLeft, FineTop - height - 12f, PageWidth, height + 8f);
                ((RectTransform)_fineNoteText.transform).sizeDelta = new Vector2(PageWidth - 12f, height);
            }
            if (!on) return;
            _fineText.text = note != null ? status + " (hover)" : status;
            _fineBar.sizeDelta = new Vector2(PageWidth * Mathf.Clamp01(progress), FineHeight);
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
            var barGo = new GameObject("DetailedTerrainBar", typeof(RectTransform), typeof(Image));
            _fineBar = Ui.Place(barGo, panel, PageLeft, FineTop, 0f, FineHeight);
            var bar = barGo.GetComponent<Image>();
            bar.color = new Color(Ui.Accent.r, Ui.Accent.g, Ui.Accent.b, 0.25f);
            bar.raycastTarget = false;
            _fineText = Ui.Label(panel, "DetailedTerrain", PageLeft + 4f, FineTop, PageWidth - 4f, FineHeight, 12f, TextAlignmentOptions.MidlineLeft, Ui.Dim);
            _fineText.raycastTarget = false;
            _fineNote = new GameObject("DetailedTerrainNote", typeof(RectTransform), typeof(Image));
            Ui.Place(_fineNote, panel, PageLeft, FineTop - 134f, PageWidth, 130f);
            var noteBack = _fineNote.GetComponent<Image>();
            noteBack.color = new Color(0.08f, 0.06f, 0.04f, 1f);
            noteBack.raycastTarget = false;
            _fineNoteText = Ui.Label(_fineNote.transform, "Text", 6f, 4f, PageWidth - 12f, 122f, 12f, TextAlignmentOptions.TopLeft, Ui.Dim);
            _fineNoteText.textWrappingMode = TextWrappingModes.Normal;
            _fineNoteText.raycastTarget = false;
            _fineText.enableAutoSizing = true; _fineText.fontSizeMin = 9f; _fineText.fontSizeMax = 12f;
            var hover = _fineText.gameObject.AddComponent<HoverNote>();
            hover.Note = _fineNote;
            _fineShown = "";
            barGo.SetActive(false); _fineText.gameObject.SetActive(false); _fineNote.SetActive(false);

            Find = new FindPage();
            Pages.Add(Find);
            Pages.Add(new LayersPage());
            Pages.Add(new SettingsPage());
            Pages.Add(new OverviewPage());
            Pages.Add(new NavigatorPage());
            Pages.Add(new TombstonesPage());

            // each tab as wide as its title needs, so every title shows whole
            int letters = 0;
            foreach (var pg in Pages) letters += pg.Title.Length + 2;
            float tabX = PageLeft, perLetter = (PageWidth - (Pages.Count - 1f) * 4f) / letters;
            for (int i = 0; i < Pages.Count; i++)
            {
                int tab = i;
                float tabWidth = (Pages[i].Title.Length + 2) * perLetter;
                TabButtons.Add(Ui.Tag(panel, "Tab" + i, Pages[i].Title, tabX, TabTop, tabWidth, TabHeight, 12f, TextAlignmentOptions.Midline, () => Select(tab), out unused));
                // the title fills the tab, the side margin of the other buttons would cut it
                var title = (RectTransform)TabButtons[i].transform.Find("Text");
                title.anchoredPosition = new Vector2(2f, 0f); title.sizeDelta = new Vector2(tabWidth - 4f, TabHeight);
                tabX += tabWidth + 4f;
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
