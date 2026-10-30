using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the settings tab. the same entries as the cfg file, changed here they are saved at once
    internal sealed class SettingsPage : Page
    {
        private const float RowStep = 34f;

        private readonly List<Ui.Switch> _rows = new List<Ui.Switch>();
        private readonly List<Ui.Switch> _marks = new List<Ui.Switch>();
        private GameObject _main;
        private GameObject _sub;
        private TMP_Text _status;
        private Ui.Scroller _options;

        internal override string Title => "Settings";

        internal override void Build(Transform parent)
        {
            _main = new GameObject("Main", typeof(RectTransform));
            Ui.Place(_main, parent, 0f, 0f, Panel.PageWidth, Panel.PageHeight);
            _sub = new GameObject("AutoMarkCategories", typeof(RectTransform));
            Ui.Place(_sub, parent, 0f, 0f, Panel.PageWidth, Panel.PageHeight);

            var scroll = Ui.Scroll(_main.transform, "Options", 0f, 0f, Panel.PageWidth, Panel.PageHeight);
            _options = scroll;
            var page = scroll.Content;
            float w = scroll.Width;
            Section(page, "Search and arrival");
            Add(Ui.Toggle(page, "AutoMark", "Auto mark on arrival", 0f, 0f, w, Settings.AutoMark, null));
            Add(Ui.Cycle(page, "AutoMarkMeters", "Arrival distance", 0f, 0f, w, Plugin.AutoMarkMeters, new[] { 10f, 20f, 30f, 50f, 100f }, " m"));
            Add(Ui.Row(page, "AutoMarkCategories", "Auto mark categories", 0f, 0f, w, () => ">", () => false, () => Open(true)));
            Add(Ui.Toggle(page, "NearestPin", "Pin the nearest result", 0f, 0f, w, Settings.NearestPin, null));
            Add(Ui.Toggle(page, "IncludeUnexplored", "Find unexplored places", 0f, 0f, w, Settings.IncludeUnexplored, null));
            Add(Ui.Toggle(page, "SearchPins", "Search my map pins", 0f, 0f, w, Settings.SearchPins, Panel.LayersChanged));
            Add(Ui.Toggle(page, "Portals", "Search my portals", 0f, 0f, w, Settings.PortalsOn, Portals.Forget));
            Add(Ui.Cycle(page, "PortalSeconds", "Portal refresh", 0f, 0f, w, Settings.PortalSeconds, new[] { 2f, 5f, 15f, 30f, 60f }, " s"));
            Section(page, "Route");
            Add(Ui.Cycle(page, "RouteRefresh", "Route follows every", 0f, 0f, w, Settings.RouteRefresh, new[] { 0.5f, 1f, 2f, 5f }, " s"));
            Add(Ui.Cycle(page, "OffRoute", "Recalculate off route", 0f, 0f, w, Settings.OffRouteMeters, new[] { 25f, 50f, 100f }, " m"));
            Add(Ui.Toggle(page, "UseShip", "Use my ship", 0f, 0f, w, Settings.UseShip, null));
            Add(Ui.Toggle(page, "UseWind", "Wind in sailing", 0f, 0f, w, Settings.UseWind, null));
            Add(Ui.Toggle(page, "UsePortals", "Use my portals", 0f, 0f, w, Settings.RoutePortals, null));
            Add(Ui.Toggle(page, "CarryingOre", "Carrying ore", 0f, 0f, w, Settings.CarryingOre, null));
            Add(Ui.Row(page, "SailBias", "Foot or sail", 0f, 0f, w, () => Settings.SailBias.Value < 0.9f ? "More sail" : Settings.SailBias.Value > 1.1f ? "More walk" : "Balanced", () => Mathf.Abs(Settings.SailBias.Value - 1f) > 0.1f,
                () => { float[] steps = { 1f, 0.6f, 1.6f }; int i = Array.FindIndex(steps, s => Mathf.Abs(s - Settings.SailBias.Value) < 0.05f); Settings.SailBias.Value = steps[(i + 1) % steps.Length]; }));
            Add(Ui.Row(page, "BiomeDanger", "Harder biomes", 0f, 0f, w, () => Navigator.DangerNames[Settings.Danger.Value], () => Settings.Danger.Value != 0,
                () => { Settings.Danger.Value = (Settings.Danger.Value + 1) % Navigator.DangerNames.Length; }));
            Add(Ui.Toggle(page, "MiniRoute", "Route on the minimap", 0f, 0f, w, Settings.MiniRoute, null));
            Add(Ui.Toggle(page, "CursorReadout", "Cursor height and depth", 0f, 0f, w, Settings.CursorTerrain, null));
            Section(page, "Terrain");
            Add(Ui.Row(page, "MapMode", "Map mode", 0f, 0f, w, () => ModeChoice.Name(Settings.MapMode.Value), () => Settings.Explorer,
                () => { Settings.MapMode.Value = Settings.MapMode.Value == 1 ? 2 : 1; ModeChoice.Applied(); }));
            Add(Ui.Toggle(page, "MinimapTerrain", "Map view on the minimap", 0f, 0f, w, Settings.MinimapTerrain, null));
            Add(Ui.Toggle(page, "FogClouds", "Game clouds in the shade", 0f, 0f, w, Settings.FogClouds, TerrainOverlay.Dirty));
            // the detailed view needs the 2 m terrain, switched off in the cfg the row says so and its hover note explains
            var view = Ui.Row(page, "View", "Map view", 0f, 0f, w, () => Settings.DetailedView && !Settings.FineTerrain.Value ? "Detailed (off in cfg)" : new[] { "Terrain", "Biomes", "Height", "Simplified", "Detailed" }[Settings.TerrainView.Value],
                () => Settings.TerrainView.Value != 0, () => { Settings.TerrainView.Value = (Settings.TerrainView.Value + 1) % 5; Panel.LayersChanged(); });
            var note = view.Button.gameObject.AddComponent<HoverNote>();
            note.Note = Panel.FineNote; note.When = () => Settings.DetailedView && !Settings.FineTerrain.Value;
            Add(view);
            Add(Ui.Row(page, "Structures", "Buildings", 0f, 0f, w, () => new[] { "None", "Placed by players", "All structures" }[Settings.Structures.Value],
                () => Settings.Structures.Value != 0, () => { Settings.Structures.Value = (Settings.Structures.Value + 1) % 3; }));
            Add(Ui.Toggle(page, "ShowUnexplored", "Show unexplored terrain", 0f, 0f, w, Settings.ShowUnexplored, () => Reveal.Apply(Minimap.instance)));
            Add(Ui.Cycle(page, "FogTint", "Unexplored shade", 0f, 0f, w, Settings.FogTint, new[] { 0f, 0.15f, 0.25f, 0.30f, 0.4f, 0.5f, 0.6f }, "", Panel.LayersChanged));
            Add(Ui.Toggle(page, "Contours", "Contour lines", 0f, 0f, w, Settings.Contours, Panel.LayersChanged));
            Add(Ui.Cycle(page, "ContourMeters", "Contour interval", 0f, 0f, w, Settings.ContourMeters, new[] { 10f, 20f, 40f, 50f }, " m", Panel.LayersChanged));
            Add(Ui.Toggle(page, "ContourAuto", "Finer contours zoomed in", 0f, 0f, w, Settings.ContourAuto, Panel.LayersChanged));
            Add(Ui.Toggle(page, "Detail", "Sharp lines zoomed in", 0f, 0f, w, Settings.Detail, Panel.LayersChanged));
            Add(Ui.Toggle(page, "Mist", "Mistlands mist", 0f, 0f, w, Settings.Mist, Panel.LayersChanged));
            Add(Ui.Toggle(page, "Steepness", "Steep ground", 0f, 0f, w, Settings.Steepness, Panel.LayersChanged));
            Section(page, "Sailing");
            Add(Ui.Toggle(page, "Shallows", "Boat clearance", 0f, 0f, w, Settings.Shallows, Panel.LayersChanged));
            Add(Ui.Cycle(page, "DangerDepth", "Red below depth", 0f, 0f, w, Settings.BoatDangerDepth, new[] { 1f, 1.5f, 2f, 3f }, " m", Panel.LayersChanged));
            Add(Ui.Cycle(page, "WarningDepth", "Yellow below depth", 0f, 0f, w, Settings.BoatWarningDepth, new[] { 2f, 3f, 4f, 5f }, " m", Panel.LayersChanged));
            Add(Ui.Toggle(page, "SeaHazards", "Known sea rocks and ice", 0f, 0f, w, Settings.SeaHazards, ObjectsChanged));
            Add(Ui.Toggle(page, "AshlandsHeat", "Boiling sea and lava", 0f, 0f, w, Settings.AshlandsHeat, Panel.LayersChanged));
            Section(page, "Known world objects");
            Add(Ui.Toggle(page, "Ore", "Known ore deposits", 0f, 0f, w, Settings.Ore, ObjectsChanged));
            // the ore types open like a dropdown under their own row
            _oreRow = Ui.Row(page, "OreTypes", "  Ore types", 0f, 0f, w, () => Settings.OreTypes.Values.Count(e => e.Value) + " of " + Settings.OreTypes.Count + (_oreOpen ? "  v" : "  >"), () => false,
                () => { _oreOpen = !_oreOpen; Layout(); });
            Add(_oreRow);
            foreach (var ore in Settings.OreTypes) Add(Ui.Toggle(page, "Ore" + ore.Key, "      " + ore.Key, 0f, 0f, w, ore.Value, () => { _oreRow.Refresh(); Panel.LayersChanged(); }), true);
            Add(Ui.Toggle(page, "SpawnSafe", "Spawn-protected areas", 0f, 0f, w, Settings.SpawnSafe, ObjectsChanged));
            Add(Ui.Toggle(page, "Beds", "Beds and owners", 0f, 0f, w, Settings.Beds, ObjectsChanged));
            Add(Ui.Toggle(page, "Vehicles", "Ships and carts", 0f, 0f, w, Settings.Vehicles, ObjectsChanged));
            Add(Ui.Toggle(page, "Tombstones", "Player tombstones", 0f, 0f, w, Settings.Tombstones, ObjectsChanged));
            Add(Ui.Toggle(page, "Tamed", "Tamed animals", 0f, 0f, w, Settings.Tamed, ObjectsChanged));
            Add(Ui.Toggle(page, "Chests", "Player-built containers", 0f, 0f, w, Settings.Chests, ObjectsChanged));
            Add(Ui.Toggle(page, "Contents", "Search container contents", 0f, 0f, w, Settings.Contents, ObjectsChanged));
            Section(page, "Map markers");
            Add(Ui.Toggle(page, "Labels", "Location names", 0f, 0f, w, Settings.Labels, Panel.LayersChanged));
            Add(Ui.Toggle(page, "TypeIcons", "Location symbols", 0f, 0f, w, Settings.TypeIcons, Panel.LayersChanged));
            Add(Ui.Cycle(page, "DotSize", "Location marker size", 0f, 0f, w, Settings.DotSize, new[] { 4f, 5f, 6f, 8f, 10f, 12f }, " px", Panel.LayersChanged));
            Add(Ui.Cycle(page, "KnownSize", "Object marker size", 0f, 0f, w, Settings.KnownSize, new[] { 4f, 5f, 7f, 9f, 11f, 14f }, " px", Panel.LayersChanged));
            Add(Ui.Row(page, "SpawnFilter", "Location filter", 0f, 0f, w, () => new[] { "All", "Generated", "Not yet" }[Settings.SpawnFilter.Value], () => Settings.SpawnFilter.Value != 0,
                () => { Settings.SpawnFilter.Value = (Settings.SpawnFilter.Value + 1) % 3; Panel.LayersChanged(); }));
            Section(page, "Panel");
            Add(Ui.Cycle(page, "Opacity", "Panel opacity", 0f, 0f, w, Settings.Opacity, new[] { 0.65f, 0.72f, 0.78f, 0.85f, 0.9f }, ""));
            Add(Ui.Cycle(page, "ScrollSpeed", "Wheel speed", 0f, 0f, w, Settings.ScrollSpeed, new[] { 60f, 120f, 180f, 240f, 360f }, ""));
            Add(Ui.Row(page, "PanelScale", "Panel size", 0f, 0f, w, () => Settings.PanelScale.Value <= 0f ? "Fit map" : "x " + Settings.PanelScale.Value.ToString("0.##"), () => false,
                () => { float[] steps = { 0f, 1f, 1.25f, 1.5f, 2f }; int i = Array.FindIndex(steps, s => Mathf.Abs(s - Settings.PanelScale.Value) < 0.01f); Settings.PanelScale.Value = steps[(i + 1) % steps.Length]; }));
            Add(Ui.Toggle(page, "GameLook", "Game window look", 0f, 0f, w, Settings.GameLook, null));
            Add(Ui.Toggle(page, "ClickSounds", "Click sounds", 0f, 0f, w, Settings.ClickSounds, null));
            _status = Ui.Label(page, "Status", 0f, 0f, Panel.PageWidth - 12f, 290f, 15f, TextAlignmentOptions.TopLeft, Ui.Dim);
            _status.textWrappingMode = TextWrappingModes.Normal;
            Layout();

            var marks = _sub.transform;
            Ui.Label(marks, "Heading", 0f, 0f, Panel.PageWidth, 28f, 16f, TextAlignmentOptions.MidlineLeft, Ui.Accent).text = "Auto mark these categories";
            for (int i = 0; i < Categories.Order.Length; i++)
            {
                string category = Categories.Order[i];
                var row = Ui.Toggle(marks, "Mark" + category, Categories.Titles[category], 0f, 32f + i * RowStep, Panel.PageWidth, Settings.AutoMarkCategory[category], null);
                row.Name.color = Ui.CategoryColor(category);
                _marks.Add(row);
            }
            Ui.Row(marks, "Back", "Back", 0f, 32f + Categories.Order.Length * RowStep + 8f, Panel.PageWidth, () => "<", () => false, () => Open(false));
            _sub.SetActive(false);
        }

        private static void ObjectsChanged() { KnownWorld.Request(true); Panel.LayersChanged(); }

        // a section is a header button that opens and closes the rows under it. sections stay as they were for the session
        private sealed class SectionData
        {
            internal string Title;
            internal Button Header;
            internal TMP_Text HeaderText;
            internal readonly List<KeyValuePair<Ui.Switch, bool>> Rows = new List<KeyValuePair<Ui.Switch, bool>>();
        }

        private const float HeaderHeight = 34f;
        private const float Gap = 4f;
        private static readonly HashSet<string> OpenSections = new HashSet<string>();
        private readonly List<SectionData> _sections = new List<SectionData>();
        private Ui.Switch _oreRow;
        private bool _oreOpen;

        private void Section(Transform page, string title)
        {
            var section = new SectionData { Title = title };
            section.Header = Ui.Tag(page, "Section" + title.Replace(" ", ""), "", 0f, 0f, _options.Width, HeaderHeight, 18f, TextAlignmentOptions.MidlineLeft, () => ToggleSection(section), out section.HeaderText);
            section.HeaderText.fontStyle = FontStyles.Bold;
            section.HeaderText.color = Ui.Accent;
            _sections.Add(section);
        }

        // a row of the last section, in the ore group when grouped
        private void Add(Ui.Switch row, bool ore = false)
        {
            _rows.Add(row);
            _sections[_sections.Count - 1].Rows.Add(new KeyValuePair<Ui.Switch, bool>(row, ore));
        }

        private void ToggleSection(SectionData section)
        {
            if (!OpenSections.Remove(section.Title)) OpenSections.Add(section.Title);
            Layout();
        }

        // puts the headers and the rows of the open sections in order, closed ones show only their header
        private void Layout()
        {
            float y = 0f;
            foreach (var section in _sections)
            {
                bool open = OpenSections.Contains(section.Title);
                ((RectTransform)section.Header.transform).anchoredPosition = new Vector2(0f, -y);
                section.HeaderText.text = (open ? "v  " : ">  ") + section.Title;
                Ui.Tint(section.Header, open ? Ui.Chosen : Ui.Enabled);
                y += HeaderHeight + Gap;
                foreach (var pair in section.Rows)
                {
                    bool show = open && (!pair.Value || _oreOpen);
                    pair.Key.Button.gameObject.SetActive(show);
                    if (!show) continue;
                    ((RectTransform)pair.Key.Button.transform).anchoredPosition = new Vector2(0f, -y);
                    y += Ui.RowHeight + 2f;
                }
                if (open) y += Gap * 2f;
            }
            y += 8f;
            ((RectTransform)_status.transform).anchoredPosition = new Vector2(0f, -y);
            _statusTop = y;
            FitStatus();
        }

        // the help text below the sections gets room for all its lines plus a margin, so it never touches the panel edge
        private float _statusTop;
        private void FitStatus()
        {
            float height = Mathf.Max(290f, _status.preferredHeight + 12f);
            ((RectTransform)_status.transform).sizeDelta = new Vector2(((RectTransform)_status.transform).sizeDelta.x, height);
            _options.SetHeight(_statusTop + height + 24f);
        }

        internal void FocusTerrain()
        {
            Open(false);
            OpenSections.Add("Terrain");
            Layout();
            float top = 0f;
            foreach (var section in _sections) { if (section.Title == "Terrain") break; top += HeaderHeight + Gap + (OpenSections.Contains(section.Title) ? section.Rows.Count(r => !r.Value || _oreOpen) * (Ui.RowHeight + 2f) + Gap * 2f : 0f); }
            float available = _options.Content.sizeDelta.y - _options.Height;
            _options.Rect.verticalNormalizedPosition = available > 0f ? 1f - Mathf.Clamp01(top / available) : 1f;
            _options.Refresh();
        }

        private void Open(bool categories)
        {
            _main.SetActive(!categories);
            _sub.SetActive(categories);
            Show();
        }

        internal override void Show()
        {
            foreach (var row in _rows) row.Refresh();
            foreach (var row in _marks) row.Refresh();
            if (Session.Ready)
                _status.text = "The index holds " + Session.Entries.Count + " locations in " + Session.Kinds.Count + " kinds. It was built in " + Session.BuildMilliseconds.ToString("F0")
                    + " ms, at most " + Session.WorstSliceMilliseconds.ToString("F1") + " ms in one frame.";
            else
                _status.text = Session.Status;
            if (Settings.UnexploredShown) _status.text += "\nUnexplored terrain is shown on the large map and in the overview.";
            _status.text += "\nMist uses the natural mask and loaded clearing sources. Boat clearance uses base depth. Known objects cover generated areas only. Spawn protection is green where loaded, pale where remembered, sand where the radius has not been seen loaded yet.";
            if (KnownWorld.Problem != null) _status.text += "\nKnown-object scan failed. See the log.";
            if (ZoneSystem.instance != null)
            {
                var bosses = new List<string>();
                foreach (string key in ZoneSystem.instance.GetGlobalKeys()) if (key.StartsWith("defeated_", System.StringComparison.Ordinal)) bosses.Add(Categories.Split(key.Substring(9)));
                _status.text += "\nBoss defeats recorded " + (bosses.Count == 0 ? "none" : string.Join(", ", bosses)) + ". Locations are in Layers and Find.";
            }
            FitStatus();
        }
    }
}
