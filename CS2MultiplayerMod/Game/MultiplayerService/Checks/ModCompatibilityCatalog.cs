using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Game
{
    /// <summary>Compatibility policy for other active mods.</summary>
    internal static class ModCompatibilityCatalog
    {
        internal enum Support
        {
            Allowed,
            Restricted,
            Blocked,
            Unknown
        }

        internal enum Risk
        {
            Cosmetic,
            PersistentWorld,
            NetworkOrTerrain,
            Simulation,
            Unknown
        }

        private static readonly Dictionary<string, Support> Entries =
            new Dictionary<string, Support>(StringComparer.OrdinalIgnoreCase)
            {
                // Supported mods.
                { "Traffic", Support.Allowed },
                { "Road Speed Adjuster", Support.Allowed },
                { "Anarchy", Support.Allowed },
                { "Building Use", Support.Allowed },
                { "Custom Chirps", Support.Allowed },
                { "Extended Tooltip", Support.Allowed },
                { "I18n Everywhere", Support.Allowed },
                { "Find It", Support.Allowed },
                { "Region Flag Icons", Support.Allowed },
                { "Asset Icon Library", Support.Allowed },
                { "Unified Icon Library", Support.Allowed },
                { "Extra Lib", Support.Allowed },
                { "Industry Boundary", Support.Allowed },
                { "All Transit + Trucks", Support.Allowed },
                { "Lumina", Support.Allowed },
                { "Stop Jaywalking", Support.Allowed },
                { "Road Name Remover", Support.Allowed },
                { "Achievement Fixer", Support.Allowed },
                // Runtime assembly name used by Achievement Fixer.
                { "AchievementFixer", Support.Allowed },
                { "Specialized Industry Freedom", Support.Allowed },
                { "Articulated Buses", Support.Allowed },
                { "No Vehicle Despawn", Support.Allowed },
                { "Realistic JobSearch", Support.Allowed },
                { "Realistic Trips", Support.Allowed },
                { "Realistic Workplaces And Households", Support.Allowed },
                { "Traffic Tool Essentials", Support.Allowed },
                { "Official Region Packs", Support.Allowed },

                // Supported with limitations; record them in the session log.
                { "Move It", Support.Restricted },
                { "Node Controller", Support.Restricted },
                { "Traffic Lights Enhancement", Support.Restricted },
                { "CoPaste", Support.Restricted },
                { "529 Tiles", Support.Restricted },
                { "Change Company", Support.Restricted },
                { "Event Rush", Support.Restricted },
                { "Decals / Props", Support.Restricted },

                // Known crash or desync risk.
                { "Better Bulldozer", Support.Blocked }
            };

        public static Support Classify(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Support.Unknown;
            return Entries.TryGetValue(name.Trim(), out Support support) ? support : Support.Unknown;
        }

        public static bool BlocksStart(string name)
        {
            Support support = Classify(name);
            return support == Support.Blocked || support == Support.Unknown;
        }

        public static string Label(string name)
        {
            switch (Classify(name))
            {
                case Support.Allowed: return "allowed";
                case Support.Restricted: return "restricted";
                case Support.Blocked: return "blocked";
                default: return "unreviewed";
            }
        }

        /// <summary>Risk class used for compatibility checks.</summary>
        public static Risk RiskOf(string name)
        {
            switch (Classify(name))
            {
                case Support.Unknown: return Risk.Unknown;
                case Support.Blocked: return Risk.NetworkOrTerrain;
            }
            if (string.Equals(name, "Lumina", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Extended Tooltip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Road Name Remover", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Achievement Fixer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "AchievementFixer", StringComparison.OrdinalIgnoreCase))
                return Risk.Cosmetic;
            if (string.Equals(name, "Traffic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Traffic Lights Enhancement", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Realistic Trips", StringComparison.OrdinalIgnoreCase))
                return Risk.Simulation;
            if (string.Equals(name, "Move It", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Node Controller", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "CoPaste", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Decals / Props", StringComparison.OrdinalIgnoreCase))
                return Risk.NetworkOrTerrain;
            return Risk.PersistentWorld;
        }
    }
}
