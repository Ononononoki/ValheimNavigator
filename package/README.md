# Valheim Navigator

Two mods in one: an interactive world map and a navigator. Search and filter every location type on the game's large map, pick one, and get a route there that the map and minimap draw and follow as you go, on foot, by ship and through your portals, planned with the wind.

**Work in progress (beta).** The Fast route detail with the Shortest mode is the tested setup. The Detailed mode is a work in progress and may not handle every place well. Multiplayer sharing was tested with a player host and a guest over crossplay, in both directions.

## Features

- **Two map modes**, asked at the first large map opening and changed any time in Settings.
  - **Explorer.** Search, map markers and routes cover the land you have explored and places close to it or close to you, even a little into the fog. The rest stays hidden. Land read from a cartography table counts as explored, and the map views draw it while the game shows the table's map.
  - **Cartographer.** The whole world is known, also what lies in the fog.
- **Find.** Search every location type of the world (bosses, traders, dungeons, caves, ore, runestones and more) by name. The nearest one gets a pin, and places you reached are marked done.
- **Layers.** Turn categories and single location types on or off on the large map. Crowded views group pins into numbered circles.
- **Terrain views.** A sharp detail view of the terrain drawn from the game's own height data, with contours, plus optional biomes, height, simplified, steep ground, mist, shallow water for sailing and a shade for unexplored land (0 turns it off). The minimap shows the same view, sharp. A cursor readout shows ground height and water depth.
- **Routes.**
  - **Shortest** or **Comfort**. Comfort charges climbing, downhill counts like flat ground.
  - On foot, by your nearest ship or by a boat built at the best shore. More boats allows a second sailing leg.
  - Your portal pairs (two portals with the same name) are used by default, through hubs too. The large map tags each portal to take with its number and name. Set Portals to Don't use on the Route page when you carry ore.
  - Sailing is planned with the predicted wind, and with Moder's power as a tailwind on every heading.
  - A ship farther than `ShipMeters` is used when it lies on land you or a cartography table explored.
  - No swim longer than 35 m, short steep banks can be jump-run, lava and the boiling Ashlands sea are respected, and known sea rocks are avoided.
  - The route follows you and recalculates when you leave it or teleport. The destination is marked done when you arrive.
  - Routes are searched in the background, so the game keeps running smoothly while one is calculated.
- **Detail.**
  - **Fast** reads the generated world map.
  - **Detailed (work in progress)** also reads the real ground, rocks and trees around you, and the faster paths of the SpeedyPaths mod. Player buildings are not read by default (`DetailedStructures`).
- **Known world** (optional, off by default). Beds, tombstones, ships and carts, tamed animals, chests and their contents, ore deposits and spawn-safe areas that your game client knows.
- Console command `navigator` for the same search.

## Multiplayer

- Full world search works in solo play and while hosting. Joining players get the host's location list when both sides use a compatible Navigator version and the host allows sharing.
- Each player chooses routes, filters and destinations independently. Completion marks stay on that player's installation. Personal pins, exploration fog and inventories are not transferred by Navigator.
- Without host support, terrain views, manual destinations and available personal pins, portals and known objects remain usable. Enable personal-pin search or optional known-object features to use those sources. Full world search shows why it is unavailable.
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
| `MinimapTerrain` | `03 - Map view` | true | The minimap shows the chosen map view |
| `LoadedTerrain` | `07 - Routes` | false | Detailed route detail (work in progress) |
| `DetailedStructures` | `07 - Routes` | false | Detailed also reads your buildings (work in progress) |
| `UsePortals` | `07 - Routes` | true | Routes may use your portal pairs |
| `UseShip`, `BoatSource` | `07 - Routes` | true, 1 | Sail, 1 builds a boat at the best shore, 2 walks to your nearest ship |
| `MoreBoats` | `07 - Routes` | false | Allow more than one sailing leg |
| `UseWind` | `07 - Routes` | true | Plan sailing with the predicted wind |
| `MaxSwimMeters` | `07 - Routes` | 35 | Longest unbroken swim |
| `MaxSlopeDegrees`, `JumpSlopeDegrees`, `JumpMeters` | `07 - Routes` | 38, 50, 12 | Steepest walkable ground, and short banks that can be jump-run |
| `BiomeDanger` | `07 - Routes` | 1 | Harder biomes: 0 ignore, 1 avoid, 2 never walk, 3 past my progress |
| `ShipSpeeds` | `07 - Routes` | empty | Speeds of ships from other mods, for example `WarShip=3/5/10` |
| `AutoMark`, `AutoMarkMeters` | `04 - Markers` | true, 25 | Mark a searched place done when you arrive |
| `Enabled` | `06 - Portals` | true | Your portals on the map and in search |
| `LogCounters` | `10 - Debug` | false | Troubleshooting logs |
| `ShareLocations` | `08 - Multiplayer` | true | Host shares world locations with requesting compatible Navigator guests |
| `ShareNavigation` | `08 - Multiplayer` | true | Host also shares portal endpoints and ship positions |
| `RefreshSeconds` | `08 - Multiplayer` | 60 | Guest refresh interval for a supported host |
| `TimeoutSeconds` | `08 - Multiplayer` | 15 | Wait without data before reporting limited navigation |
| `KilobytesPerFrame` | `08 - Multiplayer` | 48 | Total host transfer budget across requesting guests |

## Compatibility

- Client features work independently. Full location search on a guest needs compatible host support. Players without Navigator can still join normally.
- Requires BepInExPack Valheim.
- SpeedyPaths is optional. Its path speeds count in Detailed routes.
- With ShipPins installed, its pins show the ships and carts, and Navigator draws no ship markers of its own. Routes still use the ships.
- Ships from other mods sail at longship speeds unless `ShipSpeeds` lists them.
- Routes are estimates. Unknown rocks, trees and buildings can block a route, and travel times are approximate.

## Planned

- An unexplored shade that matches the game's own fog look.
- Better routes through mountains, in Fast and Detailed.
- More work on Detailed routes and the detail view.
- Better spawn-safe areas.

## Feedback and bug reports

Report problems on [GitHub](https://github.com/Ononononoki/ValheimNavigator/issues). Please add the route mode, the Detail setting and the BepInEx log lines.
