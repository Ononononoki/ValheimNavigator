using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimNavigator
{
    // the private minimap fields the map layers and the overview need
    internal static class MapAccess
    {
        internal static readonly AccessTools.FieldRef<Minimap, Texture2D> Fog = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_fogTexture");
        internal static readonly AccessTools.FieldRef<Minimap, Material> LargeShader = AccessTools.FieldRefAccess<Minimap, Material>("m_mapLargeShader");
        internal static readonly AccessTools.FieldRef<Minimap, Texture2D> Height = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");
        internal static readonly AccessTools.FieldRef<Minimap, Texture2D> Mask = AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        internal static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> Pins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
    }

    // the native material gets a clear fog sample, a separate layer shades personally unexplored terrain
    internal static class Reveal
    {
        private const string FogName = "_FogTex";

        private static Texture2D _all;
        private static bool _applied;
        private static bool _warned;

        // a uniform clear sample is shared across worlds without copying the exploration texture
        internal static Texture All(Minimap map)
        {
            if (_all != null) return _all;
            _all = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _all.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));
            _all.Apply(false);
            return _all;
        }

        // what the overview and the large map should sample as fog right now
        internal static Texture Fog(Minimap map)
        {
            var real = MapAccess.Fog(map);
            if (!Settings.UnexploredShown || !TerrainOverlay.Ready(map)) return real;
            return All(map) ?? real;
        }

        // the large map opens or the switch changes
        internal static void Apply(Minimap map)
        {
            try
            {
                if (map == null) return;
                var shader = MapAccess.LargeShader(map);
                if (shader == null) return;
                if (Settings.UnexploredShown)
                {
                    // the native fog stays until the unexplored shade is built, then the overlay swaps it
                    shader.SetTexture(FogName, Fog(map));
                    _applied = true;
                }
                else if (_applied)
                {
                    shader.SetTexture(FogName, MapAccess.Fog(map));
                    _applied = false;
                }
                TerrainOverlay.Dirty();
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("unexplored display " + (Settings.UnexploredShown ? "shaded" : "native fog"));
            }
            catch (Exception ex)
            {
                if (!_warned) Plugin.Log.LogWarning("could not show the unexplored terrain: " + ex.Message);
                _warned = true;
            }
        }
    }
}
