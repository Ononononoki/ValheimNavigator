# Valheim Navigator

Two mods in one for Valheim: an interactive world map and a navigator. The large map and the minimap show the world in detailed 2 m map views with the buildings players placed. Search and filter every location type, pick one, and follow a route there on foot, by ship and through your portals, planned with the wind, and find every player's tombstone in the Tombs tab. Beta, a work in progress.

A Valheim mod by Onononoki. Get it on Thunderstore: https://thunderstore.io/c/valheim/p/Onononoki/ValheimNavigator/

The full description, settings and changelog are in [package/README.md](package/README.md) and [package/CHANGELOG.md](package/CHANGELOG.md).

## Multiplayer

The host can share the world's locations and its map data (terraforming, paths, player buildings, location ground and tombstones) with guests who also run Navigator. Full world search works for guests when the host also runs a compatible Navigator version and allows sharing. Personal routes and completion marks stay local.

Without host support, guests can still use the map views, routes on their own known ground, manual destinations and available pins, portals and known objects. A clear message explains when the full location list is unavailable. Dedicated servers are not tested yet.

## Generated files

Navigator keeps a detailed terrain map and a route map per world, map pictures and route tables on disk. They are not deleted when the mod is removed. The folders, their sizes and how to turn the detailed terrain map off are in [package/README.md](package/README.md), section "Files Navigator makes, and how to remove them".

## Bug reports and feedback

Bug reports and ideas: open an issue at https://github.com/Ononononoki/ValheimNavigator/issues. Please add the route mode, the Detail setting, your mod list and the BepInEx log (`BepInEx/LogOutput.log`).

## Building

Needs the .NET SDK, Valheim and a mod manager profile with BepInEx.

```
dotnet build -c Release -p:GamePath="<Valheim folder>" -p:ProfilePath="<mod manager profile folder>"
```

The dll is built into `bin/Release/net472` and copied to `package/`.

The detailed map view draws with a copy of the game's own map shader, built into an AssetBundle (`assets/navigator_finemap`). It comes from the game's files, so it is not in this repository. Without it the build works, and the detailed view is not drawn.

## Credits

Reading your terraforming and ground paint from the world data follows LivingMap by tbsj1ga (https://github.com/tbsj1ga/LivingMapValheim). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

MIT, see [LICENSE](LICENSE).
