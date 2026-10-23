using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimNavigator
{
    // the nearest location of a kind that is not done
    internal sealed class Found
    {
        internal Entry Entry;
        internal float Distance;
        internal string Direction;
        internal int Left;
        internal int Done;
    }

    internal static class Nearest
    {
        private static readonly string[] Winds = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        // compass name from the player to a point, x is east and z is north
        internal static string Direction(float dx, float dz)
        {
            double degrees = Math.Atan2(dx, dz) * 180.0 / Math.PI;
            if (degrees < 0) degrees += 360.0;
            return Winds[(int)Math.Round(degrees / 45.0) % 8];
        }

        internal static float Distance(float dx, float dz)
        {
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        // the nearest entry that is neither done nor excluded, from the ground position (x, z).
        // Left counts the entries still open, Done the ones the player has done. null when none is open
        internal static Found Find(IList<Entry> items, float x, float z, Func<Entry, bool> excluded)
        {
            Entry best = null;
            float bestSquared = float.MaxValue;
            int left = 0, done = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var e = items[i];
                if (e.Done) { done++; continue; }
                if (excluded != null && excluded(e)) continue;
                left++;
                float dx = e.X - x, dz = e.Z - z, squared = dx * dx + dz * dz;
                if (squared < bestSquared) { bestSquared = squared; best = e; }
            }
            if (best == null) return null;
            return new Found
            {
                Entry = best, Distance = (float)Math.Sqrt(bestSquared), Direction = Direction(best.X - x, best.Z - z),
                Left = left, Done = done
            };
        }
    }

    internal static class Search
    {
        // the kinds a query picks, by category order and name. an exact kind or prefab name picks only that kind,
        // otherwise every kind whose words hold all the query words
        internal static List<KindData> Match(IEnumerable<KindData> kinds, string query)
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var chosen = kinds.Where(k => words.All(w => k.Words.Contains(w))).ToList();
            string text = (query ?? "").Trim();
            if (words.Length > 0)
            {
                var exact = chosen.Where(k => string.Equals(k.Kind, text, StringComparison.OrdinalIgnoreCase) || k.Prefabs.Contains(text)).ToList();
                if (exact.Count == 1) chosen = exact;
            }
            return chosen.OrderBy(k => Array.IndexOf(Categories.Order, k.Category)).ThenBy(k => k.Kind, StringComparer.Ordinal).ToList();
        }
    }
}
