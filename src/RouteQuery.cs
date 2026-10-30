using System;
using System.Collections.Generic;

namespace ValheimNavigator
{
    // a route over the zone graphs (HPA*): the start, the goal, the portal ends and one's own ship are joined to their zone's nodes by a
    // search inside the zone, an A* runs over the nodes in phases (0 on foot, 1 sailing one boat, 2 on foot after landing), then each hop is
    // refined at 2 m inside its zone and each leg straightened where the straight way costs no more (the route lab's method, 30 Oct 2026)
    internal sealed class RouteQuery
    {
        internal sealed class Request
        {
            internal float StartX, StartZ, GoalX, GoalZ;
            internal bool Comfort;
            // one boat: built at any shore (BuildBoat), or one's own ship at ShipX, ShipZ (HasShip)
            internal bool BuildBoat, HasShip;
            internal float ShipX, ShipZ;
            // portal pairs, each as x, z, x2, z2, used both ways
            internal readonly List<float[]> Portals = new List<float[]>();
            internal float PortalSeconds = 3f, SwitchSeconds = 4f, BuildSeconds = 60f;
            // spots walked flat within OpenMeters (portals and beds stand on floors over uneven ground): the start, the goal and the portal ends
            internal float OpenMeters = 26f;
            // a zone the route may enter (Explorer), a cost factor on foot in a zone (harder biomes in Comfort, +infinity to keep out)
            internal Func<int, int, bool> Allowed;
            internal Func<int, int, float> FootFactor;
            internal int MaxExpanded = 400000;
            // a second boat after the first landing, built at any shore (MoreBoats)
            internal bool MoreBoats;
            // the ship sails the boiling Ashlands sea (a drakkar)
            internal bool HeatProof;
            // the sailing speed along a direction (wind, the ship), null for the speed the zones were built with. FastestSail bounds it
            internal Func<float, float, float> Sail;
            internal float FastestSail;
            // true stops the query (the route was replaced), the result is then not reached
            internal Func<bool> Stop;
        }

        internal sealed class Result
        {
            internal bool Reached;
            internal float Seconds;
            internal int Expanded, Zones;
            internal bool Stopped;
            // how many nodes lead to one's own ship, and how many sea nodes its spot reaches (for the logs)
            internal int ShipIn, ShipOut;
            // nodes expanded in each phase (on foot, sailing, on foot after landing, the second boat, on foot after it)
            internal readonly int[] PhaseExpanded = new int[Phases];
            // the route, mode 0 on foot, 1 sailing, 2 the landing of a portal jump
            internal List<NavPoint> Points = new List<NavPoint>();
        }

        private readonly Func<int, int, ZoneGraph> _zones;
        private readonly Func<int, int, NavPoint> _sample;
        private readonly ZoneGraph.Rules _rules;
        // kept from query to query: the zones' ground points (checked by the zone's signature) and the portal ends' searches
        private readonly Dictionary<long, (ulong signature, NavPoint[] points)> _points = new Dictionary<long, (ulong, NavPoint[])>();
        private readonly Dictionary<(float, float, bool, bool), (ulong signature, Local local)> _spotSearches = new Dictionary<(float, float, bool, bool), (ulong, Local)>();
        private const int SpotSearchCache = 512;
        private readonly HashSet<long> _checked = new HashSet<long>();

        // the zones a query reads, each asked of the store once a query (the store locks on every read)
        private readonly Dictionary<long, ZoneGraph> _zoneCache = new Dictionary<long, ZoneGraph>();
        private ZoneGraph Zone(int zx, int zz)
        {
            long key = ZoneKey(zx, zz);
            if (!_zoneCache.TryGetValue(key, out var g)) { g = _zones(zx, zz); _zoneCache[key] = g; }
            return g;
        }
        private const int PointCacheZones = 1024;
        private const long Virtual = 1L << 40, VStart = Virtual, VGoal = Virtual + 1, VShipFoot = Virtual + 2, VShipSea = Virtual + 3, VPortal = Virtual + 10;

        internal RouteQuery(Func<int, int, ZoneGraph> zones, Func<int, int, NavPoint> sample, ZoneGraph.Rules rules) { _zones = zones; _sample = sample; _rules = rules; }

        // phases of a route: 0 on foot, 1 sailing, 2 on foot after landing, 3 a second boat, 4 on foot after it. odd phases sail
        internal const int Phases = 5;

        internal static int ZoneOf(float v) => (int)Math.Floor((v + ZoneGraph.Meters / 2f) / ZoneGraph.Meters);
        private static long ZoneKey(int zx, int zz) => ((long)(zx + 1024) << 11) | (uint)(zz + 1024);
        private static long NodeKey(int zx, int zz, int k) => ZoneKey(zx, zz) << 12 | (uint)k;
        private static void Unpack(long node, out int zx, out int zz, out int k) { k = (int)(node & 4095); long z = node >> 12; zz = (int)(z & 2047) - 1024; zx = (int)(z >> 11) - 1024; }

