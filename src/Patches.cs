using HarmonyLib;

namespace ValheimNavigator
{
    // the console command is made when the game builds its command list
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class TerminalInitPatch
    {
        private static void Postfix()
        {
            Commands.Register();
        }
    }

    // while the search box has the keyboard the game's own map keys are told a text box is in use
    [HarmonyPatch(typeof(Minimap), "InTextInput")]
    internal static class MinimapTextInputPatch
    {
        private static void Postfix(ref bool __result)
        {
            if (Ui.Typing) __result = true;
        }
    }

    // the large map zooms with the mouse wheel. over the panel the wheel scrolls its lists instead, so the map gets no wheel there
    [HarmonyPatch(typeof(ZInput), "GetMouseScrollWheel")]
    internal static class MouseWheelPatch
    {
        private static void Postfix(ref float __result)
        {
            if (__result != 0f && Panel.PointerOver) __result = 0f;
        }
    }

    // the panel follows the large map, it runs after the minimap's own update each frame
    [HarmonyPatch(typeof(Minimap), "Update")]
    internal static class MinimapUpdatePatch
    {
        private static void Postfix(Minimap __instance)
        {
            Panel.Tick(__instance);
        }
    }

    [HarmonyPatch(typeof(Minimap), "OnMapLeftClick")]
    internal static class MinimapMarkerClickPatch
    {
        private static bool Prefix() { return !Layers.PickUnderMouse(); }
    }
}
