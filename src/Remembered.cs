using System;
using System.Collections.Generic;
using System.IO;

namespace ValheimNavigator
{
    // what the route remembers of places that are not loaded: the solid objects of the saved world as boxes (buildings, rocks, trees, read from
    // their saved objects and their prefab colliders), and the real ground and ground paint of zones you terraformed or painted, taken while the
    // zone was loaded. both are kept per world in a cache file and survive restarts. a loaded zone's own data always comes first. no unity here,
    // so the offline checks run it as it is
    internal static class Remembered
    {
        // a solid as an upright box: center, turn about the vertical (cos, sin), half sizes, and its bottom and top heights
        internal struct Box
        {
            internal float X, Z, Cos, Sin, HalfX, HalfZ, Bottom, Top;
            internal long Zone;
            // a building piece walked on (Floor, Stair, Roof), one neither walked nor a wall (Open), else 0, and its SpeedyPaths ground type
            internal byte Walk, Ground;
        }

        internal const byte Floor = 1, Stair = 2, Roof = 3, Open = 4;
        // a floor buried this far under the ground is not walked on, the ground is
        private const float Buried = 0.3f;

        // a zone's real ground and paint on a 2 m grid, 33 by 33 points over its 64 m. paint holds SpeedyPaths ground types
        internal sealed class Snapshot
        {
            internal float[] Height = new float[Side * Side];
            internal byte[] Ground = new byte[Side * Side];
            internal uint Revision;
        }

        internal const float ZoneSize = 64f, Step = 2f;
        internal const int Side = 33;
        // the space a standing player fills above the floor, and the highest top that is stepped onto rather than blocking
        internal const float Knee = 0.6f, Head = 1.8f, Body = 0.3f, StepUp = 1f;
        private const float BoxCell = 32f;

        // replaced whole on every change, so a route on a background thread reads one finished index
        private static volatile Dictionary<long, List<Box>> BoxesByCell = new Dictionary<long, List<Box>>();
        private static readonly Dictionary<long, List<Box>> BoxesByZone = new Dictionary<long, List<Box>>();
        private static readonly Dictionary<long, Snapshot> Snapshots = new Dictionary<long, Snapshot>();
        // the tops of the rocks of an Ashlands zone on the same 2 m grid, NaN where no rock stands above the ground, measured down onto the
        // loaded colliders. replaced whole on a change, so a route on a background thread reads one finished set
        private static volatile Dictionary<long, float[]> Tops = new Dictionary<long, float[]>();
        internal static int BoxCount, Version;
        // a hash of each zone's boxes, so a route can keep work done near a zone whose solids did not change
        private static volatile Dictionary<long, int> ZoneHashes = new Dictionary<long, int>();
        internal static int ZoneHash(int zx, int zz) => ZoneHashes.TryGetValue(Key(zx, zz), out int h) ? h : 0;
        // a hash of each zone's walked pieces and its snapshot, the route map builds a zone again when it changes
        private static volatile Dictionary<long, ulong> WalkHashes = new Dictionary<long, ulong>();
        internal static ulong WalkHash(int zx, int zz) => WalkHashes.TryGetValue(Key(zx, zz), out ulong h) ? h : 0UL;
        internal static ICollection<long> WalkKeys() => WalkHashes.Keys;
        internal static int SnapshotCount => Snapshots.Count;

        internal static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }
        internal static long ZoneOf(float x, float z) { return Key((int)Math.Floor((x + ZoneSize / 2f) / ZoneSize), (int)Math.Floor((z + ZoneSize / 2f) / ZoneSize)); }

        // the boxes by zone are changed under Gate, the index by cell is rebuilt there and swapped whole
        private static readonly object Gate = new object();

        internal static void Clear()
        {
            lock (Gate) { BoxesByCell = new Dictionary<long, List<Box>>(); BoxesByZone.Clear(); Snapshots.Clear(); Tops = new Dictionary<long, float[]>(); TopsBoxes.Clear(); HugeByZone.Clear(); HugeByCell = new Dictionary<long, List<Huge>>(); Shapes = new Shape[0]; _walkBoxHashes = new Dictionary<long, ulong>(); WalkHashes = new Dictionary<long, ulong>(); BoxCount = 0; Version++; }
        }