        private static int LocalOf(int zx, int zz, float x, float z)
        {
            int i = (int)Math.Round((x - ZoneGraph.OriginX(zx)) / ZoneGraph.Step), j = (int)Math.Round((z - ZoneGraph.OriginZ(zz)) / ZoneGraph.Step);
            i = Math.Max(0, Math.Min(ZoneGraph.Size - 1, i)); j = Math.Max(0, Math.Min(ZoneGraph.Size - 1, j));
            return j * ZoneGraph.Size + i;
        }

        private static float WorldX(int zx, int local) => ZoneGraph.OriginX(zx) + local % ZoneGraph.Size * ZoneGraph.Step;
        private static float WorldZ(int zz, int local) => ZoneGraph.OriginZ(zz) + local / ZoneGraph.Size * ZoneGraph.Step;

        // the zone's 32 x 32 ground points, kept for the refinement and the straightening
        private NavPoint[] Points(int zx, int zz)
        {
            long key = ZoneKey(zx, zz);
            // a kept zone is checked against its signature once a query
            bool have = _points.TryGetValue(key, out var kept);
            if (have && _checked.Contains(key)) return kept.points;
            var zone = Zone(zx, zz);
            ulong signature = zone != null ? zone.Signature ^ 0x9E3779B97F4A7C15UL : 0UL;
            _checked.Add(key);
            if (have && kept.signature == signature) return kept.points;
            if (_points.Count >= PointCacheZones) _points.Clear();
            var p = new NavPoint[ZoneGraph.Size * ZoneGraph.Size];
            int x0 = ZoneGraph.OriginX(zx), z0 = ZoneGraph.OriginZ(zz);
            for (int j = 0; j < ZoneGraph.Size; j++)
                for (int i = 0; i < ZoneGraph.Size; i++)
                {
                    var q = _sample(x0 + i * ZoneGraph.Step, z0 + j * ZoneGraph.Step);
                    q.X = x0 + i * ZoneGraph.Step; q.Z = z0 + j * ZoneGraph.Step;
                    p[j * ZoneGraph.Size + i] = q;
                }
            _points[key] = (signature, p);
            return p;
        }

        private NavPoint PointAt(float x, float z)
        {
            int zx = ZoneOf(x), zz = ZoneOf(z);
            var p = Points(zx, zz)[LocalOf(zx, zz, x, z)];
            p.X = x; p.Z = z;
            return p;
        }

        // the sailing speed along (dx, dz), and the factor on a sailing time the zones were built with
        private float SailAlong(float dx, float dz) => _q.Sail != null ? Math.Max(0.1f, _q.Sail(dx, dz)) : _rules.Shortest.Options.SailSpeed;
        private float SeaFactor(float dx, float dz) => _q.Sail != null ? _rules.Shortest.Options.SailSpeed / SailAlong(dx, dz) : 1f;
        private static readonly float[] SideX = { 1f, 0f, -1f, 0f }, SideZ = { 0f, 1f, 0f, -1f };

        private bool Sailable(NavPoint p) => (p.Surface == NavSurface.Water || p.Surface == NavSurface.AshWater && _q.HeatProof) && _rules.Water - p.Height >= _rules.Draft;

        // ---- a search inside one zone ----

        private Request _q;
        private List<(float x, float z)> _open;

        private float FootStep(NavPoint a, NavPoint b, float d, bool comfort)
        {
            foreach (var (x, z) in _near ?? _open)
                if ((a.X - x) * (a.X - x) + (a.Z - z) * (a.Z - z) <= _q.OpenMeters * _q.OpenMeters || (b.X - x) * (b.X - x) + (b.Z - z) * (b.Z - z) <= _q.OpenMeters * _q.OpenMeters)
                    return Blocked(a) || Blocked(b) ? float.PositiveInfinity : d / _rules.Shortest.Options.WalkSpeed;
            return (comfort ? _rules.Comfort : _rules.Shortest).StepSeconds(a, b, d, 0, false);
        }

        private static bool Blocked(NavPoint p) => p.Surface == NavSurface.Obstacle || p.Surface == NavSurface.Lava || float.IsNaN(p.Height);

        // costs from a point (reverse: to it) over one layer of a block of zones (the zone alone, or with its eight neighbours for a spot near a
        // zone border), with the parent of each point toward the origin
        private sealed class Local
        {
            internal int Bzx, Bzz, Span, Layer;
            internal bool Reverse;
            internal float[] Cost;
            internal int[] Parent;
            internal int N => Span * ZoneGraph.Size;
            internal bool Holds(int zx, int zz) => zx >= Bzx && zz >= Bzz && zx < Bzx + Span && zz < Bzz + Span;
            internal int Index(int zx, int zz, int local) => ((zz - Bzz) * ZoneGraph.Size + local / ZoneGraph.Size) * N + (zx - Bzx) * ZoneGraph.Size + local % ZoneGraph.Size;
            internal float X(int index) => ZoneGraph.OriginX(Bzx) + index % N * ZoneGraph.Step;
            internal float Z(int index) => ZoneGraph.OriginZ(Bzz) + index / N * ZoneGraph.Step;
        }

