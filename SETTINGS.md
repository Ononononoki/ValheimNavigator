# Valheim Navigator: settings and details

The full settings list, multiplayer details and the files Navigator keeps. The mod page is the short version: [package/README.md](package/README.md).

## Settings

All settings are in `BepInEx/config/onononoki.ValheimNavigator.cfg`, in numbered sections (`01 - General` to `13 - Layer kinds`), and most are on the panel's Settings page. Settings from older versions move to the new sections by themselves, with their values.

| Setting | Section | Default | What it does |
|---|---|---|---|
| `MapMode` | `02 - Map mode` | 0 | 0 ask at the next large map opening, 1 Explorer, 2 Cartographer |
| `ExplorerSenseMeters`, `ExplorerEdgeMeters` | `02 - Map mode` | 1367, 367 | Explorer: places this close to you, or this far past explored land, show |
| `ExplorerShared` | `02 - Map mode` | true | Land read from a cartography table counts as explored |
| `ShowUnexplored`, `FogTint` | `02 - Map mode` | false, 0.3 | Cartographer: show the terrain in the fog under a shade of this strength (0 no shade) |
| `FogClouds` | `02 - Map mode` | false | Experimental: the game's map clouds and colors in the shade |
| `View` | `03 - Map view` | 4 | Map view: 0 the game's map, 1 biomes, 2 height, 3 simplified, 4 detailed (the game's look from the 2 m terrain) |
| `MinimapTerrain` | `03 - Map view` | true | The minimap shows the chosen map view |
| `LavaEdge` | `03 - Map view` | true | Lava drawn only where it burns, the edge routes keep to |
| `AshlandsHeat` | `03 - Map view` | true | Boiling Ashlands water tinted magenta and lava orange |
| `Structures` | `03 - Map view` | 1 | Buildings on the map: 0 none (terrain only), 1 placed by players, 2 every structure in the world |
| `StructureLimit`, `AllStructureLimit` | `09 - Performance` | 100000, 500000 | The most pieces drawn for placed buildings, and for every structure |
| `DetailedTerrain` | `09 - Performance` | true | Build the detailed 2 m terrain map in the background. Off shows the regular map |
| `DetailedTerrainThreads`, `DetailedTerrainMemoryMB` | `09 - Performance` | 3, 64 | Background threads for that build, and memory for the tiles near you |
| `LoadedTerrain` | `07 - Routes` | false | Detailed route detail (work in progress) |
| `DetailedStructures` | `07 - Routes` | false | Detailed also reads your buildings (work in progress) |
| `UsePortals` | `07 - Routes` | true | Routes may use your portal pairs |
| `UseShip`, `BoatSource` | `07 - Routes` | true, 1 | Sail, 1 builds a boat at the best shore, 2 walks to your nearest ship |
| `MoreBoats` | `07 - Routes` | false | Up to three boats on one trip |
| `RefreshSeconds` | `07 - Routes` | 5 | How often the route follows you. Under 5 s may slow the game |
| `UseWind` | `07 - Routes` | true | Plan sailing with the predicted wind |
| `MaxSwimMeters` | `07 - Routes` | 35 | Longest unbroken swim |
| `MaxSlopeDegrees`, `JumpSlopeDegrees`, `JumpMeters` | `07 - Routes` | 38, 50, 12 | Steepest walkable ground, and short banks that can be jump-run |
| `BiomeDanger` | `07 - Routes` | 1 | Harder biomes in Comfort routes: 0 ignore, 1 avoid, 2 never walk, 3 past my progress |
| `ShipSpeeds` | `07 - Routes` | empty | Speeds of ships from other mods, for example `WarShip=3/5/10` |
| `AutoMark`, `AutoMarkMeters` | `04 - Markers` | true, 25 | Mark a searched place done when you arrive |
| `MaxPins`, `Labels` | `04 - Markers` | 25, true | The nearest places shown per category, and their names when zoomed in |
| `Tombstones` | `05 - Known world` | true | Player tombstones and their owners on the map |
| `Enabled` | `06 - Portals` | true | Your portals on the map and in search |
| `LogCounters` | `10 - Debug` | false | Troubleshooting logs |
| `ShareLocations` | `08 - Multiplayer` | true | Host shares world locations with requesting compatible Navigator guests |
| `ShareNavigation` | `08 - Multiplayer` | true | Host also shares portal endpoints and ship positions |
| `ShareMap` | `08 - Multiplayer` | true | Host also shares terraforming, paths, player buildings, location ground and tombstones of the whole world |
| `RefreshSeconds` | `08 - Multiplayer` | 60 | Guest refresh interval for a supported host |
| `TimeoutSeconds` | `08 - Multiplayer` | 15 | Wait without data before reporting limited navigation |
| `KilobytesPerFrame` | `08 - Multiplayer` | 48 | Total host transfer budget across requesting guests |

