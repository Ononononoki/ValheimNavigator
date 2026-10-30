# Valheim Navigator

Two mods in one: an interactive world map and a navigator. The large map and the minimap show the world in detailed 2 m map views with the buildings players placed. Search and filter every location type, pick one, and get a route there that the map and minimap draw and follow as you go, on foot, by ship and through your portals, planned with the wind. A Tombs tab lists every player's tombstone with a route to it.

**Work in progress (beta).** The Fast route detail with the Shortest mode is the tested setup. The Detailed mode is a work in progress and may not handle every place well. Sharing locations in multiplayer was tested with a player host and a guest over crossplay, in both directions. Sharing the map data is new in this version.

## Features

- **Two map modes**, asked at the first large map opening and changed any time in Settings.
  - **Explorer.** Search, map markers and routes cover the land you have explored and places close to it or close to you, even a little into the fog. The rest stays hidden. Land read from a cartography table counts as explored, and the map views draw it while the game shows the table's map.
  - **Cartographer.** The whole world is known, also what lies in the fog.
- **Find.** Search every location type of the world (bosses, traders, dungeons, caves, ore, runestones and more) by name. The nearest one gets a pin, and places you reached are marked done.
- **Layers.** Turn categories and single location types on or off on the large map. Crowded views group pins into numbered circles.
- **Terrain views.** A sharp detail view of the terrain drawn from the game's own height data, with contours, plus optional biomes, height, simplified, steep ground, mist, shallow water for sailing and a shade for unexplored land (0 turns it off). The minimap shows the same view, sharp. A cursor readout shows ground height and water depth.
- **Detailed map view** on the large map and the minimap. The game's own map look drawn from a 2 m terrain map of the world, with your terraforming, the ground the game shapes around its locations, and your paths, fields and paved ground in the game's own ground textures.
- **Buildings on the map and minimap.** Only the buildings players placed, by default, top down and colored by material, the highest layer only, with a thin outline. The Buildings switch on the Settings page also shows none (terrain only) or every structure in the world (ruins, villages, altars). Up to 100,000 placed pieces and 500,000 structures, both configurable.
- **Routes.**
  - Planned on a route map of the whole world at 2 m, with your terraforming and the ground the game shapes around its locations. A route of several kilometers usually takes under a second. The route map builds in the background the first time a world loads, around you and your route first (a few minutes for the whole world), and is kept on disk. Until it covers your route, the older search finds it, except to the Ashlands, where the route waits for the route map so it never leads through lava.
  - **Shortest** is the fastest walkable way, running and jump-running included. **Comfort** charges climbing (downhill counts like flat ground) and, with `BiomeDanger`, keeps away from harder biomes.
  - On foot, by your nearest ship or by a boat built at the best shore, and sailing long ways across the sea. More boats allows a second sailing leg.
  - To the Ashlands the boat is a drakkar: your nearest ship that can sail the boiling sea, or one built at the shore. Ships of other mods count when the game marks them as Ashlands ready.
  - Your portal pairs (two portals with the same name) are used by default, through hubs too. The large map tags each portal to take with its number and name. Set Portals to Don't use on the Route page when you carry ore.
  - Sailing is planned with the predicted wind, and with Moder's power as a tailwind on every heading.
  - A ship farther than `ShipMeters` is used when it lies on land you or a cartography table explored.
  - No swim longer than 35 m, short steep banks can be jump-run, lava and the boiling Ashlands sea are respected. Known sea rocks are avoided only by the older search for now.
  - The route follows you and recalculates when you leave it or teleport. The destination is marked done when you arrive.
  - Routes are searched in the background, so the game keeps running smoothly while one is calculated.
- **Detail.**
  - **Fast** follows the route map.
  - **Detailed (work in progress)** follows the same route map. With `DetailedStructures` it also plans the way out of and into buildings around you. Rocks, trees and buildings along the whole way come in a later version.
- **Tombstones.** A tab with every player's tombstone in the world, searchable by name, nearest first, with a route to the one you pick. Guests see the host's list when the host shares map data.
- **Known world** (optional). Tombstones are shown by default, beds, ships and carts, tamed animals, chests and their contents, ore deposits and spawn-safe areas can be switched on, from what your game client knows.
- Console command `navigator` for the same search.