        // reach 1: the zone and its eight neighbours, 0: the zone alone. target: a point of the zone (zx, zz) to stop at
        private Local Search(int zx, int zz, int reach, int from, int layer, bool reverse, int target = -1)
        {
            var l = new Local { Bzx = zx - reach, Bzz = zz - reach, Span = 2 * reach + 1, Layer = layer, Reverse = reverse };
            // only the open spots near the block count for its steps
            float ox0 = ZoneGraph.OriginX(l.Bzx) - _q.OpenMeters, oz0 = ZoneGraph.OriginZ(l.Bzz) - _q.OpenMeters, ox1 = ZoneGraph.OriginX(l.Bzx + l.Span) + _q.OpenMeters, oz1 = ZoneGraph.OriginZ(l.Bzz + l.Span) + _q.OpenMeters;
            _near = new List<(float, float)>();
            foreach (var (x, z) in _open) if (x >= ox0 && x <= ox1 && z >= oz0 && z <= oz1) _near.Add((x, z));
            int n = l.N;
            l.Cost = new float[n * n]; l.Parent = new int[n * n];
            var points = new NavPoint[l.Span * l.Span][]; var graphs = new ZoneGraph[l.Span * l.Span];
            for (int bz = 0; bz < l.Span; bz++)
                for (int bx = 0; bx < l.Span; bx++) { points[bz * l.Span + bx] = Points(l.Bzx + bx, l.Bzz + bz); graphs[bz * l.Span + bx] = Zone(l.Bzx + bx, l.Bzz + bz); }
            NavPoint P(int index) { int i = index % n, j = index / n; return points[j / ZoneGraph.Size * l.Span + i / ZoneGraph.Size][j % ZoneGraph.Size * ZoneGraph.Size + i % ZoneGraph.Size]; }
            bool NoSwim(int index) { int i = index % n, j = index / n; var g = graphs[j / ZoneGraph.Size * l.Span + i / ZoneGraph.Size]; return g != null && g.Blocked(j % ZoneGraph.Size * ZoneGraph.Size + i % ZoneGraph.Size); }
            for (int k = 0; k < l.Cost.Length; k++) { l.Cost[k] = float.PositiveInfinity; l.Parent[k] = -1; }
            int start = l.Index(zx, zz, from), stop = target >= 0 ? l.Index(zx, zz, target) : -1;
            l.Cost[start] = 0f;
            var heap = new MinHeap();
            heap.Push(0f, start);
            bool comfort = _q.Comfort;
            while (heap.Count > 0)
            {
                var (d, c) = heap.Pop();
                if (d > l.Cost[c]) continue;
                if (c == stop) break;
                int ci = c % n, cj = c / n;
                for (int dir = 0; dir < 8; dir++)
                {
                    int ni = ci + ZoneGraph.Di[dir], nj = cj + ZoneGraph.Dj[dir];
                    if (ni < 0 || nj < 0 || ni >= n || nj >= n) continue;
                    int m = nj * n + ni;
                    float step = dir < 4 ? ZoneGraph.Step : ZoneGraph.Step * 1.41421356f, s;
                    if (layer == 1) { if (!Sailable(P(m)) && m != stop) continue; s = step / SailAlong(ZoneGraph.Di[dir], ZoneGraph.Dj[dir]); }
                    else
                    {
                        if (NoSwim(m) || NoSwim(c)) continue;
                        s = reverse ? FootStep(P(m), P(c), step, comfort) : FootStep(P(c), P(m), step, comfort);
                    }
                    if (float.IsInfinity(s)) continue;
                    float nd = d + s;
                    if (nd >= l.Cost[m]) continue;
                    l.Cost[m] = nd; l.Parent[m] = c; heap.Push(nd, m);
                }
            }
            _near = null;
            return l;
        }

        private List<(float, float)> _near;

        // a portal end's search, kept while the zones of its block keep their signature and no start, goal or ship lies near it. a portal end
        // well inside its zone searches that zone alone
        private Local SpotSearch(float x, float z, int zx, int zz, int local, bool reverse)
        {
            float bx = x - ZoneGraph.OriginX(zx), bz = z - ZoneGraph.OriginZ(zz), edge = Math.Min(Math.Min(bx, ZoneGraph.Meters - bx), Math.Min(bz, ZoneGraph.Meters - bz));
            int reach = edge < _q.OpenMeters + 4f ? 1 : 0;
            ulong signature = 1469598103934665603UL;
            unchecked
            {
                for (int dz = -reach; dz <= reach; dz++)
                    for (int dx = -reach; dx <= reach; dx++) { var g = Zone(zx + dx, zz + dz); signature = (signature ^ (g != null ? g.Signature + 1UL : 0UL)) * 1099511628211UL; }
            }
            float span = (reach + 0.5f) * ZoneGraph.Meters + _q.OpenMeters * 2f;
            bool Near(float px, float pz) => Math.Abs(px - x) < span && Math.Abs(pz - z) < span;
            bool clean = !Near(_q.StartX, _q.StartZ) && !Near(_q.GoalX, _q.GoalZ) && !(_q.HasShip && Near(_q.ShipX, _q.ShipZ));
            var key = (x, z, reverse, _q.Comfort);
            if (clean && _spotSearches.TryGetValue(key, out var kept) && kept.signature == signature) return kept.local;
            var l = Search(zx, zz, reach, local, 0, reverse);
            if (clean) { if (_spotSearches.Count >= SpotSearchCache) _spotSearches.Clear(); _spotSearches[key] = (signature, l); }
            return l;
        }

