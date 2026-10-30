# Changelog

## 0.2.0

- Detailed map view on the large map and the minimap: the game's own map look drawn from a 2 m terrain map of the world, built in the background and kept on disk, at every zoom. It shows your terraforming, the ground the game shapes around its locations, and your paths, fields and paved ground in the game's own ground textures. The real ground of places you visit is kept too.
- The panel's bottom line shows the build progress of the detailed map and the route map. `DetailedTerrain = false` turns the detailed map off, and the line then says so.
- Buildings on the large map and the minimap: the buildings players placed by default, top down in the colors of their material, the highest layer only. The Buildings switch also shows none or every structure in the world.
- Routes, in Fast and Detailed, are planned on a new route map of the whole world at 2 m, built in the background and kept on disk, and built again where the ground changes. A route of several kilometers usually takes under a second, follows the ground more closely and is drawn with the look of the ground it crosses. The older search stays as the fallback until the route map covers a route.
- Shortest is always the fastest walkable way, running and jump-running included. Harder biomes are avoided only in Comfort.
- Sailing long ways across the sea, with the wind on every leg, and a second boat with More boats.
- To the Ashlands the boat is a drakkar: your nearest ship that can sail the boiling sea, or one built at the shore. Lava is judged like the game judges it. Routes to the Ashlands always come from the route map, which walks around the lava at 2 m. While it is still being built there, the Route page says how many tiles are left.
- Tombstones tab: every player's tombstone in the world, searchable by name, nearest first, with a route to the one you pick.
- In multiplayer the host also shares the map data of the whole world with guests: terraforming, paths and fields, player buildings, location ground and tombstones (`ShareMap`).
- Panel texts get smaller instead of being cut off, and each tab is as wide as its title.
- The pin limit (`MaxPins`, pins per category) now also holds for the numbered circles: zoomed out, each category shows only its nearest places to you, so the map is not crowded.
- The minimap buildings layer slides with you instead of being drawn again.
- With LivingMap installed, the panel says that Navigator's map views cover LivingMap's bases and roads (`LivingMapMode` keeps LivingMap's close-up view on top).
- New defaults: the detailed map view, place names next to pins when zoomed in, and tombstones on the map. Coming from 0.1.x, a map view left at the old default (the game's map) moves to the detailed view once, and any view picked after that stays.

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
