# Valheim Navigator

Two mods in one: an interactive world map and a navigator. The large map and the minimap show the world in detailed 2 m map views with the buildings players placed. Search every location, pick one, and follow a route there on foot, by ship and through your portals.

**Beta.** The Fast route detail with the Shortest mode is the tested setup. Detailed routes are a work in progress.

## Features

- **Map views** on the large map and the minimap: the detailed view (the game's own map look from a 2 m terrain map, with your terraforming, paths and fields), biomes, height, simplified, with contours, steep ground, shallow water and mist. A cursor readout shows ground height and water depth.
- **Buildings** players placed, top down in the colors of their material. Or none, or every structure in the world.
- **Find**: search every location type by name (bosses, traders, dungeons, caves, ore, runestones and more). The nearest gets a pin, places you reached are marked done.
- **Layers**: turn categories and single location types on or off. Each category shows its nearest places, crowded views group them into numbered circles.
- **Routes** on a route map of the whole world at 2 m: Shortest (the fastest walkable way) or Comfort (less climbing, away from harder biomes). On foot, by your nearest ship or a boat built at the best shore, through your portal pairs, with the predicted wind. To the Ashlands by drakkar, around the sea rocks and shallows your ship would hit. The route follows you and recalculates when you leave it.
- **Tombs**: every player's tombstone in the world, searchable by name, with a route to it.
- **Two map modes**: Explorer shows only what you explored and what lies close to it, Cartographer knows the whole world.
- **Multiplayer**: the host shares the world's locations, portals, ships and its map data (terraforming, buildings, tombstones) with guests who run Navigator.

## Quick start

1. Open the large map and click **Navigator** (or hold Shift while opening the map).
2. Pick a place in **Find**, or click a marker on the map.
3. Press **Route**. The line on the map and the minimap shows the way.

## Settings people change most

All settings are on the panel's Settings page and in `BepInEx/config/onononoki.ValheimNavigator.cfg`. The full list: [SETTINGS.md](https://github.com/Ononononoki/ValheimNavigator/blob/main/SETTINGS.md).

| Setting | Default | What it does |
|---|---|---|
| `MapMode` | 0 | 0 ask at the first map opening, 1 Explorer, 2 Cartographer |
| `View` | 4 | Map view, 4 is the detailed view |
| `Structures` | 1 | Buildings: 0 none, 1 placed by players, 2 every structure |
| `MaxPins` | 25 | Places shown per category, the nearest to you |
| `UseShip`, `BoatSource` | true, 1 | Sail, 1 builds a boat at the best shore, 2 walks to your nearest ship |
| `UsePortals` | true | Routes use your portal pairs |
| `MoreBoats` | false | Up to three boats on one trip |
| `BiomeDanger` | 1 | Comfort routes avoid biomes harder than the destination's |
| `RefreshSeconds` (Routes) | 5 | How often the route follows you. Under 5 s may slow the game |
| `DetailedTerrain` | true | Builds the 2 m map in the background. Off shows the game's map |
| `ShareMap` | true | The host shares its map data with guests |

## Files on disk

The 2 m map and the route map are built in the background the first time a world loads (a few minutes, nearest to you first, the panel's bottom line shows the progress) and kept on disk: about 100 MB per world in `BepInEx/cache/ValheimNavigator`, plus map pictures in `%LOCALAPPDATA%\Temp\IronGate\Valheim\ValheimNavigator`. They stay when the mod is removed, delete those folders to free the space. Until the route map covers a route, an older quick search finds it, except to the Ashlands.

## Compatibility

- Client side. Players without Navigator can join normally, guests get the full search and map data from a host that runs Navigator.
- ShipPins: its pins show ships and carts. LivingMap: Navigator's map views cover its bases and roads where they are drawn.
- Ships from other mods sail at longship speeds unless `ShipSpeeds` lists them. A ship's draft and width are measured once it floats near you.
- Routes are estimates. Rocks, trees and buildings along the way can block a route.

## Planned

- Faster routes in worlds with many portals, and on long trips to the Ashlands.
- A smoother large map, without short hitches when it opens or moves.
- Faster Detailed routes, and routes between the floors of buildings.
- Simpler, clearer menus.
- An unexplored shade that matches the game's own fog look.

## Credits and feedback

Reading your terraforming and ground paint from the world data follows LivingMap by tbsj1ga (https://github.com/tbsj1ga/LivingMapValheim), credited in THIRD_PARTY_NOTICES.md. Report problems on [GitHub](https://github.com/Ononononoki/ValheimNavigator/issues) with the route mode and the BepInEx log lines.
