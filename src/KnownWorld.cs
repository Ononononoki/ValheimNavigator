using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;

namespace ValheimNavigator
{
    internal sealed class KnownMark
    {
        internal float X, Z, Radius;
        internal string Name, Contents, Subtype;
        internal string Id;
        internal bool Shared;
        internal int Kind;
        // an ore deposit with some of its areas mined away
        internal bool Mined;
    }

    // known object data is read only on demand, with a small frame budget and no periodic work while all layers are off
    internal static class KnownWorld
    {
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> Sectors = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static readonly Regex Hazard = new Regex(@"^(?:rock|cliff|ice|seastack|lava|Mistlands_Rock|Ashlands.*rock)", RegexOptions.IgnoreCase);
        private static readonly Regex OreName = new Regex(@"^(?:rock4_copper.*|MineRock_Copper.*|MineRock_Tin.*|Pickable_Tin|silvervein.*|rock3_silver.*|MineRock_Obsidian.*|mudpile.*|MineRock_Iron.*|MineRock_Meteorite.*|FlametalRockstand.*|LeviathanLava)$");
        private sealed class Kind
        {
            internal string Name, Prefab;
            internal bool Safe, Hazard, Ore, Bed, Vehicle, Ship, Tombstone, Tamed, Chest;
            internal float Radius, SafeRadius;
            internal int Width, Height;
            // how the game stores mining progress for this ore prefab
            internal bool Rock5, Pickable, Fractured;
            internal int Areas;
            internal float AreaHealth;
            // a solid the route remembers: its collider box in the prefab's own frame
            internal bool Solid;
            internal Vector3 Min, Max;
        }
        private static readonly Dictionary<int, Kind> Kinds = new Dictionary<int, Kind>();
        private static readonly List<KnownMark> Pending = new List<KnownMark>();
        private static readonly Dictionary<long, List<KnownMark>> Obstacles = new Dictionary<long, List<KnownMark>>();
        internal static readonly List<KnownMark> Marks = new List<KnownMark>();
        // every known ship, read on each scan for the route, the name is the prefab
        internal static readonly List<KnownMark> Ships = new List<KnownMark>();
        private static readonly List<KnownMark> PendingShips = new List<KnownMark>();
        // Detailed routes: the solids of this scan as boxes, and every zone it read, so remembered zones it did not read keep theirs
        private static readonly List<Remembered.Box> PendingBoxes = new List<Remembered.Box>();
        private static readonly HashSet<long> ZonesRead = new HashSet<long>();
        private static int _boxSignature;
        internal static bool BoxesChanged;
        private static float _routeData = -1000f;
        // the last scan read sea hazards and ships recently enough for a new route
        internal static bool RouteFresh => _manager != null && _manager == ZDOMan.instance && !_busy && Time.unscaledTime - _routeData < Settings.KnownRefresh.Value;
        private static ZDOMan _manager;
        private static List<ZDO>[] _sectors;
        private static int _sector, _item;
        private static float _last = -1000f;
        private static int _scanFrames;
        private static double _scanMilliseconds;
        private static bool _busy, _route, _warned;
        internal static int Version;
        internal static int HazardVersion;
        internal static string Problem;
        private static int _hazardSignature;
        internal static bool Busy => _busy;
        internal static bool Enabled => Settings.SeaHazards.Value || Settings.Ore.Value || Settings.SpawnSafe.Value || Settings.Beds.Value
            || Settings.Vehicles.Value || Settings.Tombstones.Value || Settings.Tamed.Value || Settings.Chests.Value;

        internal static bool Visible(KnownMark mark)
        {
            switch (mark.Kind)
            {
                case 0: return Settings.Ore.Value && (mark.Subtype == null || !Settings.OreTypes.TryGetValue(mark.Subtype, out var entry) || entry.Value);
                case 1: return Settings.SpawnSafe.Value;
                case 2: return Settings.SeaHazards.Value;
                case 3: return Settings.Beds.Value;
                case 4: return Settings.Vehicles.Value;
                case 5: return Settings.Tombstones.Value;
                case 6: return Settings.Tamed.Value;
                case 7: return Settings.Chests.Value;
                default: return false;
            }
        }

