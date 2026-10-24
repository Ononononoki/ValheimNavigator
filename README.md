# Valheim Navigator

Two mods in one for Valheim: an interactive world map and a navigator. Search and filter every location type on the large map, pick one, and follow a route there on foot, by ship and through your portals, planned with the wind. Beta, a work in progress.

A Valheim mod by Onononoki. Get it on Thunderstore: https://thunderstore.io/c/valheim/p/Onononoki/ValheimNavigator/

The full description, settings and changelog are in [package/README.md](package/README.md) and [package/CHANGELOG.md](package/CHANGELOG.md).

## Multiplayer

Version 0.1.1 adds optional host sharing. Full world search works for guests when the host also runs a compatible Navigator version and allows sharing. Personal routes and completion marks stay local.

Without host support, guests can still use terrain views, manual destinations and available pins, portals and known objects. A clear message explains when the full location list is unavailable. Dedicated servers are not tested yet.

## Bug reports and feedback

Bug reports and ideas: open an issue at https://github.com/Ononononoki/ValheimNavigator/issues. Please add the route mode, the Detail setting, your mod list and the BepInEx log (`BepInEx/LogOutput.log`).

## Building

Needs the .NET SDK, Valheim and a mod manager profile with BepInEx.

```
dotnet build -c Release -p:GamePath="<Valheim folder>" -p:ProfilePath="<mod manager profile folder>"
```

The dll is built into `bin/Release/net472` and copied to `package/`.

## License

MIT, see [LICENSE](LICENSE).
