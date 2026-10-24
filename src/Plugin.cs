using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ValheimNavigator
{
    // interactive map and navigator on the game's large map: search, layers, terrain and routes. client only
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "onononoki.ValheimNavigator";
        public const string ModName = "ValheimNavigator";
        public const string ModVersion = "0.1.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> LogCounters;
        internal static ConfigEntry<float> AutoMarkMeters;

        private void Update()
        {
            WorldSync.Tick();
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) { if (Enabled.Value) KnownWorld.Tick(); return; }
            Session.Tick(UnityEngine.Time.deltaTime);
            if (Enabled.Value) { KnownWorld.Tick(); Navigator.Tick(); }
        }

        // the mod was called MapSearch before 0.7.7. its cfg and its data folder are copied once to the new names, the old files stay
        private void Migrate()
        {
            try
            {
                string config = BepInEx.Paths.ConfigPath;
                string oldCfg = Path.Combine(config, "onononoki.MapSearch.cfg");
                if (!File.Exists(Config.ConfigFilePath) && File.Exists(oldCfg))
                {
                    File.Copy(oldCfg, Config.ConfigFilePath);
                    Config.Reload();
                    Logger.LogInfo("settings copied from onononoki.MapSearch.cfg");
                }
                string oldData = Path.Combine(config, "MapSearch"), newData = Path.Combine(config, ModName);
                if (!Directory.Exists(newData) && Directory.Exists(oldData))
                {
                    Directory.CreateDirectory(newData);
                    foreach (var file in Directory.GetFiles(oldData)) File.Copy(file, Path.Combine(newData, Path.GetFileName(file)));
                    Logger.LogInfo("done lists and spawn radii copied from config\\MapSearch");
                }
            }
            catch (System.Exception ex) { Logger.LogWarning("settings from MapSearch could not be copied: " + ex.Message); }
        }

        private void Awake()
        {
            Log = Logger;
            Migrate();
            Enabled = Config.Bind("General", "Enabled", true,
                "Search and filters on the large map. Off: the mod does nothing.");
            LogCounters = Config.Bind("Debug", "LogCounters", false,
                "Write the index counts and build times to the log.");
            AutoMarkMeters = Config.Bind("Done", "AutoMarkMeters", 25f,
                new ConfigDescription("The distance in meters at which a searched location is marked done when you arrive. Used when AutoMark is on.",
                    new AcceptableValueRange<float>(5f, 200f)));
            Settings.Bind(Config);

            new Harmony(ModGuid).PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"{ModName} {ModVersion} loaded");
        }
    }
}