## Multiplayer

- Full world search works in solo play and while hosting. Joining players get the host's location list when both sides use a compatible Navigator version and the host allows sharing.
- Each player chooses routes, filters and destinations independently. Completion marks stay on that player's installation. Personal pins, exploration fog and inventories are not transferred by Navigator.
- Without host support, the map views, routes on the guest's own known ground, manual destinations and available personal pins, portals and known objects remain usable. Enable personal-pin search or optional known-object features to use those sources. Full world search shows why it is unavailable.
- The host also shares the map data of the whole world: terraforming, paths and fields, the buildings players placed, the ground the game shapes around locations, and every player's tombstone. A guest's game only knows the world near where that guest has been, so without this the detailed map would miss the host's bases far away. The guest's own data wins where it has some, and in Explorer mode each player still sees only their own explored land.
- The host can also share portal names and endpoints and ship positions. Local observations take priority. Changed data is requested about once a minute by default, so a portal rename or moving boat can take time to appear.
- A validated location cache is stored by world UID and generation version. Cached data is used only after the current host confirms its checksum. Changing worlds clears the previous world's live data.
- Done marks of older versions (one file per world name) are copied once into the new per-world file, only for locations of that world. The old file is kept. `navigator importlegacy` repeats the copy by hand.
- `navigator sync` requests another check from the host. A short request cooldown applies.

Dedicated servers are not tested yet. On a dedicated server Navigator also shares the player buildings, the tombstones, the terraforming and the location ground.

## Files Navigator makes, and how to remove them

Navigator builds a detailed 2 m terrain map from the game's own world generator, and a route map of the whole world from it. Both run in the background, nearest to you first, and the bottom line of the panel shows the progress. The first build of a world takes about three minutes for the map and about ten more for the route map, later visits read them from disk. Your terraforming comes from the world data, and the real ground of places you visit is kept too. It is on by default so the map has its full detail.

- To turn it off, set `DetailedTerrain = false` in `09 - Performance`. The map then shows the regular map, and hovering the panel's bottom line explains that the option is off.
- Files are not deleted when you turn it off or remove the mod. Delete these folders to free the space. Navigator makes them again when it needs them.

| Folder | What is in it | Size |
|---|---|---|
| `BepInEx/cache/ValheimNavigator` in your mod manager profile | The detailed terrain map and the ground of places you visited, one folder per world. Deep sea is left out. The route map of each world in its `routes` folder | About 75 MB per world, the route map about 25 MB more |
| `%LOCALAPPDATA%\Temp\IronGate\Valheim\ValheimNavigator` | Map view pictures, route tables and the remembered world | Up to about 100 MB |
| `BepInEx/config/ValheimNavigator` in your mod manager profile | Your done marks, spawn protection sizes and the world catalog | Under 1 MB. Keep it if you want your done marks back later |

- The detailed terrain map of a world is built again by itself after a game update, the route map also after a change of the route settings (slopes, speeds, swim limit).
