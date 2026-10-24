# Changelog

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
