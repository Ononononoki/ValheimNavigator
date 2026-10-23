using System;
using System.Collections;
using UnityEngine;

namespace ValheimNavigator
{
    // the SpeedyPaths mod (nex.SpeedyPaths, Nextek): faster walking on paths and built floors. its speed per ground type is read from its own
    // settings, which follow the server's when it is authoritative. ground types and the rules are the mod's (SpeedyPathsClientMod 1.0.9,
    // UpdateGroundTypeCache): a piece by its material, otherwise the ground paint above 0.4, stone path before dirt path before cultivated
    internal static class SpeedyPaths
    {
        private const string Guid = "nex.SpeedyPaths";
        internal const int Untamed = 0, DirtPath = 1, StonePath = 2, Cultivated = 3, Wood = 4, HardWood = 5, Stone = 6, Iron = 7, Marble = 8, Grausten = 9;
        private static readonly float[] Speeds = new float[10];

        internal static bool Installed => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Guid);

        // the mod's current speeds, false when it is missing or its settings cannot be read. the fastest one is the bound's limit
        internal static bool Read(out float fastest)
        {
            fastest = 1f;
            for (int i = 0; i < Speeds.Length; i++) Speeds[i] = 1f;
            if (!Installed) return false;
            try
            {
                var type = HarmonyLib.AccessTools.TypeByName("SpeedyPaths.SpeedyPathsClientMod");
                var table = HarmonyLib.AccessTools.Field(type, "_speedModifiers")?.GetValue(null) as IDictionary;
                if (table == null) return false;
                foreach (DictionaryEntry entry in table)
                {
                    int key = Convert.ToInt32(entry.Key);
                    var value = entry.Value?.GetType().GetProperty("Value")?.GetValue(entry.Value);
                    if (key < 0 || key >= Speeds.Length || !(value is float speed) || speed <= 0f) continue;
                    Speeds[key] = speed;
                    fastest = Mathf.Max(fastest, speed);
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("SpeedyPaths speeds could not be read: " + ex.Message);
                return false;
            }
        }

        internal static float Of(int ground) { return ground > 0 && ground < Speeds.Length ? Speeds[ground] : 1f; }

        // a piece's material as the mod maps it: wood, stone, iron, core wood, marble, grausten (WearNTear.MaterialType 0 to 5)
        internal static int FromMaterial(int material)
        {
            switch (material)
            {
                case 0: return Wood;
                case 1: return Stone;
                case 2: return Iron;
                case 3: return HardWood;
                case 4: return Marble;
                case 5: return Grausten;
                default: return Untamed;
            }
        }

        // the terrain paint: blue paved, red dirt path, green cultivated
        internal static int FromPaint(Color paint)
        {
            if (paint.b > 0.4f) return StonePath;
            if (paint.r > 0.4f) return DirtPath;
            if (paint.g > 0.4f) return Cultivated;
            return Untamed;
        }
    }
}
