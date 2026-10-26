using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimNavigator
{
    // a row of the find list, a kind of place or one of the player's portals
    internal sealed class Hit
    {
        internal KindData Kind;
        internal PortalInfo Portal;
        internal Minimap.PinData Pin;
        internal KnownMark Known;

        internal bool Is(Hit other)
        {
            if (other == null) return false;
            if (Kind != null) return ReferenceEquals(Kind, other.Kind);
            if (Pin != null) return ReferenceEquals(Pin, other.Pin);
            if (Known != null) return ReferenceEquals(Known, other.Known);
            return other.Portal != null && Portal.Tag == other.Portal.Tag && Portal.X == other.Portal.X && Portal.Z == other.Portal.Z;
        }
    }

    // the find tab. a search box, the categories, the kinds of the category or the search results, the nearest result that is not done
    // and the buttons that mark it done and move on to the next nearest
    internal sealed class FindPage : Page
    {
        private const int PageSize = 8;
        private const int PortalLimit = 200;
        private const string PortalCategory = "portals";
        private const string PinCategory = "pins";
        private const string KnownCategory = "known";
        private const string PinNamePrefix = "Nearest ";

        private TMP_InputField _box;
        private TMP_Text _categoryText;
        private TMP_Text _pageText;
        private TMP_Text _card;
        private GameObject _previous;
        private GameObject _next;
        private readonly List<Button> _rowButtons = new List<Button>();
        private readonly List<TMP_Text> _rowTexts = new List<TMP_Text>();
        private List<string> _categories = new List<string>();
        private readonly List<Hit> _hits = new List<Hit>();
        private string _query = "";
        private int _categoryIndex;
        private int _page;
        private int _version = -1;
        private Hit _selected;
        private Entry _picked;
        private Minimap.PinData _pin;
        private bool _pinWarned;
        // true when the picked place came from a nearest search, false for an exact place
        private bool _nearest;

        internal override string Title => "Find";

        // true while the search box has the keyboard
        internal bool Typing => _box != null && _box.isFocused;

        private bool Searching => _query.Trim().Length > 0;

        internal override void Build(Transform parent)
        {
            TMP_Text unused;
            _box = Ui.Input(parent, "Search", "search a place, pin or portal", 0f, 0f, Panel.PageWidth, 30f, OnChanged, OnSubmit);

            _previous = Ui.Tag(parent, "PreviousCategory", "<", 0f, 36f, 34f, 30f, 18f, TextAlignmentOptions.Midline, () => Step(-1), out unused).gameObject;
            _categoryText = Ui.Label(parent, "Category", 38f, 36f, 240f, 30f, 18f, TextAlignmentOptions.Midline, Ui.Accent);
            _next = Ui.Tag(parent, "NextCategory", ">", 282f, 36f, 34f, 30f, 18f, TextAlignmentOptions.Midline, () => Step(1), out unused).gameObject;

            for (int i = 0; i < PageSize; i++)
            {
                int row = i;
                TMP_Text text;
                _rowButtons.Add(Ui.Tag(parent, "Kind" + i, "", 0f, 72f + i * 32f, Panel.PageWidth, 30f, 16f, TextAlignmentOptions.MidlineLeft, () => Choose(row), out text));
                _rowTexts.Add(text);
            }

            Ui.Tag(parent, "PreviousPage", "<", 0f, 332f, 34f, 28f, 16f, TextAlignmentOptions.Midline, () => Turn(-1), out unused);
            _pageText = Ui.Label(parent, "Page", 38f, 332f, 240f, 28f, 15f, TextAlignmentOptions.Midline, Ui.Dim);
            Ui.Tag(parent, "NextPage", ">", 282f, 332f, 34f, 28f, 16f, TextAlignmentOptions.Midline, () => Turn(1), out unused);

            _card = Ui.Label(parent, "Card", 0f, 366f, Panel.PageWidth, 100f, 16f, TextAlignmentOptions.TopLeft, Ui.Ink);
            _card.textWrappingMode = TextWrappingModes.Normal;
            Ui.Tag(parent, "Route", "Route", 0f, 472f, 100f, 28f, 15f, TextAlignmentOptions.Midline, Route, out unused);
            Ui.Tag(parent, "Copy", "Copy X,Z", 106f, 472f, 100f, 28f, 15f, TextAlignmentOptions.Midline, Copy, out unused);
            Ui.Tag(parent, "OtherEnd", "Other end", 212f, 472f, 104f, 28f, 15f, TextAlignmentOptions.Midline, OtherEnd, out unused);

            Ui.Tag(parent, "Show", "Show", 0f, 506f, 56f, 30f, 14f, TextAlignmentOptions.Midline, ShowAgain, out unused);
            Ui.Tag(parent, "Done", "Done, next", 59f, 506f, 84f, 30f, 14f, TextAlignmentOptions.Midline, MarkDone, out unused);
            Ui.Tag(parent, "Undo", "Undo", 146f, 506f, 56f, 30f, 14f, TextAlignmentOptions.Midline, UndoDone, out unused);
            Ui.Tag(parent, "Reset", "Reset", 205f, 506f, 54f, 30f, 14f, TextAlignmentOptions.Midline, ResetKind, out unused);
            Ui.Tag(parent, "Clear", "Clear", 262f, 506f, 54f, 30f, 14f, TextAlignmentOptions.Midline, ClearAll, out unused);
        }

        // the place the card, the pin and the route refer to, null when nothing is picked
        internal Vector3? Target
        {
            get
            {
                if (_selected == null) return null;
                if (_picked != null) return new Vector3(_picked.X, _picked.Y, _picked.Z);
                if (_selected.Known != null) return new Vector3(_selected.Known.X, 0f, _selected.Known.Z);
                if (_selected.Pin != null) return _selected.Pin.m_pos;
                if (_selected.Portal != null) return new Vector3(_selected.Portal.X, _selected.Portal.Y, _selected.Portal.Z);
                return null;
            }
        }

        internal void PickEntry(Entry entry)
        {
            _selected = new Hit { Kind = entry.Group };
            _picked = entry;
            _nearest = false;
            Session.Tracked.Add(entry.Group.Key);
            Picked(true);
            FollowSelection();
        }

        internal void PickKnown(KnownMark mark)
        {
            _selected = new Hit { Known = mark };
            _picked = null;
            _nearest = false;
            Refind(true);
            FollowSelection();
        }

        // the route target follows the picked place. a route that was shown or running is calculated again for it
        private void FollowSelection()
        {
            var at = Target;
            if (!at.HasValue) return;
            string name = _picked != null ? _picked.Kind : _selected.Known != null ? _selected.Known.Name : _selected.Pin != null ? _selected.Pin.m_name : "Portal " + _selected.Portal.Tag;
            bool routed = Navigator.Visible != null || Navigator.Search != null;
            Navigator.SetTarget(at.Value, name);
            if (routed) Navigator.Start();
        }

        private void Route()
        {
            if (_selected == null) return;
            if (_selected.Known != null) Navigator.SetTarget(new Vector3(_selected.Known.X, 0f, _selected.Known.Z), _selected.Known.Name);
            else if (_selected.Pin != null) Navigator.SetTarget(_selected.Pin.m_pos, _selected.Pin.m_name);
            else if (_selected.Portal != null)
            {
                var portal = _selected.Portal;
                Navigator.SetTarget(new Vector3(portal.X, portal.Y, portal.Z), "Portal " + portal.Tag);
            }
            else if (Session.Last != null)
            {
                var entry = Session.Last.Entry;
                Navigator.SetTarget(new Vector3(entry.X, entry.Y, entry.Z), entry.Kind);
            }
            else return;
            Panel.OpenRoute();
            Navigator.Start();
        }

        private void Copy()
        {
            Vector3? at = _selected?.Known != null ? new Vector3(_selected.Known.X, 0f, _selected.Known.Z)
                : _selected?.Pin != null ? _selected.Pin.m_pos : _selected?.Portal != null ? new Vector3(_selected.Portal.X, 0f, _selected.Portal.Z)
                : Session.Last != null ? new Vector3(Session.Last.Entry.X, 0f, Session.Last.Entry.Z) : (Vector3?)null;
            if (at.HasValue) { GUIUtility.systemCopyBuffer = at.Value.x.ToString("F0") + ", " + at.Value.z.ToString("F0"); if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("map coordinates copied"); }
        }

        private void OtherEnd()
        {
            var portal = _selected?.Portal;
            if (portal == null || string.IsNullOrEmpty(portal.Tag)) return;
            var other = Portals.List.FirstOrDefault(p => p.Tag == portal.Tag && (p.X != portal.X || p.Z != portal.Z));
            if (other != null) { _selected = new Hit { Portal = other }; Refind(true); FollowSelection(); }
        }

        // the route's length and time as a small last line of the card, kept current while the tab is open
        private string _routeLine;

        internal override void Frame()
        {
            if (_card == null) return;
            string line = _selected != null ? Navigator.ShortSummary() : null;
            string suffix = line == null ? null : "\n<size=13>" + line + "</size>";
            string text = _card.text ?? "";
            bool has = _routeLine != null && text.EndsWith(_routeLine, StringComparison.Ordinal);
            if (has && suffix == _routeLine) return;
            if (!has && suffix == null) { _routeLine = null; return; }
            if (has) text = text.Substring(0, text.Length - _routeLine.Length);
            _routeLine = suffix;
            _card.text = text + (suffix ?? "");
        }

        internal override void Show()
        {
            if (!Session.Active && !Session.Ready)
            {
                _hits.Clear();
                _categories = new List<string>();
                _card.text = Session.Building ? "Building the location list, it takes a few seconds." : Session.Problem ?? "Load a world first.";
                Rows();
                return;
            }
            if (_version != Session.Version)
            {
                _version = Session.Version;
                if (_selected != null && _selected.Kind != null)
                {
                    _selected = Session.Kinds.TryGetValue(_selected.Kind.Key, out var kind) ? new Hit { Kind = kind } : null;
                    var old = _picked;
                    _picked = old != null && _selected != null ? _selected.Kind.Items.FirstOrDefault(e => e.Prefab == old.Prefab && e.X == old.X && e.Z == old.Z) : null;
                }
            }
            if (Settings.PortalsOn.Value) Portals.Refresh(Settings.PortalSeconds.Value);
            List();
            Rows();
            if (_selected != null && Session.PlayerPosition() != null) Refind(false);
            else if (_selected == null) _card.text = Session.Ready ? "Pick a kind of place." : Session.Status;
        }

        private static string TitleOf(string category)
        {
            return category == PortalCategory ? "Portals" : category == PinCategory ? "My map pins" : category == KnownCategory ? "Known objects" : Categories.Titles[category];
        }

        // the rows for the search text, or for the category picked with the arrows
        private void List()
        {
            _hits.Clear();
            var kinds = Session.Kinds.Values;
            var at = Session.PlayerPosition();
            float x = at.HasValue ? at.Value.x : 0f, z = at.HasValue ? at.Value.z : 0f;
            if (Searching)
            {
                foreach (var kind in Search.Match(kinds, _query)) _hits.Add(new Hit { Kind = kind });
                if (Settings.PortalsOn.Value)
                    foreach (var portal in Portals.Match(_query, x, z, PortalLimit)) _hits.Add(new Hit { Portal = portal });
                if (Settings.SearchPins.Value) AddPins(_query, x, z);
                AddKnown(_query, x, z);
                return;
            }
            _categories = Categories.Order.Where(c => kinds.Any(k => k.Category == c)).ToList();
            if (Settings.PortalsOn.Value) _categories.Add(PortalCategory);
            if (Settings.SearchPins.Value) _categories.Add(PinCategory);
            if (KnownWorld.Enabled) _categories.Add(KnownCategory);
            if (_categoryIndex >= _categories.Count) _categoryIndex = 0;
            if (_categories.Count == 0) return;
            string category = _categories[_categoryIndex];
            if (category == PinCategory) { AddPins("", x, z); return; }
            if (category == KnownCategory) { AddKnown("", x, z); return; }
            if (category == PortalCategory)
            {
                foreach (var portal in Portals.Match("", x, z, PortalLimit)) _hits.Add(new Hit { Portal = portal });
                return;
            }
            foreach (var kind in kinds.Where(k => k.Category == category).OrderBy(k => k.Kind, StringComparer.Ordinal))
                _hits.Add(new Hit { Kind = kind });
        }

        private void AddPins(string query, float x, float z)
        {
            var map = Minimap.instance;
            if (map == null) return;
            var pins = MapAccess.Pins(map);
            if (pins == null) return;
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pin in pins.Where(p => p.m_save && words.All(w => w == "pin" || w == "pins" || (p.m_name ?? "").ToLowerInvariant().Contains(w)))
                .OrderBy(p => (p.m_pos.x - x) * (p.m_pos.x - x) + (p.m_pos.z - z) * (p.m_pos.z - z)).Take(PortalLimit))
                _hits.Add(new Hit { Pin = pin });
        }

        private void AddKnown(string query, float x, float z)
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var mark in KnownWorld.Marks.Where(m => m.Kind != 1 && KnownWorld.Visible(m) && words.All(w => ((m.Name ?? "") + " " + (m.Contents ?? "")).ToLowerInvariant().Contains(w)))
                .OrderBy(m => (m.X - x) * (m.X - x) + (m.Z - z) * (m.Z - z)).Take(PortalLimit)) _hits.Add(new Hit { Known = mark });
        }

        private void Rows()
        {
            int pages = Math.Max(1, (_hits.Count + PageSize - 1) / PageSize);
            if (_page >= pages) _page = pages - 1;
            _previous.SetActive(!Searching);
            _next.SetActive(!Searching);
            _categoryText.text = Searching ? _hits.Count + (_hits.Count == 1 ? " result" : " results") : _categories.Count == 0 ? "" : TitleOf(_categories[_categoryIndex]);
            _pageText.text = "page " + (_page + 1) + " of " + pages;
            var at = Session.PlayerPosition();
            for (int i = 0; i < PageSize; i++)
            {
                int index = _page * PageSize + i;
                bool used = index < _hits.Count;
                _rowButtons[i].gameObject.SetActive(used);
                if (!used) continue;
                var hit = _hits[index];
                if (hit.Kind != null)
                {
                    _rowTexts[i].text = hit.Kind.Kind + "   " + hit.Kind.Done + "/" + hit.Kind.Items.Count;
                    _rowTexts[i].color = hit.Kind.Done >= hit.Kind.Items.Count ? Ui.Dim : Ui.Ink;
                }
                else if (hit.Known != null)
                {
                    _rowTexts[i].text = hit.Known.Name;
                    _rowTexts[i].color = KnownWorld.Color(hit.Known);
                }
                else if (hit.Portal != null)
                {
                    string name = string.IsNullOrEmpty(hit.Portal.Tag) ? "(no name)" : hit.Portal.Tag;
                    string distance = at.HasValue ? "   " + Nearest.Distance(hit.Portal.X - at.Value.x, hit.Portal.Z - at.Value.z).ToString("F0") + " m" : "";
                    _rowTexts[i].text = "Portal  " + name + distance;
                    _rowTexts[i].color = Ui.Ink;
                }
                else
                {
                    _rowTexts[i].text = "Pin  " + (string.IsNullOrEmpty(hit.Pin.m_name) ? "(no name)" : hit.Pin.m_name);
                    _rowTexts[i].color = Ui.Ink;
                }
                Ui.Tint(_rowButtons[i], hit.Is(_selected) ? Ui.Chosen : Ui.Face);
            }
        }

        private void OnChanged(string text)
        {
            _query = text ?? "";
            _page = 0;
            if (!Session.Active && !Session.Ready) return;
            List();
            Rows();
        }

        // enter in the search box picks the first result
        private void OnSubmit(string text)
        {
            if (_hits.Count > 0) Pick(0);
        }

        private void Step(int by)
        {
            if (_categories.Count == 0) return;
            _categoryIndex = (_categoryIndex + by + _categories.Count) % _categories.Count;
            _page = 0;
            List();
            Rows();
        }

        private void Turn(int by)
        {
            int pages = Math.Max(1, (_hits.Count + PageSize - 1) / PageSize);
            _page = (_page + by + pages) % pages;
            Rows();
        }

        private void Choose(int row)
        {
            Pick(_page * PageSize + row);
        }

        private void Pick(int index)
        {
            if (index < 0 || index >= _hits.Count) return;
            _selected = _hits[index];
            _picked = null;
            Refind(true);
            FollowSelection();
        }

        private void ShowAgain()
        {
            if (_selected != null) Refind(true);
        }

        // marks the pinned place done and moves the card, the pin and the route on to the next nearest
        private void MarkDone()
        {
            if (_selected == null || _selected.Kind == null) return;
            if (_picked != null) Picked(false);
            if (Session.Last == null) return;
            var found = Session.MarkDoneAndNext();
            _picked = found?.Entry;
            _nearest = true;
            Present(found, true);
            FollowSelection();
            Panel.LayersChanged();
        }

        // brings the place just marked done back as the picked place
        private void UndoDone()
        {
            var undone = Session.LastDone;
            if (!Session.Undo() || _selected == null) return;
            if (undone != null && _selected.Kind != null && ReferenceEquals(undone.Group, _selected.Kind)) _picked = undone;
            Refind(true);
            FollowSelection();
            Panel.LayersChanged();
        }

        // forgets the done marks of the picked kind. the pin goes and nothing is pinned again until a kind is picked or shown
        private void ResetKind()
        {
            if (_selected == null || _selected.Kind == null) return;
            Session.Reset(_selected.Kind);
            RemovePin();
            _picked = null;
            Refind(false);
            Navigator.Clear();
            Panel.LayersChanged();
        }

        // nothing picked, no pin, no result
        private void ClearAll()
        {
            Navigator.Clear();
            RemovePin();
            _selected = null;
            _picked = null;
            Session.Last = null;
            _card.text = "Pick a kind of place.";
            Rows();
        }

        internal void WorldChanged()
        {
            RemovePin(); _selected = null; _picked = null; _routeLine = null;
            _hits.Clear(); _categories.Clear(); _version = -1;
        }

        // the route arrived at the picked place: its pin goes, the card says so, and nothing is picked until you pick again
        internal void Arrived(string name, int marked)
        {
            RemovePin();
            _picked = null;
            Session.Last = null;
            _card.text = "Arrived at " + name + "." + (marked > 0 ? "\nMarked done." : "");
            Rows();
            Panel.LayersChanged();
        }

        private void RemovePin()
        {
            if (_pin == null) return;
            var map = Minimap.instance;
            try { if (map != null) map.RemovePin(_pin); }
            catch (Exception ex) { Plugin.Log.LogWarning("could not remove the map pin: " + ex.Message); }
            _pin = null;
        }

        // looks the picked kind or portal up again and shows it
        private void Refind(bool move)
        {
            if (_picked != null) Picked(move);
            else if (_selected.Kind != null)
            {
                var found = Session.Find(_selected.Kind);
                _picked = found?.Entry;
                _nearest = true;
                Present(found, move);
            }
            else if (_selected.Pin != null)
            {
                Session.Last = null;
                var pin = _selected.Pin;
                var at = Session.PlayerPosition();
                float dx = at.HasValue ? pin.m_pos.x - at.Value.x : 0f, dz = at.HasValue ? pin.m_pos.z - at.Value.z : 0f;
                _card.text = (string.IsNullOrEmpty(pin.m_name) ? "Map pin" : pin.m_name) + "\n" + Nearest.Distance(dx, dz).ToString("F0") + " m " + Nearest.Direction(dx, dz)
                    + "\nx " + pin.m_pos.x.ToString("F0") + ", z " + pin.m_pos.z.ToString("F0");
                if (move) Point(pin.m_pos, pin.m_name);
            }
            else if (_selected.Known != null)
            {
                Session.Last = null;
                var mark = _selected.Known;
                _card.text = mark.Name + "\nx " + mark.X.ToString("F0") + ", z " + mark.Z.ToString("F0")
                    + (string.IsNullOrEmpty(mark.Contents) ? "" : "\n" + mark.Contents.Substring(0, Math.Min(160, mark.Contents.Length)));
                if (move) Point(new Vector3(mark.X, 0f, mark.Z), mark.Name);
            }
            else PresentPortal(_selected.Portal, move);
        }

        private void Picked(bool move)
        {
            var at = Session.PlayerPosition();
            float dx = at.HasValue ? _picked.X - at.Value.x : 0f, dz = at.HasValue ? _picked.Z - at.Value.z : 0f;
            Session.Last = new Found { Entry = _picked, Distance = Nearest.Distance(dx, dz), Direction = Nearest.Direction(dx, dz), Left = _picked.Group.Items.Count - _picked.Group.Done, Done = _picked.Group.Done };
            Present(Session.Last, move);
        }

        // the result card, a temporary pin and the map centered on it. the counts in the list are read again
        private void Present(Found found, bool move)
        {
            Rows();
            if (_selected == null || _selected.Kind == null) return;
            var kind = _selected.Kind;
            if (Session.PlayerPosition() == null)
            {
                _card.text = "Your character is not in the world.";
                return;
            }
            if (found == null)
            {
                _card.text = kind.Kind + "\nNothing left to find." + (Settings.FindsUnexplored ? "\nReset starts again." : "\nUnexplored places are off in Settings.");
                return;
            }
            var e = found.Entry;
            string possible = e.Possible ? "\npossible site, 1 of " + e.Candidates : "";
            _card.text = e.Kind + "\n" + found.Distance.ToString("F0") + " m " + found.Direction + "   x " + e.X.ToString("F0") + "  z " + e.Z.ToString("F0")
                + "\n" + Categories.Split(e.Biome) + possible + "\n" + found.Left + " left, " + found.Done + " done";
            if (move) Point(new Vector3(e.X, e.Y, e.Z), (_nearest ? PinNamePrefix : "") + e.Kind);
        }

        private void PresentPortal(PortalInfo portal, bool move)
        {
            var at = Session.PlayerPosition();
            if (at == null)
            {
                _card.text = "Your character is not in the world.";
                return;
            }
            float dx = portal.X - at.Value.x, dz = portal.Z - at.Value.z;
            string name = string.IsNullOrEmpty(portal.Tag) ? "(no name)" : portal.Tag;
            _card.text = "Portal " + name + "\n" + Nearest.Distance(dx, dz).ToString("F0") + " m " + Nearest.Direction(dx, dz) + "   x " + portal.X.ToString("F0") + "  z " + portal.Z.ToString("F0");
            if (move) Point(new Vector3(portal.X, portal.Y, portal.Z), "Portal " + name);
        }

        // a pin on the map and the map centered on it, when the setting is on
        private void Point(Vector3 at, string name)
        {
            if (!Settings.NearestPin.Value) return;
            var map = Minimap.instance;
            if (map == null) return;
            try
            {
                RemovePin();
                _pin = map.AddPin(at, Minimap.PinType.Icon3, name, false, false, 0L, default);
                map.ShowPointOnMap(at);
            }
            catch (Exception ex)
            {
                _pin = null;
                if (!_pinWarned) Plugin.Log.LogWarning("could not add the map pin: " + ex.Message);
                _pinWarned = true;
            }
        }
    }
}
