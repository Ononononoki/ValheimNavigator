using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimNavigator
{
    // one 64 m zone of the route graph (HPA*, Botea, Mueller, Schaeffer 2004, picked in the route lab on 30 Oct 2026: 0 of 61 routes failed,
    // 1.018 of the 2 m grid A*, 24 ms a route). the zone's 32 x 32 points 2 m apart (x from zone x * 64 - 32), its entrances on the four
    // borders, its shore links between the sea and the land, and the costs between its entrances found by a Dijkstra inside the zone. two
    // layers: on foot (Shortest and Comfort costs) and at sea (water at least the ship's draft deep). built from a sample function only, so
    // any thread can build it, and the route lab tests the same code
    internal sealed class ZoneGraph
    {
        internal const int Size = 32, Meters = 64, Step = 2, Version = 6;
        // shore links a zone keeps at most, and their spacing in meters
        internal const int ShoreLinks = 16;
        // biome ranks 0 (none, ocean) to 8 (deep north)
        internal const int RankCount = 9;
        internal const float ShoreSpacing = 16f;

        internal int Zx, Zz;
        internal ulong Signature;
        // the highest biome rank of the zone's points (meadows 1 to deep north 8), for the harder biomes rule in Comfort
        internal byte Rank;
        // the share of the zone's ground points (out of 255) of each biome rank 0 to 8, so the harder biomes rule counts how much of a zone is
        // harder, not only whether any point is (version 6, null in older tiles)
        internal byte[] RankShare;
        // nodes: the point inside the zone (j * Size + i), the layer (0 on foot, 1 at sea)
        internal int[] Cells = new int[0];
        internal byte[] Layers = new byte[0];
        // costs between nodes inside the zone: on foot Shortest and Comfort. at sea Shortest keeps to water that is not boiling, Comfort is a
        // heat-proof ship's (the Ashlands sea too)
        internal int[] From = new int[0], To = new int[0];
        internal float[] Shortest = new float[0], Comfort = new float[0];
        // crossings to the next zone: the node, the side (0 east, 1 north, 2 west, 3 south), the step's costs out and in (Shortest, Comfort, at
        // sea not boiling and heat-proof)
        internal int[] CrossNode = new int[0];
        internal byte[] CrossSide = new byte[0];
        internal float[] CrossOut = new float[0], CrossIn = new float[0], CrossOutComfort = new float[0], CrossInComfort = new float[0];
        // shore links: the foot node and the sea node (landing costs the switch time, boarding a built boat the build time too)
        internal int[] ShoreFoot = new int[0], ShoreSea = new int[0];
        // the zone's points too far out for the swim limit, one bit a point (j * Size + i), so a path refined inside the zone keeps the limit
        internal ulong[] NoSwim = new ulong[Size * Size / 64];

        internal bool Blocked(int local) => (NoSwim[local >> 6] >> (local & 63) & 1UL) != 0;

        // a copy with other inner costs for one query (rocks and solids it knows), the rest shared
        internal ZoneGraph Repriced(float[] shortest, float[] comfort)
        {
            var copy = (ZoneGraph)MemberwiseClone();
            copy.Shortest = shortest; copy.Comfort = comfort;
            return copy;
        }

        // the node of a point on a layer, -1 when the point is no node (built once a graph is used)
        private Dictionary<int, int> _footAt, _seaAt;
        // the edges leaving each node: inner edge indices, crossing indices, the shore link index (-1 for none)
        private List<int>[] _out, _cross;
        private int[] _shoreOfFoot, _shoreOfSea;

        internal int NodeAt(int local, int layer)
        {
            if (_footAt == null) Index();
            return (layer == 0 ? _footAt : _seaAt).TryGetValue(local, out int k) ? k : -1;
        }

        internal List<int> Out(int node) { if (_out == null) Index(); return _out[node]; }
        internal List<int> Crossings(int node) { if (_out == null) Index(); return _cross[node]; }
        internal int ShoreOfFoot(int node) { if (_out == null) Index(); return _shoreOfFoot[node]; }
        internal int ShoreOfSea(int node) { if (_out == null) Index(); return _shoreOfSea[node]; }

        private void Index()
        {
            var foot = new Dictionary<int, int>(); var sea = new Dictionary<int, int>();
            var o = new List<int>[Cells.Length]; var c = new List<int>[Cells.Length];
            for (int k = 0; k < Cells.Length; k++) { (Layers[k] == 0 ? foot : sea)[Cells[k]] = k; o[k] = new List<int>(); c[k] = new List<int>(); }
            for (int e = 0; e < From.Length; e++) o[From[e]].Add(e);
            for (int e = 0; e < CrossNode.Length; e++) c[CrossNode[e]].Add(e);
            var sf = new int[Cells.Length]; var ss = new int[Cells.Length];
            for (int k = 0; k < Cells.Length; k++) sf[k] = ss[k] = -1;
            for (int s = 0; s < ShoreFoot.Length; s++) { sf[ShoreFoot[s]] = s; ss[ShoreSea[s]] = s; }
            _shoreOfFoot = sf; _shoreOfSea = ss; _out = o; _cross = c; _seaAt = sea; _footAt = foot;
        }

        // the point across a side in the next zone: east (31, j) is the next zone's (0, j), north (i, 31) its (i, 0), and back
        internal static int Across(int local, int side)
        {
            int i = local % Size, j = local / Size;
            switch (side)
            {
                case 0: return j * Size;
                case 1: return i;
                case 2: return j * Size + Size - 1;
                default: return (Size - 1) * Size + i;
            }
        }

        internal static void Next(int zx, int zz, int side, out int nx, out int nz)
        {
            nx = zx + (side == 0 ? 1 : side == 2 ? -1 : 0); nz = zz + (side == 1 ? 1 : side == 3 ? -1 : 0);
        }

        internal static int OriginX(int zx) => zx * Meters - Meters / 2;
        internal static int OriginZ(int zz) => zz * Meters - Meters / 2;

        // the rules a zone is built with, the same for every zone of a world
        internal sealed class Rules
        {
            internal NavTerrain Shortest, Comfort;
            internal float Water = 30f, Draft = 1f, MaxSwim = 35f, Wade = 2f;
        }

        // the points read around a zone: the border steps reach one point out, the swim limit reaches half MaxSwim out
        internal static int Ring(Rules rules) => Math.Max(1, (int)Math.Ceiling(rules.MaxSwim / 2f / Step) + 1);

        // sample: the ground point at a world position (x and z even meters)
        internal static ZoneGraph Build(int zx, int zz, Func<int, int, NavPoint> sample, Rules rules)
        {
            var g = new ZoneGraph { Zx = zx, Zz = zz };
            // a ring around the zone: the border steps reach one point out, the swim limit reaches half MaxSwim out
            int ring = Ring(rules), n = Size + 2 * ring;
            int x0 = OriginX(zx) - ring * Step, z0 = OriginZ(zz) - ring * Step;
            var points = new NavPoint[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var p = sample(x0 + i * Step, z0 + j * Step);
                    p.X = x0 + i * Step; p.Z = z0 + j * Step;
                    points[j * n + i] = p;
                }
            // the swim limit blocks walking only, the sea layer sails the same water
            var noSwim = BlockLongSwims(points, n, rules);
            int R(int i, int j) => (j + ring) * n + i + ring;
            // sailable water for a heat-proof ship (the boiling Ashlands sea too), and water any ship sails
            bool Sail(int k) => (points[k].Surface == NavSurface.Water || points[k].Surface == NavSurface.AshWater) && rules.Water - points[k].Height >= rules.Draft;
            bool Cold(int k) => points[k].Surface == NavSurface.Water && rules.Water - points[k].Height >= rules.Draft;
            float Foot(NavTerrain t, int a, int b)
            {
                if (noSwim[a] || noSwim[b]) return float.PositiveInfinity;
                int ai = a % n, aj = a / n, bi = b % n, bj = b / n;
                float d = (float)Math.Sqrt((ai - bi) * (ai - bi) + (aj - bj) * (aj - bj)) * Step;
                return t.StepSeconds(points[a], points[b], d, 0, false);
            }
            var nodes = new List<int>(); var layers = new List<byte>();
            var footAt = new Dictionary<int, int>(); var seaAt = new Dictionary<int, int>();
            int FootNode(int local) { if (!footAt.TryGetValue(local, out int k)) { k = nodes.Count; nodes.Add(local); layers.Add(0); footAt[local] = k; } return k; }
            int SeaNode(int local) { if (!seaAt.TryGetValue(local, out int k)) { k = nodes.Count; nodes.Add(local); layers.Add(1); seaAt[local] = k; } return k; }

            // entrances: along each side the pairs (inside point, outside point) open both ways, each run's middle when short, both ends when long.
            // the next zone finds the same pairs from the other side, so the crossings meet
            var cross = new List<(int node, byte side, float o, float i, float oc, float ic)>();
            var seaCross = new List<(int node, byte side, float o, float i, float oc, float ic)>();
            var seaCrossAt = new HashSet<int>();
            for (byte side = 0; side < 4; side++)
            {
                var run = new List<(int a, int b)>(); var seaRun = new List<(int a, int b)>(); var coldRun = new List<(int a, int b)>();
                void Close()
                {
                    if (run.Count > 0)
                    {
                        var picks = run.Count <= 6 ? new[] { run[run.Count / 2] } : new[] { run[0], run[run.Count - 1] };
                        foreach (var (a, b) in picks)
                            cross.Add((FootNode(Local(a, n, ring)), side, Foot(rules.Shortest, a, b), Foot(rules.Shortest, b, a), Foot(rules.Comfort, a, b), Foot(rules.Comfort, b, a)));
                        run.Clear();
                    }
                }
                // runs of any sailable water and runs of water that is not boiling, so both kinds of ship find their crossings
                void CloseSea(List<(int a, int b)> r)
                {
                    if (r.Count > 0)
                    {
                        var picks = r.Count <= 6 ? new[] { r[r.Count / 2] } : new[] { r[0], r[r.Count / 2], r[r.Count - 1] };
                        foreach (var (a, b) in picks)
                        {
                            if (!seaCrossAt.Add(side * Size * Size + Local(a, n, ring))) continue;
                            float s = Step / rules.Shortest.Options.SailSpeed, cold = Cold(a) && Cold(b) ? s : float.PositiveInfinity;
                            seaCross.Add((SeaNode(Local(a, n, ring)), side, cold, cold, s, s));
                        }
                        r.Clear();
                    }
                }
                for (int k = 0; k < Size; k++)
                {
                    int a, b;
                    switch (side)
                    {
                        case 0: a = R(Size - 1, k); b = R(Size, k); break;
                        case 1: a = R(k, Size - 1); b = R(k, Size); break;
                        case 2: a = R(0, k); b = R(-1, k); break;
                        default: a = R(k, 0); b = R(k, -1); break;
                    }
                    if (float.IsInfinity(Foot(rules.Shortest, a, b)) || float.IsInfinity(Foot(rules.Shortest, b, a))) Close(); else run.Add((a, b));
                    if (Sail(a) && Sail(b)) seaRun.Add((a, b)); else CloseSea(seaRun);
                    if (Cold(a) && Cold(b)) coldRun.Add((a, b)); else CloseSea(coldRun);
                }
                Close(); CloseSea(seaRun); CloseSea(coldRun);
            }

            // shore links: sailable points beside ground walked on foot (land, or water too shallow to sail and waded ashore, as on the gentle
            // Ashlands beaches), spread over the whole zone (each next one the farthest from those taken), so each shore of a broken coast gets
            // its landings
            var shoreFoot = new List<int>(); var shoreSea = new List<int>();
            var candidates = new List<(int sea, int land)>();
            for (int j = 0; j < Size; j++)
                for (int i = 0; i < Size; i++)
                {
                    int c = R(i, j);
                    if (!Sail(c)) continue;
                    int land = -1;
                    for (int d = 0; d < 8 && land < 0; d++)
                    {
                        int ni = i + Di[d], nj = j + Dj[d];
                        if (ni < 0 || nj < 0 || ni >= Size || nj >= Size) continue;
                        int m = R(ni, nj);
                        bool walked = points[m].Surface == NavSurface.Land && points[m].Height >= rules.Water || (points[m].Surface == NavSurface.Water || points[m].Surface == NavSurface.AshWater) && !Sail(m);
                        if (walked && !noSwim[m]) land = m;
                    }
                    if (land >= 0) candidates.Add((c, land));
                }
            var chosen = new List<int>();
            var nearest = new float[candidates.Count];
            for (int k = 0; k < nearest.Length; k++) nearest[k] = float.MaxValue;
            for (int pick = candidates.Count > 0 ? 0 : -1; pick >= 0 && chosen.Count < ShoreLinks; )
            {
                var (c, land) = candidates[pick];
                chosen.Add(c);
                shoreSea.Add(SeaNode(Local(c, n, ring))); shoreFoot.Add(FootNode(Local(land, n, ring)));
                pick = -1; float far = ShoreSpacing;
                for (int k = 0; k < candidates.Count; k++)
                {
                    nearest[k] = Math.Min(nearest[k], Distance(candidates[k].sea, c, n));
                    if (nearest[k] >= far) { far = nearest[k]; pick = k; }
                }
            }

            // costs inside the zone between its nodes of the same layer
            var from = new List<int>(); var to = new List<int>(); var shortest = new List<float>(); var comfort = new List<float>();
            var cost = new float[Size * Size];
            var heap = new MinHeap();
            // each step on foot priced once (Shortest and Comfort), every search from an entrance reads the table
            var footShortest = new float[Size * Size * 8]; var footComfort = new float[Size * Size * 8];
            for (int c = 0; c < Size * Size; c++)
                for (int dir = 0; dir < 8; dir++)
                {
                    int ni = c % Size + Di[dir], nj = c / Size + Dj[dir];
                    bool inside = ni >= 0 && nj >= 0 && ni < Size && nj < Size;
                    footShortest[c * 8 + dir] = inside ? Foot(rules.Shortest, R(c % Size, c / Size), R(ni, nj)) : float.PositiveInfinity;
                    footComfort[c * 8 + dir] = inside ? Foot(rules.Comfort, R(c % Size, c / Size), R(ni, nj)) : float.PositiveInfinity;
                }
            // hot: at sea the boiling water is sailed too (a heat-proof ship)
            void Dijkstra(int start, int layer, NavTerrain t, float[] steps, float[] result, bool hot = false)
            {
                for (int k = 0; k < result.Length; k++) result[k] = float.PositiveInfinity;
                if (layer == 1 && !hot && !Cold(R(start % Size, start / Size))) return;
                result[start] = 0f; heap.Clear(); heap.Push(0f, start);
                while (heap.Count > 0)
                {
                    var (d, c) = heap.Pop();
                    if (d > result[c]) continue;
                    int ci = c % Size, cj = c / Size;
                    for (int dir = 0; dir < 8; dir++)
                    {
                        int ni = ci + Di[dir], nj = cj + Dj[dir];
                        if (ni < 0 || nj < 0 || ni >= Size || nj >= Size) continue;
                        int m = nj * Size + ni;
                        float s;
                        if (layer == 1) { if (!(hot ? Sail(R(ni, nj)) : Cold(R(ni, nj)))) continue; s = (dir < 4 ? Step : Step * 1.41421356f) / t.Options.SailSpeed; }
                        else s = steps[c * 8 + dir];
                        if (float.IsInfinity(s)) continue;
                        float nd = d + s;
                        if (nd >= result[m]) continue;
                        result[m] = nd; heap.Push(nd, m);
                    }
                }
            }
            var comfortCost = new float[Size * Size];
            for (int a = 0; a < nodes.Count; a++)
            {
                Dijkstra(nodes[a], layers[a], rules.Shortest, footShortest, cost);
                if (layers[a] == 0) Dijkstra(nodes[a], 0, rules.Comfort, footComfort, comfortCost);
                else Dijkstra(nodes[a], 1, rules.Shortest, null, comfortCost, true);
                for (int b = 0; b < nodes.Count; b++)
                {
                    if (b == a || layers[b] != layers[a] || float.IsInfinity(cost[nodes[b]]) && (layers[a] == 0 || float.IsInfinity(comfortCost[nodes[b]]))) continue;
                    from.Add(a); to.Add(b); shortest.Add(cost[nodes[b]]); comfort.Add(comfortCost[nodes[b]]);
                }
            }

            g.Cells = nodes.ToArray(); g.Layers = layers.ToArray();
            g.From = from.ToArray(); g.To = to.ToArray(); g.Shortest = shortest.ToArray(); g.Comfort = comfort.ToArray();
            int crossings = cross.Count + seaCross.Count;
            g.CrossNode = new int[crossings]; g.CrossSide = new byte[crossings];
            g.CrossOut = new float[crossings]; g.CrossIn = new float[crossings]; g.CrossOutComfort = new float[crossings]; g.CrossInComfort = new float[crossings];
            for (int k = 0; k < cross.Count; k++) { var c = cross[k]; g.CrossNode[k] = c.node; g.CrossSide[k] = c.side; g.CrossOut[k] = c.o; g.CrossIn[k] = c.i; g.CrossOutComfort[k] = c.oc; g.CrossInComfort[k] = c.ic; }
            for (int k = 0; k < seaCross.Count; k++) { var c = seaCross[k]; int m = cross.Count + k; g.CrossNode[m] = c.node; g.CrossSide[m] = c.side; g.CrossOut[m] = c.o; g.CrossIn[m] = c.i; g.CrossOutComfort[m] = c.oc; g.CrossInComfort[m] = c.ic; }
            g.ShoreFoot = shoreFoot.ToArray(); g.ShoreSea = shoreSea.ToArray();
            for (int j = 0; j < Size; j++) for (int i = 0; i < Size; i++) g.Rank = Math.Max(g.Rank, points[R(i, j)].Rank);
            var ranks = new int[RankCount]; int ground = 0;
            for (int j = 0; j < Size; j++)
                for (int i = 0; i < Size; i++)
                {
                    var p = points[R(i, j)];
                    if (p.Surface == NavSurface.Water || p.Surface == NavSurface.AshWater) continue;
                    ranks[Math.Min(RankCount - 1, (int)p.Rank)]++; ground++;
                }
            g.RankShare = new byte[RankCount];
            for (int k = 0; k < RankCount; k++) g.RankShare[k] = ground == 0 ? (byte)0 : (byte)Math.Round(ranks[k] * 255.0 / ground);
            for (int j = 0; j < Size; j++)
                for (int i = 0; i < Size; i++)
                    if (noSwim[R(i, j)]) { int local = j * Size + i; g.NoSwim[local >> 6] |= 1UL << (local & 63); }
            return g;
        }

        // the zone point of a ring point
        private static int Local(int k, int n, int ring) => (k / n - ring) * Size + k % n - ring;

        private static float Distance(int a, int b, int n)
        {
            int dx = a % n - b % n, dz = a / n - b / n;
            return (float)Math.Sqrt(dx * dx + dz * dz) * Step;
        }

        internal static readonly int[] Di = { 1, -1, 0, 0, 1, 1, -1, -1 }, Dj = { 0, 0, 1, -1, 1, -1, 1, -1 };

        // MaxSwim: swimming water (deeper than wading) farther than half MaxSwim from land or wading water cannot be swum on foot, so no swim
        // on a route is longer than MaxSwim. a breadth first walk from the shallow points, true where swimming is too far
        private static bool[] BlockLongSwims(NavPoint[] points, int n, Rules rules)
        {
            var blocked = new bool[points.Length];
            if (rules.MaxSwim <= 0f) return blocked;
            var distance = new int[points.Length];
            var queue = new Queue<int>();
            for (int k = 0; k < points.Length; k++)
            {
                bool deep = (points[k].Surface == NavSurface.Water || points[k].Surface == NavSurface.AshWater) && rules.Water - points[k].Height >= rules.Wade;
                distance[k] = deep ? int.MaxValue : 0;
                if (!deep) queue.Enqueue(k);
            }
            while (queue.Count > 0)
            {
                int k = queue.Dequeue(), i = k % n, j = k / n;
                for (int d = 0; d < 4; d++)
                {
                    int ni = i + Di[d], nj = j + Dj[d];
                    if (ni < 0 || nj < 0 || ni >= n || nj >= n) continue;
                    int m = nj * n + ni;
                    if (distance[m] != int.MaxValue) continue;
                    distance[m] = distance[k] + 1; queue.Enqueue(m);
                }
            }
            int limit = (int)Math.Floor(rules.MaxSwim / 2f / Step);
            // points that reach no shallow water inside the ring keep MaxValue: the ring is MaxSwim / 2 wide, so they are past the limit too
            for (int k = 0; k < points.Length; k++) blocked[k] = distance[k] > limit;
            return blocked;
        }

        // ---- the file form: version, zone, signature, then the arrays ----

        internal void Write(BinaryWriter w)
        {
            w.Write(Version); w.Write(Zx); w.Write(Zz); w.Write(Signature); w.Write(Rank);
            w.Write(RankShare ?? new byte[RankCount]);
            w.Write(Cells.Length); for (int k = 0; k < Cells.Length; k++) { w.Write((short)Cells[k]); w.Write(Layers[k]); }
            w.Write(From.Length); for (int k = 0; k < From.Length; k++) { w.Write((short)From[k]); w.Write((short)To[k]); w.Write(Shortest[k]); w.Write(Comfort[k]); }
            w.Write(CrossNode.Length);
            for (int k = 0; k < CrossNode.Length; k++) { w.Write((short)CrossNode[k]); w.Write(CrossSide[k]); w.Write(CrossOut[k]); w.Write(CrossIn[k]); w.Write(CrossOutComfort[k]); w.Write(CrossInComfort[k]); }
            w.Write(ShoreFoot.Length); for (int k = 0; k < ShoreFoot.Length; k++) { w.Write((short)ShoreFoot[k]); w.Write((short)ShoreSea[k]); }
            foreach (ulong bits in NoSwim) w.Write(bits);
        }

        // a zone holds at most this many nodes (four sides of single point runs and the shore links fit far below it)
        private const int MaxNodes = 2048;

        internal static ZoneGraph Read(BinaryReader r)
        {
            // version 5 has no RankShare (6 added it and changed the Comfort costs in the swamp's water). the store's key file keeps a world's
            // tiles to one version, the probes read older tiles from disk
            int version = r.ReadInt32();
            if (version != Version && version != 5) throw new InvalidDataException("route zone version");
            var g = new ZoneGraph { Zx = r.ReadInt32(), Zz = r.ReadInt32(), Signature = r.ReadUInt64(), Rank = r.ReadByte() };
            if (version >= 6) g.RankShare = r.ReadBytes(RankCount);
            int nodes = Count(r, MaxNodes);
            g.Cells = new int[nodes]; g.Layers = new byte[nodes];
            for (int k = 0; k < nodes; k++) { g.Cells[k] = r.ReadInt16(); g.Layers[k] = r.ReadByte(); if (g.Cells[k] < 0 || g.Cells[k] >= Size * Size || g.Layers[k] > 1) throw new InvalidDataException("route zone node"); }
            int edges = Count(r, nodes * nodes);
            g.From = new int[edges]; g.To = new int[edges]; g.Shortest = new float[edges]; g.Comfort = new float[edges];
            for (int k = 0; k < edges; k++) { g.From[k] = Node(r, nodes); g.To[k] = Node(r, nodes); g.Shortest[k] = r.ReadSingle(); g.Comfort[k] = r.ReadSingle(); }
            int crossings = Count(r, MaxNodes);
            g.CrossNode = new int[crossings]; g.CrossSide = new byte[crossings]; g.CrossOut = new float[crossings]; g.CrossIn = new float[crossings]; g.CrossOutComfort = new float[crossings]; g.CrossInComfort = new float[crossings];
            for (int k = 0; k < crossings; k++) { g.CrossNode[k] = Node(r, nodes); g.CrossSide[k] = r.ReadByte(); g.CrossOut[k] = r.ReadSingle(); g.CrossIn[k] = r.ReadSingle(); g.CrossOutComfort[k] = r.ReadSingle(); g.CrossInComfort[k] = r.ReadSingle(); if (g.CrossSide[k] > 3) throw new InvalidDataException("route zone side"); }
            int shore = Count(r, ShoreLinks);
            g.ShoreFoot = new int[shore]; g.ShoreSea = new int[shore];
            for (int k = 0; k < shore; k++) { g.ShoreFoot[k] = Node(r, nodes); g.ShoreSea[k] = Node(r, nodes); }
            for (int k = 0; k < g.NoSwim.Length; k++) g.NoSwim[k] = r.ReadUInt64();
            return g;
        }

        private static int Count(BinaryReader r, int limit)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > limit) throw new InvalidDataException("route zone count " + count);
            return count;
        }

        private static int Node(BinaryReader r, int nodes)
        {
            int k = r.ReadInt16();
            if (k < 0 || k >= nodes) throw new InvalidDataException("route zone node index");
            return k;
        }
    }

    // a small binary heap of (cost, item)
    internal sealed class MinHeap
    {
        private readonly List<(float f, int c)> _items = new List<(float, int)>();
        internal int Count => _items.Count;
        internal void Clear() => _items.Clear();

        internal void Push(float f, int c)
        {
            _items.Add((f, c)); int i = _items.Count - 1;
            while (i > 0) { int p = (i - 1) / 2; if (_items[p].f <= _items[i].f) break; (_items[p], _items[i]) = (_items[i], _items[p]); i = p; }
        }

        internal (float f, int c) Pop()
        {
            var top = _items[0]; var last = _items[_items.Count - 1]; _items.RemoveAt(_items.Count - 1);
            if (_items.Count > 0)
            {
                _items[0] = last; int i = 0;
                while (true) { int l = 2 * i + 1, r = l + 1, m = i; if (l < _items.Count && _items[l].f < _items[m].f) m = l; if (r < _items.Count && _items[r].f < _items[m].f) m = r; if (m == i) break; (_items[m], _items[i]) = (_items[i], _items[m]); i = m; }
            }
            return top;
        }
    }
}
