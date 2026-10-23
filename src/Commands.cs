using System.Collections.Generic;
using System.Linq;

namespace ValheimNavigator
{
    // the "navigator" console command, the same index and done list as the panel
    internal static class Commands
    {
        private const string Usage = "navigator count | types [text] | nearest <type> | done | undo | reset <type>";
        private const int ListLimit = 40;
        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;
            new Terminal.ConsoleCommand("navigator", Usage, new Terminal.ConsoleEvent(Handle));
        }

        private static void Handle(Terminal.ConsoleEventArgs args)
        {
            var say = args.Context;
            if (!Plugin.Enabled.Value) { say.AddString("Valheim Navigator is off in its config."); return; }
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            string rest = args.Length > 2 ? string.Join(" ", args.Args.Skip(2)) : "";
            if (!Session.BuildNow())
            {
                say.AddString(Session.Problem ?? "Valheim Navigator has no location list yet, load a world first.");
                return;
            }

            switch (sub)
            {
                case "count": Count(say); break;
                case "types": Types(say, rest); break;
                case "nearest": Pick(say, rest); break;
                case "done": Done(say); break;
                case "undo": Undo(say); break;
                case "reset": Reset(say, rest); break;
                default: say.AddString(Usage); break;
            }
        }

        private static void Count(Terminal say)
        {
            var entries = Session.Entries;
            say.AddString($"Navigator: {entries.Count} locations, {entries.Count(e => e.Placed)} placed, {Session.Kinds.Count} kinds, indexed in {Session.BuildMilliseconds:F1} ms, worst slice {Session.WorstSliceMilliseconds:F2} ms");
            foreach (var category in Categories.Order)
            {
                int n = entries.Count(e => e.Category == category);
                if (n > 0) say.AddString($"   {Categories.Titles[category]} {n}");
            }
            var other = Session.Unclassified;
            if (other.Count > 0) say.AddString("   no category yet for " + string.Join(", ", other.Take(10)) + (other.Count > 10 ? " and " + (other.Count - 10) + " more" : ""));
        }

        private static List<KindData> Matches(string query)
        {
            return Search.Match(Session.Kinds.Values, query);
        }

        private static void Types(Terminal say, string query)
        {
            var kinds = Matches(query);
            if (kinds.Count == 0) { say.AddString("Navigator: no type matches '" + query + "'."); return; }
            foreach (var kind in kinds.Take(ListLimit))
                say.AddString($"   {Categories.Titles[kind.Category]}: {kind.Kind}, {kind.Done} of {kind.Items.Count} done");
            if (kinds.Count > ListLimit) say.AddString($"   {kinds.Count - ListLimit} more, add words to narrow it down");
        }

        private static KindData One(Terminal say, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { say.AddString(Usage); return null; }
            var kinds = Matches(query);
            if (kinds.Count == 0) { say.AddString("Navigator: no type matches '" + query + "'. Try navigator types."); return null; }
            if (kinds.Count > 1)
            {
                say.AddString("Navigator: '" + query + "' matches " + kinds.Count + " types, add words or use one of");
                foreach (var kind in kinds.Take(8)) say.AddString("   " + kind.Kind);
                return null;
            }
            return kinds[0];
        }

        private static void Pick(Terminal say, string query)
        {
            var kind = One(say, query);
            if (kind != null) Show(say, kind);
        }

        private static void Show(Terminal say, KindData kind)
        {
            if (Session.PlayerPosition() == null) { say.AddString("Valheim Navigator needs your character in the world."); return; }
            var found = Session.Find(kind);
            if (found == null) { say.AddString($"Navigator: every {kind.Kind} is done, navigator reset {kind.Kind} starts again."); return; }
            var e = found.Entry;
            string note = e.Possible ? $", possible site, 1 of {e.Candidates}" : "";
            say.AddString($"Navigator: nearest {kind.Kind} not done is {found.Distance:F0} m {found.Direction}, x {e.X:F0} z {e.Z:F0}, {Categories.Split(e.Biome)}{note}. {found.Left} left, {found.Done} done.");
            if (Plugin.LogCounters.Value)
                Plugin.Log.LogInfo($"nearest {kind.Kind} {found.Distance:F0} m {found.Direction} at {e.X:F0} {e.Z:F0}, {found.Left} left, {found.Done} done");
        }

        // marks the last result done and shows the next nearest one of its kind
        private static void Done(Terminal say)
        {
            var last = Session.Last;
            if (last == null) { say.AddString("Navigator: run navigator nearest <type> first."); return; }
            var entry = last.Entry;
            say.AddString($"Navigator: {entry.Kind} at x {entry.X:F0} z {entry.Z:F0} marked done.");
            Session.MarkDoneAndNext();
            Show(say, entry.Group);
        }

        private static void Undo(Terminal say)
        {
            var entry = Session.LastDone;
            if (!Session.Undo()) { say.AddString("Navigator: nothing to undo in this session."); return; }
            say.AddString($"Navigator: {entry.Kind} at x {entry.X:F0} z {entry.Z:F0} is not done again.");
        }

        private static void Reset(Terminal say, string query)
        {
            var kind = One(say, query);
            if (kind == null) return;
            int removed = Session.Reset(kind);
            say.AddString($"Navigator: {kind.Kind} done list cleared, {removed} locations.");
        }
    }
}