        // a scan's boxes replace the remembered ones of every zone it read, zones it did not read keep theirs
        internal static void ReplaceBoxes(ICollection<long> zonesRead, List<Box> boxes)
        {
            lock (Gate)
            {
                foreach (long zone in zonesRead) BoxesByZone.Remove(zone);
                foreach (var box in boxes)
                {
                    if (!BoxesByZone.TryGetValue(box.Zone, out var list)) BoxesByZone[box.Zone] = list = new List<Box>();
                    list.Add(box);
                }
                RebuildCells();
            }
        }

        // the same on a background thread, so a scan of the whole world (every tree and rock) does not hold up a frame
        internal static void ReplaceBoxesLater(ICollection<long> zonesRead, List<Box> boxes)
        {
            var zones = new List<long>(zonesRead); var copy = new List<Box>(boxes);
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { ReplaceBoxes(zones, copy); } catch (Exception) { } });
        }

        private static void RebuildCells()
        {
            var cells = new Dictionary<long, List<Box>>(); int count = 0;
            var hashes = new Dictionary<long, int>();
            foreach (var pair in BoxesByZone)
            {
                // a sum, so the order of a scan does not change the hash
                int h = 17;
                unchecked { foreach (var box in pair.Value) h += (((box.X.GetHashCode() * 31 + box.Z.GetHashCode()) * 31 + box.Top.GetHashCode()) * 31 + box.Walk) * 397 + 1; }
                hashes[pair.Key] = h;
            }
            ZoneHashes = hashes;
            RebuildWalkHashes();
            foreach (var list in BoxesByZone.Values)
                foreach (var box in list)
                {
                    count++;
                    float reach = (float)Math.Sqrt(box.HalfX * box.HalfX + box.HalfZ * box.HalfZ);
                    int x0 = (int)Math.Floor((box.X - reach) / BoxCell), x1 = (int)Math.Floor((box.X + reach) / BoxCell);
                    int z0 = (int)Math.Floor((box.Z - reach) / BoxCell), z1 = (int)Math.Floor((box.Z + reach) / BoxCell);
                    for (int cz = z0; cz <= z1; cz++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            long key = Key(cx, cz);
                            if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new List<Box>();
                            cell.Add(box);
                        }
                }
            BoxesByCell = cells; BoxCount = count;
            Version++;
        }

        // under Gate: the walked pieces by zone, then joined with the painted snapshots
        private static Dictionary<long, ulong> _walkBoxHashes = new Dictionary<long, ulong>();

        private static ulong Mix(ulong h, ulong v) { unchecked { for (int i = 0; i < 8; i++) { h = (h ^ (v & 0xFF)) * 1099511628211UL; v >>= 8; } } return h; }

        private static void RebuildWalkHashes()
        {
            var boxes = new Dictionary<long, ulong>();
            foreach (var pair in BoxesByZone)
            {
                ulong h = 0UL;
                foreach (var box in pair.Value)
                {
                    if (box.Walk == 0) continue;
                    // a sum of each box's hash, so the order of a scan does not count
                    unchecked { h += Mix(Mix(Mix(1469598103934665603UL, (ulong)BitConverter.DoubleToInt64Bits(box.X) ^ (ulong)BitConverter.DoubleToInt64Bits(box.Z)), (ulong)BitConverter.DoubleToInt64Bits(box.Top)), (ulong)(box.Walk << 8 | box.Ground)); }
                }
                if (h != 0UL) boxes[pair.Key] = h;
            }
            _walkBoxHashes = boxes;
            JoinWalkHashes();
        }

        private static void JoinWalkHashes()
        {
            var hashes = new Dictionary<long, ulong>(_walkBoxHashes);
            foreach (var pair in Snapshots)
            {
                ulong paint = 0UL;
                for (int k = 0; k < pair.Value.Ground.Length; k++)
                    if (pair.Value.Ground[k] != 0) { if (paint == 0UL) paint = 1469598103934665603UL; paint = Mix(paint, (ulong)(k << 8 | pair.Value.Ground[k])); }
                if (paint == 0UL) continue;
                hashes[pair.Key] = hashes.TryGetValue(pair.Key, out ulong h) ? Mix(h, paint) : paint;
            }
            // the huge rocks, in every zone they reach, so the route map builds those zones again when they change
            foreach (var list in HugeByZone.Values)
                foreach (var h in list)
                {
                    ulong v = Mix(Mix(Mix(1469598103934665603UL, (ulong)BitConverter.DoubleToInt64Bits(h.X)), (ulong)BitConverter.DoubleToInt64Bits(h.Z)), (ulong)BitConverter.DoubleToInt64Bits(h.Y) ^ (ulong)h.Shape);
                    long a = ZoneOf(h.X - h.Reach, h.Z - h.Reach), b = ZoneOf(h.X + h.Reach, h.Z + h.Reach);
                    for (int zz = (int)(uint)a; zz <= (int)(uint)b; zz++)
                        for (int zx = (int)(a >> 32); zx <= (int)(b >> 32); zx++)
                        {
                            long key = Key(zx, zz);
                            unchecked { hashes[key] = hashes.TryGetValue(key, out ulong old) ? old + v : v; }
                        }
                }
            // the rock tops, so the route map builds a zone again when its rocks are measured
            foreach (var pair in Tops)
            {
                ulong rock = 1469598103934665603UL;
                for (int k = 0; k < pair.Value.Length; k++) if (!float.IsNaN(pair.Value[k])) rock = Mix(Mix(rock, (ulong)k), (ulong)BitConverter.DoubleToInt64Bits(pair.Value[k]));
                hashes[pair.Key] = hashes.TryGetValue(pair.Key, out ulong h) ? Mix(h, rock) : rock;
            }
            WalkHashes = hashes;
        }

        // the zone's object hash (ZoneHashes) when its rocks were measured, kept for this session only: after a restart a zone near you is
        // measured once more, a changed hash (a rock destroyed or placed, seen by the world scan) measures it again
        private static readonly Dictionary<long, int> TopsBoxes = new Dictionary<long, int>();

        // the zone's rock tops are measured and its objects did not change since
        internal static bool TopsCurrent(long zone)
        {
            if (!Tops.ContainsKey(zone) || !TopsBoxes.TryGetValue(zone, out int seen)) return false;
            return seen == (ZoneHashes.TryGetValue(zone, out int now) ? now : 0);
        }

        internal static void StoreTops(long zone, float[] tops)
        {
            lock (Gate)
            {
                TopsBoxes[zone] = ZoneHashes.TryGetValue(zone, out int boxes) ? boxes : 0;
                // the same tops again (nothing changed) leave the route map as it is
                if (Tops.TryGetValue(zone, out var old) && Same(old, tops)) return;
                Tops = new Dictionary<long, float[]>(Tops) { [zone] = tops }; JoinWalkHashes(); Version++;
            }
        }

        private static bool Same(float[] a, float[] b)
        {
            for (int k = 0; k < a.Length; k++) if (!(a[k] == b[k] || float.IsNaN(a[k]) && float.IsNaN(b[k]))) return false;
            return true;
        }

        // the measured top of a rock at a point (its nearest 2 m grid point), false where none was measured
        internal static bool RockTop(float x, float z, out float top)
        {
            top = 0f;
            var tops = Tops;
            long zone = ZoneOf(x, z);
            // a zone measured near you has its real tops, a zone not measured yet the tops of its huge rocks' shapes
            if (!tops.TryGetValue(zone, out var t)) return HugeTop(x, z, out top);
            int zx = (int)(zone >> 32), zz = (int)(uint)zone;
            int i = Math.Max(0, Math.Min(Side - 1, (int)Math.Round((x - (zx * ZoneSize - ZoneSize / 2f)) / Step)));
            int j = Math.Max(0, Math.Min(Side - 1, (int)Math.Round((z - (zz * ZoneSize - ZoneSize / 2f)) / Step)));
            top = t[j * Side + i];
            return !float.IsNaN(top);
        }

        // ---- huge rocks ----

        // a huge rock's top surface in its own frame on a 1 m grid (unscaled), NaN where it has none, measured once a kind from its colliders
        internal sealed class Shape
        {
            internal string Name;
            internal float MinX, MinZ;
            internal int W, H;
            internal float[] Top;
        }

        // a huge rock of the saved world: its place, its turn about the vertical, its scale, its shape and how far it reaches from its place
        internal struct Huge
        {
            internal float X, Y, Z, Cos, Sin, SX, SY, SZ, Reach;
            internal int Shape;
            internal long Zone;
        }

        private static volatile Shape[] Shapes = new Shape[0];
        private static readonly Dictionary<long, List<Huge>> HugeByZone = new Dictionary<long, List<Huge>>();
        private static volatile Dictionary<long, List<Huge>> HugeByCell = new Dictionary<long, List<Huge>>();

        internal static int ShapeIndex(string name)
        {
            var shapes = Shapes;
            for (int i = 0; i < shapes.Length; i++) if (shapes[i].Name == name) return i;
            return -1;
        }

        internal static int AddShape(Shape shape)
        {
            lock (Gate)
            {
                var shapes = new Shape[Shapes.Length + 1];
                Array.Copy(Shapes, shapes, Shapes.Length); shapes[shapes.Length - 1] = shape;
                Shapes = shapes;
                return shapes.Length - 1;
            }
        }

        // how far a shape reaches from its place at scale 1
        internal static float ShapeReach(int index)
        {
            var s = Shapes[index];
            float x = Math.Max(Math.Abs(s.MinX), Math.Abs(s.MinX + s.W)), z = Math.Max(Math.Abs(s.MinZ), Math.Abs(s.MinZ + s.H));
            return (float)Math.Sqrt(x * x + z * z);
        }

        // a scan's huge rocks replace the remembered ones of every zone it read, on a background thread
        internal static void ReplaceHugeLater(ICollection<long> zonesRead, List<Huge> huge)
        {
            var zones = new List<long>(zonesRead); var copy = new List<Huge>(huge);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (Gate)
                    {
                        foreach (long zone in zones) HugeByZone.Remove(zone);
                        foreach (var h in copy)
                        {
                            if (!HugeByZone.TryGetValue(h.Zone, out var list)) HugeByZone[h.Zone] = list = new List<Huge>();
                            list.Add(h);
                        }
                        RebuildHuge(); JoinWalkHashes(); Version++;
                    }
                }
                catch (Exception) { }
            });
        }

        // under Gate: the huge rocks by 32 m cell, every cell their reach covers
        private static void RebuildHuge()
        {
            var cells = new Dictionary<long, List<Huge>>();
            foreach (var list in HugeByZone.Values)
                foreach (var h in list)
                {
                    int x0 = (int)Math.Floor((h.X - h.Reach) / BoxCell), x1 = (int)Math.Floor((h.X + h.Reach) / BoxCell);
                    int z0 = (int)Math.Floor((h.Z - h.Reach) / BoxCell), z1 = (int)Math.Floor((h.Z + h.Reach) / BoxCell);
                    for (int cz = z0; cz <= z1; cz++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            long key = Key(cx, cz);
                            if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new List<Huge>();
                            cell.Add(h);
                        }
                }
            HugeByCell = cells;
        }

        internal static int ShapeCount => Shapes.Length;

        // the highest huge rock top at a point, false where none covers it
        private static bool HugeTop(float x, float z, out float top)
        {
            top = float.MinValue;
            var cells = HugeByCell; var shapes = Shapes;
            if (cells.Count == 0 || !cells.TryGetValue(Key((int)Math.Floor(x / BoxCell), (int)Math.Floor(z / BoxCell)), out var cell)) return false;
            foreach (var h in cell)
            {
                if (h.Shape < 0 || h.Shape >= shapes.Length) continue;
                var s = shapes[h.Shape];
                float dx = x - h.X, dz = z - h.Z;
                float lx = (dx * h.Cos + dz * h.Sin) / h.SX, lz = (-dx * h.Sin + dz * h.Cos) / h.SZ;
                int i = (int)Math.Round(lx - s.MinX), j = (int)Math.Round(lz - s.MinZ);
                if (i < 0 || j < 0 || i >= s.W || j >= s.H) continue;
                float t = s.Top[j * s.W + i];
                if (!float.IsNaN(t)) top = Math.Max(top, h.Y + t * h.SY);
            }
            return top > float.MinValue;
        }

        // the walked piece under a point as the route map stands on it: a stair (at its middle height, it rises across its box) before a
        // floor, the lowest floor not buried under the ground (floor), so the ground floor of a building with storeys is walked. over dry
        // ground a floor with room to stand under it (a room above a gate, a walkway) is walked under, over water it is walked on (a bridge,
        // a dock). a roof counts as a floor at its middle height. false when none covers the point
        internal static bool Surface(float x, float z, float floor, bool wet, out float top, out int ground)
        {
            top = floor; ground = 0;
            var cells = BoxesByCell;
            if (BoxCount == 0 || !cells.TryGetValue(Key((int)Math.Floor(x / BoxCell), (int)Math.Floor(z / BoxCell)), out var cell)) return false;
            bool any = false, stair = false;
            float best = float.MaxValue;
            foreach (var box in cell)
            {
                if (box.Walk == 0 || box.Walk == Open || !Inside(box, x, z, wet ? DeckMargin : 0f)) continue;
                bool isStair = box.Walk == Stair;
                float middle = (box.Bottom + box.Top) * 0.5f;
                float surface = isStair ? Math.Max(middle, floor) : box.Walk == Roof ? middle : box.Top;
                if (!isStair && (surface < floor - Buried || !wet && surface - floor >= Head + Body)) continue;
                if (stair && !isStair) continue;
                if (any && isStair == stair && surface >= best) continue;
                best = surface; ground = box.Ground; any = true; stair = isStair;
            }
            if (any) top = best;
            return any;
        }

        private static bool Inside(Box box, float x, float z, float margin = 0f)
        {
            float dx = x - box.X, dz = z - box.Z;
            return Math.Abs(dx * box.Cos + dz * box.Sin) <= box.HalfX + margin && Math.Abs(-dx * box.Sin + dz * box.Cos) <= box.HalfZ + margin;
        }

        // over water a walked piece reaches this much further for the 2 m points: a 2 m deck at a slant missed points of the grid and the
        // route swam beside the bridge (5 Nov test)
        private const float DeckMargin = 0.7f;

        // the same rule as a loaded zone: a box in the space a player fills above the floor is stood on when its top is within StepUp, and
        // blocks when higher. boxes wholly above the head (roofs, bridges) leave the ground walkable. false when no box is here
        internal static bool Point(float x, float z, float floor, out float top, out bool blocked)
        {
            top = floor; blocked = false;
            var cells = BoxesByCell;
            if (BoxCount == 0 || !cells.TryGetValue(Key((int)Math.Floor(x / BoxCell), (int)Math.Floor(z / BoxCell)), out var cell)) return false;
            bool any = false;
            float highest = float.MinValue;
            foreach (var box in cell)
            {
                // the loaded check's body: a 0.3 m wide capsule from knee to head, so it reaches 0.3 m below the knee and above the head
                if (!Inside(box, x, z) || box.Bottom >= floor + Head + Body || box.Top <= floor + Knee - Body) continue;
                any = true;
                highest = Math.Max(highest, box.Top);
            }
            if (!any) return false;
            if (highest - floor <= StepUp) top = highest; else blocked = true;
            return true;
        }

        // true when the straight step between two points passes a box that fills the space a player needs and is too high to step onto
        internal static bool Crosses(float ax, float az, float floorA, float bx, float bz, float floorB)
        {
            var cells = BoxesByCell;
            if (BoxCount == 0) return false;
            float floor = Math.Min(floorA, floorB), rise = Math.Max(floorA, floorB);
            // a step on a measured rock top is judged by the measured heights, the rock's box is no wall there
            bool onRock = RockTop(ax, az, out _) || RockTop(bx, bz, out _);
            int x0 = (int)Math.Floor(Math.Min(ax, bx) / BoxCell), x1 = (int)Math.Floor(Math.Max(ax, bx) / BoxCell);
            int z0 = (int)Math.Floor(Math.Min(az, bz) / BoxCell), z1 = (int)Math.Floor(Math.Max(az, bz) / BoxCell);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!cells.TryGetValue(Key(cx, cz), out var cell)) continue;
                    foreach (var box in cell)
                    {
                        // a stair or a roof is walked up and down (Surface), only the slope limits it. other roof pieces are no walls
                        if (onRock && box.Walk == 0 || box.Walk == Stair || box.Walk == Roof || box.Walk == Open || box.Bottom >= rise + Head || box.Top <= floor + Knee || box.Top - floor <= StepUp) continue;
                        if (SegmentHits(box, ax, az, bx, bz)) return true;
                    }
                }
            return false;
        }

        // the segment in the box's own frame, clipped against both slabs
        private static bool SegmentHits(Box box, float ax, float az, float bx, float bz)
        {
            float px = (ax - box.X) * box.Cos + (az - box.Z) * box.Sin, pz = -(ax - box.X) * box.Sin + (az - box.Z) * box.Cos;
            float qx = (bx - box.X) * box.Cos + (bz - box.Z) * box.Sin, qz = -(bx - box.X) * box.Sin + (bz - box.Z) * box.Cos;
            float t0 = 0f, t1 = 1f;
            return Clip(-box.HalfX, box.HalfX, px, qx - px, ref t0, ref t1) && Clip(-box.HalfZ, box.HalfZ, pz, qz - pz, ref t0, ref t1);
        }

        private static bool Clip(float min, float max, float start, float delta, ref float t0, ref float t1)
        {
            if (Math.Abs(delta) < 1e-6f) return start >= min && start <= max;
            float a = (min - start) / delta, b = (max - start) / delta;
            if (a > b) { float swap = a; a = b; b = swap; }
            t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
            return t0 <= t1;
        }

        internal static bool HasSnapshot(long zone, uint revision) { return Snapshots.TryGetValue(zone, out var s) && s.Revision == revision; }

        internal static void Store(long zone, Snapshot snapshot) { lock (Gate) { Snapshots[zone] = snapshot; JoinWalkHashes(); Version++; } }

        // the remembered real ground at a point (bilinear) and its ground type, false where no snapshot covers it
        internal static bool Ground(float x, float z, out float height, out int ground)
        {
            height = 0f; ground = 0;
            long zone = ZoneOf(x, z);
            if (!Snapshots.TryGetValue(zone, out var s)) return false;
            int zx = (int)(zone >> 32), zz = (int)(uint)zone;
            float gx = (x - (zx * ZoneSize - ZoneSize / 2f)) / Step, gz = (z - (zz * ZoneSize - ZoneSize / 2f)) / Step;
            int i = Math.Max(0, Math.Min(Side - 2, (int)Math.Floor(gx))), j = Math.Max(0, Math.Min(Side - 2, (int)Math.Floor(gz)));
            float fx = Math.Max(0f, Math.Min(1f, gx - i)), fz = Math.Max(0f, Math.Min(1f, gz - j));
            int k = j * Side + i;
            height = (s.Height[k] * (1 - fx) + s.Height[k + 1] * fx) * (1 - fz) + (s.Height[k + Side] * (1 - fx) + s.Height[k + Side + 1] * fx) * fz;
            int gi = Math.Max(0, Math.Min(Side - 1, (int)Math.Round(gx))), gj = Math.Max(0, Math.Min(Side - 1, (int)Math.Round(gz)));
            ground = s.Ground[gj * Side + gi];
            return true;
        }

        // the world point of a snapshot grid point
        internal static void GridPoint(long zone, int i, int j, out float x, out float z)
        {
            int zx = (int)(zone >> 32), zz = (int)(uint)zone;
            x = zx * ZoneSize - ZoneSize / 2f + i * Step; z = zz * ZoneSize - ZoneSize / 2f + j * Step;
        }

        // 2: boxes without the parts above a standing player (tree crowns). 3: each box with its walk kind and ground type, a version 2 file
        // is still read (its boxes walk nothing until the world scan replaces them, its snapshots are kept). 4: the Ashlands rock tops after
        // the snapshots, older files are read without them. 5: the huge rocks' shapes and places after the tops
        private const int FileVersion = 5;

        internal static void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            lock (Gate)
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(FileVersion);
                w.Write(BoxesByZone.Count);
                foreach (var pair in BoxesByZone)
                {
                    w.Write(pair.Key); w.Write(pair.Value.Count);
                    foreach (var b in pair.Value) { w.Write(b.X); w.Write(b.Z); w.Write(b.Cos); w.Write(b.Sin); w.Write(b.HalfX); w.Write(b.HalfZ); w.Write(b.Bottom); w.Write(b.Top); w.Write(b.Walk); w.Write(b.Ground); }
                }
                w.Write(Snapshots.Count);
                foreach (var pair in Snapshots)
                {
                    w.Write(pair.Key); w.Write(pair.Value.Revision);
                    foreach (float h in pair.Value.Height) w.Write(h);
                    w.Write(pair.Value.Ground);
                }
                var tops = Tops;
                w.Write(tops.Count);
                foreach (var pair in tops) { w.Write(pair.Key); foreach (float t in pair.Value) w.Write(t); }
                var shapes = Shapes;
                w.Write(shapes.Length);
                foreach (var sh in shapes) { w.Write(sh.Name); w.Write(sh.MinX); w.Write(sh.MinZ); w.Write(sh.W); w.Write(sh.H); foreach (float t in sh.Top) w.Write(t); }
                w.Write(HugeByZone.Count);
                foreach (var pair in HugeByZone)
                {
                    w.Write(pair.Key); w.Write(pair.Value.Count);
                    foreach (var h in pair.Value) { w.Write(h.X); w.Write(h.Y); w.Write(h.Z); w.Write(h.Cos); w.Write(h.Sin); w.Write(h.SX); w.Write(h.SY); w.Write(h.SZ); w.Write(h.Reach); w.Write(h.Shape); }
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        // false when the file is missing or from another version, then nothing is remembered yet
        internal static bool Load(string path)
        {
            Clear();
            if (!File.Exists(path)) return false;
            lock (Gate)
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                int version = r.ReadInt32();
                if (version != FileVersion && version != 2 && version != 3 && version != 4) return false;
                int zones = r.ReadInt32();
                for (int z = 0; z < zones; z++)
                {
                    long zone = r.ReadInt64(); int count = r.ReadInt32();
                    var list = new List<Box>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var box = new Box { X = r.ReadSingle(), Z = r.ReadSingle(), Cos = r.ReadSingle(), Sin = r.ReadSingle(), HalfX = r.ReadSingle(), HalfZ = r.ReadSingle(), Bottom = r.ReadSingle(), Top = r.ReadSingle(), Zone = zone };
                        if (version >= 3) { box.Walk = r.ReadByte(); box.Ground = r.ReadByte(); }
                        list.Add(box);
                    }
                    BoxesByZone[zone] = list;
                }
                int snaps = r.ReadInt32();
                for (int s = 0; s < snaps; s++)
                {
                    long zone = r.ReadInt64();
                    var snap = new Snapshot { Revision = r.ReadUInt32() };
                    for (int i = 0; i < snap.Height.Length; i++) snap.Height[i] = r.ReadSingle();
                    snap.Ground = r.ReadBytes(Side * Side);
                    Snapshots[zone] = snap;
                }
                if (version >= 4)
                {
                    int count = r.ReadInt32();
                    var tops = new Dictionary<long, float[]>(count);
                    for (int s = 0; s < count; s++)
                    {
                        long zone = r.ReadInt64(); var t = new float[Side * Side];
                        for (int i = 0; i < t.Length; i++) t[i] = r.ReadSingle();
                        tops[zone] = t;
                    }
                    Tops = tops;
                }
                if (version >= 5)
                {
                    var shapes = new Shape[r.ReadInt32()];
                    for (int i = 0; i < shapes.Length; i++)
                    {
                        var sh = new Shape { Name = r.ReadString(), MinX = r.ReadSingle(), MinZ = r.ReadSingle(), W = r.ReadInt32(), H = r.ReadInt32() };
                        sh.Top = new float[sh.W * sh.H];
                        for (int k = 0; k < sh.Top.Length; k++) sh.Top[k] = r.ReadSingle();
                        shapes[i] = sh;
                    }
                    Shapes = shapes;
                    int hugeZones = r.ReadInt32();
                    for (int z = 0; z < hugeZones; z++)
                    {
                        long zone = r.ReadInt64(); int count = r.ReadInt32();
                        var list = new List<Huge>(count);
                        for (int i = 0; i < count; i++)
                            list.Add(new Huge { X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Cos = r.ReadSingle(), Sin = r.ReadSingle(), SX = r.ReadSingle(), SY = r.ReadSingle(), SZ = r.ReadSingle(), Reach = r.ReadSingle(), Shape = r.ReadInt32(), Zone = zone });
                        HugeByZone[zone] = list;
                    }
                    RebuildHuge();
                }
                RebuildCells();
            }
            return true;
        }
    }
}
