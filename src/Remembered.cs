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
        }

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
        internal static int BoxCount, Version;
        internal static int SnapshotCount => Snapshots.Count;

        internal static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }
        internal static long ZoneOf(float x, float z) { return Key((int)Math.Floor((x + ZoneSize / 2f) / ZoneSize), (int)Math.Floor((z + ZoneSize / 2f) / ZoneSize)); }

        // the boxes by zone are changed under Gate, the index by cell is rebuilt there and swapped whole
        private static readonly object Gate = new object();

        internal static void Clear()
        {
            lock (Gate) { BoxesByCell = new Dictionary<long, List<Box>>(); BoxesByZone.Clear(); Snapshots.Clear(); BoxCount = 0; Version++; }
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

        private static bool Inside(Box box, float x, float z)
        {
            float dx = x - box.X, dz = z - box.Z;
            return Math.Abs(dx * box.Cos + dz * box.Sin) <= box.HalfX && Math.Abs(-dx * box.Sin + dz * box.Cos) <= box.HalfZ;
        }

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
            int x0 = (int)Math.Floor(Math.Min(ax, bx) / BoxCell), x1 = (int)Math.Floor(Math.Max(ax, bx) / BoxCell);
            int z0 = (int)Math.Floor(Math.Min(az, bz) / BoxCell), z1 = (int)Math.Floor(Math.Max(az, bz) / BoxCell);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!cells.TryGetValue(Key(cx, cz), out var cell)) continue;
                    foreach (var box in cell)
                    {
                        if (box.Bottom >= rise + Head || box.Top <= floor + Knee || box.Top - floor <= StepUp) continue;
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

        internal static void Store(long zone, Snapshot snapshot) { lock (Gate) { Snapshots[zone] = snapshot; Version++; } }

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

        // 2: boxes without the parts above a standing player (tree crowns)
        private const int FileVersion = 2;

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
                    foreach (var b in pair.Value) { w.Write(b.X); w.Write(b.Z); w.Write(b.Cos); w.Write(b.Sin); w.Write(b.HalfX); w.Write(b.HalfZ); w.Write(b.Bottom); w.Write(b.Top); }
                }
                w.Write(Snapshots.Count);
                foreach (var pair in Snapshots)
                {
                    w.Write(pair.Key); w.Write(pair.Value.Revision);
                    foreach (float h in pair.Value.Height) w.Write(h);
                    w.Write(pair.Value.Ground);
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
                if (r.ReadInt32() != FileVersion) return false;
                int zones = r.ReadInt32();
                for (int z = 0; z < zones; z++)
                {
                    long zone = r.ReadInt64(); int count = r.ReadInt32();
                    var list = new List<Box>(count);
                    for (int i = 0; i < count; i++)
                        list.Add(new Box { X = r.ReadSingle(), Z = r.ReadSingle(), Cos = r.ReadSingle(), Sin = r.ReadSingle(), HalfX = r.ReadSingle(), HalfZ = r.ReadSingle(), Bottom = r.ReadSingle(), Top = r.ReadSingle(), Zone = zone });
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
                RebuildCells();
            }
            return true;
        }
    }
}
