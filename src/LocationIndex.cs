using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ValheimNavigator
{
    // one location of the world, classified
    internal sealed class Entry
    {
        internal string Prefab;
        internal string Kind;
        internal string Category;
        internal string Biome;
        internal float X, Y, Z;
        internal bool Placed;
        // a unique site (a trader, a quest camp) that is not placed yet, one of Candidates possible places
        internal bool Possible;
        internal int Candidates;
        // the player has done this one, kept in step with the done file
        internal bool Done;
        internal KindData Group;

        // the key of the done list, a location is told apart by its prefab and its rounded ground position
        internal string Key => Prefab + "\t" + (int)Math.Round(X) + "\t" + (int)Math.Round(Z);
    }

    // all locations of one kind, with the words a text search looks at
    internal sealed class KindData
    {
        internal string Key;
        internal string Category;
        internal string Kind;
        internal string Words;
        internal int Done;
        internal readonly List<Entry> Items = new List<Entry>();
        internal readonly HashSet<string> Prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seen = new HashSet<string>();

        // adds a prefab or biome name to the search words once
        internal void Know(string word)
        {
            if (!string.IsNullOrEmpty(word) && _seen.Add(word)) Words += " " + word.ToLowerInvariant();
        }
    }

    // a location as the game lists it, before classification
    internal struct Raw
    {
        internal string Prefab;
        internal bool Unique;
        internal float X, Y, Z;
        internal bool Placed;
        internal string Biome;
    }

    // makes the entries of a location list a few at a time. a unique site that has a placed place keeps only the placed one,
    // one that has none yet keeps all its candidates, each marked possible
    internal sealed class IndexBuilder
    {
        private readonly List<Raw> _raw;
        private readonly HashSet<string> _placedUnique = new HashSet<string>();
        private readonly Dictionary<string, int> _candidates = new Dictionary<string, int>();
        private readonly Dictionary<string, KindData> _byPrefab = new Dictionary<string, KindData>();
        private int _next;

        internal readonly List<Entry> Entries;
        internal readonly Dictionary<string, KindData> Kinds = new Dictionary<string, KindData>();
        internal readonly HashSet<string> Other = new HashSet<string>();

        internal IndexBuilder(List<Raw> raw)
        {
            _raw = raw;
            Entries = new List<Entry>(raw.Count);
            foreach (var r in raw)
                if (r.Unique && r.Placed) _placedUnique.Add(r.Prefab);
            foreach (var r in raw)
            {
                if (!r.Unique || _placedUnique.Contains(r.Prefab)) continue;
                _candidates.TryGetValue(r.Prefab, out var n);
                _candidates[r.Prefab] = n + 1;
            }
        }

        internal bool Finished => _next >= _raw.Count;

        internal int Total => _raw.Count;

        // makes entries until the time is used up, the clock is read every 64 entries
        internal void Step(double milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (_next < _raw.Count)
            {
                Add(_raw[_next++]);
                if ((_next & 63) == 0 && watch.Elapsed.TotalMilliseconds >= milliseconds) break;
            }
        }

        private KindData GroupOf(string prefab)
        {
            if (_byPrefab.TryGetValue(prefab, out var known)) return known;
            Categories.Classify(prefab, out var category, out var kind);
            if (category == "other") Other.Add(prefab);
            string key = category + "|" + kind;
            if (!Kinds.TryGetValue(key, out var group))
            {
                group = new KindData { Key = key, Category = category, Kind = kind, Words = (kind + " " + category + " " + Categories.Titles[category]).ToLowerInvariant() };
                Kinds[key] = group;
            }
            group.Know(prefab);
            group.Prefabs.Add(prefab);
            _byPrefab[prefab] = group;
            return group;
        }

        private void Add(Raw r)
        {
            bool placedGroup = r.Unique && _placedUnique.Contains(r.Prefab);
            if (placedGroup && !r.Placed) return;
            var group = GroupOf(r.Prefab);
            group.Know(r.Biome);
            bool possible = r.Unique && !placedGroup;
            var entry = new Entry
            {
                Prefab = r.Prefab, Kind = group.Kind, Category = group.Category, Biome = r.Biome,
                X = r.X, Y = r.Y, Z = r.Z, Placed = r.Placed,
                Possible = possible, Candidates = possible ? _candidates[r.Prefab] : 0, Group = group
            };
            group.Items.Add(entry);
            Entries.Add(entry);
        }
    }

    internal static class LocationIndex
    {
        // every location of the world as an entry in one go, for the console command and the checks
        internal static List<Entry> Build(IEnumerable<Raw> raw, out List<string> unclassified)
        {
            var builder = new IndexBuilder(raw.ToList());
            builder.Step(double.MaxValue);
            unclassified = builder.Other.OrderBy(p => p, StringComparer.Ordinal).ToList();
            return builder.Entries;
        }

        // the world's own location list, null when no world with a list is loaded
        internal static List<Raw> FromGame()
        {
            var zone = ZoneSystem.instance;
            if (zone == null) return null;
            var result = new List<Raw>();
            foreach (var location in zone.GetLocationList())
            {
                if (location.m_location == null) continue;
                result.Add(new Raw
                {
                    Prefab = location.m_location.m_prefabName, Unique = location.m_location.m_unique,
                    X = location.m_position.x, Y = location.m_position.y, Z = location.m_position.z,
                    Placed = location.m_placed, Biome = location.m_location.m_biome.ToString()
                });
            }
            return result;
        }

        // the size of the game's list and how many are placed, it changes when a site is placed or more are generated
        internal static string Signature()
        {
            var zone = ZoneSystem.instance;
            if (zone == null) return "";
            int count = 0, placed = 0;
            foreach (var location in zone.GetLocationList())
            {
                count++;
                if (location.m_placed) placed++;
            }
            return count + "|" + placed;
        }
    }
}
