namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Player authorization roles in a multiplayer session.
    /// </summary>
    public enum PlayerRole
    {
        Admin = 0,
        Builder = 1,
        RoadPlanner = 2,
        ZoningManager = 3,
        Spectator = 4
    }

    /// <summary>
    /// Helper utilities for verifying command permissions per role.
    /// </summary>
    public static class RoleMatrix
    {
        public static bool CanExecuteCommand(PlayerRole role, ushort commandId)
        {
            if (role == PlayerRole.Admin || role == PlayerRole.Builder) return true;
            if (role == PlayerRole.Spectator) return false;

            if (role == PlayerRole.RoadPlanner)
            {
                // Road/net commands: NetPlacement (2), NetDelete (4), NetUpgrade (9), NetReplace (19), Routes (12, 13, 17)
                return commandId == 2 || commandId == 4 || commandId == 9 || commandId == 19 ||
                       commandId == 12 || commandId == 13 || commandId == 17;
            }

            if (role == PlayerRole.ZoningManager)
            {
                // Zoning & area commands: ZonePaint (5), Areas (10, 11, 16, 23), Policies (15)
                return commandId == 5 || commandId == 10 || commandId == 11 || commandId == 16 ||
                       commandId == 23 || commandId == 15;
            }

            return false;
        }
    }
}
