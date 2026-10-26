using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ValheimNavigator
{
    // Explorer's visible area: places within ExplorerSenseMeters of you, or within ExplorerEdgeMeters of explored land (yours, and a
    // cartography table's when ExplorerShared is on). the edge distance is a field over the fog texels, built on a background thread
    // whenever the fog changed, at most every few seconds
    internal static class ExplorerArea
    {
        private const float RebuildSeconds = 3f, MoveMeters = 50f;
        // texels to the nearest explored texel, capped: farther than any edge margin the setting allows
        private const int Cap = 4000;
        private static ushort[] _distance, _building, _column;
        private static int _side, _builtFog = -1;
        private static bool _builtShared, _busy, _fallbackNoted;
        private static volatile bool _done;
        private static float _next;
        private static Vector3 _player, _filtered;
        private static bool _hasPlayer;
        internal static int Builds;
        internal static double LastMilliseconds;
        internal static bool Ready => _distance != null;

        // fog state 0 is explored by you, 2 explored only by others at a cartography table
        internal static bool Explored(byte state, bool shared) { return state == 0 || shared && state == 2; }

        // every frame from the plugin
        internal static void Tick()
        {
            if (!Settings.Explorer) return;
            var map = Minimap.instance;
            if (map == null) return;
            var player = Player.m_localPlayer;
            _hasPlayer = player != null;
            if (_hasPlayer)
            {
                _player = player.transform.position;
                // the sense radius moves with you, the markers are collected again after a walk of MoveMeters
                float dx = _player.x - _filtered.x, dz = _player.z - _filtered.z;
                if (dx * dx + dz * dz > MoveMeters * MoveMeters) { _filtered = _player; Layers.Dirty(); }
            }
            // the fog is read here too, so the area follows exploration when no map view reads it
            if (DetailOverlay.DataSide > 0) DetailOverlay.RefreshFog(MapAccess.Fog(map));
            if (_done)
            {
                _done = false; _busy = false;
                var kept = _distance; _distance = _building; _building = kept;
                Builds++;
                Layers.Dirty();
                if (Plugin.LogCounters.Value && (Builds <= 3 || Builds % 20 == 0))
                    Plugin.Log.LogInfo("explorer area built, " + _side + " px, " + LastMilliseconds.ToString("F0") + " ms on a background thread, shared " + _builtShared + ", build " + Builds);
            }
            bool shared = Settings.ExplorerShared.Value;
            if (_busy || !DetailOverlay.FogReady || Time.unscaledTime < _next) return;
            if (_builtFog == DetailOverlay.FogVersion && _builtShared == shared && _distance != null && _distance.Length == DetailOverlay.DataSide * DetailOverlay.DataSide) return;
            var fog = DetailOverlay.FogData;
            int side = DetailOverlay.DataSide;
            if (fog == null || side <= 0 || fog.Length != side * side) return;
            _next = Time.unscaledTime + RebuildSeconds;
            _builtFog = DetailOverlay.FogVersion; _builtShared = shared;
            if (_side != side) { _side = side; _distance = _building = _column = null; }
            // two fields are kept and swapped, so the one in use is never written
            if (_building == null) _building = new ushort[side * side];
            if (_column == null) _column = new ushort[side * side];
            _busy = true;
            var job = new Job { Fog = fog, Side = side, Shared = shared, Output = _building, Column = _column };
            ThreadPool.QueueUserWorkItem(Build, job);
        }

        private sealed class Job
        {
            internal byte[] Fog;
            internal int Side;
            internal bool Shared;
            internal ushort[] Output, Column;
        }

        private static void Build(object state)
        {
            var job = (Job)state;
            var clock = Stopwatch.StartNew();
            try { Field(job.Fog, job.Side, job.Shared, job.Column, job.Output); }
            catch (Exception ex) { Plugin.Log.LogWarning("explorer area build failed: " + ex.Message); }
            LastMilliseconds = clock.Elapsed.TotalMilliseconds;
            _done = true;
        }

        // exact distance in texels to the nearest explored texel: first along each column, then the lower envelope of parabolas along each row
        // (Felzenszwalb and Huttenlocher), capped at Cap
        internal static void Field(byte[] fog, int side, bool shared, ushort[] column, ushort[] output)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = DetailOverlay.Threads };
            Parallel.For(0, side, options, x =>
            {
                int d = Cap;
                for (int y = 0; y < side; y++) { int i = y * side + x; d = Explored(fog[i], shared) ? 0 : Math.Min(Cap, d + 1); column[i] = (ushort)d; }
                d = Cap;
                for (int y = side - 1; y >= 0; y--) { int i = y * side + x; d = Explored(fog[i], shared) ? 0 : Math.Min(Cap, d + 1); if (d < column[i]) column[i] = (ushort)d; }
            });
            Parallel.For(0, side, options, () => new Envelope(side), (y, loop, e) =>
            {
                int o = y * side, k = 0;
                for (int q = 0; q < side; q++) { double g = column[o + q]; e.F[q] = g >= Cap ? 1e12 : g * g; }
                e.V[0] = 0; e.Z[0] = double.NegativeInfinity; e.Z[1] = double.PositiveInfinity;
                for (int q = 1; q < side; q++)
                {
                    // z[0] is minus infinity, so the walk back stops at the first parabola at the latest
                    double s = Meet(e, q, e.V[k]);
                    while (s <= e.Z[k]) { k--; s = Meet(e, q, e.V[k]); }
                    k++; e.V[k] = q; e.Z[k] = s; e.Z[k + 1] = double.PositiveInfinity;
                }
                k = 0;
                for (int q = 0; q < side; q++)
                {
                    while (e.Z[k + 1] < q) k++;
                    int v = e.V[k];
                    double d = Math.Sqrt((double)(q - v) * (q - v) + e.F[v]);
                    output[o + q] = (ushort)Math.Min(Cap, (int)Math.Round(d));
                }
                return e;
            }, _ => { });
        }

        // where the parabolas of texels q and v cross
        private static double Meet(Envelope e, int q, int v) { return (e.F[q] + (double)q * q - (e.F[v] + (double)v * v)) / (2.0 * q - 2.0 * v); }

        private sealed class Envelope
        {
            internal readonly double[] F, Z;
            internal readonly int[] V;
            internal Envelope(int side) { F = new double[side]; V = new int[side]; Z = new double[side + 1]; }
        }

        // a place Explorer may show and route to. outside Explorer everything is visible
        internal static bool Visible(float x, float z)
        {
            if (!Settings.Explorer) return true;
            float sense = Settings.ExplorerSenseMeters.Value;
            if (_hasPlayer && (x - _player.x) * (x - _player.x) + (z - _player.z) * (z - _player.z) <= sense * sense) return true;
            return NearExplored(x, z, Settings.ExplorerEdgeMeters.Value);
        }

        // within meters of explored land. before the first field is built, the explored state itself
        internal static bool NearExplored(float x, float z, float meters)
        {
            var map = Minimap.instance;
            if (map == null) return true;
            var field = _distance;
            int side = _side;
            if (field != null && side > 0 && field.Length == side * side) return Within(field, side, map.m_pixelSize, x, z, meters);
            if (!_fallbackNoted && Plugin.LogCounters.Value) { _fallbackNoted = true; Plugin.Log.LogInfo("explorer area not built yet, explored land only until it is"); }
            return ExploredAt(x, z);
        }

        // the same test as NearExplored on the arrays in use now, for a route searched on a background thread (a new field is a new array,
        // the fog's bytes only change between explored states). null while neither the field nor the fog is read yet, the route then runs on
        // the game's thread as before
        internal static Func<float, float, bool> NearTest(float meters)
        {
            var map = Minimap.instance;
            if (map == null) return null;
            float pixel = map.m_pixelSize;
            var field = _distance;
            int side = _side;
            if (field != null && side > 0 && field.Length == side * side) return (x, z) => Within(field, side, pixel, x, z, meters);
            var fog = DetailOverlay.FogData;
            int fogSide = DetailOverlay.DataSide;
            bool shared = Settings.ExplorerShared.Value;
            if (DetailOverlay.FogReady && fog != null && fogSide > 0 && fog.Length == fogSide * fogSide) return (x, z) => Explored(StateAt(fog, fogSide, pixel, x, z), shared);
            return null;
        }

        // a world point's texel in the field is within meters of explored land, points off the map are not
        internal static bool Within(ushort[] field, int side, float pixel, float x, float z, float meters)
        {
            int sx = (int)(x / pixel + side / 2f), sz = (int)(z / pixel + side / 2f);
            if (sx < 0 || sz < 0 || sx >= side || sz >= side) return false;
            return field[sz * side + sx] * pixel <= meters;
        }

        // an Explorer route sails any open sea, and walks only within meters of explored land. the spots kept open (start, goal, portal
        // ends) are never blocked
        internal static bool Blocks(NavSurface surface, bool open, float x, float z, float meters)
        {
            if (surface == NavSurface.Water || surface == NavSurface.AshWater || open) return false;
            return !NearExplored(x, z, meters);
        }

        // the same rule with a test from NearTest, for a route searched on a background thread
        internal static bool BlocksWith(NavSurface surface, bool open, float x, float z, Func<float, float, bool> near)
        {
            if (surface == NavSurface.Water || surface == NavSurface.AshWater || open) return false;
            return !near(x, z);
        }

        // on the map explored by you or by a cartography table, whatever ExplorerShared says. false while the fog is not read yet
        internal static bool OnExploredMap(float x, float z)
        {
            var map = Minimap.instance;
            var fog = DetailOverlay.FogData;
            int side = DetailOverlay.DataSide;
            if (map == null || !DetailOverlay.FogReady || fog == null || side <= 0 || fog.Length != side * side) return false;
            return Explored(StateAt(fog, side, map.m_pixelSize, x, z), true);
        }

        // the fog state of a world point, 1 (unexplored) off the map
        internal static byte StateAt(byte[] fog, int side, float pixel, float x, float z)
        {
            int sx = (int)(x / pixel + side / 2f), sz = (int)(z / pixel + side / 2f);
            if (sx < 0 || sz < 0 || sx >= side || sz >= side) return 1;
            return fog[sz * side + sx];
        }

        // explored by you, or by a cartography table when ExplorerShared is on
        internal static bool ExploredAt(float x, float z)
        {
            var map = Minimap.instance;
            if (map == null) return true;
            var fog = DetailOverlay.FogData;
            int side = DetailOverlay.DataSide;
            if (DetailOverlay.FogReady && fog != null && side > 0 && fog.Length == side * side)
            {
                int sx = (int)(x / map.m_pixelSize + side / 2f), sz = (int)(z / map.m_pixelSize + side / 2f);
                if (sx < 0 || sz < 0 || sx >= side || sz >= side) return false;
                return Explored(fog[sz * side + sx], Settings.ExplorerShared.Value);
            }
            return Session.ExploredAt(x, z);
        }
    }
}
