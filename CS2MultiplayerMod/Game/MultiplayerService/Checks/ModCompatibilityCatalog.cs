using System;
using System.Collections.Generic;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// The compatibility policy used at the multiplayer boundary.  Keep the names here in
    /// lock-step with help/mods.md: the game must not advertise a mod as supported while
    /// silently rejecting it at Host/Join.
    ///
    /// Playset metadata available across supported game builds exposes a display name but
    /// not a stable publisher/version identifier.  Exact-name matching is therefore
    /// deliberately conservative: a renamed or unknown mod remains blocked until it has
    /// been reviewed.  The handshake manifest introduced later will add identity/version
    /// comparison; this catalog answers the policy question in the meantime.
    /// </summary>
    internal static class ModCompatibilityCatalog
    {
        internal enum Support
        {
            Allowed,
            Restricted,
            Blocked,
            Unknown
        }

        private static readonly Dictionary<string, Support> Entries =
            new Dictionary<string, Support>(StringComparer.OrdinalIgnoreCase)
            {
                // Officially supported and tested client-safe mods.
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
                { "Specialized Industry Freedom", Support.Allowed },
                { "Articulated Buses", Support.Allowed },
                { "No Vehicle Despawn", Support.Allowed },
                { "Realistic JobSearch", Support.Allowed },
                { "Realistic Trips", Support.Allowed },
                { "Realistic Workplaces And Households", Support.Allowed },
                { "Traffic Tool Essentials", Support.Allowed },
                { "Official Region Packs", Support.Allowed },

                // These have documented host-only or manual-resync limitations.  They do
                // not block a session, but their presence is explicit in the session log.
                { "Move It", Support.Restricted },
                { "Node Controller", Support.Restricted },
                { "Traffic Lights Enhancement", Support.Restricted },
                { "CoPaste", Support.Restricted },
                { "529 Tiles", Support.Restricted },
                { "Change Company", Support.Restricted },
                { "Event Rush", Support.Restricted },
                { "Decals / Props", Support.Restricted },

                // Known crash/desync risk.  This cannot be overridden accidentally.
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
    }
}
