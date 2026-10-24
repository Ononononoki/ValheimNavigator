using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace ValheimNavigator
{
    // optional peer service, neither a guest's route nor personal progress is transmitted
    internal static class WorldSync
    {
        private const string RequestName = "ValheimNavigator.WorldRequest1";
        private const string DataName = "ValheimNavigator.WorldData1";
        private const float MinimumRequestSeconds = 3f;
        private const int MaximumDiscoveryRequests = 3;
        private const int MaximumCachedWorlds = 8;
        // the host reads its location list again only when its count changed or after this long (placed flags), and rescans its world
        // objects for ship positions after this long. a changed portal list is sent at once with the last known ships
        private const float LocationRefreshSeconds = 600f, ShipRefreshSeconds = 300f;
        private static ZNet _net;
        private static long _world, _nonce;
        private static int _generation, _discoveryAttempts;
        private static readonly HashSet<ZRpc> Registered = new HashSet<ZRpc>();
        private static readonly Dictionary<ZRpc, Pending> PendingPeers = new Dictionary<ZRpc, Pending>();
        private static readonly Dictionary<ZRpc, float> RequestedAt = new Dictionary<ZRpc, float>();
        private static readonly List<ZRpc> Remove = new List<ZRpc>();
        private static WorldBlob _host, _navigation, _active, _cached;
        private static Task<WorldBlob> _encoding, _encodingNavigation, _decoding, _decodingNavigation, _reading;
        private static WorldTransfer _transfer, _navigationTransfer;
        private static IEnumerator<ZoneSystem.LocationInstance> _capture;
        private static WorldCatalog _capturing;
        private static float _hostAt, _navigationAt, _deadline, _next, _captureRetry, _encodingAt, _encodingNavigationAt, _cleanupAt, _lastAskedAt;
        private static bool _asked, _responded, _warned;
        private static string _navigationHash;
        private static int _hostSource, _navigationPortals;
        private static float _shipsAt;
        internal static int Version, NavigationVersion;
        internal static string Status = "Load a world first.";
        // the local player is in the world, the sandbox replaces it
        internal static Func<bool> Spawned = () => Player.m_localPlayer != null;
        internal static List<Raw> Locations => _active?.Catalog.Locations;
        internal static readonly List<WorldObject> SharedPortals = new List<WorldObject>();
        internal static readonly List<WorldObject> SharedShips = new List<WorldObject>();
        internal static long World => _world;
        internal static bool Supported => _responded;

        private sealed class Pending
        {
            internal long Nonce;
            internal string Hash;
            internal bool WantNavigation;
            internal int Stage, Index;
            internal WorldBlob Blob;
        }

        private static bool Guest => _net != null && !_net.IsServer();
        private static string CacheFile => WorldCodec.CachePath(Path.Combine(Paths.ConfigPath, "ValheimNavigator", "worldcache"), _world, _generation);

        internal static void Tick()
        {
            var net = ZNet.instance;
            var world = net != null ? net.GetWorld() : null;
            long uid = world != null ? world.m_uid : 0L;
            if (net != _net || uid != _world)
            {
                Reset(); _net = net; _world = uid; _generation = world != null ? world.m_worldGenVersion : 0;
                if (uid != 0 && Guest)
                {
                    string path = CacheFile; long id = _world; int generation = _generation;
                    _reading = Task.Run(() => { try { return WorldCodec.ReadCache(path, id, generation); } catch { return null; } });
                    Status = "Connecting to this host. Pins and manual routes are available.";
                }
            }
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
                _cleanupAt = Time.unscaledTime + Settings.SyncTimeout.Value;
            }
            if (_net.IsServer()) Host(); else Client();
        }

        private static void Reset()
        {
            foreach (var rpc in Registered) { rpc.Unregister(RequestName); rpc.Unregister(DataName); }
            Registered.Clear(); PendingPeers.Clear(); RequestedAt.Clear();
            _capture?.Dispose(); _capture = null; _capturing = null;
            _host = _navigation = _active = _cached = null;
            _encoding = _encodingNavigation = _decoding = _decodingNavigation = _reading = null;
            _transfer = _navigationTransfer = null;
            SharedPortals.Clear(); SharedShips.Clear(); KnownWorld.Forget(); Version++; NavigationVersion++;
            _asked = _responded = _warned = false; _hostAt = _navigationAt = _captureRetry = _next = _cleanupAt = 0f;
            _navigationHash = null; _hostSource = _navigationPortals = 0; _shipsAt = 0f;
            _discoveryAttempts = 0; _lastAskedAt = 0f;
            Status = "Load a world first.";
        }

        private static void Note(string message)
        {
            if (Plugin.LogCounters.Value && Plugin.Log != null) Plugin.Log.LogInfo(message);
        }

        private static void Warn(Exception error)
        {
            if (!_warned && Plugin.Log != null) { _warned = true; Plugin.Log.LogWarning("world data could not be synchronized (" + error.GetType().Name + "). Local navigation remains available."); }
        }

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
                    bool navigation = reader.ReadBoolean();
                    string hash = bytes.Length == 89 ? Encoding.ASCII.GetString(reader.ReadBytes(64)) : "";
                    if (protocol != WorldCodec.Protocol || world != _world || generation != _generation) { Send(rpc, nonce, -3, null, 0); return; }
                    if (hash.Length != 0 && !WorldCodec.ValidHash(hash)) return;
                    float now = Time.unscaledTime;
                    if (RequestedAt.TryGetValue(rpc, out float last) && now - last < MinimumRequestSeconds) return;
                    RequestedAt[rpc] = now;
                    if (!Plugin.Enabled.Value || !Settings.ShareLocations.Value) { Send(rpc, nonce, -1, null, 0); return; }
                    PendingPeers[rpc] = new Pending { Nonce = nonce, Hash = hash, WantNavigation = navigation };
                    Send(rpc, nonce, 2, null, 0);
                }
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Host()
        {
            if (PendingPeers.Count == 0) return;
            float now = Time.unscaledTime;
            if (!Settings.ShareLocations.Value)
            {
                foreach (var pair in PendingPeers) Send(pair.Key, pair.Value.Nonce, -1, null, 0);
                PendingPeers.Clear(); return;
            }
            try
            {
                if (_encoding != null && _encoding.IsCompleted)
                {
                    _host = _encoding.GetAwaiter().GetResult(); _encoding = null; _hostAt = _encodingAt;
                    Note("host world catalog ready, " + _host.Catalog.Locations.Count + " locations, " + _host.Bytes.Length + " bytes");
                }
                if (_encodingNavigation != null && _encodingNavigation.IsCompleted)
                { _navigation = _encodingNavigation.GetAwaiter().GetResult(); _encodingNavigation = null; _navigationAt = _encodingNavigationAt; }
                bool hostStale = HostStale(now);
                if (hostStale) Capture();
                int budget = Settings.SyncKilobytes.Value * 1024;
                Remove.Clear();
                foreach (var pair in PendingPeers)
                {
                    var peer = Peer(pair.Key);
                    if (peer == null) { Remove.Add(pair.Key); continue; }
                    if (peer.m_socket != null && peer.m_socket.GetSendQueueSize() > WorldCodec.ChunkBytes * 4) continue;
                    var pending = pair.Value;
                    if (pending.Stage == 0)
                    {
                        if (pending.Blob == null)
                        {
                            if (hostStale || _capture != null || _encoding != null) continue;
                            if (pending.Hash == _host.Hash) { Send(pair.Key, pending.Nonce, 1, _host, 0); pending.Stage = 1; }
                            else pending.Blob = _host;
                        }
                        if (pending.Stage == 0 && budget >= WorldCodec.ChunkBytes)
                        {
                            Send(pair.Key, pending.Nonce, 0, pending.Blob, pending.Index++); budget -= WorldCodec.ChunkBytes;
                            if (pending.Index * WorldCodec.ChunkBytes >= pending.Blob.Bytes.Length) { pending.Stage = 1; pending.Index = 0; pending.Blob = null; }
                        }
                    }
                    if (pending.Stage == 1)
                    {
                        if (!pending.WantNavigation || !Settings.ShareNavigation.Value) { Send(pair.Key, pending.Nonce, 4, null, 0); Remove.Add(pair.Key); continue; }
                        if (NavigationStale(now)) CaptureNavigation(now);
                        if (pending.Blob == null)
                        {
                            if (_navigation == null || _encodingNavigation != null || KnownWorld.Busy) continue;
                            pending.Blob = _navigation;
                        }
                        if (budget >= WorldCodec.ChunkBytes)
                        {
                            Send(pair.Key, pending.Nonce, 3, pending.Blob, pending.Index++); budget -= WorldCodec.ChunkBytes;
                            if (pending.Index * WorldCodec.ChunkBytes >= pending.Blob.Bytes.Length) Remove.Add(pair.Key);
                        }
                    }
                    if (budget < WorldCodec.ChunkBytes) break;
                }
                foreach (var rpc in Remove) { PendingPeers.Remove(rpc); if (Peer(rpc) == null) RequestedAt.Remove(rpc); }
            }
            catch (Exception ex)
            {
                _capture?.Dispose(); _capture = null; _capturing = null; _encoding = _encodingNavigation = null;
                _captureRetry = now + (ex is InvalidOperationException ? MinimumRequestSeconds : Settings.SyncSeconds.Value); Warn(ex);
            }
        }

        private static bool HostStale(float now)
        {
            var zone = ZoneSystem.instance;
            return _host == null || now - _hostAt >= LocationRefreshSeconds || zone != null && zone.GetLocationList().Count != _hostSource;
        }

        private static bool NavigationStale(float now)
        {
            if (_navigation == null || now - _shipsAt >= ShipRefreshSeconds) return true;
            Portals.Refresh(Settings.PortalSeconds.Value);
            return PortalSignature() != _navigationPortals;
        }

        private static int PortalSignature()
        {
            int signature = Portals.List.Count;
            foreach (var portal in Portals.List)
                unchecked { signature = signature * 31 + (portal.Tag ?? "").GetHashCode() ^ portal.X.GetHashCode() * 7 ^ portal.Z.GetHashCode(); }
            return signature;
        }

        private static void Capture()
        {
            if (_encoding != null || Time.unscaledTime < _captureRetry) return;
            var zone = ZoneSystem.instance;
            if (zone == null || !zone.LocationsGenerated) return;
            if (_capture == null) { _capturing = new WorldCatalog { World = _world, Generation = _generation }; _hostSource = zone.GetLocationList().Count; _capture = zone.GetLocationList().GetEnumerator(); }
            var clock = Stopwatch.StartNew();
            while (_capture.MoveNext())
            {
                var site = _capture.Current;
                if (site.m_location != null) _capturing.Locations.Add(new Raw { Prefab = site.m_location.m_prefabName, Biome = site.m_location.m_biome.ToString(), Unique = site.m_location.m_unique,
                    Placed = site.m_placed, X = site.m_position.x, Y = site.m_position.y, Z = site.m_position.z });
                if ((_capturing.Locations.Count & 63) == 0 && clock.Elapsed.TotalMilliseconds >= Settings.SliceMilliseconds.Value) return;
            }
            _capture.Dispose(); _capture = null;
            var catalog = _capturing; _capturing = null;
            _encodingAt = Time.unscaledTime;
            _encoding = Task.Run(() => WorldCodec.Encode(catalog));
        }

        private static void CaptureNavigation(float now)
        {
            if (_encodingNavigation != null) return;
            // ship positions need a world object scan, only when the last snapshot's ships are old. a portal change reuses them
            bool ships = _navigation == null || now - _shipsAt >= ShipRefreshSeconds;
            if (ships && !KnownWorld.RouteFresh) { KnownWorld.Request(true); return; }
            if (ships) _shipsAt = now;
            Portals.Refresh(0f);
            _navigationPortals = PortalSignature();
            var catalog = new WorldCatalog { World = _world, Generation = _generation };
            foreach (var portal in Portals.List) catalog.Portals.Add(new WorldObject { Id = portal.Id, Name = portal.Tag, X = portal.X, Y = portal.Y, Z = portal.Z });
            foreach (var ship in KnownWorld.Ships) if (!ship.Shared) catalog.Ships.Add(new WorldObject { Id = ship.Id, Name = ship.Name, X = ship.X, Z = ship.Z });
            _encodingNavigationAt = Time.unscaledTime;
            _encodingNavigation = Task.Run(() => WorldCodec.Encode(catalog));
        }

        private static void Send(ZRpc rpc, long nonce, int kind, WorldBlob blob, int index)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(WorldCodec.Protocol); writer.Write(_world); writer.Write(_generation); writer.Write(nonce); writer.Write(kind);
                if (blob != null)
                {
                    writer.Write(Encoding.ASCII.GetBytes(blob.Hash));
                    if (kind != 1)
                    {
                        int offset = index * WorldCodec.ChunkBytes, count = Math.Min(WorldCodec.ChunkBytes, blob.Bytes.Length - offset);
                        writer.Write(blob.Bytes.Length); writer.Write(index); writer.Write(count); writer.Write(blob.Bytes, offset, count);
                    }
                }
                writer.Flush(); rpc.Invoke(DataName, new ZPackage(stream.ToArray()));
            }
        }

        private static void Data(ZRpc rpc, ZPackage package)
        {
            if (!Guest || _net.GetServerPeer()?.m_rpc != rpc || !_asked) return;
            try
            {
                byte[] bytes = package.GetArray();
                if (bytes.Length < 28 || bytes.Length > WorldCodec.ChunkBytes + 104) return;
                using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    int protocol = reader.ReadInt32(); long world = reader.ReadInt64(); int generation = reader.ReadInt32(); long nonce = reader.ReadInt64(); int kind = reader.ReadInt32();
                    if (nonce != _nonce) return;
                    // a mismatch reply carries the host's own world and version, so it is matched by the request nonce alone
                    if (protocol != WorldCodec.Protocol || kind == -3) { _responded = true; Invalidate("This host uses a different Navigator data version. Local routes remain available."); return; }
                    if (world != _world || generation != _generation) return;
                    _responded = true;
                    if (kind == -1) { Invalidate("World search is disabled by this host. Pins and manual routes are available."); return; }
                    if (kind == 2) { if (_active == null) Status = "Receiving world locations from the host."; return; }
                    if (kind == 4)
                    {
                        bool changed = SharedPortals.Count != 0 || SharedShips.Count != 0;
                        _navigationTransfer = null; _navigationHash = null; SharedPortals.Clear(); SharedShips.Clear(); NavigationVersion++;
                        Portals.Refresh(0f); KnownWorld.ApplySharedShips(); if (changed) Navigator.Recalculate(); return;
                    }
                    string hash = Encoding.ASCII.GetString(reader.ReadBytes(64));
                    if (!WorldCodec.ValidHash(hash)) return;
                    if (kind == 1)
                    {
                        if (reader.BaseStream.Position != bytes.Length || _cached == null || _cached.Hash != hash) return;
                        Activate(_cached); _transfer = null; _next = Time.unscaledTime + Settings.SyncSeconds.Value; return;
                    }
                    if (kind != 0 && kind != 3) return;
                    int length = reader.ReadInt32(), index = reader.ReadInt32(), count = reader.ReadInt32();
                    if (count < 0 || count > WorldCodec.ChunkBytes || count != bytes.Length - reader.BaseStream.Position) return;
                    var transfer = kind == 0 ? _transfer : _navigationTransfer;
                    if (transfer == null) return;
                    if (!transfer.Add(world, generation, nonce, hash, length, index, reader.ReadBytes(count))) return;
                    _deadline = Time.unscaledTime + Settings.SyncTimeout.Value;
                    if (kind == 0 && _active == null) Status = "Receiving world locations, " + transfer.Received + " parts received.";
                    if (!transfer.Complete) return;
                    byte[] data = transfer.Bytes;
                    if (kind == 0) { _decoding = Task.Run(() => WorldCodec.Decode(data, world, generation, hash)); _transfer = null; }
                    else { _decodingNavigation = Task.Run(() => WorldCodec.Decode(data, world, generation, hash)); _navigationTransfer = null; }
                }
            }
            catch (Exception ex) { Warn(ex); }
        }

        private static void Activate(WorldBlob blob)
        {
            if (_active == null || _active.Hash != blob.Hash) { _active = blob; Version++; }
            _cached = blob;
            Status = "World locations available from the host.";
        }

        private static void Invalidate(string status)
        {
            bool changed = SharedPortals.Count != 0 || SharedShips.Count != 0;
            _active = null; Version++; _transfer = _navigationTransfer = null;
            _navigationHash = null;
            SharedPortals.Clear(); SharedShips.Clear(); NavigationVersion++; Portals.Refresh(0f); KnownWorld.ApplySharedShips(); Status = status;
            if (changed) Navigator.Recalculate();
            _next = Time.unscaledTime + Settings.SyncSeconds.Value;
        }

        private static void Client()
        {
            try
            {
                if (_reading != null && _reading.IsCompleted) { _cached = _reading.GetAwaiter().GetResult(); _reading = null; }
                if (_decoding != null && _decoding.IsCompleted)
                {
                    var blob = _decoding.GetAwaiter().GetResult(); _decoding = null; Activate(blob);
                    string path = CacheFile; byte[] bytes = blob.Bytes;
                    Task.Run(() =>
                    {
                        try
                        {
                            string folder = Path.GetDirectoryName(path);
                            if (File.Exists(path) || !Directory.Exists(folder) || Directory.GetFiles(folder, "*.catalog").Length < MaximumCachedWorlds) WorldCodec.SaveCache(path, bytes);
                        }
                        catch { }
                    });
                    _next = Time.unscaledTime + Settings.SyncSeconds.Value;
                    Note("guest world catalog verified, " + blob.Catalog.Locations.Count + " locations, " + blob.Bytes.Length + " bytes");
                }
                if (_decodingNavigation != null && _decodingNavigation.IsCompleted)
                {
                    var blob = _decodingNavigation.GetAwaiter().GetResult(); _decodingNavigation = null;
                    if (_navigationHash != blob.Hash)
                    {
                        _navigationHash = blob.Hash;
                        SharedPortals.Clear(); SharedPortals.AddRange(blob.Catalog.Portals);
                        SharedShips.Clear(); SharedShips.AddRange(blob.Catalog.Ships); NavigationVersion++;
                        Portals.Refresh(0f); KnownWorld.ApplySharedShips();
                        Navigator.Recalculate();
                        Note("guest navigation data verified, " + SharedPortals.Count + " portals, " + SharedShips.Count + " ships");
                    }
                }
            }
            catch (Exception ex)
            {
                _reading = _decoding = _decodingNavigation = null;
                Status = "Host world data could not be read. Local navigation remains available."; Warn(ex);
                _next = Time.unscaledTime + Settings.SyncSeconds.Value;
            }
            float now = Time.unscaledTime;
            // the first request waits until the player has spawned, so it is not lost in the loading freeze, and a retry is sent only after
            // the previous request timed out. a reply always belongs to the newest request (test 12: three requests during loading, none answered)
            bool spawned = Spawned();
            if (spawned && (!_asked && _reading == null || _responded && now >= _next && _transfer == null && _navigationTransfer == null && _decoding == null && _decodingNavigation == null)) Ask();
            else if (spawned && _asked && !_responded && _discoveryAttempts < MaximumDiscoveryRequests && now >= _deadline) Ask();
            if (_asked && !_responded && now >= _deadline && _discoveryAttempts >= MaximumDiscoveryRequests)
            {
                Status = "This host has no Navigator support. Pins, known objects and manual routes are available.";
                _transfer = _navigationTransfer = null;
            }
            else if (_asked && !_responded) Status = "Asking the host for world locations. Pins and manual routes are available.";
            else if (_asked && _responded && (_transfer != null || _navigationTransfer != null) && now >= _deadline)
            {
                if (_active == null) Status = "Host world data timed out. Local navigation remains available.";
                _transfer = _navigationTransfer = null; _next = now + Settings.SyncSeconds.Value;
            }
        }

        private static void Ask()
        {
            var peer = _net.GetServerPeer();
            if (peer == null || peer.m_uid == 0 || !Registered.Contains(peer.m_rpc)) return;
            _lastAskedAt = Time.unscaledTime;
            if (!_responded) _discoveryAttempts++;
            _nonce++; _asked = true; _deadline = Time.unscaledTime + Settings.SyncTimeout.Value;
            _next = Time.unscaledTime + Settings.SyncSeconds.Value;
            _transfer = new WorldTransfer(_world, _generation, _nonce); _navigationTransfer = new WorldTransfer(_world, _generation, _nonce);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(WorldCodec.Protocol); writer.Write(_world); writer.Write(_generation); writer.Write(_nonce);
                writer.Write(Settings.RoutePortals.Value || Settings.PortalsOn.Value || Settings.UseShip.Value);
                if (_cached != null) writer.Write(Encoding.ASCII.GetBytes(_cached.Hash));
                writer.Flush(); peer.m_rpc.Invoke(RequestName, new ZPackage(stream.ToArray()));
            }
            Note("world data requested from host");
        }

        internal static void Retry()
        {
            if (Guest && Time.unscaledTime - _lastAskedAt >= MinimumRequestSeconds) { _discoveryAttempts = 0; Ask(); }
        }

        private static ZNetPeer Peer(ZRpc rpc)
        {
            if (_net == null) return null;
            foreach (var peer in _net.GetPeers()) if (peer.m_rpc == rpc && peer.m_uid != 0) return peer;
            return null;
        }
    }
}
