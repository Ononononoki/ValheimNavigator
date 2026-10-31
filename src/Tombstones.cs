using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ValheimNavigator
{
    // every player tombstone in the world: read from the world's objects on the host (or in single player), a guest gets the host's list
    // with the map data (MapShare) and adds the ones its own game knows
    internal static class Tombstones
    {
        internal struct Grave { internal string Owner; internal float X, Y, Z; internal long Died; }

        private const string Prefab = "Player_tombstone";
        private const float ScanSeconds = 10f;
        private const int StepsPerFrame = 4;

        private static readonly List<ZDO> Found = new List<ZDO>();
        private static List<Grave> _own = new List<Grave>();
        private static Grave[] _shared;
        private static int _index = -1;
        private static float _next;
        private static ZDOMan _source;

        // grows whenever the list changes
        internal static int Version;

        // the guest's copy of the host's list (null clears it)
        internal static void SetShared(Grave[] graves) { _shared = graves; Version++; }

        // the tombstones this game read itself, for the host to share
        internal static List<Grave> Own => _own;

        // every frame on the game's thread: a sliced read of the tombstones every ScanSeconds
        internal static void Tick()
        {
            var man = ZDOMan.instance;
            if (man != _source) { _source = man; _own = new List<Grave>(); _index = -1; _next = 0f; Found.Clear(); Version++; }
            if (man == null) return;
            if (_index < 0)
            {
                if (Time.unscaledTime < _next) return;
                Found.Clear(); _index = 0;
            }
            for (int i = 0; i < StepsPerFrame; i++)
            {
                if (!man.GetAllZDOsWithPrefabIterative(Prefab, Found, ref _index)) continue;
                var graves = new List<Grave>(Found.Count);
                foreach (var zdo in Found)
                {
                    if (zdo == null || !zdo.IsValid()) continue;
                    var p = zdo.GetPosition();
                    graves.Add(new Grave { Owner = zdo.GetString(ZDOVars.s_ownerName, ""), X = p.x, Y = p.y, Z = p.z, Died = zdo.GetLong(ZDOVars.s_timeOfDeath, 0L) });
                }
                if (!Same(graves, _own)) { _own = graves; Version++; }
                Found.Clear(); _index = -1; _next = Time.unscaledTime + ScanSeconds;
                return;
            }
        }

        private static bool Same(List<Grave> a, List<Grave> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Owner != b[i].Owner || Math.Abs(a[i].X - b[i].X) > 0.5f || Math.Abs(a[i].Z - b[i].Z) > 0.5f) return false;
            return true;
        }

        // the host's list and this game's own, a tombstone known to both counted once
        internal static List<Grave> All()
        {
            var all = new List<Grave>(_own);
            var shared = _shared;
            if (shared != null)
                foreach (var g in shared)
                {
                    bool known = false;
                    foreach (var o in _own) if (o.Owner == g.Owner && Math.Abs(o.X - g.X) < 2f && Math.Abs(o.Z - g.Z) < 2f) { known = true; break; }
                    if (!known) all.Add(g);
                }
            return all;
        }
    }

    // the tombstones tab: every player's tombstone, searchable by name, nearest first, a route to the picked one
    internal sealed class TombstonesPage : Page
    {
        private const int PageSize = 12;
        private TMP_InputField _box;
        private TMP_Text _count, _pageText;
        private readonly List<UnityEngine.UI.Button> _rows = new List<UnityEngine.UI.Button>();
        private readonly List<TMP_Text> _texts = new List<TMP_Text>();
        private readonly List<Tombstones.Grave> _shown = new List<Tombstones.Grave>();
        private int _page, _version = -1;
        private string _filter = "";
        private float _refreshed;

        internal override string Title => "Tombs";

        internal override void Build(Transform parent)
        {
            TMP_Text unused;
            _box = Ui.Input(parent, "Search", "search a player name", 0f, 0f, Panel.PageWidth, 30f, text => { _filter = text ?? ""; _page = 0; Fill(); }, _ => { });
            _count = Ui.Label(parent, "Count", 0f, 36f, Panel.PageWidth, 24f, 15f, TextAlignmentOptions.MidlineLeft, Ui.Dim);
            for (int i = 0; i < PageSize; i++)
            {
                int row = i;
                TMP_Text text;
                _rows.Add(Ui.Tag(parent, "Grave" + i, "", 0f, 64f + i * 32f, Panel.PageWidth, 30f, 15f, TextAlignmentOptions.MidlineLeft, () => Route(row), out text));
                _texts.Add(text);
            }
            float y = 64f + PageSize * 32f + 4f;
            Ui.Tag(parent, "PreviousPage", "<", 0f, y, 34f, 28f, 16f, TextAlignmentOptions.Midline, () => { _page = Math.Max(0, _page - 1); Fill(); }, out unused);
            _pageText = Ui.Label(parent, "Page", 38f, y, 240f, 28f, 15f, TextAlignmentOptions.Midline, Ui.Dim);
            Ui.Tag(parent, "NextPage", ">", 282f, y, 34f, 28f, 16f, TextAlignmentOptions.Midline, () => { _page++; Fill(); }, out unused);
            var note = Ui.Label(parent, "Note", 0f, y + 34f, Panel.PageWidth, 40f, 12f, TextAlignmentOptions.TopLeft, Ui.Dim);
            note.textWrappingMode = TextWrappingModes.Normal;
            note.text = "Tombstones of all players in this world, nearest first, with the world time since the death. Click one for a route. Guests see the host's list when the host shares map data.";
        }

        internal override void Show() { Fill(); }

        internal override void Frame()
        {
            if (Tombstones.Version != _version || Time.unscaledTime - _refreshed > 2f) Fill();
        }

        private void Fill()
        {
            if (_box == null) return;
            _version = Tombstones.Version; _refreshed = Time.unscaledTime;
            var at = Session.PlayerPosition() ?? Vector3.zero;
            _shown.Clear();
            foreach (var g in Tombstones.All())
                if (_filter.Length == 0 || (g.Owner ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) _shown.Add(g);
            _shown.Sort((a, b) => Distance(a, at).CompareTo(Distance(b, at)));
            int pages = Math.Max(1, (_shown.Count + PageSize - 1) / PageSize);
            _page = Math.Min(_page, pages - 1);
            _count.text = _shown.Count == 0 ? "No tombstones known" : _shown.Count + (_shown.Count == 1 ? " tombstone" : " tombstones");
            _pageText.text = (_page + 1) + " / " + pages;
            for (int i = 0; i < PageSize; i++)
            {
                int k = _page * PageSize + i;
                bool on = k < _shown.Count;
                _rows[i].gameObject.SetActive(on);
                if (!on) continue;
                var g = _shown[k];
                _texts[i].text = (string.IsNullOrEmpty(g.Owner) ? "Unknown" : g.Owner) + "   " + Meters(Distance(g, at)) + Ago(g.Died);
            }
        }

        private static float Distance(Tombstones.Grave g, Vector3 at) => NavSearch.Distance(g.X, g.Z, at.x, at.z);

        // the world time since the death (the game keeps it as ZNet time ticks), empty when the tombstone has none
        private static string Ago(long died)
        {
            if (died <= 0L || ZNet.instance == null) return "";
            double minutes = (ZNet.instance.GetTime().Ticks - died) / (double)TimeSpan.TicksPerMinute;
            if (minutes < 0) return "";
            string ago = minutes < 1 ? "just now" : minutes < 60 ? (int)minutes + " min ago" : minutes < 1440 ? (int)(minutes / 60) + " h ago" : (int)(minutes / 1440) + " d ago";
            return "   died " + ago;
        }

        private static string Meters(float d) => d >= 1000f ? (d / 1000f).ToString("0.0") + " km" : d.ToString("F0") + " m";

        private void Route(int row)
        {
            int k = _page * PageSize + row;
            if (k >= _shown.Count) return;
            var g = _shown[k];
            Navigator.SetTarget(new Vector3(g.X, g.Y, g.Z), "Tombstone of " + (string.IsNullOrEmpty(g.Owner) ? "unknown" : g.Owner));
            Panel.OpenRoute();
            Navigator.Start();
        }
    }
}
