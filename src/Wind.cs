using System;
using UnityEngine;

namespace ValheimNavigator
{
    // the game's wind depends only on the world clock (EnvMan.UpdateWind): four octaves, each seeded by the time step of its period, add an
    // angle and an intensity. the same steps give the wind of any future moment. the weather sets the strength range
    internal static class Wind
    {
        internal struct Sample { internal float X, Z, Intensity; }

        // the target wind at a world time in seconds, x and z are the direction the wind blows to
        internal static Sample At(double seconds)
        {
            var env = EnvMan.instance;
            if (env == null || env.m_windPeriodDuration < 8) return new Sample { X = 0f, Z = 1f, Intensity = 0.5f };
            long time = (long)seconds;
            float angle = 0f, intensity = 0.5f;
            var state = UnityEngine.Random.state;
            foreach (int octave in new[] { 1, 2, 4, 8 })
            {
                UnityEngine.Random.InitState((int)(time / (env.m_windPeriodDuration / octave)));
                angle += UnityEngine.Random.value * (float)(Math.PI * 2) / octave;
                intensity += -(0.5f / octave) + UnityEngine.Random.value / octave;
            }
            UnityEngine.Random.state = state;
            var setup = env.GetCurrentEnvironment();
            float strength = setup != null ? Mathf.Lerp(setup.m_windMin, setup.m_windMax, intensity) : intensity;
            return new Sample { X = Mathf.Sin(angle), Z = Mathf.Cos(angle), Intensity = Mathf.Clamp01(strength) };
        }

        // the mean wind from now over a stretch of time, sampled every 30 s. turning winds average to a weaker wind, which is honest
        internal static Sample Mean(float horizonSeconds)
        {
            double now = ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : 0.0;
            int count = Math.Max(1, (int)(horizonSeconds / 30f) + 1);
            float x = 0f, z = 0f, intensity = 0f;
            for (int i = 0; i < count; i++)
            {
                var s = At(now + i * 30.0);
                x += s.X * s.Intensity; z += s.Z * s.Intensity; intensity += s.Intensity;
            }
            float length = Mathf.Sqrt(x * x + z * z);
            if (length < 0.0001f) return new Sample { X = 0f, Z = 1f, Intensity = 0f };
            return new Sample { X = x / length, Z = z / length, Intensity = Mathf.Clamp01(length / count) };
        }
    }

    // measured ship speeds in m/s from the Valheim wiki (Boats, 22 Oct): paddling, full sail at 5 percent and at full wind. ships from other
    // mods can be added in the cfg as Name=paddle/low/high, a ship nobody listed sails like a longship
    internal static class ShipSpeeds
    {
        // true when the ship's speeds are known, false when longship speeds stand in
        internal static bool Of(string prefab, out float paddle, out float low, out float high)
        {
            string name = prefab ?? "";
            foreach (var entry in (Settings.ShipSpeedList.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('=');
                if (parts.Length != 2 || !string.Equals(parts[0].Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                var speeds = parts[1].Split('/');
                if (speeds.Length == 3 && float.TryParse(speeds[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out paddle)
                    && float.TryParse(speeds[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out low)
                    && float.TryParse(speeds[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out high)
                    && paddle > 0f && low > 0f && high >= low) return true;
            }
            if (name.IndexOf("Raft", StringComparison.OrdinalIgnoreCase) >= 0) { paddle = 1.6f; low = 2.2f; high = 3.1f; return true; }
            if (name.IndexOf("Karve", StringComparison.OrdinalIgnoreCase) >= 0) { paddle = 3.14f; low = 3.9f; high = 7.0f; return true; }
            if (name.IndexOf("Ashlands", StringComparison.OrdinalIgnoreCase) >= 0) { paddle = 2.5f; low = 4.65f; high = 8.9f; return true; }
            paddle = 3.16f; low = 5.0f; high = 9.5f;
            return name == "VikingShip";
        }
    }
}
