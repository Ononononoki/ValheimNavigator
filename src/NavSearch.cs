using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ValheimNavigator
{
    internal enum NavSurface { Land, Water, AshWater, Lava, Obstacle }

    internal struct NavPoint
    {
        internal float X, Z, Height;
        internal NavSurface Surface;
        // biome rank, meadows 1 to deep north 8, 0 for ocean or unknown. mode 0 is on foot, 1 is sailing, 2 a portal landing on a drawn route
        internal byte Rank, Mode;
        // walking speed factor here from a path or a built floor (SpeedyPaths), 0 for none
        internal float Speed;
        // where the point came from: 0 the generated world, 1 a loaded zone, 2 remembered data
        internal byte Source;
    }

    internal sealed class NavOptions
    {
        internal float Cell = 24f, Sample = 6f, MaxSlope = 0.78f, SlopePenalty = 5f;
        internal float WalkSpeed = 4f, WaterLevel = 30f, Radius = 10000f;
        // the player swims only deeper than the wade depth (Character.m_swimDepth 2). swimming drains stamina, so its time is multiplied
        internal float SwimSpeed = 2f, SwimRisk = 5f, WadeDepth = 2f, WadePenalty = 1.3f, BoilingPenalty = 10f;
        // the longest unbroken swim a route may hold, 0 for no limit. on foot a route never stops in water deeper than the wade depth
        internal float MaxSwim;
        // a steep bank up to JumpSlope is jump-run up or slid down when it is at most JumpMeters long, at JumpPenalty times the time. 0 m is off
        internal float JumpSlope = 1.19f, JumpMeters, JumpPenalty = 3f;
        // the ship, sailing speed and draft are estimates. bias below 1 prefers sailing, above 1 prefers walking
        internal bool HasShip, HeatProof;
        internal float ShipX, ShipZ, SailSpeed = 7f, ShipDraft = 1f, SailBias = 1f, SwitchSeconds = 4f;
        // without a ship to walk to, BuildBoat lets the one sailing leg start at any shore after BuildSeconds, as a player who builds a boat
        // there. MoreBoats allows further legs, each boat built at a shore: the second takes only its build time, every later one ExtraBoatSeconds more
        internal bool BuildBoat, MoreBoats;
        internal float BuildSeconds = 60f, ExtraBoatSeconds = 120f;
        internal bool Boats => HasShip || BuildBoat;
        // landmark lower bound on time from one point to another, on foot only or with sailing, null when none is ready
        internal Func<float, float, float, float, bool, float> Landmarks;
        // paths can make walking up to PathSpeed times faster, only inside the disks where they are read: the loaded area around the start
        // (PathX, PathZ) and remembered paths around the destination (PathX2, PathZ2), both of PathRadius. a radius of 0 turns a disk off
        internal float PathSpeed = 1f, PathX, PathZ, PathRadius, PathX2, PathZ2, PathRadius2;
        // true when the straight step between two points passes a wall, trunk or rock a player cannot walk through (Detailed routes)
        internal Func<NavPoint, NavPoint, bool> Crosses;

        // how much of the straight way from (x, z) to (xx, zz) lies over the path disks: that share of the time can shrink by the path speed.
        // the way must pass every distance along that line, and only the stretches the disks cover can go faster (RouteBench note, 23 Oct)
        internal float PathCredit(float x, float z, float xx, float zz)
        {
            if (PathSpeed <= 1f) return 1f;
            float dx = xx - x, dz = zz - z, d = (float)Math.Sqrt(dx * dx + dz * dz);
            if (d < 0.001f) return 1f / PathSpeed;
            float a0 = 0f, a1 = 0f, b0 = 0f, b1 = 0f;
            if (PathRadius > 0f) { float s = ((PathX - x) * dx + (PathZ - z) * dz) / d; a0 = Math.Max(0f, s - PathRadius); a1 = Math.Min(d, s + PathRadius); }
            if (PathRadius2 > 0f) { float s = ((PathX2 - x) * dx + (PathZ2 - z) * dz) / d; b0 = Math.Max(0f, s - PathRadius2); b1 = Math.Min(d, s + PathRadius2); }
            float covered = Math.Max(0f, a1 - a0) + Math.Max(0f, b1 - b0) - Math.Max(0f, Math.Min(a1, b1) - Math.Max(a0, b0));
            return 1f - Math.Min(d, covered) / d * (1f - 1f / PathSpeed);
        }

        // the most time paths can save on any route, for bounds that are not straight lines (landmarks, portals)
        internal float PathSaving => PathSpeed <= 1f ? 0f : 2f * (PathRadius + PathRadius2) / WalkSpeed * (1f - 1f / PathSpeed);
        // walking in a biome ranked above DangerRank is slowed by DangerPenalty, or blocked when DangerBlock
        internal int DangerRank = 99;
        internal float DangerPenalty = 4f;
        internal bool DangerBlock;
        // the heuristic is inflated by this weight. RouteBench audit (22 Oct): 1.5 at most 4.6 percent longer than the exact route, 1.2 with landmarks at most 1.9
        internal float Weight = 1.5f;
        internal int MaxNodes = 160000;
        internal bool Comfortable;
        // portal pairs the route may use, each way, at PortalSeconds a jump
        internal readonly List<float[]> Portals = new List<float[]>();
        internal float PortalSeconds = 3f;
        // the wind for comfortable routes: the direction it blows to, its strength 0 to 1, and the ship's measured speeds
        internal bool Wind;
        internal float WindX, WindZ = 1f, WindIntensity, ShipPaddle = 3.16f, ShipLow = 5f, ShipHigh = 9.5f;
        // the fastest travel still possible, the heuristic divides distance by it so it stays a lower bound on time
        internal float Fastest => Boats ? Math.Max(WalkSpeed, (Wind ? Math.Max(SailSpeed, ShipHigh) : SailSpeed) / SailBias) : WalkSpeed;

        // sailing speed along a heading. tail, cross and cross-tail wind are about the same, head-cross wind gives about 65 percent,
        // straight into the wind the ship tacks or paddles (Valheim wiki measurements)
        internal float SailSpeedAlong(float dx, float dz)
        {
            if (!Wind) return SailSpeed;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length < 0.001f) return SailSpeed;
            float cos = Math.Max(-1f, Math.Min(1f, (dx * WindX + dz * WindZ) / length));
            float angle = (float)(Math.Acos(cos) * 180.0 / Math.PI);
            float full = ShipLow + (ShipHigh - ShipLow) * Math.Max(0f, Math.Min(1f, (WindIntensity - 0.05f) / 0.95f));
            float upwind = Math.Max(ShipPaddle, 0.46f * full);
            if (angle <= 100f) return full;
            if (angle <= 140f) return full + (0.65f * full - full) * (angle - 100f) / 40f;
            if (angle <= 160f) return 0.65f * full + (upwind - 0.65f * full) * (angle - 140f) / 20f;
            return upwind;
        }

        private static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 }, Dz = { 0, 1, 0, -1, 1, 1, -1, -1 };
        private float[] _footFacets, _sailFacets;

        // a lower bound on the time to travel (dx, dz). moves follow the 8 grid directions, each at its best speed: walking, or sailing with
        // this route's wind and bias. the fastest mix of directions (tacking) is the gauge of the hull of those 8 velocities, the largest n.d
        // over the hull's edges n.v = 1. on foot it is the octile distance at walking speed. RouteBench: 15 percent less work, worst case 4.6 to
        // 3.0 percent with weight 1.5 (22 Oct)
        internal float Bound(float dx, float dz, bool sail)
        {
            var facets = sail ? _sailFacets ?? (_sailFacets = Facets(true)) : _footFacets ?? (_footFacets = Facets(false));
            float h = 0f;
            for (int i = 0; i < facets.Length; i += 2) h = Math.Max(h, facets[i] * dx + facets[i + 1] * dz);
            return h;
        }

        private float[] Facets(bool sail)
        {
            var vx = new float[8]; var vz = new float[8];
            for (int d = 0; d < 8; d++)
            {
                float length = d < 4 ? 1f : 1.41421356f, speed = WalkSpeed;
                if (sail) speed = Math.Max(speed, SailSpeedAlong(Dx[d], Dz[d]) / SailBias);
                vx[d] = Dx[d] / length * speed; vz[d] = Dz[d] / length * speed;
            }
            var facets = new List<float>();
            for (int i = 0; i < 8; i++)
                for (int j = i + 1; j < 8; j++)
                {
                    float det = vx[i] * vz[j] - vz[i] * vx[j];
                    if (Math.Abs(det) < 1e-6f) continue;
                    float nx = (vz[j] - vz[i]) / det, nz = (vx[i] - vx[j]) / det;
                    bool support = true;
                    for (int k = 0; k < 8 && support; k++) support = nx * vx[k] + nz * vz[k] <= 1f + 1e-4f;
                    if (support) { facets.Add(nx); facets.Add(nz); }
                }
            return facets.ToArray();
        }
    }

    // terrain samples and the edge rule shared by the kept backward tree and the forward fallback. edge costs are seconds
    internal sealed class NavTerrain
    {
        private readonly Func<float, float, NavPoint> _sample;
        internal readonly NavOptions Options;
        private readonly Dictionary<long, NavPoint> _samples = new Dictionary<long, NavPoint>();
        internal int SampleCount;

        internal NavTerrain(Func<float, float, NavPoint> sample, NavOptions options) { _sample = sample; Options = options; }

        internal static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }

        internal NavPoint Read(float x, float z)
        {
            // centimeter sample keys keep small terrain features distinct
            int ix = (int)Math.Round(x * 100f), iz = (int)Math.Round(z * 100f);
            long key = Key(ix, iz);
            if (_samples.TryGetValue(key, out var point)) return point;
            point = _sample(x, z);
            point.X = x; point.Z = z;
            SampleCount++;
            if (_samples.Count < Options.MaxNodes * 8) _samples.Add(key, point);
            return point;
        }

        internal void Clear() { _samples.Clear(); }

        internal static bool Water(NavPoint point) { return point.Surface == NavSurface.Water || point.Surface == NavSurface.AshWater; }
        internal static bool Blocked(NavPoint point) { return point.Surface == NavSurface.Lava || point.Surface == NavSurface.Obstacle || float.IsNaN(point.Height) || float.IsInfinity(point.Height); }
        internal float SurfaceHeight(NavPoint point) { return Water(point) ? Options.WaterLevel : point.Height; }

        // a point on foot in swimming water farther than half the swim limit from water you can stand in or land, which MaxSwim forbids. a
        // crossing up to the limit keeps its middle point, a longer one has none, and each step caps its own swim (test 8: a 179 m swim)
        internal bool SwimSpot(float x, float z)
        {
            if (Options.MaxSwim <= 0f) return false;
            var p = Read(x, z);
            if (!Water(p) || Depth(p) < Options.WadeDepth) return false;
            float reach = Options.MaxSwim / 2f;
            for (int d = 0; d < 8; d++)
            {
                double angle = d * Math.PI / 4.0;
                var q = Read(x + reach * (float)Math.Cos(angle), z + reach * (float)Math.Sin(angle));
                if (!Water(q) || Depth(q) < Options.WadeDepth) return false;
            }
            return true;
        }
        internal float Depth(NavPoint point) { return Water(point) ? Options.WaterLevel - point.Height : 0f; }

        // a point next to the ship, where sailing may start in shallow water. two cells, so a ship moored in a shallow harbour can still leave
        internal bool NearShip(float x, float z) { return Options.HasShip && NavSearch.Distance(x, z, Options.ShipX, Options.ShipZ) <= Options.Cell * 2f; }

        // seconds for one sampled step, infinity when the step cannot be taken in this mode
        internal float StepSeconds(NavPoint before, NavPoint next, float step, byte mode, bool nearShip)
        {
            float steep = 0f, swim = 0f;
            return StepSeconds(before, next, step, mode, nearShip, ref steep, ref swim);
        }

        // steep and swim carry the unbroken steep and swum meters from the step before, for the jump and swim limits
        internal float StepSeconds(NavPoint before, NavPoint next, float step, byte mode, bool nearShip, ref float steep, ref float swum)
        {
            if (Blocked(before) || Blocked(next)) return float.PositiveInfinity;
            if (mode != 1 && Options.Crosses != null && Options.Crosses(before, next)) return float.PositiveInfinity;
            bool wb = Water(before), wn = Water(next);
            bool boiling = before.Surface == NavSurface.AshWater || next.Surface == NavSurface.AshWater;
            if (mode == 1)
            {
                if (!nearShip && (!wb || !wn || Depth(before) < Options.ShipDraft || Depth(next) < Options.ShipDraft)) return float.PositiveInfinity;
                if (boiling && !Options.HeatProof) return float.PositiveInfinity;
                return step / Options.SailSpeedAlong(next.X - before.X, next.Z - before.Z) * Options.SailBias;
            }
            if (wb && wn)
            {
                steep = 0f;
                bool swim = Math.Max(Depth(before), Depth(next)) >= Options.WadeDepth;
                swum = swim ? swum + step : 0f;
                if (Options.MaxSwim > 0f && swum > Options.MaxSwim) return float.PositiveInfinity;
                float seconds = swim ? step / Options.SwimSpeed * Options.SwimRisk : step / Options.WalkSpeed * Options.WadePenalty;
                return boiling ? seconds * Options.BoilingPenalty : seconds;
            }
            swum = 0f;
            float rise = SurfaceHeight(next) - SurfaceHeight(before);
            float slope = step < 0.001f ? 0f : Math.Abs(rise) / step;
            // shoreline steps are checked too, a cliff above water is not a safe landing. a short bank up to the jump slope is jump-run up or
            // slid down, slowly (test 8: the route looped around the Frost Cave mountain)
            float jump = 1f;
            if (slope > Options.MaxSlope)
            {
                steep += step;
                if (slope > Options.JumpSlope || steep > Options.JumpMeters) return float.PositiveInfinity;
                jump = Options.JumpPenalty;
            }
            else steep = 0f;
            float length = (float)Math.Sqrt(step * step + rise * rise);
            // comfortable charges only climbing, going down is walked like flat ground. a path or built floor divides the time by its speed
            float penalty = (Options.Comfortable && rise > 0f ? 1f + Options.SlopePenalty * slope * slope : 1f) * jump;
            if (before.Speed > 0f || next.Speed > 0f) penalty /= 0.5f * ((before.Speed > 0f ? before.Speed : 1f) + (next.Speed > 0f ? next.Speed : 1f));
            if (next.Rank > Options.DangerRank)
            {
                if (Options.DangerBlock) return float.PositiveInfinity;
                penalty *= Options.DangerPenalty;
            }
            return length / Options.WalkSpeed * penalty;
        }

        // every edge, including the final connection, is sampled before it can enter a route. the cost is for travel from (x, z) to (xx, zz)
        internal bool Edge(float x, float z, float xx, float zz, byte mode, out float seconds, List<NavPoint> collect = null)
        {
            seconds = 0f;
            float distance = NavSearch.Distance(x, z, xx, zz);
            int steps = Math.Max(1, (int)Math.Ceiling(distance / Options.Sample));
            var before = Read(x, z);
            if (Blocked(before)) return false;
            float step = distance / steps, steep = 0f, swum = 0f;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                var next = Read(x + (xx - x) * t, z + (zz - z) * t);
                // only the steps beside the ship may sail shallow water. once this held for a whole edge when one end was near the ship, and a
                // smoothed shortcut from the ship sailed straight across land (test 6, 22 Oct)
                bool nearShip = mode == 1 && NearShip(before.X, before.Z) && NearShip(next.X, next.Z);
                float s = StepSeconds(before, next, step, mode, nearShip, ref steep, ref swum);
                if (float.IsInfinity(s)) return false;
                seconds += s;
                if (collect != null) { next.Mode = mode; collect.Add(next); }
                before = next;
            }
            return true;
        }
    }

    // a weighted A* rooted at the destination and searched backward toward the player. a state is a cell and a mode: 0 on foot before any
    // boat, 1 sailing the first boat, 2 on foot after it, 3 sailing a later boat, 4 on foot after a later boat. it is kept between route
    // updates, so a player who walks or wanders reads the route from the parents already found and the search only resumes when the player
    // leaves the explored area. the first boat is boarded at the ship's cell or built at any shore, whichever is quicker. every leg ends by walking
    // ashore, and a later boat is only built when MoreBoats allows it
    internal sealed class NavTree
    {
        internal sealed class Node
        {
            internal int X, Z;
            internal byte Mode;
            internal float G = float.MaxValue;
            internal Node Parent;
            internal bool Closed;
        }

        private struct QueueItem
        {
            internal Node Node;
            internal float F, G;
        }

        private static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 };
        private static readonly int[] Dz = { 0, 1, 0, -1, 1, 1, -1, -1 };
        internal readonly NavTerrain Terrain;
        internal readonly float GoalX, GoalZ;
        private readonly Dictionary<long, Node> _nodes = new Dictionary<long, Node>();
        private readonly List<QueueItem> _heap = new List<QueueItem>();
        private readonly int _shipX, _shipZ;
        private byte _startMode;
        private Node _current;
        private int _neighbor;
        private float _tx, _tz;
        internal int Expanded;
        internal bool Exhausted;
        // the abstraction's corridor for a long route, null searches everywhere
        internal bool[] Corridor;
        internal int NodeCount => _nodes.Count;
        internal float Weight => Terrain.Options.Weight;

        internal NavTree(float goalX, float goalZ, NavTerrain terrain)
        {
            GoalX = goalX; GoalZ = goalZ; Terrain = terrain;
            var options = terrain.Options;
            _shipX = (int)Math.Round((options.ShipX - goalX) / options.Cell); _shipZ = (int)Math.Round((options.ShipZ - goalZ) / options.Cell);
            LinkPortals();
            _tx = goalX; _tz = goalZ;
            PortalBounds();
            // the destination is reached on foot, before, after or without boats
            foreach (byte mode in new byte[] { 0, 2, 4 })
            {
                if (mode == 2 && !options.Boats || mode == 4 && !(options.Boats && options.MoreBoats)) continue;
                var root = Get(0, 0, mode);
                root.G = 0f; root.Parent = root;
                Push(root);
            }
        }

        internal float X(Node n) { return GoalX + n.X * Terrain.Options.Cell; }
        internal float Z(Node n) { return GoalZ + n.Z * Terrain.Options.Cell; }

        private Node Get(int x, int z, byte mode)
        {
            long key = NavTerrain.Key(x * 8 + mode, z);
            if (!_nodes.TryGetValue(key, out var node)) { node = new Node { X = x, Z = z, Mode = mode }; _nodes.Add(key, node); }
            return node;
        }

        // the lattice node nearest to a world point in a mode
        internal Node NodeAt(float x, float z, byte mode)
        {
            return Get((int)Math.Round((x - GoalX) / Terrain.Options.Cell), (int)Math.Round((z - GoalZ) / Terrain.Options.Cell), mode);
        }

        // a lower bound on the player's time to this node: the wind-aware bound, the landmarks' bound when ready, or through a portal when
        // that is less. a node on foot before any boat is reached on foot only, from a start on foot
        private float F(Node n, float g)
        {
            var options = Terrain.Options;
            float x = X(n), z = Z(n);
            bool sail = options.Boats && (n.Mode != 0 || _startMode != 0);
            float h = options.Bound(x - _tx, z - _tz, sail) * options.PathCredit(_tx, _tz, x, z);
            if (options.Landmarks != null) h = Math.Max(h, options.Landmarks(_tx, _tz, x, z, sail) - options.PathSaving);
            if (_portalArrive != null)
            {
                var arrive = _portalArrive[sail ? 1 : 0];
                for (int e = 0; e < arrive.Length; e++) h = Math.Min(h, arrive[e] + options.Bound(x - _portalEnds[2 * e], z - _portalEnds[2 * e + 1], sail) - options.PathSaving);
            }
            return g + Weight * h;
        }

        // portal ends as x, z pairs, the partner of end e is e ^ 1
        private float[] _portalEnds;
        // a lower bound on the player's time to step out of each portal end, through any number of jumps, on foot and with boats. one jump
        // only was counted before, so a route through a hub (bunker to hub, hub to elder2) looked too slow and Comfort sailed instead (test 9)
        private float[][] _portalArrive;

        private void PortalBounds()
        {
            var options = Terrain.Options;
            int count = options.Portals.Count * 2;
            if (count == 0) { _portalArrive = null; return; }
            _portalEnds = new float[count * 2];
            for (int i = 0; i < options.Portals.Count; i++)
                for (int k = 0; k < 4; k++) _portalEnds[4 * i + k] = options.Portals[i][k];
            _portalArrive = new float[2][];
            for (int s = 0; s < 2; s++)
            {
                bool sail = s == 1;
                var reach = new float[count];
                var arrive = new float[count];
                for (int e = 0; e < count; e++) { reach[e] = options.Bound(_portalEnds[2 * e] - _tx, _portalEnds[2 * e + 1] - _tz, sail); arrive[e] = float.PositiveInfinity; }
                for (int round = 0; round < count; round++)
                {
                    bool changed = false;
                    for (int e = 0; e < count; e++)
                    {
                        float a = reach[e ^ 1] + options.PortalSeconds;
                        if (a < arrive[e]) { arrive[e] = a; changed = true; }
                    }
                    for (int e = 0; e < count; e++)
                        for (int f = 0; f < count; f++)
                        {
                            float r = arrive[f] + options.Bound(_portalEnds[2 * e] - _portalEnds[2 * f], _portalEnds[2 * e + 1] - _portalEnds[2 * f + 1], sail);
                            if (r < reach[e]) { reach[e] = r; changed = true; }
                        }
                    if (!changed) break;
                }
                _portalArrive[s] = arrive;
            }
        }

        // the best portal lower bound from the start to a point, infinity without portals (for the route log)
        internal float PortalBound(float x, float z, bool sail)
        {
            if (_portalArrive == null) return float.PositiveInfinity;
            var arrive = _portalArrive[sail ? 1 : 0];
            float best = float.PositiveInfinity;
            for (int e = 0; e < arrive.Length; e++) best = Math.Min(best, arrive[e] + Terrain.Options.Bound(x - _portalEnds[2 * e], z - _portalEnds[2 * e + 1], sail));
            return best;
        }

        // the lattice cells of portal ends and the cells of their partners
        private readonly Dictionary<long, List<long>> _portalLinks = new Dictionary<long, List<long>>();

        private void LinkPortals()
        {
            var options = Terrain.Options;
            foreach (var p in options.Portals)
            {
                long a = NavTerrain.Key((int)Math.Round((p[0] - GoalX) / options.Cell), (int)Math.Round((p[1] - GoalZ) / options.Cell));
                long b = NavTerrain.Key((int)Math.Round((p[2] - GoalX) / options.Cell), (int)Math.Round((p[3] - GoalZ) / options.Cell));
                if (!_portalLinks.TryGetValue(a, out var la)) _portalLinks[a] = la = new List<long>();
                if (!_portalLinks.TryGetValue(b, out var lb)) _portalLinks[b] = lb = new List<long>();
                la.Add(b); lb.Add(a);
            }
        }

        private void Push(Node node)
        {
            var item = new QueueItem { Node = node, G = node.G, F = F(node, node.G) };
            int i = _heap.Count;
            _heap.Add(item);
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heap[parent].F <= item.F) break;
                _heap[i] = _heap[parent]; i = parent;
            }
            _heap[i] = item;
        }

        private Node Pop()
        {
            while (_heap.Count > 0)
            {
                var item = _heap[0];
                var last = _heap[_heap.Count - 1];
                _heap.RemoveAt(_heap.Count - 1);
                if (_heap.Count > 0)
                {
                    int i = 0;
                    while (2 * i + 1 < _heap.Count)
                    {
                        int child = 2 * i + 1;
                        if (child + 1 < _heap.Count && _heap[child + 1].F < _heap[child].F) child++;
                        if (last.F <= _heap[child].F) break;
                        _heap[i] = _heap[child]; i = child;
                    }
                    _heap[i] = last;
                }
                if (!item.Node.Closed && item.G == item.Node.G) return item.Node;
            }
            return null;
        }

        // the heuristic points at a new start, the open nodes are ordered again. the re-ordering runs inside Grow's time budget, a few
        // thousand nodes a frame, so a reroute never stalls a frame
        private List<QueueItem> _reaim;
        private int _reaimAt;

        internal void Aim(float x, float z, byte startMode)
        {
            if (Math.Abs(_tx - x) < 0.5f && Math.Abs(_tz - z) < 0.5f && _startMode == startMode) return;
            _tx = x; _tz = z; _startMode = startMode;
            PortalBounds();
            var open = new List<QueueItem>(_heap);
            if (_reaim != null) for (int i = _reaimAt; i < _reaim.Count; i++) open.Add(_reaim[i]);
            _heap.Clear();
            _reaim = open; _reaimAt = 0;
        }

        // true when the re-ordering is done, false when the time ran out first
        private bool Reaim(Stopwatch clock, float milliseconds)
        {
            if (_reaim == null) return true;
            while (_reaimAt < _reaim.Count)
            {
                var item = _reaim[_reaimAt++];
                if (!item.Node.Closed && item.G == item.Node.G) Push(item.Node);
                if ((_reaimAt & 255) == 0 && clock.Elapsed.TotalMilliseconds >= milliseconds) return false;
            }
            _reaim = null;
            return true;
        }

        private bool SwimSpot(float x, float z) { return Terrain.SwimSpot(x, z); }

        private void Relax(Node from, float cost)
        {
            if (from.Closed || float.IsInfinity(cost)) return;
            float g = _current.G + cost;
            if (g < from.G) { from.G = g; from.Parent = _current; Push(from); }
        }

        // expands nodes until the target is closed, the time is up, the nodes run out or the limit is reached. true when the target is closed.
        // the search runs backward, so each move found is a predecessor: the player travels from it to the current node
        internal bool Grow(Node target, Stopwatch clock, float milliseconds)
        {
            var options = Terrain.Options;
            if (!target.Closed && !Reaim(clock, milliseconds)) return false;
            while (!target.Closed)
            {
                if (_current == null)
                {
                    if (_nodes.Count >= options.MaxNodes) { Exhausted = true; return false; }
                    _current = Pop();
                    if (_current == null) { Exhausted = true; return false; }
                    _current.Closed = true;
                    Expanded++;
                    _neighbor = 0;
                }
                if (_neighbor < Dx.Length)
                {
                    int nx = _current.X + Dx[_neighbor], nz = _current.Z + Dz[_neighbor];
                    float wx = GoalX + nx * options.Cell, wz = GoalZ + nz * options.Cell;
                    if (wx * wx + wz * wz <= options.Radius * options.Radius && (Corridor == null || Abstraction.Inside(Corridor, wx, wz)))
                    {
                        float cx = X(_current), cz = Z(_current);
                        byte mode = _current.Mode;
                        if (mode % 2 == 0)
                        {
                            var walk = Get(nx, nz, mode);
                            if (!walk.Closed && !SwimSpot(wx, wz) && Terrain.Edge(wx, wz, cx, cz, 0, out float seconds)) Relax(walk, seconds);
                            // landing: sailing that leg's boat at the neighbor, then walking ashore onto this land cell
                            if (mode != 0 && !NavTerrain.Water(Terrain.Read(cx, cz)) && NavTerrain.Water(Terrain.Read(wx, wz)))
                            {
                                var boat = Get(nx, nz, (byte)(mode - 1));
                                if (!boat.Closed && Terrain.Edge(wx, wz, cx, cz, 0, out float ashore)) Relax(boat, ashore + options.SwitchSeconds);
                            }
                        }
                        else
                        {
                            var sail = Get(nx, nz, mode);
                            if (!sail.Closed && Terrain.Edge(wx, wz, cx, cz, 1, out float seconds)) Relax(sail, seconds);
                            // building a boat: walking from the land neighbor into this water cell and building the boat here
                            bool first = mode == 1 && options.BuildBoat, later = mode == 3 && options.MoreBoats;
                            if ((first || later) && NavTerrain.Water(Terrain.Read(cx, cz)) && !NavTerrain.Water(Terrain.Read(wx, wz)) && Terrain.Edge(wx, wz, cx, cz, 0, out float launch))
                            {
                                if (first) Relax(Get(nx, nz, 0), launch + options.BuildSeconds);
                                if (later)
                                {
                                    Relax(Get(nx, nz, 2), launch + options.BuildSeconds);
                                    Relax(Get(nx, nz, 4), launch + options.BuildSeconds + options.ExtraBoatSeconds);
                                }
                            }
                        }
                    }
                }
                else if (_current.Mode == 1 && options.HasShip && _current.X == _shipX && _current.Z == _shipZ)
                    Relax(Get(_current.X, _current.Z, 0), options.SwitchSeconds); // boarding the ship from its own cell
                else if (_current.Mode % 2 == 0 && _portalLinks.Count > 0 && _portalLinks.TryGetValue(NavTerrain.Key(_current.X, _current.Z), out var partners))
                    foreach (long key in partners) Relax(Get((int)(key >> 32), (int)(uint)key, _current.Mode), options.PortalSeconds); // walking into the partner portal lands here
                _neighbor++;
                if (_neighbor > Dx.Length) _current = null;
                if (clock.Elapsed.TotalMilliseconds >= milliseconds) return target.Closed;
            }
            return true;
        }

        // the nodes from a closed node to the destination, each point keeps the mode used to travel on from it
        internal List<NavPoint> Path(Node from)
        {
            var path = new List<NavPoint>();
            for (var node = from; ; node = node.Parent)
            {
                var point = Terrain.Read(X(node), Z(node));
                // a drawn point is on foot (0) or sailing (1), whichever boat it is
                point.Mode = (byte)(node.Mode % 2);
                path.Add(point);
                if (node.Parent == node || path.Count > 100000) break;
            }
            return path;
        }
    }

    // one route request. it reads or grows a kept backward tree from the destination, and only when the destination cannot be reached that way
    // it runs the forward search on foot from the player, which finds the closest reachable point. the search has no unity dependencies
    internal sealed class NavSearch
    {
        private sealed class Node
        {
            internal int X, Z;
            internal float G = float.MaxValue;
            internal Node Parent;
            internal bool Closed;
        }

        private struct QueueItem
        {
            internal Node Node;
            internal float F, G;
        }

        private static readonly int[] Dx = { 1, 0, -1, 0, 1, -1, -1, 1 };
        private static readonly int[] Dz = { 0, 1, 0, -1, 1, 1, -1, -1 };
        private readonly NavOptions _options;
        private readonly NavTerrain _terrain;
        private readonly NavTree _tree;
        private NavTree.Node _treeTarget;
        private readonly byte _startMode;
        private bool _forward;
        private readonly Dictionary<long, Node> _nodes = new Dictionary<long, Node>();
        private readonly List<QueueItem> _heap = new List<QueueItem>();
        private readonly float _startX, _startZ, _goalX, _goalZ;
        private readonly float _minX, _maxX, _minZ, _maxZ;
        private Node _current, _closest;
        private Node _goal;
        private float _goalScore = float.MaxValue;
        private int _neighbor;
        private List<NavPoint> _path;
        private int _pathIndex;
        private bool _reached;
        private float _closestDistance = float.MaxValue;
        private float[] _prefix;

        internal readonly List<NavPoint> Points = new List<NavPoint>();
        // the corridor held no route, the caller searches again without it
        internal bool CorridorFailed;
        internal bool Finished, Reached, Cancelled, Limited;
        internal float Length, Climb, WaterLength, AshWaterLength, Seconds;
        // sailing, swimming and harder-biome walking along the route, the longest unbroken swim, and where the first boat is boarded or built
        internal float SailLength, SwimLength, DangerLength, LongestSwim;
        // the highest biome rank walked through above the danger limit, 0 when none
        internal int DangerTop;
        internal bool Boards, FirstBuilt;
        internal float BoardX, BoardZ;
        internal int BoatCount;
        internal int Expanded;
        internal int SampleCount => _terrain.SampleCount;
        internal int Version;
        internal float WorstSlice;
        internal NavTree Tree => _tree;
        internal bool UsedTree => !_forward;
        internal NavOptions Options => _options;

        internal NavSearch(float startX, float startZ, float goalX, float goalZ, Func<float, float, NavPoint> sample, NavOptions options, byte startMode = 0)
            : this(startX, startZ, new NavTree(goalX, goalZ, new NavTerrain(sample, Validate(options))), startMode)
        {
        }

        // a request on an existing tree, the destination and the rules come from the tree. start mode 1 means the player is aboard the ship
        internal NavSearch(float startX, float startZ, NavTree tree, byte startMode = 0)
        {
            _tree = tree; _terrain = tree.Terrain; _options = _terrain.Options;
            _startX = startX; _startZ = startZ; _goalX = tree.GoalX; _goalZ = tree.GoalZ;
            _startMode = _options.Boats ? startMode : (byte)0;
            float distance = Distance(startX, startZ, _goalX, _goalZ);
            float margin = Math.Max(768f, distance * 0.5f);
            _minX = Math.Min(startX, _goalX) - margin; _maxX = Math.Max(startX, _goalX) + margin;
            _minZ = Math.Min(startZ, _goalZ) - margin; _maxZ = Math.Max(startZ, _goalZ) + margin;
            _treeTarget = tree.Exhausted ? null : tree.NodeAt(startX, startZ, _startMode);
            if (_treeTarget != null) tree.Aim(tree.X(_treeTarget), tree.Z(_treeTarget), _startMode);
            else StartForward();
        }

        private static NavOptions Validate(NavOptions options)
        {
            if (options.Cell < 1f || options.Sample < 0.5f || options.MaxNodes < 1 || options.WalkSpeed <= 0f || options.SwimSpeed <= 0f || options.SailSpeed <= 0f
                || options.SailBias <= 0f || options.Weight < 1f) throw new ArgumentOutOfRangeException(nameof(options));
            return options;
        }

        private void StartForward()
        {
            _forward = true;
            var start = new Node { X = 0, Z = 0, G = 0f };
            _nodes.Add(NavTerrain.Key(0, 0), start);
            _closest = start;
            Push(start, Distance(_startX, _startZ, _goalX, _goalZ) / _options.WalkSpeed);
        }

        internal static float Distance(float x, float z, float xx, float zz)
        {
            float dx = xx - x, dz = zz - z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        internal static NavSurface Classify(float height, float water, bool ashlands, float lava, float oceanHeat)
        {
            if (height < water) return oceanHeat >= 0f ? NavSurface.AshWater : NavSurface.Water;
            return ashlands && lava > 0.1f ? NavSurface.Lava : NavSurface.Land;
        }

        private float X(Node node) { return _startX + node.X * _options.Cell; }
        private float Z(Node node) { return _startZ + node.Z * _options.Cell; }

        private void Push(Node node, float f)
        {
            var item = new QueueItem { Node = node, G = node.G, F = f };
            int i = _heap.Count;
            _heap.Add(item);
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_heap[parent].F <= f) break;
                _heap[i] = _heap[parent]; i = parent;
            }
            _heap[i] = item;
        }

        private Node Pop()
        {
            while (_heap.Count > 0)
            {
                var item = _heap[0];
                var last = _heap[_heap.Count - 1];
                _heap.RemoveAt(_heap.Count - 1);
                if (_heap.Count > 0)
                {
                    int i = 0;
                    while (2 * i + 1 < _heap.Count)
                    {
                        int child = 2 * i + 1;
                        if (child + 1 < _heap.Count && _heap[child + 1].F < _heap[child].F) child++;
                        if (last.F <= _heap[child].F) break;
                        _heap[i] = _heap[child]; i = child;
                    }
                    _heap[i] = last;
                }
                if (!item.Node.Closed && item.G == item.Node.G) return item.Node;
            }
            return null;
        }

        internal void Cancel()
        {
            Cancelled = true; Finished = true;
            Points.Clear(); Version++;
            _heap.Clear(); _nodes.Clear();
        }

        internal void Step(float milliseconds)
        {
            if (Finished) return;
            var clock = Stopwatch.StartNew();
            if (!_forward && _path == null)
            {
                if (_tree.Grow(_treeTarget, clock, milliseconds)) PrepareFromTree();
                else if (_tree.Exhausted && _tree.Corridor != null) { CorridorFailed = true; Finished = true; return; }
                else if (_tree.Exhausted) StartForward();
                if (_path == null && !_forward) { WorstSlice = Math.Max(WorstSlice, (float)clock.Elapsed.TotalMilliseconds); return; }
            }
            do
            {
                if (_path != null) { FinishStep(); continue; }
                if (_current == null)
                {
                    _current = Pop();
                    if (_current == null) { Prepare(_goal ?? _closest, _goal != null); continue; }
                    _current.Closed = true;
                    Expanded++;
                    float distance = Distance(X(_current), Z(_current), _goalX, _goalZ);
                    if (_goal != null && _current.G + distance / _options.WalkSpeed >= _goalScore) { Prepare(_goal, true); continue; }
                    if (distance < _closestDistance) { _closest = _current; _closestDistance = distance; }
                    if (distance <= _options.Cell * 1.5f && _terrain.Edge(X(_current), Z(_current), _goalX, _goalZ, 0, out float finalScore))
                    {
                        float score = _current.G + finalScore;
                        if (score < _goalScore) { _goal = _current; _goalScore = score; }
                    }
                    if (_nodes.Count >= _options.MaxNodes) { Limited = true; Prepare(_goal ?? _closest, _goal != null); continue; }
                    _neighbor = 0;
                }
                int nx = _current.X + Dx[_neighbor], nz = _current.Z + Dz[_neighbor];
                _neighbor++;
                float wx = _startX + nx * _options.Cell, wz = _startZ + nz * _options.Cell;
                if (wx >= _minX && wx <= _maxX && wz >= _minZ && wz <= _maxZ && wx * wx + wz * wz <= _options.Radius * _options.Radius)
                {
                    long key = NavTerrain.Key(nx, nz);
                    _nodes.TryGetValue(key, out var next);
                    // the swim limit holds here too: no stop on foot in swimming water (test 8: the fallback swam 112 m)
                    if ((next == null || !next.Closed) && !_terrain.SwimSpot(wx, wz) && _terrain.Edge(X(_current), Z(_current), wx, wz, 0, out float score))
                    {
                        if (next == null) { next = new Node { X = nx, Z = nz }; _nodes.Add(key, next); }
                        float g = _current.G + score;
                        if (g < next.G)
                        {
                            next.G = g; next.Parent = _current;
                            Push(next, g + Distance(wx, wz, _goalX, _goalZ) / _options.WalkSpeed);
                        }
                    }
                }
                if (_neighbor == Dx.Length) _current = null;
            } while (!Finished && clock.Elapsed.TotalMilliseconds < milliseconds);
            WorstSlice = Math.Max(WorstSlice, (float)clock.Elapsed.TotalMilliseconds);
        }

        // the kept tree reached the player's node, the route starts at the player when the short connection to that node is passable
        private void PrepareFromTree()
        {
            Expanded = _tree.Expanded;
            var nodes = _tree.Path(_treeTarget);
            var first = nodes[0];
            if ((Math.Abs(first.X - _startX) > 0.01f || Math.Abs(first.Z - _startZ) > 0.01f) && _terrain.Edge(_startX, _startZ, first.X, first.Z, first.Mode, out _))
            {
                var start = _terrain.Read(_startX, _startZ);
                start.Mode = first.Mode;
                nodes.Insert(0, start);
            }
            BeginPath(nodes, true);
        }

        private void Prepare(Node end, bool reached)
        {
            var nodes = new List<NavPoint>();
            for (var node = end; node != null; node = node.Parent) nodes.Add(_terrain.Read(X(node), Z(node)));
            nodes.Reverse();
            if (reached && Distance(X(end), Z(end), _goalX, _goalZ) > 0.01f) nodes.Add(_terrain.Read(_goalX, _goalZ));
            BeginPath(nodes, reached);
        }

        // the mode of the move from point a to point b: sailing only between two sailing points, any landing or other move is walked
        private static byte MoveMode(NavPoint a, NavPoint b) { return a.Mode == 1 && b.Mode == 1 ? (byte)1 : (byte)0; }
        private static bool Switch(NavPoint a, NavPoint b) { return a.Mode != b.Mode && Distance(a.X, a.Z, b.X, b.Z) < 0.01f; }
        // two path nodes on foot farther apart than a grid step were joined by a portal. drawn points that arrive by portal have mode 2
        private bool Jump(NavPoint a, NavPoint b) { return a.Mode != 1 && b.Mode != 1 && Distance(a.X, a.Z, b.X, b.Z) > _options.Cell * 1.5f; }
        internal int PortalJumps;

        private void BeginPath(List<NavPoint> nodes, bool reached)
        {
            _reached = reached;
            _path = nodes;
            Points.Add(_path[0]);
            _pathIndex = 0;
            // the grid path's seconds up to each node, so a shortcut can be compared with the part it replaces
            _prefix = new float[_path.Count];
            for (int i = 1; i < _path.Count; i++)
            {
                float segment = Jump(_path[i - 1], _path[i]) ? _options.PortalSeconds : _options.SwitchSeconds;
                if (!Switch(_path[i - 1], _path[i]) && !Jump(_path[i - 1], _path[i])) _terrain.Edge(_path[i - 1].X, _path[i - 1].Z, _path[i].X, _path[i].Z, MoveMode(_path[i - 1], _path[i]), out segment);
                _prefix[i] = _prefix[i - 1] + segment;
            }
        }

        // the grid's 8 directions make zig-zags, a straight shortcut replaces them when every point between has the same mode, it stays
        // passable and costs at most 3 percent more time, so steep or rough ground keeps its detours. tried lengths halve from 48 cells
        private const int SmoothWindow = 48;
        private const float SmoothTolerance = 1.03f;

        private void FinishStep()
        {
            if (_pathIndex >= _path.Count - 1)
            {
                Reached = _reached; Finished = true;
                Summarize(); Version++;
                _heap.Clear(); _nodes.Clear();
                return;
            }
            int next = _pathIndex + 1, last = _path.Count - 1;
            var from = _path[_pathIndex];
            if (Switch(from, _path[next]))
            {
                // boarding or leaving keeps the point, only the mode changes
                Points.Add(_path[next]);
                _pathIndex = next;
                return;
            }
            if (Jump(from, _path[next]))
            {
                var landing = _path[next]; landing.Mode = 2;
                Points.Add(landing);
                _pathIndex = next;
                return;
            }
            for (int span = SmoothWindow; span >= 2; span /= 2)
            {
                int j = Math.Min(last, _pathIndex + span);
                if (j <= _pathIndex + 1) continue;
                bool same = true;
                for (int k = _pathIndex + 1; k <= j && same; k++) same = _path[k].Mode == from.Mode && !Jump(_path[k - 1], _path[k]);
                if (!same) continue;
                var b = _path[j];
                if (_terrain.Edge(from.X, from.Z, b.X, b.Z, from.Mode, out float shortcut) && shortcut <= (_prefix[j] - _prefix[_pathIndex]) * SmoothTolerance) { next = j; break; }
            }
            var to = _path[next];
            byte mode = MoveMode(from, to);
            if (!_terrain.Edge(from.X, from.Z, to.X, to.Z, mode, out _, Points)) throw new InvalidOperationException("a verified route edge became blocked");
            // the last point of the move carries the mode the route continues in
            var end = Points[Points.Count - 1]; end.Mode = to.Mode; Points[Points.Count - 1] = end;
            _pathIndex = next;
        }

        // a finer stretch from the local planner replaces the drawn points from first to last (inclusive), then the times are summed again
        internal void Replace(int first, int last, List<NavPoint> finer)
        {
            if (!Finished || Cancelled || first < 0 || last >= Points.Count || first > last || finer.Count == 0) return;
            Points.RemoveRange(first, last - first + 1);
            Points.InsertRange(first, finer);
            Summarize(); Version++;
        }

        // the part of the route behind the player is dropped. returns the distance from the player to the route
        internal float Trim(float x, float z)
        {
            if (!Finished || Cancelled || Points.Count < 2) return float.MaxValue;
            int bestSegment = 0; float best = float.MaxValue, bestT = 0f;
            for (int i = 1; i < Points.Count; i++)
            {
                var a = Points[i - 1]; var b = Points[i];
                if (b.Mode == 2) continue; // a portal jump is not a line the player walks
                float dx = b.X - a.X, dz = b.Z - a.Z, length2 = dx * dx + dz * dz;
                float t = length2 < 0.0001f ? 0f : Math.Max(0f, Math.Min(1f, ((x - a.X) * dx + (z - a.Z) * dz) / length2));
                float d = Distance(x, z, a.X + dx * t, a.Z + dz * t);
                if (d < best) { best = d; bestSegment = i; bestT = t; }
            }
            if (bestSegment > 1 || bestT > 0.05f)
            {
                var a = Points[bestSegment - 1]; var b = Points[bestSegment];
                var cut = new NavPoint { X = a.X + (b.X - a.X) * bestT, Z = a.Z + (b.Z - a.Z) * bestT, Height = a.Height + (b.Height - a.Height) * bestT, Surface = bestT < 0.5f ? a.Surface : b.Surface, Rank = b.Rank, Mode = a.Mode };
                Points.RemoveRange(0, bestSegment);
                Points.Insert(0, cut);
                Summarize(); Version++;
            }
            return best;
        }

        private void Summarize()
        {
            Length = Climb = WaterLength = AshWaterLength = Seconds = SailLength = SwimLength = DangerLength = LongestSwim = 0f;
            Boards = FirstBuilt = false; DangerTop = 0; PortalJumps = 0; BoatCount = 0;
            float swim = 0f;
            for (int i = 1; i < Points.Count; i++)
            {
                var a = Points[i - 1]; var b = Points[i];
                if (b.Mode == 2) { Seconds += _options.PortalSeconds; PortalJumps++; continue; }
                if (Switch(a, b))
                {
                    Seconds += _options.SwitchSeconds;
                    if (b.Mode == 1) { BoatCount++; if (!Boards) { Boards = true; BoardX = b.X; BoardZ = b.Z; } }
                    continue;
                }
                // a boat built at the shore: the walk into the water ends sailing
                if (a.Mode == 0 && b.Mode == 1)
                {
                    BoatCount++;
                    Seconds += _options.BuildSeconds + (BoatCount > 2 ? _options.ExtraBoatSeconds : 0f);
                    if (!Boards) { Boards = true; FirstBuilt = true; BoardX = b.X; BoardZ = b.Z; }
                }
                byte mode = MoveMode(a, b);
                float ground = Distance(a.X, a.Z, b.X, b.Z);
                float rise = _terrain.SurfaceHeight(b) - _terrain.SurfaceHeight(a);
                bool water = NavTerrain.Water(a) || NavTerrain.Water(b);
                float length = water ? ground : (float)Math.Sqrt(ground * ground + rise * rise);
                Length += length;
                if (!water) Climb += Math.Max(0f, rise);
                if (water) WaterLength += length;
                if (a.Surface == NavSurface.AshWater || b.Surface == NavSurface.AshWater) AshWaterLength += length;
                bool swimming = mode == 0 && NavTerrain.Water(a) && NavTerrain.Water(b) && Math.Max(_terrain.Depth(a), _terrain.Depth(b)) >= _options.WadeDepth;
                if (mode == 1) SailLength += length;
                if (swimming) { SwimLength += length; swim += length; LongestSwim = Math.Max(LongestSwim, swim); } else swim = 0f;
                if (mode == 0 && !water && b.Rank > _options.DangerRank) { DangerLength += length; DangerTop = Math.Max(DangerTop, b.Rank); }
                float seconds = _terrain.StepSeconds(a, b, ground, mode, mode == 1);
                Seconds += float.IsInfinity(seconds) ? length / _options.WalkSpeed : seconds;
            }
        }
    }
}