        // one color per ore so deposits read apart at a glance
        internal static Color32 OreColor(string ore)
        {
            switch (ore)
            {
                case "Copper": return new Color32(214, 128, 70, 245);
                case "Tin": return new Color32(206, 206, 196, 245);
                case "Silver": return new Color32(150, 196, 236, 245);
                case "Obsidian": return new Color32(150, 112, 200, 245);
                case "Iron": return new Color32(170, 82, 60, 245);
                case "Flametal": return new Color32(246, 196, 64, 245);
                default: return new Color32(189, 151, 104, 245);
            }
        }

        internal static Color32 Color(KnownMark mark)
        {
            if (mark.Kind != 0) return FamilyColor(mark);
            var color = OreColor(mark.Subtype);
            if (mark.Mined) color.a = 110;
            return color;
        }

        // the shared color of a family, used for groups that mix ore types
        internal static Color32 FamilyColor(KnownMark mark)
        {
            switch (mark.Kind)
            {
                case 0: return new Color32(189, 151, 104, 245);
                case 1: return new Color32(144, 177, 133, 110);
                case 2: return new Color32(192, 105, 75, 210);
                case 3: return new Color32(213, 203, 174, 235);
                case 4: return new Color32(131, 172, 180, 235);
                case 5: return new Color32(192, 175, 158, 235);
                case 6: return new Color32(160, 181, 130, 235);
                default: return new Color32(186, 154, 111, 235);
            }
        }

        internal static string GroupName(KnownMark mark, bool mixed)
        {
            switch (mark.Kind)
            {
                case 0: return mixed ? "ore deposits" : (mark.Subtype ?? "ore").ToLowerInvariant() + " deposits";
                case 2: return "known sea rocks";
                case 3: return "beds";
                case 4: return "ships and carts";
                case 5: return "tombstones";
                case 6: return "tamed animals";
                case 7: return "containers";
                default: return "objects";
            }
        }

        internal static void Forget()
        {
            _manager = null; _sectors = null; _busy = _route = false; _last = _routeData = -1000f;
            Kinds.Clear(); Marks.Clear(); Ships.Clear(); Pending.Clear(); PendingShips.Clear(); Obstacles.Clear();
            PendingBoxes.Clear(); ZonesRead.Clear(); Version++; HazardVersion++;
        }

        internal static void ApplySharedShips()
        {
            Ships.RemoveAll(s => s.Shared);
            foreach (var ship in WorldSync.SharedShips)
            {
                bool known = false;
                foreach (var local in Ships) if (local.Id == ship.Id) { known = true; break; }
                if (!known) Ships.Add(new KnownMark { Id = ship.Id, Name = ship.Name, X = ship.X, Z = ship.Z, Kind = 4, Shared = true });
            }
        }

        internal static void CancelRoute()
        {
            _route = false;
            if (!Enabled) { _busy = false; Pending.Clear(); }
        }

        private static long Key(int x, int z) { return ((long)x << 32) | (uint)z; }

        internal static void Request(bool route = false)
        {
            _route |= route;
            if (_busy) return;
            var manager = ZDOMan.instance;
            if (manager == null || ZNetScene.instance == null) return;
            if (_manager != manager)
            {
                _manager = manager; Kinds.Clear(); Marks.Clear(); Obstacles.Clear(); _last = -1000f; _warned = false;
            }
            if (!route && Time.unscaledTime - _last < Settings.KnownRefresh.Value) return;
            _sectors = Sectors(manager); _sector = 0; _item = 0; Pending.Clear(); PendingShips.Clear(); PendingBoxes.Clear(); ZonesRead.Clear();
            Problem = null;
            _busy = _sectors != null; _last = Time.unscaledTime; _scanFrames = 0; _scanMilliseconds = 0;
        }

