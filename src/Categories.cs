using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValheimNavigator
{
    // the location kinds and categories of the earlier map tool, its RULES table as data.
    // the first matching rule wins, a prefab no rule covers goes to "other" with a split-up name.
    internal static class Categories
    {
        internal sealed class Rule
        {
            internal readonly string Pattern;
            internal readonly string Category;
            internal readonly string Name;
            internal readonly Regex Expression;

            internal Rule(string pattern, string category, string name)
            {
                Pattern = pattern;
                Category = category;
                Name = name;
                // ascii digits and word characters, the whole prefab name must match
                Expression = new Regex("^(?:" + pattern + ")$", RegexOptions.ECMAScript);
            }
        }

        internal static readonly string[] Order =
        {
            "spawn", "bosses", "traders", "quests", "dungeons", "caves", "spawners", "settlements",
            "dvergr", "materials", "runestones", "rare", "camps", "ruins", "other"
        };

        internal static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
        {
            { "spawn", "Spawn" }, { "bosses", "Bosses" }, { "traders", "Traders" }, { "quests", "Quest sites" },
            { "dungeons", "Dungeons" }, { "caves", "Caves" }, { "spawners", "Spawners" }, { "settlements", "Settlements" },
            { "dvergr", "Dvergr" }, { "materials", "Materials" }, { "runestones", "Runestones" }, { "rare", "Rare finds" },
            { "camps", "Camps" }, { "ruins", "Ruins" }, { "other", "Other" }
        };

        internal static readonly Rule[] Rules =
        {
            new Rule(@"StartTemple", "spawn", "Sacrificial stones"),
            new Rule(@"Eikthyrnir", "bosses", "Eikthyr"), new Rule(@"GDKing", "bosses", "The Elder"), new Rule(@"Bonemass", "bosses", "Bonemass"),
            new Rule(@"Dragonqueen", "bosses", "Moder"), new Rule(@"GoblinKing", "bosses", "Yagluth"),
            new Rule(@"Mistlands_DvergrBossEntrance1", "bosses", "The Queen"), new Rule(@"FaderLocation", "bosses", "Fader"),
            new Rule(@"DN_Bossroom", "bosses", "Deep North boss"),
            new Rule(@"Vendor_BlackForest", "traders", "Haldor"), new Rule(@"Hildir_camp", "traders", "Hildir"),
            new Rule(@"BogWitch_Camp", "traders", "Bog Witch"),
            new Rule(@"Hildir_crypt", "quests", "Smouldering Tomb"), new Rule(@"Hildir_cave", "quests", "Howling Cavern"),
            new Rule(@"Hildir_plainsfortress", "quests", "Sealed Tower"),
            new Rule(@"Crypt\d", "dungeons", "Burial Chambers"), new Rule(@"SunkenCrypt\d", "dungeons", "Sunken Crypt"),
            new Rule(@"MountainCave\d+", "dungeons", "Frost Cave"), new Rule(@"Mistlands_DvergrTownEntrance\d", "dungeons", "Infested Mine"),
            new Rule(@"CharredFortress", "dungeons", "Charred Fortress"),
            new Rule(@"TrollCave\d+", "caves", "Troll Cave"), new Rule(@"BearCave", "caves", "Bear Cave"),
            new Rule(@"MorgenHole\d", "caves", "Morgen Hole"), new Rule(@"TheHole\d+", "caves", "The Hole"),
            new Rule(@"Greydwarf_camp\d", "spawners", "Greydwarf nest camp"), new Rule(@"Grave1", "spawners", "Swamp grave"),
            new Rule(@"CharredStone_Spawner", "spawners", "Charred spawner"), new Rule(@"VoltureNest", "spawners", "Volture nest"),
            new Rule(@"FireHole", "spawners", "Surtling fire hole"),
            new Rule(@"GoblinCamp\d", "settlements", "Fuling village"), new Rule(@"WoodVillage\d", "settlements", "Draugr village"),
            new Rule(@"StoneTower\d", "settlements", "Fuling tower"), new Rule(@"NorthVillage", "settlements", "Northern village"),
            new Rule(@"MorkBorg", "settlements", "Mork Borg"), new Rule(@"LumberCamp", "settlements", "Lumber camp"),
            new Rule(@"DN_hut\d+", "settlements", "Northern hut"),
            new Rule(@"Mistlands_Harbour\d", "dvergr", "Dvergr harbour"), new Rule(@"Mistlands_Lighthouse\d_new", "dvergr", "Dvergr lighthouse"),
            new Rule(@"Mistlands_Excavation\d", "dvergr", "Dvergr excavation"), new Rule(@"Mistlands_GuardTower\d_new", "dvergr", "Dvergr guard tower"),
            new Rule(@"Mistlands_RoadPost\d", "dvergr", "Dvergr road post"), new Rule(@"CharredTowerRuins\d_dvergr", "dvergr", "Dvergr outpost"),
            new Rule(@"TarPit\d(_\d)?", "materials", "Tar pit"), new Rule(@"InfestedTree\d+", "materials", "Guck tree"),
            new Rule(@"DrakeNest\d+", "materials", "Drake nest"), new Rule(@"Mistlands_Giant\d", "materials", "Giant remains"),
            new Rule(@"LeviathanLava", "materials", "Lava leviathan"), new Rule(@"ShipWreck\d+(_DN)?", "materials", "Shipwreck"),
            new Rule(@"FrozenShip\d+_DN", "materials", "Frozen ship"),
            new Rule(@"Runestone_(\w+)", "runestones", "Runestone"), new Rule(@"DrakeLorestone", "runestones", "Drake lore stone"),
            new Rule(@"AncientUpgradeStation", "rare", "Ancient upgrade station"), new Rule(@"PlaceofMystery\d", "rare", "Place of mystery"),
            new Rule(@"BigRockClearing", "rare", "Big rock clearing"), new Rule(@"StoneTowerRuins05_leet", "rare", "Stone tower ruin (rare)"),
            new Rule(@"CombatRuin01", "rare", "Combat ruin"),
            new Rule(@"WoodHouse\d+", "camps", "Abandoned house"), new Rule(@"WoodFarm\d", "camps", "Abandoned farm"),
            new Rule(@"SwampHut\d(_\d)?", "camps", "Swamp hut"), new Rule(@"GoblinHut\d+", "camps", "Goblin hut"),
            new Rule(@"AbandonedLogCabin\d+", "camps", "Log cabin"), new Rule(@"SwampWell\d", "camps", "Swamp well"),
            new Rule(@"MountainWell\d", "camps", "Mountain well"),
            new Rule(@"Ruin\d", "ruins", "Ruin"), new Rule(@"StoneHouse\d", "ruins", "Stone house ruin"),
            new Rule(@"StoneTowerRuins\d+(_sunk)?", "ruins", "Stone tower ruin"), new Rule(@"SwampRuin\d", "ruins", "Swamp ruin"),
            new Rule(@"MountainGrave\d+", "ruins", "Mountain grave"), new Rule(@"Mistlands_GuardTower\d_ruined_new\d?", "ruins", "Ruined guard tower"),
            new Rule(@"Mistlands_Viaduct\d", "ruins", "Viaduct"), new Rule(@"Mistlands_Statue\d", "ruins", "Giant statue"),
            new Rule(@"Mistlands_StatueGroup\d", "ruins", "Statue group"), new Rule(@"Mistlands_Swords\d", "ruins", "Giant swords"),
            new Rule(@"Mistlands_RockSpire\d", "ruins", "Rock spire"), new Rule(@"AshlandRuins", "ruins", "Ashlands ruin"),
            new Rule(@"CharredRuins\d", "ruins", "Charred ruin"), new Rule(@"CharredTowerRuins\d", "ruins", "Charred tower ruin"),
            new Rule(@"FortressRuins", "ruins", "Fortress ruin"), new Rule(@"SulfurArch", "ruins", "Sulfur arch"),
            new Rule(@"NorthMemorialPlace", "ruins", "Memorial"), new Rule(@"DN_gammeltrollFrac\d+", "ruins", "Fallen troll"),
            new Rule(@"IcePond\d", "ruins", "Ice pond"), new Rule(@"Dolmen\d+", "ruins", "Dolmen"), new Rule(@"StoneHenge\d", "ruins", "Standing stones"),
            new Rule(@"StoneCircle", "ruins", "Stone circle"), new Rule(@"Waymarker\d+", "ruins", "Waymarker"), new Rule(@"ShipSetting\d+", "ruins", "Stone ship")
        };

        private static readonly Dictionary<string, KeyValuePair<string, string>> Cache = new Dictionary<string, KeyValuePair<string, string>>();

        // "CamelCase_name" to "Camel Case name", the way the map tool names a prefab no rule covers
        internal static string Split(string text)
        {
            return Regex.Replace(text, "(?<!^)(?=[A-Z])", " ");
        }

        internal static void Classify(string prefab, out string category, out string name)
        {
            if (Cache.TryGetValue(prefab, out var hit))
            {
                category = hit.Key;
                name = hit.Value;
                return;
            }

            category = "other";
            name = Split(prefab.Replace("_", " "));
            foreach (var rule in Rules)
            {
                var match = rule.Expression.Match(prefab);
                if (!match.Success) continue;
                category = rule.Category;
                name = rule.Name;
                if (category == "runestones" && match.Groups.Count > 1 && match.Groups[1].Value.Length > 0)
                    name = "Runestone (" + Split(match.Groups[1].Value) + ")";
                break;
            }
            Cache[prefab] = new KeyValuePair<string, string>(category, name);
        }
    }
}
