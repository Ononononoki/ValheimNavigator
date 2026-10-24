using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ValheimNavigator
{
    // plain world facts, safe to encode and validate away from the game thread
    internal sealed class WorldCatalog
    {
        internal long World;
        internal int Generation;
        internal readonly List<Raw> Locations = new List<Raw>();
        internal readonly List<WorldObject> Portals = new List<WorldObject>();
        internal readonly List<WorldObject> Ships = new List<WorldObject>();
    }

    internal sealed class WorldObject
    {
        internal string Id, Name;
        internal float X, Y, Z;
    }

    internal sealed class WorldBlob
    {
        internal byte[] Bytes;
        internal string Hash;
        internal WorldCatalog Catalog;
    }

    internal static class WorldCodec
    {
        internal const int Protocol = 1;
        internal const int MaxBytes = 16 * 1024 * 1024;
        internal const int MaxRecords = 100000;
        internal const int MaxObjects = 20000;
        internal const int MaxString = 512;
        internal const int ChunkBytes = 24 * 1024;
        internal const float CoordinateLimit = 20000f;
        private const int Magic = 0x564E4331;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        internal static bool ValidHash(string hash)
        {
            if (hash == null || hash.Length != 64) return false;
            foreach (char c in hash) if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }

        private static void Text(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value ?? "");
            if (bytes.Length > MaxString) throw new InvalidDataException("World text is too long.");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }

        private static string Text(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            if (count > MaxString || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid world text.");
            return Utf8.GetString(reader.ReadBytes(count));
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= CoordinateLimit; }
        private static void Point(BinaryWriter writer, float x, float y, float z)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z)) throw new InvalidDataException("Invalid world coordinate.");
            writer.Write(x); writer.Write(y); writer.Write(z);
        }

        private static void Objects(BinaryWriter writer, List<WorldObject> objects)
        {
            if (objects.Count > MaxObjects) throw new InvalidDataException("Too many navigation objects.");
            writer.Write(objects.Count);
            foreach (var item in objects) { Text(writer, item.Id); Text(writer, item.Name); Point(writer, item.X, item.Y, item.Z); }
        }

        private static void Objects(BinaryReader reader, List<WorldObject> objects)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxObjects || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 16) throw new InvalidDataException("Invalid navigation object count.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                var item = new WorldObject { Id = Text(reader), Name = Text(reader), X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                if (item.Id.Length == 0 || !ids.Add(item.Id) || !Finite(item.X) || !Finite(item.Y) || !Finite(item.Z)) throw new InvalidDataException("Invalid navigation object.");
                objects.Add(item);
            }
        }

        internal static WorldBlob Encode(WorldCatalog catalog)
        {
            if (catalog.Locations.Count > MaxRecords) throw new InvalidDataException("Too many world locations.");
            byte[] bytes;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8, true))
            {
                writer.Write(Magic); writer.Write(Protocol); writer.Write(catalog.World); writer.Write(catalog.Generation);
                writer.Write(catalog.Locations.Count);
                foreach (var site in catalog.Locations)
                {
                    if (string.IsNullOrEmpty(site.Prefab)) throw new InvalidDataException("Missing location prefab.");
                    Text(writer, site.Prefab); Text(writer, site.Biome);
                    Point(writer, site.X, site.Y, site.Z);
                    writer.Write((byte)((site.Unique ? 1 : 0) | (site.Placed ? 2 : 0)));
                    if (stream.Length > MaxBytes) throw new InvalidDataException("World catalog is too large.");
                }
                Objects(writer, catalog.Portals); Objects(writer, catalog.Ships);
                writer.Flush();
                if (stream.Length > MaxBytes) throw new InvalidDataException("World catalog is too large.");
                bytes = stream.ToArray();
            }
            return new WorldBlob { Bytes = bytes, Hash = Hash(bytes), Catalog = catalog };
        }

        internal static WorldBlob Decode(byte[] bytes, long world, int generation, string hash = null)
        {
            if (bytes == null || bytes.Length < 32 || bytes.Length > MaxBytes) throw new InvalidDataException("Invalid world catalog size.");
            string actual = Hash(bytes);
            if (hash != null && (!ValidHash(hash) || !string.Equals(hash, actual, StringComparison.Ordinal))) throw new InvalidDataException("World catalog checksum differs.");
            var catalog = new WorldCatalog();
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, Utf8))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Protocol) throw new InvalidDataException("Unsupported world catalog.");
                catalog.World = reader.ReadInt64(); catalog.Generation = reader.ReadInt32();
                if (catalog.World != world || catalog.Generation != generation) throw new InvalidDataException("World catalog belongs to another world.");
                int count = reader.ReadInt32();
                if (count < 0 || count > MaxRecords || count > (stream.Length - stream.Position) / 17) throw new InvalidDataException("Invalid location count.");
                for (int i = 0; i < count; i++)
                {
                    var site = new Raw { Prefab = Text(reader), Biome = Text(reader), X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle() };
                    byte flags = reader.ReadByte();
                    if (site.Prefab.Length == 0 || flags > 3 || !Finite(site.X) || !Finite(site.Y) || !Finite(site.Z)) throw new InvalidDataException("Invalid world location.");
                    site.Unique = (flags & 1) != 0; site.Placed = (flags & 2) != 0;
                    catalog.Locations.Add(site);
                }
                Objects(reader, catalog.Portals); Objects(reader, catalog.Ships);
                if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected catalog data.");
            }
            return new WorldBlob { Bytes = bytes, Hash = actual, Catalog = catalog };
        }

        internal static string CachePath(string folder, long world, int generation)
        {
            return Path.Combine(folder, "world_" + world.ToString("X16") + "_" + generation + "_" + Protocol + ".catalog");
        }

        internal static WorldBlob ReadCache(string path, long world, int generation)
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            return Decode(File.ReadAllBytes(path), world, generation);
        }

        internal static void SaveCache(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".pending";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    // validates chunk identity before allocating and never exposes an incomplete catalog
    internal sealed class WorldTransfer
    {
        internal readonly long World, Nonce;
        internal readonly int Generation;
        private byte[] _bytes;
        private bool[] _received;
        private int _count;
        private string _hash;
        internal int Received => _count;
        internal bool Complete => _received != null && _count == _received.Length;
        internal string Hash => _hash;
        internal byte[] Bytes => Complete ? _bytes : null;

        internal WorldTransfer(long world, int generation, long nonce) { World = world; Generation = generation; Nonce = nonce; }

        internal bool Add(long world, int generation, long nonce, string hash, int length, int index, byte[] chunk)
        {
            if (world != World || generation != Generation || nonce != Nonce || !WorldCodec.ValidHash(hash)
                || length < 32 || length > WorldCodec.MaxBytes || chunk == null) return false;
            int chunks = (length + WorldCodec.ChunkBytes - 1) / WorldCodec.ChunkBytes;
            if (index < 0 || index >= chunks || chunk.Length != Math.Min(WorldCodec.ChunkBytes, length - index * WorldCodec.ChunkBytes)) return false;
            if (_bytes == null) { _bytes = new byte[length]; _received = new bool[chunks]; _hash = hash; }
            if (_bytes.Length != length || _hash != hash) return false;
            int offset = index * WorldCodec.ChunkBytes;
            if (_received[index])
            {
                for (int i = 0; i < chunk.Length; i++) if (_bytes[offset + i] != chunk[i]) return false;
                return true;
            }
            Buffer.BlockCopy(chunk, 0, _bytes, offset, chunk.Length); _received[index] = true; _count++;
            return true;
        }
    }
}
