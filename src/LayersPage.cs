using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // the layers tab. a list of the categories, each opens to its kinds (Greydwarf nest camp, Charred spawner and so on),
    // and a switch per kind draws all its places on the large map. the list scrolls
    internal sealed class LayersPage : Page
    {
        private const float CategoryHeight = 28f;
        private const float CategoryStep = 30f;
        private const float KindHeight = 26f;
        private const float KindStep = 28f;
        private const float KindIndent = 24f;
        private const float ExpandWidth = 28f;
        private const float TopRowHeight = 28f;
        private const float ScrollTop = 34f;
        private const float ScrollHeight = 340f;
        private const float RowHeight = 32f;

        private sealed class KindRow
        {
            internal KindData Kind;
            internal Button Button;
            internal TMP_Text Name;
            internal TMP_Text State;
        }

        private sealed class CategoryRow
        {
            internal string Category;
            internal List<KindData> Kinds;
            internal Button Expand;
            internal TMP_Text ExpandText;
            internal Button Toggle;
            internal TMP_Text Name;
            internal TMP_Text State;
            internal readonly List<KindRow> Rows = new List<KindRow>();
            internal bool Open;
        }

        private readonly List<CategoryRow> _categories = new List<CategoryRow>();
        private Ui.Scroller _scroll;
        private TMP_Text _status;
        private TMP_Text _expandAll;
        private Ui.Switch _limit;
        private Ui.Switch _lines;
        private int _version = -1;
        private bool _allOpen;

        internal override string Title => "Layers";

        internal override void Build(Transform parent)
        {
            TMP_Text unused;
            Ui.Tag(parent, "HideAll", "Hide all", 0f, 0f, 154f, TopRowHeight, 15f, TextAlignmentOptions.Midline, HideAll, out unused);
            Ui.Tag(parent, "ExpandAll", "Open all", 162f, 0f, 154f, TopRowHeight, 15f, TextAlignmentOptions.Midline, ToggleOpenAll, out _expandAll);
            _scroll = Ui.Scroll(parent, "Kinds", 0f, ScrollTop, Panel.PageWidth, ScrollHeight);
            float below = ScrollTop + ScrollHeight + 8f;
            _limit = Ui.Cycle(parent, "MaxPins", "Pins per category", 0f, below, Panel.PageWidth, Settings.MaxPins, new[] { 0, 5, 15, 25, 50, 100, 250 }, Changed, "No limit");
            _lines = Ui.Toggle(parent, "PortalLines", "Portal lines", 0f, below + RowHeight + 2f, Panel.PageWidth, Settings.PortalLines, Changed);
            _status = Ui.Label(parent, "Status", 0f, below + 2f * (RowHeight + 2f) + 2f, Panel.PageWidth, 86f, 14f, TextAlignmentOptions.TopLeft, Ui.Dim);
            _status.textWrappingMode = TextWrappingModes.Normal;
            Layers.Drawn += UpdateStatus;
        }

        private void Changed()
        {
            Panel.LayersChanged();
            RefreshRows();
            UpdateStatus();
        }

        // the rows are made again when a new index is ready
        private void Rebuild()
        {
            foreach (Transform child in _scroll.Content) UnityEngine.Object.Destroy(child.gameObject);
            _categories.Clear();
            if (!Session.Ready) return;
            foreach (var category in Categories.Order)
            {
                var kinds = Session.Kinds.Values.Where(k => k.Category == category).OrderBy(k => k.Kind, StringComparer.Ordinal).ToList();
                if (kinds.Count == 0) continue;
                var row = new CategoryRow { Category = category, Kinds = kinds, Open = _allOpen };
                var content = _scroll.Content;
                TMP_Text expandText;
                row.Expand = Ui.Tag(content, "Expand" + category, row.Open ? "v" : ">", 0f, 0f, ExpandWidth, CategoryHeight, 15f, TextAlignmentOptions.Midline, () => Toggle(row), out expandText);
                row.ExpandText = expandText;
                TMP_Text name;
                row.Toggle = Ui.Tag(content, "Category" + category, "", ExpandWidth + 2f, 0f, _scroll.Width - ExpandWidth - 2f, CategoryHeight, 15f, TextAlignmentOptions.MidlineLeft, () => SwitchCategory(row), out name);
                row.Name = name;
                name.color = Ui.CategoryColor(category);
                row.State = Ui.Label(row.Toggle.transform, "State", _scroll.Width - ExpandWidth - 2f - 96f, 0f, 88f, CategoryHeight, 14f, TextAlignmentOptions.MidlineRight, Ui.Ink);
                foreach (var kind in kinds)
                {
                    var kindRow = new KindRow { Kind = kind };
                    TMP_Text kindName;
                    var captured = kindRow;
                    kindRow.Button = Ui.Tag(content, "Kind" + kind.Key, "", KindIndent, 0f, _scroll.Width - KindIndent, KindHeight, 14f, TextAlignmentOptions.MidlineLeft, () => SwitchKind(captured), out kindName);
                    kindRow.Name = kindName;
                    kindRow.State = Ui.Label(kindRow.Button.transform, "State", _scroll.Width - KindIndent - 80f, 0f, 72f, KindHeight, 13f, TextAlignmentOptions.MidlineRight, Ui.Ink);
                    row.Rows.Add(kindRow);
                }
                _categories.Add(row);
            }
            Layout();
            RefreshRows();
        }

        // puts the rows in their places, the kinds of a closed category are hidden
        private void Layout()
        {
            float y = 0f;
            foreach (var row in _categories)
            {
                Position(row.Expand, 0f, y);
                Position(row.Toggle, ExpandWidth + 2f, y);
                y += CategoryStep;
                foreach (var kind in row.Rows)
                {
                    kind.Button.gameObject.SetActive(row.Open);
                    if (!row.Open) continue;
                    Position(kind.Button, KindIndent, y);
                    y += KindStep;
                }
                row.ExpandText.text = row.Open ? "v" : ">";
            }
            _scroll.SetHeight(y);
        }

        private static void Position(Button button, float x, float y)
        {
            ((RectTransform)button.transform).anchoredPosition = new Vector2(x, -y);
        }

        private static string Count(KindData kind)
        {
            return kind.Items.Count.ToString();
        }

        private void RefreshRows()
        {
            foreach (var row in _categories)
            {
                int on = row.Kinds.Count(Settings.KindOn);
                int places = row.Kinds.Sum(k => k.Items.Count);
                row.Name.text = Categories.Titles[row.Category] + "   " + places;
                row.State.text = on == 0 ? "Off" : on == row.Kinds.Count ? "On" : on + " of " + row.Kinds.Count;
                Ui.Tint(row.Toggle, on > 0 ? Ui.Enabled : Ui.Face);
                foreach (var kind in row.Rows)
                {
                    bool kindOn = Settings.KindOn(kind.Kind);
                    kind.Name.text = kind.Kind.Kind + "   " + Count(kind.Kind);
                    kind.State.text = kindOn ? "On" : "Off";
                    Ui.Tint(kind.Button, kindOn ? Ui.Enabled : Ui.Face);
                }
            }
            _limit.Refresh();
            _lines.Refresh();
            _expandAll.text = _allOpen ? "Close all" : "Open all";
        }

        private void Toggle(CategoryRow row)
        {
            row.Open = !row.Open;
            Layout();
        }

        private void ToggleOpenAll()
        {
            _allOpen = !_allOpen;
            foreach (var row in _categories) row.Open = _allOpen;
            Layout();
            RefreshRows();
        }

        private void SwitchCategory(CategoryRow row)
        {
            bool any = row.Kinds.Any(Settings.KindOn);
            Settings.SetKinds(row.Kinds, !any);
            Changed();
        }

        private void SwitchKind(KindRow row)
        {
            Settings.SetKinds(new[] { row.Kind }, !Settings.KindOn(row.Kind));
            Changed();
        }

        private void HideAll()
        {
            if (!Session.Ready) return;
            Settings.SetKinds(Session.Kinds.Values, false);
            Changed();
        }

        private void UpdateStatus()
        {
            if (_status == null || Root == null || !Root.activeInHierarchy) return;
            if (!Session.Ready) _status.text = Session.Building ? "Building the location list, it takes a few seconds." : Session.Problem ?? "Load a world first.";
            else if (Layers.Wanted == 0) _status.text = "Switch a kind on to draw its places on the large map. Hover a dot to see what it is.";
            else
            {
                _status.text = Layers.Wanted + " places on, " + Layers.InView + " in view as " + Layers.SinglesShown + " pins and " + Layers.GroupsShown + " groups."
                    + (Layers.SinglesHidden > 0 ? " " + Layers.SinglesHidden + " pins hidden by the limit." : "");
            }
            if (Layers.KnownHidden > 0) _status.text += " " + Layers.KnownHidden + " object markers hidden by the limit.";
            if (Settings.PortalLines.Value && !Settings.PortalsOn.Value) _status.text += " Portal lines need Search my portals in Settings.";
            if (Settings.Ore.Value) _status.text += "\n" + Legend();
        }

        // the ore colors, written in their own colors
        private static string Legend()
        {
            var parts = new List<string>();
            foreach (var ore in Settings.OreTypes.Keys)
            {
                var c = KnownWorld.OreColor(ore);
                parts.Add("<color=#" + c.r.ToString("X2") + c.g.ToString("X2") + c.b.ToString("X2") + ">" + ore + "</color>");
            }
            return "Ore " + string.Join("  ", parts);
        }

        internal override void Show()
        {
            if (Session.Ready && _version != Session.Version)
            {
                _version = Session.Version;
                Rebuild();
            }
            RefreshRows();
            UpdateStatus();
        }
    }
}
