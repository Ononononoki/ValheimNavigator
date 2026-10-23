# Valheim Navigator

Two mods in one: an interactive world map and a navigator. Search and filter every location type on the game's large map, pick one, and get a route there that the map and minimap draw and follow as you go, on foot, by ship and through your portals, planned with the wind.

**Work in progress (0.1.0 beta).** The Fast route detail with the Shortest mode is the tested setup. The Detailed mode is a work in progress and may not handle every place well. Feedback is welcome.

## Features

- **Find.** Search every location type of the world (bosses, traders, dungeons, caves, ore, runestones and more) by name. The nearest one gets a pin, and places you reached are marked done.
- **Layers.** Turn categories and single location types on or off on the large map. Crowded views group pins into numbered circles.
- **Terrain views.** A sharp detail view of the terrain drawn from the game's own height data, with contours, plus optional biomes, height, steep ground, mist, shallow water for sailing and a shade for unexplored land. A cursor readout shows ground height and water depth.
- **Routes.**
  - **Shortest** or **Comfort**. Comfort charges climbing, downhill counts like flat ground.
  - On foot, by your nearest ship or by a boat built at the best shore. More boats allows a second sailing leg.
  - Your portal pairs (two portals with the same name) are used by default, through hubs too. The large map tags each portal to take with its number and name. Set Portals to Don't use on the Route page when you carry ore.
  - Sailing is planned with the predicted wind.
  - No swim longer than 35 m, short steep banks can be jump-run, lava and the boiling Ashlands sea are respected, and known sea rocks are avoided.
  - The route follows you and recalculates when you leave it or teleport. The destination is marked done when you arrive.
- **Detail.**
  - **Fast** reads the generated world map.
  - **Detailed (work in progress)** also reads the real ground, rocks and trees around you, and the faster paths of the SpeedyPaths mod. Player buildings are not read by default (`DetailedStructures`).
- **Known world** (optional, off by default). Beds, tombstones, ships and carts, tamed animals, chests and their contents, ore deposits and spawn-safe areas that your game client knows.
- Console command `navigator` for the same search.

## Settings

All settings are in `BepInEx/config/onononoki.ValheimNavigator.cfg`, and most are on the panel's Settings page.

| Setting | Default | What it does |
|---|---|---|
| `Navigator.LoadedTerrain` | false | Detailed route detail (work in progress) |
| `Navigator.DetailedStructures` | false | Detailed also reads your buildings (work in progress) |
| `Navigator.UsePortals` | true | Routes may use your portal pairs |
| `Navigator.UseShip`, `BoatSource` | true, 1 | Sail, 1 builds a boat at the best shore, 2 walks to your nearest ship |
| `Navigator.MoreBoats` | false | Allow more than one sailing leg |
| `Navigator.UseWind` | true | Plan sailing with the predicted wind |
| `Navigator.MaxSwimMeters` | 35 | Longest unbroken swim |
| `Navigator.MaxSlopeDegrees`, `JumpSlopeDegrees`, `JumpMeters` | 38, 50, 12 | Steepest walkable ground, and short banks that can be jump-run |
| `Navigator.BiomeDanger` | 1 | Harder biomes: 0 ignore, 1 avoid, 2 never walk, 3 past my progress |
| `Navigator.ShipSpeeds` | empty | Speeds of ships from other mods, for example `WarShip=3/5/10` |
| `Done.AutoMark`, `AutoMarkMeters` | true, 25 | Mark a searched place done when you arrive |
| `Portals.Enabled` | true | Your portals on the map and in search |
| `Debug.LogCounters` | false | Troubleshooting logs |

## Compatibility

- Client side only. Every player can use it on their own, the server does not need it.
- Requires BepInExPack Valheim.
- SpeedyPaths is optional. Its path speeds count in Detailed routes.
- Ships from other mods sail at longship speeds unless `ShipSpeeds` lists them.
- Routes are estimates. Unknown rocks, trees and buildings can block a route, and travel times are approximate.

## Planned

- An unexplored shade that matches the game's own fog look.
- Better spawn-safe areas.
- More work on Detailed routes and the detail view.

## Feedback and bug reports

Report problems on [GitHub](https://github.com/Ononononoki/ValheimNavigator/issues). Please add the route mode, the Detail setting and the BepInEx log lines.
