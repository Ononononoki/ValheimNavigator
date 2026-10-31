using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ValheimNavigator
{
    // each ship's draft (keel below the water line) and beam, measured from a ship of yours floating level near you and kept in the cache
    // folder. the game floats a ship by its centre of mass and sets no draft anywhere, so it is measured. estimates until one was seen afloat
    internal static class ShipHulls
    {
        internal struct Hull { internal float Draft, Beam; internal int Samples; }

        private static readonly Dictionary<string, Hull> Estimates = new Dictionary<string, Hull>
        {
            { "Raft", new Hull { Draft = 0.6f, Beam = 3f } }, { "Karve", new Hull { Draft = 0.9f, Beam = 3.5f } },
            { "VikingShip", new Hull { Draft = 1.3f, Beam = 5f } }, { "VikingShip_Ashlands", new Hull { Draft = 1.6f, Beam = 6f } }
        };
        private static readonly Dictionary<string, Hull> Measured = new Dictionary<string, Hull>();
        // samples kept per ship kind (waves lift and drop the hull, the mean settles), and how far a measured ship may be
        private const int MaxSamples = 200, SaveEvery = 20;
        private const float MeasureMeters = 60f, Level = 0.985f, SampleSeconds = 1f;
        // a beam outside this is a measuring error (an oar, a rope, a part sticking out), dropped
        private const float MinBeam = 1f, MaxBeam = 12f;
        private static float _last;
        private static string _file;
        private static bool _loaded;
        // changes whenever a measured value moves, for the layers and routes that use it
        internal static int Version;

        // the hull of a ship prefab: measured, else estimated, else a longship's (ships of other mods, OdinShip's too, until one was measured)
        internal static Hull Of(string prefab)
        {
            Load();
            if (prefab != null && Measured.TryGetValue(prefab, out var hull) && hull.Samples >= SaveEvery) return hull;
            if (prefab != null && Estimates.TryGetValue(prefab, out hull)) return hull;
            return Estimates["VikingShip"];
        }

        // a ship of another mod not measured yet: planned with a longship's hull
        internal static bool Untested(string prefab)
        {
            Load();
            return prefab != null && !Estimates.ContainsKey(prefab) && !(Measured.TryGetValue(prefab, out var hull) && hull.Samples >= SaveEvery);
        }

        // the deepest keel of the known ships, for the sea rocks a hull can hit
        internal static float Deepest
        {
            get
            {
                Load();
                float deepest = Settings.ShipDraft.Value;
                foreach (var name in Estimates.Keys) deepest = Math.Max(deepest, Of(name).Draft);
                foreach (var pair in Measured) if (pair.Value.Samples >= SaveEvery) deepest = Math.Max(deepest, pair.Value.Draft);
                return deepest;
            }
        }

        internal static void Tick()
        {
            if (Time.unscaledTime - _last < SampleSeconds || Player.m_localPlayer == null || Ship.Instances == null) return;
            _last = Time.unscaledTime;
            Load();
            var player = Player.m_localPlayer.transform.position;
            foreach (var item in Ship.Instances)
            {
                var ship = item as Ship;
                var body = ship != null ? ship.GetComponent<Rigidbody>() : null;
                if (body == null || body.isKinematic || ship.transform.up.y < Level) continue;
                if ((ship.transform.position - player).sqrMagnitude > MeasureMeters * MeasureMeters) continue;
                try { Sample(ship); }
                catch (Exception e) { Plugin.Log.LogWarning("ship hull measure failed: " + e.Message); }
            }
        }

        private static void Sample(Ship ship)
        {
            string name = Utils.GetPrefabName(ship.gameObject);
            WaterVolume volume = null;
            float water = Floating.GetWaterLevel(ship.transform.position, ref volume);
            float bottom = float.MaxValue, left = float.MaxValue, right = float.MinValue;
            // each collider's own box turned with it (a world bounding box of a turned 20 m hull is as wide as it is long, test of 10.2.5: a
            // 27 m beam closed nearly all water)
            foreach (var collider in ship.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger || !collider.enabled) continue;
                Vector3 center, size;
                switch (collider)
                {
                    case BoxCollider box: center = box.center; size = box.size; break;
                    case MeshCollider mesh when mesh.sharedMesh != null: center = mesh.sharedMesh.bounds.center; size = mesh.sharedMesh.bounds.size; break;
                    case SphereCollider sphere: center = sphere.center; size = Vector3.one * 2f * sphere.radius; break;
                    case CapsuleCollider capsule:
                        center = capsule.center;
                        float d = 2f * capsule.radius;
                        size = capsule.direction == 0 ? new Vector3(capsule.height, d, d) : capsule.direction == 1 ? new Vector3(d, capsule.height, d) : new Vector3(d, d, capsule.height);
                        break;
                    default: continue;
                }
                for (int c = 0; c < 8; c++)
                {
                    var corner = collider.transform.TransformPoint(center + Vector3.Scale(size * 0.5f, new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f)));
                    bottom = Math.Min(bottom, corner.y);
                    float x = ship.transform.InverseTransformPoint(corner).x;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                }
            }
            if (right - left < MinBeam || right - left > MaxBeam) return;
            // a ship lying on the ground or in water shallower than its keel is not floating
            if (bottom == float.MaxValue || water - bottom <= 0f || water - bottom > 6f) return;
            float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(ship.transform.position) : float.MinValue;
            if (bottom - ground < 0.5f) return;
            Measured.TryGetValue(name, out var hull);
            int n = Math.Min(hull.Samples, MaxSamples - 1);
            hull.Draft = (hull.Draft * n + (water - bottom)) / (n + 1);
            hull.Beam = (hull.Beam * n + (right - left)) / (n + 1);
            hull.Samples = n + 1;
            Measured[name] = hull;
            if (hull.Samples % SaveEvery == 0)
            {
                Version++;
                Save();
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("ship hull " + name + ": draft " + hull.Draft.ToString("F2") + " m, beam " + hull.Beam.ToString("F1") + " m over " + hull.Samples + " samples");
            }
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                _file = Path.Combine(BepInEx.Paths.CachePath, Plugin.ModName, "ship_hulls.txt");
                if (!File.Exists(_file)) return;
                foreach (var line in File.ReadAllLines(_file))
                {
                    var parts = line.Split(' ');
                    if (parts.Length != 4) continue;
                    var read = new Hull { Draft = float.Parse(parts[1], CultureInfo.InvariantCulture), Beam = float.Parse(parts[2], CultureInfo.InvariantCulture), Samples = int.Parse(parts[3], CultureInfo.InvariantCulture) };
                    if (read.Beam >= MinBeam && read.Beam <= MaxBeam) Measured[parts[0]] = read;
                }
                Version++;
            }
            catch (Exception e) { Plugin.Log.LogWarning("ship hulls not read: " + e.Message); }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var lines = new List<string>();
                foreach (var pair in Measured)
                    lines.Add(pair.Key + " " + pair.Value.Draft.ToString("R", CultureInfo.InvariantCulture) + " " + pair.Value.Beam.ToString("R", CultureInfo.InvariantCulture) + " " + pair.Value.Samples.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(_file, lines);
            }
            catch (Exception e) { Plugin.Log.LogWarning("ship hulls not saved: " + e.Message); }
        }
    }
}
