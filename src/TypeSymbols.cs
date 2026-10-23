using System;

namespace ValheimNavigator
{
    internal static class TypeSymbols
    {
        // a familiar symbol for the common types, 0 leaves a plain dot in its category color
        internal static int Code(Entry entry)
        {
            string kind = entry.Kind.ToLowerInvariant();
            if (kind.Contains("burial")) return 1;
            if (kind.Contains("sunken")) return 2;
            if (kind.Contains("frost")) return 3;
            if (kind.Contains("infested")) return 4;
            if (kind.Contains("troll")) return 5;
            if (kind.Contains("bear")) return 6;
            if (kind.Contains("morgen")) return 7;
            if (kind.Contains("tower")) return 8;
            if (kind.Contains("fortress")) return 9;
            if (entry.Category == "traders") return 10;
            if (entry.Category == "bosses") return 11;
            if (entry.Category == "runestones") return 12;
            return 0;
        }
    }
}