        internal static void Tick()
        {
            if (!_busy)
            {
                if (Enabled && Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) Request();
                return;
            }
            if (ZDOMan.instance != _manager) { _busy = false; Marks.Clear(); Obstacles.Clear(); return; }
            try
            {
                var clock = Stopwatch.StartNew();
                while (_sector < _sectors.Length)
                {
                    var sector = _sectors[_sector];
                    if (sector == null || _item >= sector.Count) { _sector++; _item = 0; }
                    else { var zdo = sector[_item++]; if (zdo != null) Read(zdo); }
                    if (clock.Elapsed.TotalMilliseconds >= Settings.MapBudget) { _scanFrames++; _scanMilliseconds += clock.Elapsed.TotalMilliseconds; return; }
                }
                Marks.Clear(); Marks.AddRange(Pending); Pending.Clear(); Obstacles.Clear();
                int signature = 0;
                foreach (var mark in Marks)
                {
                    if (mark.Kind != 2) continue;
                    unchecked { signature += mark.X.GetHashCode() * 397 ^ mark.Z.GetHashCode() ^ mark.Radius.GetHashCode(); }
                    long key = Key((int)Math.Floor(mark.X / 256f), (int)Math.Floor(mark.Z / 256f));
                    if (!Obstacles.TryGetValue(key, out var list)) { list = new List<KnownMark>(); Obstacles.Add(key, list); }
                    list.Add(mark);
                }
                if (signature != _hazardSignature) { _hazardSignature = signature; HazardVersion++; }
                Ships.Clear(); Ships.AddRange(PendingShips); PendingShips.Clear(); ApplySharedShips();
                if (Settings.LoadedTerrain.Value && Settings.RememberedWorld.Value && ZonesRead.Count > 0)
                {
                    int boxes = PendingBoxes.Count;
                    foreach (var box in PendingBoxes) unchecked { boxes = boxes * 31 + box.X.GetHashCode() ^ box.Z.GetHashCode() * 7 ^ box.Top.GetHashCode(); }
                    Remembered.ReplaceBoxes(ZonesRead, PendingBoxes);
                    // a changed set of solids is a new world for the kept route, as changed sea hazards are
                    if (boxes != _boxSignature) { _boxSignature = boxes; HazardVersion++; BoxesChanged = true; }
                    PendingBoxes.Clear(); ZonesRead.Clear();
                }
                if (_route || Settings.SeaHazards.Value) _routeData = Time.unscaledTime;
                _busy = false; _route = false; Version++; Layers.Dirty();
                if (Plugin.LogCounters.Value) Plugin.Log.LogInfo("known world layers ready, " + Marks.Count + " objects, " + (_scanMilliseconds + clock.Elapsed.TotalMilliseconds).ToString("F0") + " ms over " + (_scanFrames + 1) + " frames");
            }
            catch (Exception ex)
            {
                Problem = ex.ToString();
                _busy = false; _route = false;
                if (!_warned) Plugin.Log.LogWarning("known world layers unavailable: " + ex);
                _warned = true;
            }
        }

