using System.Collections.Generic;
using HarmonyLib;

namespace ValheimNavigator
{
    internal sealed class PortalInfo
    {
        internal string Tag;
        internal float X, Y, Z;
    }

    // the world's portals by their name, read from the game's own portal registry while the option is on.
    // the registry holds only portals, so nothing scans all objects
    internal static class Portals
    {
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>> Registry =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>("m_portalObjects");

        private static readonly int TagHash = "tag".GetStableHashCode();
        private static float _last = -1000f;

        internal static readonly List<PortalInfo> List = new List<PortalInfo>();

        // counts up each time the list was read again, the marker layer draws its lines again when it changes
        internal static int Version;

        // reads the portals again when the refresh time has passed
        internal static void Refresh(float seconds)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _last < seconds) return;
            _last = now;
            List.Clear();
            var manager = ZDOMan.instance;
            var registry = manager != null ? Registry(manager) : null;
            if (registry == null) return;
            foreach (var sector in registry.Values)
                foreach (var zdo in sector)
                {
                    var at = zdo.GetPosition();
                    List.Add(new PortalInfo { Tag = zdo.GetString(TagHash, ""), X = at.x, Y = at.y, Z = at.z });
                }
            Version++;
        }

        internal static void Forget()
        {
            List.Clear();
            _last = -1000f;
            Version++;
        }

        // the portals whose name holds all the query words, nearest first, at most the limit
        internal static List<PortalInfo> Match(string query, float x, float z, int limit)
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            var found = new List<PortalInfo>();
            foreach (var portal in List)
            {
                string tag = (portal.Tag ?? "").ToLowerInvariant();
                bool all = true;
                foreach (var word in words) if (!tag.Contains(word) && !"portal".Contains(word)) { all = false; break; }
                if (all) found.Add(portal);
            }
            found.Sort((a, b) => ((a.X - x) * (a.X - x) + (a.Z - z) * (a.Z - z)).CompareTo((b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z)));
            if (found.Count > limit) found.RemoveRange(limit, found.Count - limit);
            return found;
        }
    }
}
