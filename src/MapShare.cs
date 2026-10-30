using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace ValheimNavigator
{
    // the host's map data for guests: terrain edits and visited ground by zone, the player building pieces and the placed locations of the
    // whole world. a guest's game holds the world data only near where it has been, so without this its detailed map lacks the host's bases,
    // paths and location ground far away. sent in chunks inside the host's transfer budget, asked again every RefreshSeconds, and an
    // unchanged copy is not sent again. the guest's own world data wins wherever it has some
    internal static class MapShare
    {
        private const string RequestName = "ValheimNavigator.MapRequest1", DataName = "ValheimNavigator.MapData1";
        internal const int Protocol = 1;
        private const int Magic = 0x4E564D31, Points = 33;
        internal const int MaxZones = 20000, MaxPieces = 500000, MaxPlaced = 100000, MaxGraves = 10000;
        // the host walks its world objects for player pieces this often, encodes a changed copy at most this often, and waits this long for
        // the location walk to finish before it sends without placed locations
        private const float WalkSeconds = 30f, EncodeSeconds = 30f, PlacedWaitSeconds = 15f, CleanupSeconds = 30f, MinimumRequestSeconds = 3f;
        private const double SliceMilliseconds = 0.5;
        private const int UnansweredLimit = 3;

        internal sealed class MapData
        {
            internal long World;
            internal int Generation;
            internal readonly List<KeyValuePair<long, TerrainEdits.Zone>> Zones = new List<KeyValuePair<long, TerrainEdits.Zone>>();
            internal readonly List<KeyValuePair<long, short[]>> Visited = new List<KeyValuePair<long, short[]>>();
            internal readonly List<Buildings.SharedPiece> Pieces = new List<Buildings.SharedPiece>();
            internal readonly List<LocationShaping.Placed> Placed = new List<LocationShaping.Placed>();
            internal readonly List<Tombstones.Grave> Graves = new List<Tombstones.Grave>();
        }

        private sealed class Pending
        {
            internal long Nonce;
            internal string Hash, BlobHash;
            internal byte[] Bytes;
            internal int Index;
        }

        private static ZNet _net;
        private static long _world;
        private static int _generation;
        private static readonly HashSet<ZRpc> Registered = new HashSet<ZRpc>();
        private static readonly List<ZRpc> Remove = new List<ZRpc>();
        private static float _cleanupAt;
        private static bool _warned;

        // host
        private static readonly Dictionary<ZRpc, Pending> PendingPeers = new Dictionary<ZRpc, Pending>();
        private static readonly Dictionary<ZRpc, float> RequestedAt = new Dictionary<ZRpc, float>();
        private static System.Reflection.FieldInfo _sectorsField;
        private static List<ZDO>[] _sectors;
        private static int _sector = -1, _inSector;
        private static List<Buildings.SharedPiece> _walking = new List<Buildings.SharedPiece>(), _walked;
        private static ulong _walkHash, _piecesHash, _blobSignature;
        private static float _walkAt, _encodedAt, _placedWaitFrom;
        private static byte[] _blob;
        private static string _blobHash;
        private static Task<byte[]> _encoding;

        // guest
        private static long _nonce;
        private static float _next, _deadline;
        private static int _unanswered;
        private static bool _answered, _applied;
        private static WorldTransfer _transfer;
        private static Task<MapData> _decoding;
        private static string _hash, _incoming;

        internal static void Tick()
        {
            try
            {
                var net = ZNet.instance;
                var world = net != null ? net.GetWorld() : null;
                long uid = world != null ? world.m_uid : 0L;
                if (net != _net || uid != _world) { Reset(); _net = net; _world = uid; _generation = world != null ? world.m_worldGenVersion : 0; }
                if (_net == null || _world == 0 || !Plugin.Enabled.Value) return;
                foreach (var peer in _net.GetPeers())
                {
                    if (peer.m_rpc == null || !Registered.Add(peer.m_rpc)) continue;
                    peer.m_rpc.Register<ZPackage>(RequestName, Request);
                    peer.m_rpc.Register<ZPackage>(DataName, Data);
                }
                if (Time.unscaledTime >= _cleanupAt)
                {
                    Remove.Clear();
                    foreach (var rpc in Registered) if (Peer(rpc) == null) Remove.Add(rpc);
                    foreach (var rpc in Remove) { Registered.Remove(rpc); RequestedAt.Remove(rpc); PendingPeers.Remove(rpc); }
                    _cleanupAt = Time.unscaledTime + CleanupSeconds;
                }
                if (_net.IsServer()) Host(); else Guest();
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Reset()
        {
            foreach (var rpc in Registered) { rpc.Unregister(RequestName); rpc.Unregister(DataName); }
            Registered.Clear(); PendingPeers.Clear(); RequestedAt.Clear();
            _sector = -1; _walking.Clear(); _walked = null; _walkHash = _piecesHash = _blobSignature = 0; _walkAt = _encodedAt = _placedWaitFrom = 0f;
            _blob = null; _blobHash = null; _encoding = null;
            _next = _deadline = 0f; _unanswered = 0; _answered = false; _transfer = null; _decoding = null; _hash = _incoming = null;
            if (_applied) Apply(null);
            _cleanupAt = 0f;
        }

        private static void Note(string message)
        {
            if (Plugin.LogCounters.Value && Plugin.Log != null) Plugin.Log.LogInfo(message);
        }

        private static void Warn(Exception error)
        {
            if (!_warned && Plugin.Log != null) { _warned = true; Plugin.Log.LogWarning("map data could not be shared (" + error.GetType().Name + ": " + error.Message + "). The map uses this game's own world data."); }
        }

        private static ZNetPeer Peer(ZRpc rpc)
        {
            if (_net == null) return null;
            foreach (var peer in _net.GetPeers()) if (peer.m_rpc == rpc && peer.m_uid != 0) return peer;
            return null;
        }

        // ---- host ----

        private static void Request(ZRpc rpc, ZPackage package)
        {
            if (_net == null || !_net.IsServer() || Peer(rpc) == null) return;
            try
            {
                byte[] bytes = package.GetArray();
                if (bytes.Length != 25 && bytes.Length != 89) return;
                using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    int protocol = reader.ReadInt32(); long world = reader.ReadInt64(); int generation = reader.ReadInt32(); long nonce = reader.ReadInt64();
                    bool known = reader.ReadBoolean();
                    string hash = known && bytes.Length == 89 ? Encoding.ASCII.GetString(reader.ReadBytes(64)) : null;
                    if (protocol != Protocol || world != _world || generation != _generation) { Send(rpc, nonce, -3, null, null, 0); return; }
                    if (hash != null && !WorldCodec.ValidHash(hash)) return;
                    float now = Time.unscaledTime;
                    if (RequestedAt.TryGetValue(rpc, out float last) && now - last < MinimumRequestSeconds) return;
                    RequestedAt[rpc] = now;
                    if (!Plugin.Enabled.Value || !Settings.ShareMap.Value) { Send(rpc, nonce, -1, null, null, 0); return; }
                    PendingPeers[rpc] = new Pending { Nonce = nonce, Hash = hash };
                    Send(rpc, nonce, 2, null, null, 0);
                }
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Host()
        {
            if (!Settings.ShareMap.Value)
            {
                foreach (var pair in PendingPeers) Send(pair.Key, pair.Value.Nonce, -1, null, null, 0);
                PendingPeers.Clear(); return;
            }
            if (_net.GetPeers().Count == 0) return;
            Walk();
            if (_encoding != null && _encoding.IsCompleted)
            {
                var task = _encoding; _encoding = null;
                _blob = task.GetAwaiter().GetResult(); _blobHash = WorldCodec.Hash(_blob);
                Note("host map data ready for guests, " + _blob.Length + " bytes");
            }
            if (PendingPeers.Count == 0) return;
            float now = Time.unscaledTime;
            ulong signature;
            unchecked { signature = _piecesHash * 0x9E3779B97F4A7C15UL ^ (ulong)TerrainEdits.Version * 0xC2B2AE3D27D4EB4FUL ^ LocationShaping.Signature * 0x165667B19E3779F9UL ^ (ulong)Tombstones.Version * 0x27D4EB2F165667C5UL; }
            if (_encoding == null && _walked != null && (_blob == null || signature != _blobSignature && now - _encodedAt >= EncodeSeconds)) Encode(now, signature);
            if (_blob == null) return;
            int budget = Settings.SyncKilobytes.Value * 1024;
            Remove.Clear();
            foreach (var pair in PendingPeers)
            {
                var peer = Peer(pair.Key);
                if (peer == null) { Remove.Add(pair.Key); continue; }
                var pending = pair.Value;
                if (pending.Bytes == null)
                {
                    if (pending.Hash == _blobHash) { Send(pair.Key, pending.Nonce, 1, null, null, 0); Remove.Add(pair.Key); continue; }
                    pending.Bytes = _blob; pending.BlobHash = _blobHash;
                }
                if (peer.m_socket != null && peer.m_socket.GetSendQueueSize() > WorldCodec.ChunkBytes * 4) continue;
                if (budget < WorldCodec.ChunkBytes) break;
                Send(pair.Key, pending.Nonce, 0, pending.BlobHash, pending.Bytes, pending.Index++); budget -= WorldCodec.ChunkBytes;
                if (pending.Index * WorldCodec.ChunkBytes >= pending.Bytes.Length) Remove.Add(pair.Key);
            }
            foreach (var rpc in Remove) PendingPeers.Remove(rpc);
        }

        // a sliced walk over the host's world objects for the pieces players placed, every WalkSeconds while guests are connected
        private static void Walk()
        {
            float now = Time.unscaledTime;
            if (ZDOMan.instance == null || ZNetScene.instance == null) return;
            if (_sector < 0)
            {
                if (now < _walkAt) return;
                if (_sectorsField == null) _sectorsField = HarmonyLib.AccessTools.Field(typeof(ZDOMan), "m_objectsBySector");
                _sectors = _sectorsField?.GetValue(ZDOMan.instance) as List<ZDO>[];
                if (_sectors == null) { _walkAt = now + WalkSeconds; return; }
                _sector = 0; _inSector = 0; _walking.Clear(); _walkHash = 0;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int steps = 0;
            while (_sector < _sectors.Length)
            {
                if ((++steps & 15) == 0 && clock.Elapsed.TotalMilliseconds > SliceMilliseconds) return;
                var list = _sectors[_sector];
                if (list == null || _inSector >= list.Count) { _sector++; _inSector = 0; continue; }
                var zdo = list[_inSector++];
                if (zdo == null || !zdo.IsValid()) continue;
                int prefab = zdo.GetPrefab();
                if (!Buildings.IsPiece(prefab) || zdo.GetLong(ZDOVars.s_creator, 0L) == 0L || _walking.Count >= MaxPieces) continue;
                var p = zdo.GetPosition();
                float angle = zdo.GetRotation().eulerAngles.y * Mathf.Deg2Rad;
                _walking.Add(new Buildings.SharedPiece { Prefab = prefab, X = p.x, Y = p.y, Z = p.z, Angle = angle });
                unchecked { ulong key = Buildings.PieceKey(prefab, p) ^ (ulong)(long)Mathf.RoundToInt(angle * 100f) * 0xFF51AFD7ED558CCDUL; _walkHash += key ^ (key >> 29); }
            }
            var done = _walked ?? new List<Buildings.SharedPiece>();
            _walked = _walking; _walking = done;
            _piecesHash = _walkHash ^ (ulong)_walked.Count;
            _sector = -1; _walkAt = now + WalkSeconds;
        }

        // the copy is taken on the game's thread (zones and height arrays are never changed in place), the encoding runs in the background
        private static void Encode(float now, ulong signature)
        {
            var data = new MapData { World = _world, Generation = _generation };
            if (!LocationShaping.Snapshot(data.Placed))
            {
                if (_placedWaitFrom <= 0f) _placedWaitFrom = now;
                if (now - _placedWaitFrom < PlacedWaitSeconds) return;
            }
            _placedWaitFrom = 0f;
            TerrainEdits.Snapshot(data.Zones, data.Visited);
            data.Pieces.AddRange(_walked);
            data.Graves.AddRange(Tombstones.Own);
            _blobSignature = signature; _encodedAt = now;
            _encoding = Task.Run(() => Encode(data));
        }

        private static void Send(ZRpc rpc, long nonce, int kind, string hash, byte[] bytes, int index)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Protocol); writer.Write(_world); writer.Write(_generation); writer.Write(nonce); writer.Write(kind);
                if (kind == 0)
                {
                    int offset = index * WorldCodec.ChunkBytes, count = Math.Min(WorldCodec.ChunkBytes, bytes.Length - offset);
                    writer.Write(Encoding.ASCII.GetBytes(hash)); writer.Write(bytes.Length); writer.Write(index); writer.Write(count); writer.Write(bytes, offset, count);
                }
                writer.Flush(); rpc.Invoke(DataName, new ZPackage(stream.ToArray()));
            }
        }

        // ---- guest ----

        private static void Guest()
        {
            float now = Time.unscaledTime;
            if (_decoding != null && _decoding.IsCompleted)
            {
                var task = _decoding; _decoding = null;
                try { var data = task.GetAwaiter().GetResult(); _hash = _incoming; Apply(data); }
                catch (Exception ex) { Warn(ex); }
            }
            var server = _net.GetServerPeer();
            if (server == null || server.m_uid == 0 || server.m_rpc == null || !Registered.Contains(server.m_rpc) || Player.m_localPlayer == null) return;
            if (_transfer != null && now >= _deadline)
            {
                _transfer = null;
                if (!_answered && ++_unanswered >= UnansweredLimit) Note("this host does not share map data (no answer), the map uses this game's own world data");
            }
            if (_transfer == null && _decoding == null && now >= _next && _unanswered < UnansweredLimit) Ask(server);
        }

        private static void Ask(ZNetPeer server)
        {
            float now = Time.unscaledTime;
            _nonce++; _answered = false;
            _transfer = new WorldTransfer(_world, _generation, _nonce);
            _deadline = now + Settings.SyncTimeout.Value; _next = now + Settings.SyncSeconds.Value;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Protocol); writer.Write(_world); writer.Write(_generation); writer.Write(_nonce);
                writer.Write(_hash != null);
                if (_hash != null) writer.Write(Encoding.ASCII.GetBytes(_hash));
                writer.Flush(); server.m_rpc.Invoke(RequestName, new ZPackage(stream.ToArray()));
            }
        }

        private static void Data(ZRpc rpc, ZPackage package)
        {
            if (_net == null || _net.IsServer() || _net.GetServerPeer()?.m_rpc != rpc) return;
            try
            {
                byte[] bytes = package.GetArray();
                if (bytes.Length < 28 || bytes.Length > WorldCodec.ChunkBytes + 128) return;
                using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    int protocol = reader.ReadInt32(); long world = reader.ReadInt64(); int generation = reader.ReadInt32(); long nonce = reader.ReadInt64(); int kind = reader.ReadInt32();
                    if (nonce != _nonce || _transfer == null) return;
                    float now = Time.unscaledTime;
                    if (protocol != Protocol || kind == -3) { _transfer = null; _unanswered = UnansweredLimit; Note("this host uses a different Navigator map data version, the map uses this game's own world data"); return; }
                    if (world != _world || generation != _generation) return;
                    _answered = true; _unanswered = 0;
                    if (kind == -1) { _transfer = null; _hash = null; if (_applied) Apply(null); Note("this host does not share map data (ShareMap off)"); return; }
                    if (kind == 1) { _transfer = null; return; }
                    // the host may first walk its world and encode, so the wait is longer after its first answer
                    if (kind == 2) { _deadline = now + Settings.SyncTimeout.Value + WalkSeconds + PlacedWaitSeconds; return; }
                    if (kind != 0) return;
                    string hash = Encoding.ASCII.GetString(reader.ReadBytes(64));
                    if (!WorldCodec.ValidHash(hash)) return;
                    int length = reader.ReadInt32(), index = reader.ReadInt32(), count = reader.ReadInt32();
                    if (count < 0 || count > WorldCodec.ChunkBytes || count != bytes.Length - reader.BaseStream.Position) return;
                    if (!_transfer.Add(world, generation, nonce, hash, length, index, reader.ReadBytes(count))) return;
                    _deadline = now + Settings.SyncTimeout.Value;
                    if (!_transfer.Complete) return;
                    byte[] data = _transfer.Bytes; _transfer = null;
                    if (WorldCodec.Hash(data) != hash) return;
                    _incoming = hash;
                    long id = _world; int version = _generation;
                    _decoding = Task.Run(() => Decode(data, id, version));
                }
            }
            catch (Exception ex) { Warn(ex); }
        }

        // the host's copy into the map parts (null clears it)
        private static void Apply(MapData data)
        {
            _applied = data != null;
            if (data == null)
            {
                TerrainEdits.SetShared(null, null); Buildings.SetShared(null); LocationShaping.SetShared(null); Tombstones.SetShared(null);
                return;
            }
            var zones = new Dictionary<long, TerrainEdits.Zone>(data.Zones.Count);
            foreach (var pair in data.Zones) zones[pair.Key] = pair.Value;
            var visited = new Dictionary<long, short[]>(data.Visited.Count);
            foreach (var pair in data.Visited) visited[pair.Key] = pair.Value;
            TerrainEdits.SetShared(zones, visited);
            Buildings.SetShared(data.Pieces.ToArray());
            LocationShaping.SetShared(data.Placed.ToArray());
            Tombstones.SetShared(data.Graves.ToArray());
            Note("map data from the host: " + zones.Count + " edited zones, " + visited.Count + " visited zones, " + data.Pieces.Count + " player pieces, " + data.Placed.Count + " placed locations, " + data.Graves.Count + " tombstones");
        }

        // ---- format: deflated, "NVM1", protocol, world, generation, then the zones, visited zones, pieces and placed locations ----

        internal static byte[] Encode(MapData data)
        {
            using (var output = new MemoryStream())
            {
                using (var writer = new BinaryWriter(new BufferedStream(new DeflateStream(output, CompressionMode.Compress, true), 1 << 16)))
                {
                    writer.Write(Magic); writer.Write(Protocol); writer.Write(data.World); writer.Write(data.Generation);
                    writer.Write(data.Zones.Count);
                    foreach (var pair in data.Zones)
                    {
                        writer.Write(pair.Key); writer.Write(pair.Value.Changed); writer.Write(pair.Value.Painted);
                        foreach (short h in pair.Value.Height) writer.Write(h);
                        writer.Write(pair.Value.Paint);
                    }
                    writer.Write(data.Visited.Count);
                    foreach (var pair in data.Visited) { writer.Write(pair.Key); foreach (short h in pair.Value) writer.Write(h); }
                    writer.Write(data.Pieces.Count);
                    foreach (var p in data.Pieces) { writer.Write(p.Prefab); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Angle); }
                    writer.Write(data.Placed.Count);
                    foreach (var p in data.Placed) { writer.Write(p.Location); writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); writer.Write(p.Angle); }
                    // appended after the first version's sections, a guest of that version stops reading before it
                    writer.Write(data.Graves.Count);
                    foreach (var g in data.Graves) { writer.Write(g.Owner ?? ""); writer.Write(g.X); writer.Write(g.Y); writer.Write(g.Z); writer.Write(g.Died); }
                }
                return output.ToArray();
            }
        }

        internal static MapData Decode(byte[] bytes, long world, int generation)
        {
            using (var reader = new BinaryReader(new BufferedStream(new DeflateStream(new MemoryStream(bytes, false), CompressionMode.Decompress), 1 << 16)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Protocol) throw new InvalidDataException("not Navigator map data");
                var data = new MapData { World = reader.ReadInt64(), Generation = reader.ReadInt32() };
                if (data.World != world || data.Generation != generation) throw new InvalidDataException("map data of another world");
                int zones = Count(reader, MaxZones);
                for (int i = 0; i < zones; i++)
                {
                    long key = reader.ReadInt64();
                    var zone = new TerrainEdits.Zone { Changed = reader.ReadInt32(), Painted = reader.ReadInt32() };
                    for (int k = 0; k < Points * Points; k++) zone.Height[k] = reader.ReadInt16();
                    byte[] paint = reader.ReadBytes(Points * Points);
                    if (paint.Length != Points * Points) throw new EndOfStreamException();
                    for (int k = 0; k < paint.Length; k++)
                    {
                        if (paint[k] > TerrainEdits.PaintCleared) throw new InvalidDataException("paint class");
                        zone.Paint[k] = paint[k];
                    }
                    data.Zones.Add(new KeyValuePair<long, TerrainEdits.Zone>(key, zone));
                }
                int visited = Count(reader, MaxZones);
                for (int i = 0; i < visited; i++)
                {
                    long key = reader.ReadInt64();
                    var heights = new short[Points * Points];
                    for (int k = 0; k < heights.Length; k++) heights[k] = reader.ReadInt16();
                    data.Visited.Add(new KeyValuePair<long, short[]>(key, heights));
                }
                int pieces = Count(reader, MaxPieces);
                for (int i = 0; i < pieces; i++)
                {
                    var p = new Buildings.SharedPiece { Prefab = reader.ReadInt32(), X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Angle = reader.ReadSingle() };
                    if (Inside(p.X, p.Y, p.Z) && !float.IsNaN(p.Angle) && !float.IsInfinity(p.Angle)) data.Pieces.Add(p);
                }
                int placed = Count(reader, MaxPlaced);
                for (int i = 0; i < placed; i++)
                {
                    var p = new LocationShaping.Placed { Location = reader.ReadInt32(), X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Angle = reader.ReadSingle() };
                    if (Inside(p.X, p.Y, p.Z) && !float.IsNaN(p.Angle) && !float.IsInfinity(p.Angle)) data.Placed.Add(p);
                }
                // a host of the first version sends no tombstones
                int graves;
                try { graves = Count(reader, MaxGraves); } catch (EndOfStreamException) { return data; }
                for (int i = 0; i < graves; i++)
                {
                    string owner = reader.ReadString();
                    var g = new Tombstones.Grave { Owner = owner.Length > 64 ? owner.Substring(0, 64) : owner, X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Died = reader.ReadInt64() };
                    if (Inside(g.X, g.Y, g.Z)) data.Graves.Add(g);
                }
                return data;
            }
        }

        private static int Count(BinaryReader reader, int limit)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > limit) throw new InvalidDataException("count " + count);
            return count;
        }

        private static bool Inside(float x, float y, float z) =>
            Math.Abs(x) <= WorldCodec.CoordinateLimit && Math.Abs(z) <= WorldCodec.CoordinateLimit && Math.Abs(y) <= WorldCodec.CoordinateLimit;
    }
}