## Multiplayer

- Full world search works in solo play and while hosting. Joining players get the host's location list when both sides use a compatible Navigator version and the host allows sharing.
- Each player chooses routes, filters and destinations independently. Completion marks stay on that player's installation. Personal pins, exploration fog and inventories are not transferred by Navigator.
- Without host support, terrain views, manual destinations and available personal pins, portals and known objects remain usable. Enable personal-pin search or optional known-object features to use those sources. Full world search shows why it is unavailable.
- The host also shares the map data of the whole world: terraforming, paths and fields, the buildings players placed, and the ground the game shapes around locations. A guest's game only knows the world near where that guest has been, so without this the detailed map would miss the host's bases far away. The guest's own data wins where it has some, and in Explorer mode each player still sees only their own explored land.
- The host can also share portal names and endpoints and ship positions. Local observations take priority. Changed data is requested about once a minute by default, so a portal rename or moving boat can take time to appear.
- A validated location cache is stored by world UID and generation version. Cached data is used only after the current host confirms its checksum. Changing worlds clears the previous world's live data.
- Done marks of older versions (one file per world name) are copied once into the new per-world file, only for locations of that world. The old file is kept. `navigator importlegacy` repeats the copy by hand.
- `navigator sync` requests another check from the host. A short request cooldown applies.

Dedicated servers are not tested yet.

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
| `Structures` | `03 - Map view` | 1 | Buildings on the map: 0 none (terrain only), 1 placed by players, 2 every structure in the world |
| `StructureLimit`, `AllStructureLimit` | `09 - Performance` | 100000, 500000 | The most pieces drawn for placed buildings, and for every structure |
| `DetailedTerrain` | `09 - Performance` | true | Build the detailed 2 m terrain map in the background. Off shows the regular map |
| `DetailedTerrainThreads`, `DetailedTerrainMemoryMB` | `09 - Performance` | 3, 64 | Background threads for that build, and memory for the tiles near you |
| `LoadedTerrain` | `07 - Routes` | false | Detailed route detail (work in progress) |
| `DetailedStructures` | `07 - Routes` | false | Detailed also reads your buildings (work in progress) |
| `UsePortals` | `07 - Routes` | true | Routes may use your portal pairs |
| `UseShip`, `BoatSource` | `07 - Routes` | true, 1 | Sail, 1 builds a boat at the best shore, 2 walks to your nearest ship |
| `MoreBoats` | `07 - Routes` | false | Allow more than one sailing leg |
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
| `ShareMap` | `08 - Multiplayer` | true | Host also shares terraforming, paths, player buildings and location ground of the whole world |
| `RefreshSeconds` | `08 - Multiplayer` | 60 | Guest refresh interval for a supported host |
| `TimeoutSeconds` | `08 - Multiplayer` | 15 | Wait without data before reporting limited navigation |
| `KilobytesPerFrame` | `08 - Multiplayer` | 48 | Total host transfer budget across requesting guests |

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

## Compatibility

- Client features work independently. Full location search on a guest needs compatible host support. Players without Navigator can still join normally.
- Requires BepInExPack Valheim.
- SpeedyPaths is optional. Its path speeds count in Detailed routes.
- With ShipPins installed, its pins show the ships and carts, and Navigator draws no ship markers of its own. Routes still use the ships.
- Ships from other mods sail at longship speeds unless `ShipSpeeds` lists them.
- LivingMap works next to Navigator. Both draw on the large map, so where a Navigator map view is drawn it covers LivingMap's bases and roads. Hovering the panel's bottom line says so in game.
- Routes are estimates. Unknown rocks, trees and buildings can block a route, and travel times are approximate.

## Planned

- An unexplored shade that matches the game's own fog look.
- Detailed routes that also go around rocks, trees, buildings and known sea rocks along the whole way, and between the floors of buildings.
- More work on the detail view.
- Better spawn-safe areas.

## Credits

Reading your terraforming and ground paint from the world data follows LivingMap by tbsj1ga (https://github.com/tbsj1ga/LivingMapValheim), credited in THIRD_PARTY_NOTICES.md.

## Feedback and bug reports

Report problems on [GitHub](https://github.com/Ononononoki/ValheimNavigator/issues). Please add the route mode, the Detail setting and the BepInEx log lines.