        // the cost of the search at a point of a zone, +infinity outside its block
        private static float CostAt(Local l, int zx, int zz, int local) => l.Holds(zx, zz) ? l.Cost[l.Index(zx, zz, local)] : float.PositiveInfinity;

        // the points (block indices) from the search's origin to a point of a zone (reverse: from the point to the origin)
        private static List<int> Trace(Local l, int zx, int zz, int local)
        {
            if (!l.Holds(zx, zz)) return null;
            int to = l.Index(zx, zz, local);
            if (float.IsInfinity(l.Cost[to])) return null;
            var cells = new List<int>();
            for (int c = to; c != -1; c = l.Parent[c]) cells.Add(c);
            if (!l.Reverse) cells.Reverse();
            return cells;
        }

        // ---- the query ----

        private sealed class Spot { internal long Id; internal int Zx, Zz, Local; internal float X, Z; internal Local Out, In; }

        internal Result Find(Request q)
        {
            _q = q;
            _checked.Clear(); _zoneCache.Clear();
            var result = new Result();
            _open = new List<(float, float)> { (q.StartX, q.StartZ), (q.GoalX, q.GoalZ) };
            foreach (var p in q.Portals) { _open.Add((p[0], p[1])); _open.Add((p[2], p[3])); }
            // a ship lies at a dock or a shore, the way onto it is kept open like a portal's
            if (q.HasShip) _open.Add((q.ShipX, q.ShipZ));
            var spots = new Dictionary<long, Spot>();
            Spot Make(long id, float x, float z)
            {
                int zx = ZoneOf(x), zz = ZoneOf(z);
                var s = new Spot { Id = id, Zx = zx, Zz = zz, Local = LocalOf(zx, zz, x, z), X = x, Z = z };
                spots[id] = s;
                return s;
            }
            // each spot is joined to the nodes of its zone and the eight around it (a spot near a zone border reaches across it)
            const int reach = 1;
            var start = Make(VStart, q.StartX, q.StartZ); start.Out = Search(start.Zx, start.Zz, reach, start.Local, 0, false);
            var goal = Make(VGoal, q.GoalX, q.GoalZ); goal.In = Search(goal.Zx, goal.Zz, reach, goal.Local, 0, true);
            for (int i = 0; i < q.Portals.Count * 2; i++)
            {
                var p = q.Portals[i / 2];
                var s = Make(VPortal + i, i % 2 == 0 ? p[0] : p[2], i % 2 == 0 ? p[1] : p[3]);
                s.Out = SpotSearch(s.X, s.Z, s.Zx, s.Zz, s.Local, false); s.In = SpotSearch(s.X, s.Z, s.Zx, s.Zz, s.Local, true);
            }
            Spot shipFoot = null, shipSea = null;
            if (q.HasShip)
            {
                shipFoot = Make(VShipFoot, q.ShipX, q.ShipZ); shipFoot.In = Search(shipFoot.Zx, shipFoot.Zz, reach, shipFoot.Local, 0, true);
                shipSea = Make(VShipSea, q.ShipX, q.ShipZ); shipSea.Out = Search(shipSea.Zx, shipSea.Zz, reach, shipSea.Local, 1, false);
            }
            // the zones of a spot's block with the nodes on its layer
            IEnumerable<(int zx, int zz, ZoneGraph g)> Block(Local l)
            {
                for (int bz = 0; bz < l.Span; bz++)
                    for (int bx = 0; bx < l.Span; bx++)
                    {
                        var g = Zone(l.Bzx + bx, l.Bzz + bz);
                        if (g != null) yield return (l.Bzx + bx, l.Bzz + bz, g);
                    }
            }
            // nodes that lead into a spot (the goal, a portal end, the ship), with the cost
            var into = new Dictionary<long, List<(long spot, float cost)>>();
            foreach (var s in spots.Values)
            {
                if (s.In == null) continue;
                foreach (var (zx, zz, g) in Block(s.In))
                    for (int k = 0; k < g.Cells.Length; k++)
                    {
                        float c = CostAt(s.In, zx, zz, g.Cells[k]);
                        if (g.Layers[k] != 0 || float.IsInfinity(c)) continue;
                        long key = NodeKey(zx, zz, k);
                        if (!into.TryGetValue(key, out var list)) into[key] = list = new List<(long, float)>();
                        list.Add((s.Id, c));
                        if (s.Id == VShipFoot) result.ShipIn++;
                    }
            }
            if (shipSea != null)
                foreach (var (zx, zz, g) in Block(shipSea.Out))
                    for (int k = 0; k < g.Cells.Length; k++) if (g.Layers[k] == 1 && !float.IsInfinity(CostAt(shipSea.Out, zx, zz, g.Cells[k]))) result.ShipOut++;

            bool boats = q.BuildBoat || q.HasShip;
            float fastest = Math.Max(_rules.Shortest.Options.WalkSpeed, boats ? (q.Sail != null ? q.FastestSail : _rules.Shortest.Options.SailSpeed) : 0f);
            float Straight(float x, float z, float xx, float zz) => (float)Math.Sqrt((x - xx) * (x - xx) + (z - zz) * (z - zz)) / fastest;
            float H(float x, float z)
            {
                float h = Straight(x, z, q.GoalX, q.GoalZ);
                foreach (var p in q.Portals)
                    h = Math.Min(h, Math.Min(Straight(x, z, p[0], p[1]) + q.PortalSeconds + Straight(p[2], p[3], q.GoalX, q.GoalZ), Straight(x, z, p[2], p[3]) + q.PortalSeconds + Straight(p[0], p[1], q.GoalX, q.GoalZ)));
                return h;
            }
            void Position(long node, out float x, out float z)
            {
                if (node >= Virtual) { var s = spots[node]; x = s.X; z = s.Z; return; }
                Unpack(node, out int zx, out int zz, out int k);
                var g = Zone(zx, zz);
                x = WorldX(zx, g.Cells[k]); z = WorldZ(zz, g.Cells[k]);
            }
            var cost = new Dictionary<long, float>();
            var parent = new Dictionary<long, long>();
            var open = new List<(float f, long s)>();
            void Push(float f, long s) { open.Add((f, s)); int i = open.Count - 1; while (i > 0) { int p = (i - 1) / 2; if (open[p].f <= open[i].f) break; (open[p], open[i]) = (open[i], open[p]); i = p; } }
            (float f, long s) Pop()
            {
                var top = open[0]; var last = open[open.Count - 1]; open.RemoveAt(open.Count - 1);
                if (open.Count > 0) { open[0] = last; int i = 0; while (true) { int l = 2 * i + 1, r = l + 1, m = i; if (l < open.Count && open[l].f < open[m].f) m = l; if (r < open.Count && open[r].f < open[m].f) m = r; if (m == i) break; (open[m], open[i]) = (open[i], open[m]); i = m; } }
                return top;
            }
            long State(long node, int phase) => node * Phases + phase;
            void Relax(long node, int phase, float c, long from)
            {
                if (float.IsInfinity(c) || float.IsNaN(c)) return;
                long s = State(node, phase);
                if (cost.TryGetValue(s, out float old) && c >= old) return;
                cost[s] = c; parent[s] = from;
                Position(node, out float x, out float z);
                Push(c + H(x, z), s);
            }
            bool Enter(int zx, int zz) => q.Allowed == null || q.Allowed(zx, zz);
            float Factor(int zx, int zz) => q.FootFactor == null ? 1f : q.FootFactor(zx, zz);
            // a spot's links to the nodes of its block, and straight to other spots inside the block
            void FromSpot(Spot s, int phase, float g0, long from)
            {
                var l = s.Out;
                if (l == null) return;
                foreach (var (zx, zz, z) in Block(l))
                    for (int k = 0; k < z.Cells.Length; k++)
                    {
                        float c = CostAt(l, zx, zz, z.Cells[k]);
                        if (z.Layers[k] == l.Layer && !float.IsInfinity(c)) Relax(NodeKey(zx, zz, k), phase, g0 + c * (l.Layer == 0 ? Factor(zx, zz) : 1f), from);
                    }
                if (l.Layer != 0) return;
                foreach (var t in spots.Values)
                {
                    if (t == s || t.In == null || t.Id == VShipFoot && phase != 0) continue;
                    float c = CostAt(l, t.Zx, t.Zz, t.Local);
                    if (!float.IsInfinity(c)) Relax(t.Id, phase, g0 + c * Factor(s.Zx, s.Zz), from);
                }
            }
            Relax(VStart, 0, 0f, -1);
            float best = float.PositiveInfinity; long bestState = -1;
            while (open.Count > 0 && result.Expanded < q.MaxExpanded)
            {
                if ((result.Expanded & 511) == 0 && q.Stop != null && q.Stop()) { result.Stopped = true; return result; }
                var (f, s) = Pop();
                if (f >= best) break;
                float g0 = cost[s];
                long node = s / Phases; int phase = (int)(s % Phases);
                Position(node, out float px, out float pz);
                if (f > g0 + H(px, pz) + 1e-3f) continue;
                result.Expanded++; result.PhaseExpanded[phase]++;
                if (node == VGoal) { best = g0; bestState = s; break; }
                if (node >= Virtual)
                {
                    var spot = spots[node];
                    if (node == VShipFoot) Relax(VShipSea, 1, g0 + q.SwitchSeconds, s);
                    else if (node == VShipSea) FromSpot(spot, 1, g0, s);
                    else
                    {
                        if (node >= VPortal && phase % 2 == 0) Relax(VPortal + ((node - VPortal) ^ 1), phase, g0 + q.PortalSeconds, s);
                        if (node != VGoal) FromSpot(spot, phase, g0, s);
                    }
                    continue;
                }
                Unpack(node, out int zx, out int zz, out int k);
                var z = Zone(zx, zz);
                int layer = z.Layers[k];
                foreach (int e in z.Out(k))
                {
                    float c = layer == 0 ? (q.Comfort ? z.Comfort[e] : z.Shortest[e]) * Factor(zx, zz)
                        : (q.HeatProof ? z.Comfort[e] : z.Shortest[e]) * SeaFactor(WorldX(zx, z.Cells[z.To[e]]) - WorldX(zx, z.Cells[k]), WorldZ(zz, z.Cells[z.To[e]]) - WorldZ(zz, z.Cells[k]));
                    Relax(NodeKey(zx, zz, z.To[e]), phase, g0 + c, s);
                }
                foreach (int e in z.Crossings(k))
                {
                    int side = z.CrossSide[e];
                    ZoneGraph.Next(zx, zz, side, out int nx, out int nz);
                    // Explorer keeps the walk on explored land, the sea is sailed anywhere
                    if (layer == 0 && !Enter(nx, nz)) continue;
                    var n = Zone(nx, nz);
                    if (n == null) continue;
                    int nk = n.NodeAt(ZoneGraph.Across(z.Cells[k], side), layer);
                    if (nk < 0) continue;
                    float c = layer == 0 ? (q.Comfort ? z.CrossOutComfort[e] : z.CrossOut[e]) * Factor(nx, nz) : (q.HeatProof ? z.CrossOutComfort[e] : z.CrossOut[e]) * SeaFactor(SideX[side], SideZ[side]);
                    Relax(NodeKey(nx, nz, nk), phase, g0 + c, s);
                }
                if (layer == 1)
                {
                    int shore = z.ShoreOfSea(k);
                    if (shore >= 0 && Enter(zx, zz)) Relax(NodeKey(zx, zz, z.ShoreFoot[shore]), phase + 1, g0 + q.SwitchSeconds, s);
                }
                else
                {
                    int shore = z.ShoreOfFoot(k);
                    if (shore >= 0 && (phase == 0 && q.BuildBoat || phase == 2 && q.MoreBoats)) Relax(NodeKey(zx, zz, z.ShoreSea[shore]), phase + 1, g0 + q.BuildSeconds + q.SwitchSeconds, s);
                    if (into.TryGetValue(node, out var links))
                        foreach (var (spot, c) in links)
                            if (spot != VShipFoot || phase == 0) Relax(spot, phase, g0 + c * Factor(zx, zz), s);
                }
            }
            if (bestState < 0) return result;
            result.Reached = true; result.Seconds = best;
            var states = new List<long>();
            for (long st = bestState; st != -1; st = parent[st]) states.Add(st);
            states.Reverse();
            Refine(states, spots, result);
            if (result.Stopped) result.Reached = false;
            return result;
        }