        private static void Read(ZDO zdo)
        {
            int hash = zdo.GetPrefab();
            if (!Kinds.TryGetValue(hash, out var kind))
            {
                var prefab = ZNetScene.instance.GetPrefab(hash);
                string name = prefab != null ? prefab.name : "";
                var container = prefab != null ? prefab.GetComponent<Container>() : null;
                var character = prefab != null ? prefab.GetComponent<Character>() : null;
                kind = new Kind { Name = name, Prefab = name, Ore = OreName.IsMatch(name) && (prefab == null || prefab.GetComponent<ItemDrop>() == null), Hazard = Hazard.IsMatch(name), Radius = 8f,
                    Bed = prefab != null && prefab.GetComponent<Bed>() != null,
                    Vehicle = prefab != null && (prefab.GetComponent<Ship>() != null || prefab.GetComponent<Vagon>() != null),
                    Ship = prefab != null && prefab.GetComponent<Ship>() != null,
                    Tombstone = name == "Player_tombstone", Tamed = character != null,
                    Chest = container != null, Width = container != null ? container.m_width : 8, Height = container != null ? container.m_height : 4 };
                if (character != null) kind.Name = Localize(character.m_name);
                else if (container != null) kind.Name = Localize(container.m_name);
                // every prefab with a PlayerBase effect area protects against spawns, the game checks nothing else
                if (prefab != null)
                    foreach (var area in prefab.GetComponentsInChildren<EffectArea>(true))
                        if ((area.m_type & EffectArea.Type.PlayerBase) != 0 && area.GetComponent<Collider>() != null)
                        {
                            kind.Safe = true;
                            kind.SafeRadius = Math.Max(kind.SafeRadius, SpawnAreas.RadiusOf(area.GetComponent<Collider>()));
                        }
                if (kind.Ore && prefab != null)
                {
                    kind.Rock5 = prefab.GetComponent<MineRock5>() != null;
                    kind.Pickable = prefab.GetComponent<Pickable>() != null;
                    kind.Fractured = name.IndexOf("_frac", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Fractured", StringComparison.Ordinal) >= 0;
                    var rock = prefab.GetComponent<MineRock>();
                    if (rock != null && rock.m_areaRoot != null) { kind.Areas = rock.m_areaRoot.GetComponentsInChildren<Collider>(true).Length; kind.AreaHealth = rock.m_health; }
                }
                kind.Solid = prefab != null && SolidKind(prefab) && LocalBox(prefab, out kind.Min, out kind.Max);
                if (kind.Hazard && prefab != null)
                    foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        var bounds = renderer.bounds;
                        var center = bounds.center - prefab.transform.position;
                        kind.Radius = Math.Max(kind.Radius, Math.Max(Math.Abs(center.x) + bounds.extents.x, Math.Abs(center.z) + bounds.extents.z));
                    }
                Kinds.Add(hash, kind);
            }
            var position = zdo.GetPosition();
            if (Settings.LoadedTerrain.Value && Settings.RememberedWorld.Value)
            {
                long zone = Remembered.ZoneOf(position.x, position.z);
                ZonesRead.Add(zone);
                if (kind.Solid) PendingBoxes.Add(BoxOf(zdo, position, kind, zone));
            }
            if (kind.Ship) PendingShips.Add(new KnownMark { Id = zdo.m_uid.ToString(), X = position.x, Z = position.z, Name = kind.Prefab, Kind = 4 });
            if (Settings.SpawnSafe.Value && kind.Safe)
                Pending.Add(new KnownMark { X = position.x, Z = position.z, Radius = kind.SafeRadius, Name = kind.Prefab, Kind = 1 });
            if (Settings.Ore.Value && kind.Ore)
            {
                string n = kind.Name;
                string ore = n.StartsWith("rock4_copper", StringComparison.Ordinal) || n.StartsWith("MineRock_Copper", StringComparison.Ordinal) ? "Copper"
                    : n.StartsWith("MineRock_Tin", StringComparison.Ordinal) || n == "Pickable_Tin" ? "Tin"
                    : n.StartsWith("silvervein", StringComparison.Ordinal) || n.StartsWith("rock3_silver", StringComparison.Ordinal) ? "Silver"
                    : n.StartsWith("MineRock_Obsidian", StringComparison.Ordinal) ? "Obsidian"
                    : n.StartsWith("mudpile", StringComparison.Ordinal) || n.StartsWith("MineRock_Iron", StringComparison.Ordinal) ? "Iron" : "Flametal";
                int mined = MinedState(zdo, kind);
                if (mined < 2) Pending.Add(new KnownMark { X = position.x, Z = position.z, Radius = 5f, Name = ore + " deposit" + (mined == 1 ? " (partly mined)" : ""), Subtype = ore, Kind = 0, Mined = mined == 1 });
            }
            int objectKind = Settings.Beds.Value && kind.Bed ? 3 : Settings.Vehicles.Value && kind.Vehicle ? 4
                : Settings.Tombstones.Value && kind.Tombstone ? 5 : Settings.Tamed.Value && kind.Tamed && zdo.GetBool("tamed", false) ? 6
                : Settings.Chests.Value && kind.Chest && !kind.Vehicle && !kind.Tombstone && zdo.GetLong("creator", 0L) != 0L ? 7 : -1;
            if (objectKind >= 0)
            {
                string name = Categories.Split(kind.Name);
                string owner = zdo.GetString(objectKind == 6 ? "TamedName" : "ownerName", "");
                if (!string.IsNullOrEmpty(owner)) name += " (" + owner + ")";
                Pending.Add(new KnownMark { X = position.x, Z = position.z, Radius = 5f, Name = name, Kind = objectKind,
                    Contents = Settings.Contents.Value && kind.Chest ? ReadContents(zdo, kind) : null });
            }
            if ((Settings.SeaHazards.Value || _route) && kind.Hazard && !kind.Ore && WorldGenerator.instance != null
                && (WorldGenerator.GetAshlandsOceanGradient(position.x, position.z) >= 0f || position.z > 7000f))
            {
                float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                if (WorldGenerator.instance.GetHeight(position.x, position.z) >= water) return;
                var scale = zdo.GetVec3("scale", Vector3.one);
                float radius = Mathf.Clamp(kind.Radius * Math.Max(Math.Abs(scale.x), Math.Abs(scale.z)) + Settings.HazardClearance.Value, Settings.HazardClearance.Value, 120f);
                Pending.Add(new KnownMark { X = position.x, Z = position.z, Radius = radius, Name = kind.Name, Kind = 2 });
            }
        }

