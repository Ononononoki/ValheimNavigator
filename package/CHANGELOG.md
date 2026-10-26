# Changelog

## 0.1.2

- Two map modes, asked at the first large map opening. Explorer shows places, markers and routes for the land you explored and what lies close to it or close to you, even a little into the fog. Cartographer knows the whole world.
- Land read from a cartography table counts as explored (`ExplorerShared`). The map views draw it while the game shows the table's map.
- Explorer routes can cross open sea.
- Routes are searched in the background, so the game no longer slows down while one is calculated. The route tables build about three times faster and are kept on disk, so a world seen before has them at once.
- The minimap shows the chosen map view, as sharp as the large map.
- Unexplored shade: 0 turns it off, and the same value is lighter than before. The game's cloud look is optional and off by default (`FogClouds`), it does not match the game's fog yet.
- Faster map view switches (the map picture is kept, mist included), and the Layers page opens without a hitch.
- A ship farther than `ShipMeters` is used when it lies on explored or cartography land.
- With ShipPins installed, Navigator draws no ship or cart markers of its own.
- Moder's power counts as a tailwind on every heading when routes plan sailing, and a weak wind no longer starts reroutes.
- Spawn-safe areas use each object's own live radius.
- Settings are grouped into numbered sections. Your values move with them by themselves.
- Fixed about 300 font warnings in the log and a slow first opening of the Layers page.

## 0.1.1

- Multiplayer: a guest gets the host's full location search, portals and ships when both use Navigator 0.1.1. Only map data is sent, and the host can turn sharing off.
- Guests keep pins, portals, known objects and manual routes without host support. Routes and done marks stay personal.
- The location list is cached per world, so a rejoin needs only a quick check. The request waits until you have spawned.
- Done marks from 0.1.0 carry over automatically.
- Fixed portal tags naming the wrong portal at a hub of many portals.
- Fixed a white large map and a missing large-map route after the game rebuilds its map, for example after rejoining.
- Much less garbage and fewer stutters while the large map is open.

## 0.1.0

- First public beta, a work in progress.
- Search, layer filters and terrain views on the large map.
- Shortest and Comfort routes on foot, by ship or by a boat built at the shore, with portal pairs, the predicted wind and a 35 m swim limit.
- Detailed route detail as a work in progress. It reads the nearby real ground, rocks and trees. Player buildings are optional and off by default.