        // ---- refinement: each hop at 2 m inside its zone, then each leg straightened ----

        private void Refine(List<long> states, Dictionary<long, Spot> spots, Result result)
        {
            var points = new List<NavPoint>();
            var zones = new HashSet<long>();
            void Add(float x, float z, byte mode) { var p = PointAt(x, z); p.X = x; p.Z = z; p.Mode = mode; points.Add(p); zones.Add(ZoneKey(ZoneOf(x), ZoneOf(z))); }
            void AddCells(Local l, List<int> cells, byte mode, int skip) { if (cells == null) return; for (int i = skip; i < cells.Count; i++) Add(l.X(cells[i]), l.Z(cells[i]), mode); }
            Add(_q.StartX, _q.StartZ, 0);
            for (int h = 1; h < states.Count; h++)
            {
                if (_q.Stop != null && _q.Stop()) { result.Stopped = true; return; }
                long a = states[h - 1] / Phases, b = states[h] / Phases; int pa = (int)(states[h - 1] % Phases), pb = (int)(states[h] % Phases);
                byte mode = (byte)(pb % 2 == 1 ? 1 : 0);
                if (a >= VPortal && b >= VPortal) { var t = spots[b]; Add(t.X, t.Z, 2); continue; }
                // the ship is boarded where it lies: the walk ends there, the sailing starts at the same point
                if (a == VShipFoot && b == VShipSea) { Add(_q.ShipX, _q.ShipZ, 0); Add(_q.ShipX, _q.ShipZ, 1); continue; }
                if (a >= Virtual && b >= Virtual)
                {
                    var s = spots[a]; var t = spots[b];
                    AddCells(s.Out, Trace(s.Out, t.Zx, t.Zz, t.Local), mode, 1);
                    if (b == VGoal || b >= VPortal) Add(t.X, t.Z, mode);
                    continue;
                }
                if (a >= Virtual)
                {
                    var s = spots[a]; Unpack(b, out int bzx, out int bzz, out int bk);
                    AddCells(s.Out, Trace(s.Out, bzx, bzz, Zone(bzx, bzz).Cells[bk]), mode, 1);
                    continue;
                }
                Unpack(a, out int zx, out int zz, out int k);
                var z = Zone(zx, zz);
                if (b >= Virtual)
                {
                    var t = spots[b];
                    AddCells(t.In, Trace(t.In, zx, zz, z.Cells[k]), mode, 1);
                    if (b == VGoal || b >= VPortal) Add(t.X, t.Z, mode);
                    continue;
                }
                Unpack(b, out int nzx, out int nzz, out int nk);
                var n = Zone(nzx, nzz);
                if (pa != pb || nzx != zx || nzz != zz) { Add(WorldX(nzx, n.Cells[nk]), WorldZ(nzz, n.Cells[nk]), mode); continue; }
                var l = Search(zx, zz, 0, z.Cells[k], z.Layers[k], false, n.Cells[nk]);
                AddCells(l, Trace(l, zx, zz, n.Cells[nk]), mode, 1);
            }
            result.Points = Straighten(points);
            result.Zones = zones.Count;
            result.Seconds = Price(result.Points);
            // a straightened stretch that prices as blocked keeps the refined path instead
            if (float.IsInfinity(result.Seconds)) { result.Points = points; result.Seconds = Price(points); }
            result.Points = Densify(result.Points);
        }