        // solids the route avoids or stands on: pieces, rocks, trees and other static objects with colliders. doors stay open, and anything that
        // moves or is picked up (items, creatures, ships, carts, bushes) is no obstacle
        private static bool SolidKind(GameObject prefab)
        {
            return prefab.GetComponent<ItemDrop>() == null && prefab.GetComponent<Character>() == null && prefab.GetComponent<Ship>() == null
                && prefab.GetComponent<Vagon>() == null && prefab.GetComponentInChildren<Door>(true) == null && prefab.GetComponent<Pickable>() == null;
        }

        // the box around a prefab's solid colliders in its own frame, from each collider's shape so inactive prefabs work too
        private static bool LocalBox(GameObject prefab, out Vector3 min, out Vector3 max)
        {
            min = Vector3.one * float.MaxValue; max = Vector3.one * float.MinValue;
            bool any = false;
            var toRoot = prefab.transform.worldToLocalMatrix;
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (collider.isTrigger) continue;
                Vector3 center, size;
                switch (collider)
                {
                    case BoxCollider box: center = box.center; size = box.size; break;
                    case SphereCollider sphere: center = sphere.center; size = Vector3.one * 2f * sphere.radius; break;
                    case CapsuleCollider capsule:
                        center = capsule.center;
                        float d = 2f * capsule.radius;
                        size = capsule.direction == 0 ? new Vector3(capsule.height, d, d) : capsule.direction == 1 ? new Vector3(d, capsule.height, d) : new Vector3(d, d, capsule.height);
                        break;
                    case MeshCollider mesh when mesh.sharedMesh != null: center = mesh.sharedMesh.bounds.center; size = mesh.sharedMesh.bounds.size; break;
                    default: continue;
                }
                var matrix = toRoot * collider.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    var corner = center + Vector3.Scale(size * 0.5f, new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                    var p = matrix.MultiplyPoint3x4(corner);
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                    any = true;
                }
            }
            // flat litter and tiny parts are no obstacle. rock formations wider than 24 m are left to the terrain: one box would block far more
            // ground than their uneven shape covers, and in a loaded zone the real colliders decide
            return any && max.y - min.y >= 0.4f && (max.x - min.x >= 0.3f || max.z - min.z >= 0.3f) && max.x - min.x <= 24f && max.z - min.z <= 24f;
        }

