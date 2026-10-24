using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace ValheimNavigator
{
    // the index of the loaded world and the nearest-not-done state, shared by the console command and the panel.
    // the index is built when a world loads, a few entries a frame, and stays until the world is left
    internal static class Session
    {
        private const float PollSeconds = 1f;

        internal static List<Entry> Entries;
        internal static Dictionary<string, KindData> Kinds = new Dictionary<string, KindData>();
        internal static List<string> Unclassified = new List<string>();
        internal static double BuildMilliseconds;
        internal static double WorstSliceMilliseconds;
        internal static DoneStore Store;
        internal static Found Last;
        internal static Entry LastDone;
        internal static string Problem;
        // counts up each time a new index is ready, the panel looks its picked kind up again when it changes
        internal static int Version;
        // the kinds searched in this session, the ones the auto mark looks at
        internal static readonly HashSet<string> Tracked = new HashSet<string>();

        private static IndexBuilder _builder;
        private static string _signature = "";
        private static string _world;
        private static ZoneSystem _zone;
        private static long _uid;
        private static string _legacy;
        private static int _remoteVersion = -1;
        private static float _poll;
        private static float _arrival;
        private static int _slices;
        private static Func<Minimap, Vector3, bool> _explored;

        internal static bool Ready => Entries != null;
        internal static bool Active => _zone != null;
        internal static string Status => Ready ? "World search is ready." : Building ? "Building the location list." : Problem ?? "Load a world first.";

        internal static bool Building => _builder != null;

        // every frame from the plugin. it feeds the index builder and runs the arrival check
        internal static void Tick(float dt)
        {
            if (!Plugin.Enabled.Value) return;
            if (_builder != null) Slice();
            _poll += dt;
            if (_poll >= PollSeconds)
            {
                _poll = 0f;
                Poll();
            }
            _arrival += dt;
            if (_arrival >= Settings.ArrivalSeconds.Value)
            {
                _arrival = 0f;
                if (Settings.AutoMark.Value)
                {
                    int marked = AutoMark(Plugin.AutoMarkMeters.Value);
                    if (marked > 0 && Plugin.LogCounters.Value) Plugin.Log.LogInfo($"auto mark, {marked} locations done");
                }
            }
        }

        // the world loads or changes: build its index. a world that is left drops the index
        private static void Poll()
        {
            var zone = ZoneSystem.instance;
            if (zone == null)
            {
                if (Ready || Building || _world != null) { Panel.Find?.WorldChanged(); Reset(); Navigator.Clear(); Portals.Forget(); KnownWorld.Forget(); }
                return;
            }
            if (Player.m_localPlayer == null) return;
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "";
            long uid = ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L;
            if (!ReferenceEquals(zone, _zone) || uid != _uid || world != _world)
            {
                if (_zone != null) { Panel.Find?.WorldChanged(); Navigator.Clear(); Portals.Forget(); KnownWorld.Forget(); }
                Reset(); _zone = zone; _world = world; _uid = uid;
                string folder = Path.Combine(BepInEx.Paths.ConfigPath, "ValheimNavigator"), file = DoneStore.FileFor(folder, world, uid);
                Store = new DoneStore(file);
                // a world without its own done file yet takes the marks of its old name file once, the old file stays (0.1.0 used names)
                _legacy = uid != 0 && !File.Exists(file) && File.Exists(DoneStore.FileFor(folder, world)) ? DoneStore.FileFor(folder, world) : null;
                Version++;
            }
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                if (WorldSync.Version != _remoteVersion)
                {
                    _remoteVersion = WorldSync.Version;
                    if (WorldSync.World == uid && WorldSync.Locations != null) Begin(WorldSync.Locations);
                    else if (Ready || Building) { Entries = null; Kinds = new Dictionary<string, KindData>(); _builder = null; Version++; }
                }
                Problem = WorldSync.Status;
                return;
            }
            if (Ready || Building) return;
            if (!zone.LocationsGenerated) { Problem = "World locations are still being generated. Pins and manual routes are available."; return; }
            Start(world, zone);
        }

        private static void Reset()
        {
            Entries = null;
            Kinds = new Dictionary<string, KindData>();
            _builder = null;
            _signature = "";
            _world = null;
            _zone = null;
            _uid = 0; _remoteVersion = -1;
            Store = null;
            Last = null;
            LastDone = null;
            Tracked.Clear();
            Problem = null;
        }

        private static void Start(string world, ZoneSystem zone)
        {
            var raw = LocationIndex.FromGame();
            if (raw == null)
            {
                Problem = "World locations are not available. Pins and manual routes are available.";
                return;
            }
            _world = world;
            _zone = zone;
            _signature = LocationIndex.Signature();
            Begin(raw);
        }

        // a replacement catalog is built separately, personal progress is applied only when it is complete
        private static void Begin(List<Raw> raw)
        {
            _builder = new IndexBuilder(raw);
            BuildMilliseconds = 0;
            WorstSliceMilliseconds = 0;
            _slices = 0;
        }

        private static void Slice()
        {
            var watch = Stopwatch.StartNew();
            _builder.Step(Settings.SliceMilliseconds.Value);
            double ms = watch.Elapsed.TotalMilliseconds;
            BuildMilliseconds += ms;
            _slices++;
            if (ms > WorstSliceMilliseconds) WorstSliceMilliseconds = ms;
            if (_builder.Finished) Finish();
        }

        private static void Finish()
        {
            var entries = _builder.Entries;
            if (_legacy != null)
            {
                int imported = Store.ImportLegacy(_legacy, entries);
                if (imported > 0) Plugin.Log.LogInfo("done marks taken over from the old world name file, " + imported + " locations");
                _legacy = null;
            }
            foreach (var entry in entries)
            {
                entry.Done = Store.Contains(entry);
                if (entry.Done) entry.Group.Done++;
            }
            Unclassified = new List<string>(_builder.Other);
            Unclassified.Sort(StringComparer.Ordinal);
            Kinds = _builder.Kinds;
            Settings.BindKinds(Kinds.Values);
            Entries = entries;
            _builder = null;
            Problem = null;
            Last = null;
            LastDone = null;
            Version++;
            if (Plugin.LogCounters.Value)
                Plugin.Log.LogInfo($"index ready, {entries.Count} locations in {Kinds.Count} kinds, {_slices} slices, {BuildMilliseconds:F1} ms in all, worst slice {WorstSliceMilliseconds:F2} ms, {Unclassified.Count} prefabs without a category");
        }

        // the map opens: a changed location list (a site was placed) builds the index again, the old one stays usable meanwhile
        internal static void EnsureFresh()
        {
            if (ZNet.instance != null && !ZNet.instance.IsServer()) { Poll(); return; }
            if (Building) return;
            if (!Ready) { Poll(); return; }
            var zone = ZoneSystem.instance;
            if (zone != null && LocationIndex.Signature() != _signature) Start(_world, zone);
        }

        // builds the whole index at once, for the console command
        internal static bool BuildNow()
        {
            EnsureFresh();
            if (Building)
            {
                while (_builder != null) { _builder.Step(double.MaxValue); Finish(); }
            }
            return Ready;
        }

        internal static int ImportLegacy()
        {
            if (!Ready || Store == null || _uid == 0) return 0;
            string legacy = DoneStore.FileFor(Path.Combine(BepInEx.Paths.ConfigPath, "ValheimNavigator"), _world);
            int count = Store.ImportLegacy(legacy, Entries);
            foreach (var kind in Kinds.Values)
            {
                kind.Done = 0;
                foreach (var entry in kind.Items) { entry.Done = Store.Contains(entry); if (entry.Done) kind.Done++; }
            }
            Version++; Layers.Dirty(); return count;
        }

        internal static Vector3? PlayerPosition()
        {
            var player = Player.m_localPlayer;
            return player != null ? (Vector3?)player.transform.position : null;
        }

        private static bool Explored(Entry entry)
        {
            var map = Minimap.instance;
            if (map == null) return true;
            if (_explored == null)
                _explored = HarmonyLib.AccessTools.MethodDelegate<Func<Minimap, Vector3, bool>>(HarmonyLib.AccessTools.Method(typeof(Minimap), "IsExplored"));
            return _explored(map, new Vector3(entry.X, entry.Y, entry.Z));
        }

        // the nearest location of a kind that is not done, null when none is left or the character is not in the world
        internal static Found Find(KindData kind)
        {
            var at = PlayerPosition();
            if (at == null) { Last = null; return null; }
            Tracked.Add(kind.Key);
            Func<Entry, bool> excluded = Settings.IncludeUnexplored.Value ? (Func<Entry, bool>)null : e => !Explored(e);
            Last = Nearest.Find(kind.Items, at.Value.x, at.Value.z, excluded);
            return Last;
        }

        internal static bool Mark(Entry entry)
        {
            if (Store == null || !Store.Mark(entry)) return false;
            entry.Done = true;
            Layers.Dirty();
            entry.Group.Done++;
            return true;
        }

        internal static bool Unmark(Entry entry)
        {
            if (Store == null || !Store.Unmark(entry)) return false;
            entry.Done = false;
            Layers.Dirty();
            entry.Group.Done--;
            return true;
        }

        // marks the last result done and finds the next nearest one of its kind
        internal static Found MarkDoneAndNext()
        {
            if (Last == null) return null;
            var entry = Last.Entry;
            if (Mark(entry)) LastDone = entry;
            return Find(entry.Group);
        }

        internal static bool Undo()
        {
            if (LastDone == null) return false;
            Unmark(LastDone);
            LastDone = null;
            return true;
        }

        internal static int Reset(KindData kind)
        {
            Last = null;
            int removed = Store.Reset(kind.Items);
            foreach (var entry in kind.Items) entry.Done = false;
            kind.Done = 0;
            Layers.Dirty();
            return removed;
        }

        // the route arrived: every place within the distance of its destination is marked done when its category has auto mark on, searched
        // in Find or not. returns how many
        internal static int MarkNear(float x, float z, float meters)
        {
            if (!Ready || Store == null || meters <= 0f) return 0;
            float limit = meters * meters;
            int marked = 0;
            foreach (var group in Kinds.Values)
            {
                if (!Settings.AutoMarkFor(group.Category)) continue;
                foreach (var e in group.Items)
                {
                    if (e.Done) continue;
                    float dx = e.X - x, dz = e.Z - z;
                    if (dx * dx + dz * dz <= limit && Mark(e)) marked++;
                }
            }
            return marked;
        }

        // marks the searched kinds' locations within the distance as done, returns how many. only the searched kinds are looked at
        internal static int AutoMark(float meters)
        {
            var at = PlayerPosition();
            if (at == null || !Ready || Store == null || meters <= 0f || Tracked.Count == 0) return 0;
            float limit = meters * meters;
            int marked = 0;
            foreach (var key in Tracked)
            {
                if (!Kinds.TryGetValue(key, out var group) || !Settings.AutoMarkFor(group.Category)) continue;
                var items = group.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    var e = items[i];
                    if (e.Done) continue;
                    float dx = e.X - at.Value.x, dz = e.Z - at.Value.z;
                    if (dx * dx + dz * dz <= limit && Mark(e)) marked++;
                }
            }
            return marked;
        }
    }
}