        // the drawn route: points every DrawMeters along each straight stretch, each with its own ground, so the line shows where it walks,
        // wades or climbs (a long stretch took the look of its two ends)
        private const float DrawMeters = 4f;

        private List<NavPoint> Densify(List<NavPoint> path)
        {
            var result = new List<NavPoint>(path.Count * 4);
            if (path.Count > 0) result.Add(path[0]);
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1]; var b = path[i];
                float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
                if (b.Mode != 2 && d > DrawMeters)
                {
                    // on every second point of the 2 m checks of Walk and SailClear, so the drawn route has the checked ground
                    int steps = (int)Math.Ceiling(d / ZoneGraph.Step);
                    byte mode = (byte)(a.Mode == 1 && b.Mode == 1 ? 1 : 0);
                    for (int s = 2; s < steps; s += 2)
                    {
                        float t = (float)s / steps;
                        var p = Between(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                        p.Mode = mode;
                        result.Add(p);
                    }
                }
                result.Add(b);
            }
            return result;
        }

        // the time of the final route: its walks step by step, its sailing at the sailing speed, a portal jump, boarding and landing
        private float Price(List<NavPoint> path)
        {
            float total = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1]; var b = path[i];
                float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
                if (b.Mode == 2) total += _q.PortalSeconds;
                else if (b.Mode == 1 && a.Mode != 1) total += (_q.HasShip && Math.Abs(b.X - _q.ShipX) < 0.5f && Math.Abs(b.Z - _q.ShipZ) < 0.5f ? 0f : _q.BuildSeconds) + _q.SwitchSeconds + d / SailAlong(b.X - a.X, b.Z - a.Z);
                else if (b.Mode == 1) total += d / SailAlong(b.X - a.X, b.Z - a.Z);
                else if (a.Mode == 1) total += _q.SwitchSeconds;
                else total += Walk(a, b);
            }
            return total;
        }

        // each leg (on foot, or sailing) straightened: from a point the farthest later point of the same leg, up to 120 m on foot, whose straight
        // way costs no more than the path between them (on foot) or stays in water deep enough (sailing)
        private List<NavPoint> Straighten(List<NavPoint> path)
        {
            // a leg is a run of points of one mode, straightened from its own first point (a sailing leg never cuts back over the land it left),
            // a portal landing is a leg of its own
            var result = new List<NavPoint>();
            int a = 0;
            while (a < path.Count)
            {
                int b = a;
                while (b + 1 < path.Count && path[b + 1].Mode == path[a].Mode && path[a].Mode != 2) b++;
                var leg = path.GetRange(a, b - a + 1);
                result.AddRange(path[a].Mode == 1 ? StraightenSea(leg) : path[a].Mode == 2 ? leg : StraightenFoot(leg));
                a = b + 1;
            }
            return result;
        }

        // how far ahead a straight walk is tried, in path points (2 m apart after the refinement): 300 m, so the corners of the zone entrances
        // on open ground straighten out
        private const int StraightPoints = 150;

        private List<NavPoint> StraightenFoot(List<NavPoint> path)
        {
            var prefix = new float[path.Count];
            for (int k = 1; k < path.Count; k++) prefix[k] = prefix[k - 1] + Walk(path[k - 1], path[k]);
            var result = new List<NavPoint> { path[0] };
            int i = 0;
            while (i < path.Count - 1)
            {
                int next = i + 1;
                for (int j = Math.Min(path.Count - 1, i + StraightPoints); j > i + 1; j -= 4)
                {
                    float straight = Walk(path[i], path[j]);
                    if (!float.IsInfinity(straight) && straight <= prefix[j] - prefix[i] + 1e-3f) { next = j; break; }
                }
                result.Add(path[next]); i = next;
            }
            return result;
        }

        // a boat may be boarded and left in shallow water near the ends of its sailing leg (the old search's rule: two of its cells)
        private const float ShoreMeters = 48f;

        private List<NavPoint> StraightenSea(List<NavPoint> path)
        {
            var first = path[0]; var last = path[path.Count - 1];
            var result = new List<NavPoint> { path[0] };
            int i = 0;
            while (i < path.Count - 1)
            {
                int next = i + 1;
                for (int j = Math.Min(path.Count - 1, i + 400); j > i + 1; j -= 5)
                    if (SailClear(path[i], path[j], first, last)) { next = j; break; }
                result.Add(path[next]); i = next;
            }
            return result;
        }

        private static bool Near(NavPoint a, NavPoint b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z) <= ShoreMeters * ShoreMeters;

        // the ground between the 2 m points: the height interpolated from the four around it (the game's terrain is smooth between its
        // points), the surface by that height, a blocked point (rock, lava, outside) kept blocked
        private NavPoint Between(float x, float z)
        {
            float fx = x / ZoneGraph.Step, fz = z / ZoneGraph.Step;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
            float tx = fx - i, tz = fz - j;
            var p00 = PointAt(i * ZoneGraph.Step, j * ZoneGraph.Step); var p10 = PointAt((i + 1) * ZoneGraph.Step, j * ZoneGraph.Step);
            var p01 = PointAt(i * ZoneGraph.Step, (j + 1) * ZoneGraph.Step); var p11 = PointAt((i + 1) * ZoneGraph.Step, (j + 1) * ZoneGraph.Step);
            var p = tx < 0.5f ? (tz < 0.5f ? p00 : p01) : (tz < 0.5f ? p10 : p11);
            p.X = x; p.Z = z;
            if (Blocked(p00) || Blocked(p10) || Blocked(p01) || Blocked(p11)) { p.Surface = NavSurface.Obstacle; return p; }
            p.Height = (p00.Height * (1 - tx) + p10.Height * tx) * (1 - tz) + (p01.Height * (1 - tx) + p11.Height * tx) * tz;
            if (p.Surface == NavSurface.Water || p.Surface == NavSurface.Land) p.Surface = p.Height < _rules.Water ? NavSurface.Water : NavSurface.Land;
            return p;
        }

        // the walk along a straight line, every 2 m at most on the interpolated ground, so a short steep bank between two checks is not missed
        // (a single 2 m or diagonal grid step is one step)
        private float Walk(NavPoint a, NavPoint b)
        {
            float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            int steps = d <= 2.9f ? 1 : (int)Math.Ceiling(d / ZoneGraph.Step);
            float total = 0f, step = d / steps;
            var before = a;
            for (int s = 1; s <= steps; s++)
            {
                float t = (float)s / steps;
                var next = s == steps ? b : Between(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                int zx = ZoneOf(next.X), zz = ZoneOf(next.Z);
                var g = Zone(zx, zz);
                if (g != null && g.Blocked(LocalOf(zx, zz, next.X, next.Z))) return float.PositiveInfinity;
                total += FootStep(before, next, step, _q.Comfort);
                if (float.IsInfinity(total)) return total;
                before = next;
            }
            return total;
        }

        // the straight sail stays in water the draft deep, every 2 m. within ShoreMeters of the leg's first or last point (boarding and landing)
        // shallow water is allowed, rocks and land never
        private bool SailClear(NavPoint a, NavPoint b, NavPoint first, NavPoint last)
        {
            float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
            int steps = Math.Max(1, (int)Math.Ceiling(d / ZoneGraph.Step));
            for (int s = 0; s <= steps; s++)
            {
                float t = (float)s / steps;
                var p = Between(a.X + (b.X - a.X) * t, a.Z + (b.Z - a.Z) * t);
                if (p.Surface == NavSurface.Obstacle) return false;
                if (Near(p, first) || Near(p, last)) continue;
                if (p.Surface == NavSurface.Land || p.Surface == NavSurface.AshWater && !_q.HeatProof || _rules.Water - p.Height < _rules.Draft) return false;
            }
            return true;
        }
    }
}
