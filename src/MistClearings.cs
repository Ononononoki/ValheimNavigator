using System.Collections.Generic;
using UnityEngine;

namespace ValheimNavigator
{
    // only loaded clearing sources are available, distant natural mist remains an estimate
    internal static class MistClearings
    {
        private struct Area { internal Vector3 Position; internal float Radius; }
        // replaced as a whole on each update, so the sharp view's background thread always reads a complete set
        private static Dictionary<long, List<Area>> Areas = new Dictionary<long, List<Area>>();
        private static float _next;
        private static int _signature;
        private static WorldGenerator _world;
        private static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }

        internal static void Frame()
        {
            if (!Settings.Mist.Value || Time.unscaledTime < _next && _world == WorldGenerator.instance) return;
            _world = WorldGenerator.instance; _next = Time.unscaledTime + Mathf.Max(5f, Settings.RouteRefresh.Value);
            var areas = new Dictionary<long, List<Area>>(); int signature = 0, count = 0;
            foreach (var source in Demister.GetDemisters())
            {
                if (source == null || source.m_forceField == null) continue;
                var area = new Area { Position = source.transform.position, Radius = source.m_forceField.endRange };
                if (area.Radius <= 0f) continue;
                count++;
                unchecked { signature += area.Position.GetHashCode() ^ area.Radius.GetHashCode(); }
                int minX = Mathf.FloorToInt((area.Position.x - area.Radius) / 128f), maxX = Mathf.FloorToInt((area.Position.x + area.Radius) / 128f);
                int minZ = Mathf.FloorToInt((area.Position.z - area.Radius) / 128f), maxZ = Mathf.FloorToInt((area.Position.z + area.Radius) / 128f);
                for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
                {
                    long key = Key(x, z);
                    if (!areas.TryGetValue(key, out var list)) { list = new List<Area>(); areas.Add(key, list); }
                    list.Add(area);
                }
            }
            Areas = areas;
            if (_signature == signature) return;
            _signature = signature; TerrainOverlay.Dirty();
            if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("mist clearing sources updated, " + count + " loaded sources");
        }

        internal static bool Clear(float x, float y, float z)
        {
            var areas = Areas;
            if (!areas.TryGetValue(Key(Mathf.FloorToInt(x / 128f), Mathf.FloorToInt(z / 128f)), out var list)) return false;
            var position = new Vector3(x, y, z);
            foreach (var area in list) if ((area.Position - position).sqrMagnitude < area.Radius * area.Radius) return true;
            return false;
        }
    }
}