        // a saved object's box in the world: its position, its turn about the vertical and its scale
        private static Remembered.Box BoxOf(ZDO zdo, Vector3 position, Kind kind, long zone)
        {
            var rotation = zdo.GetRotation();
            var scale = zdo.GetVec3("scale", Vector3.one);
            scale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var center = position + rotation * Vector3.Scale((kind.Min + kind.Max) * 0.5f, scale);
            var half = Vector3.Scale((kind.Max - kind.Min) * 0.5f, scale);
            var axis = rotation * Vector3.right;
            float length = Mathf.Sqrt(axis.x * axis.x + axis.z * axis.z);
            if (length < 0.01f) { axis = Vector3.right; length = 1f; }
            return new Remembered.Box { X = center.x, Z = center.z, Cos = axis.x / length, Sin = axis.z / length, HalfX = half.x, HalfZ = half.z,
                Bottom = center.y - half.y, Top = center.y + half.y, Zone = zone };
        }

        // 0 untouched, 1 partly mined, 2 fully mined. MineRock5 keeps a base64 list of area healths under s_health,
        // MineRock keeps one float per area named Health0, Health1 and so on, a picked Pickable keeps s_picked
        private static int MinedState(ZDO zdo, Kind kind)
        {
            if (kind.Pickable) return zdo.GetBool(ZDOVars.s_picked, false) ? 2 : 0;
            if (kind.Rock5)
            {
                string data = zdo.GetString(ZDOVars.s_health, "");
                if (string.IsNullOrEmpty(data)) return kind.Fractured ? 1 : 0;
                try
                {
                    var package = new ZPackage(Convert.FromBase64String(data));
                    int count = package.ReadInt(), gone = 0;
                    for (int i = 0; i < count; i++) if (package.ReadSingle() <= 0f) gone++;
                    return count > 0 && gone >= count ? 2 : gone > 0 || kind.Fractured ? 1 : 0;
                }
                catch { return kind.Fractured ? 1 : 0; }
            }
            if (kind.Areas > 0)
            {
                int gone = 0;
                for (int i = 0; i < kind.Areas; i++) if (zdo.GetFloat("Health" + i, kind.AreaHealth) <= 0f) gone++;
                return gone >= kind.Areas ? 2 : gone > 0 ? 1 : 0;
            }
            return kind.Fractured ? 1 : 0;
        }

        private static string Localize(string text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("$")) return text ?? "";
            try { return Localization.instance.Localize(text); }
            catch { return text.TrimStart('$').Replace('_', ' '); }
        }

        private static string ReadContents(ZDO zdo, Kind kind)
        {
            string data = zdo.GetString("items", "");
            if (string.IsNullOrEmpty(data) || ObjectDB.instance == null) return "Contents unavailable";
            try
            {
                var names = new List<string>();
                var package = new ZPackage(data);
                int version = package.ReadInt();
                if (version >= 108)
                {
                    int count = package.ReadUShort();
                    for (int i = 0; i < count; i++)
                    {
                        var item = ItemDrop.ItemData.Load(package, (global::Version.Item)version);
                        var prefab = ObjectDB.instance.GetItemPrefab(item.Item1);
                        var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                        names.Add((drop != null ? Localize(drop.m_itemData.m_shared.m_name) : "Unknown item " + item.Item1) + " x" + item.Item2.m_stack);
                    }
                }
                else
                {
                    var inventory = new Inventory("ValheimNavigator read only", null, kind.Width, kind.Height);
                    inventory.Load(new ZPackage(data));
                    foreach (var item in inventory.GetAllItems()) names.Add(Localize(item.m_shared.m_name) + " x" + item.m_stack);
                }
                return names.Count == 0 ? "Empty" : string.Join(", ", names);
            }
            catch { return "Contents unavailable"; }
        }

        internal static bool Blocked(float x, float z)
        {
            int cx = (int)Math.Floor(x / 256f), cz = (int)Math.Floor(z / 256f);
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
            {
                if (!Obstacles.TryGetValue(Key(cx + dx, cz + dz), out var marks)) continue;
                foreach (var mark in marks)
                {
                    float xx = mark.X - x, zz = mark.Z - z;
                    if (xx * xx + zz * zz < mark.Radius * mark.Radius) return true;
                }
            }
            return false;
        }
    }
}
