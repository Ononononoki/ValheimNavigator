# Changelog

## 0.2.2

- Routes are found much faster, through portals 3 to 4 times faster.
- A route you follow is kept as the plan. Step off it and it rejoins quickly, take its portal and it goes on, a check every minute looks for a faster way.
- After a teleport off the route, the new route uses all your portals again.
- Detailed routes walk your floors, bridges, docks and stairs, go faster on paths and floors, and find the way around walls near the start and the goal.
- Lava is closed wherever it burns. In the Ashlands the rocks near you are measured, routes can cross lava over them (drawn red), and the route updates when they appear or are destroyed.
- The route map is built again once after the update (a few minutes).

## 0.2.1

- Fixed game freezes when you pick a new place while a route is still being found.
- Fixed routes at sea. On a ship, or far out at sea, the route sails from where you are.
- Routes keep clear of known sea rocks, shallows and gaps too narrow for your ship. A ship's size is measured once it floats near you.
- Sailing no longer cuts across small spits of land.
- A start on a rock, on lava or inside a building moves to the nearest open spot. Steep ground near the start and the goal is avoided again.
- A route is found far more often. When no way avoids rocks, trees, walls or harder biomes, the route says so.
- A route you follow no longer flips between two equally fast ways.
- The route updates every 5 s by default (was 1 s).
- Long trips to the Ashlands are found faster.
- Detailed routes avoid remembered trees, rocks and walls along the whole way.
- Comfort walks around swamp water, and avoids harder biomes by how much of them it would cross.
- More boats allows up to three boats on one trip.
- Lava is drawn where it burns, with a soft edge (`LavaEdge`).
- Tombs show how long ago each player died.
- The large map opens more smoothly, and the route map builds faster while the game is paused.
- Dedicated servers share terraforming and location ground too (not tested yet).
- Old route search settings were removed from the config.
- The route map is built again once after the update (a few minutes).

## 0.2.0

- New detailed map view on the large map and the minimap, drawn from a 2 m map of the world in the game's own look. It shows terraforming, paths, fields and paved ground. It is built in the background, with its progress on the panel (`DetailedTerrain` turns it off).
- Player buildings on the map and the minimap, seen from above in the colors of their material (Buildings: none, yours or all).
- New route map of the whole world at 2 m. A route of several kilometers usually takes under a second and follows the ground closely.
- Shortest is always the fastest walkable way. Only Comfort avoids harder biomes.
- Long sea trips use the wind on every leg, and More boats allows a second boat.
- Routes to the Ashlands use a drakkar, your own or one built at the shore, and walk around the lava.
- New Tombstones tab: every player's tombstone, searchable, with a route to it.
- In multiplayer the host shares the whole map data with guests (`ShareMap`).
- Panel texts shrink instead of being cut off.
- `MaxPins` also limits the numbered circles, so a zoomed out map is not crowded.
- With LivingMap installed, the panel explains how both maps work together (`LivingMapMode`).
- New defaults: the detailed map view, place names next to pins, and tombstones on the map.

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
