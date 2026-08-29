namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Player authorization role in a multiplayer session: Normal Player or Spectator.
    /// </summary>
    public enum PlayerRole
    {
        Player = 0,
        Spectator = 1
    }

    /// <summary>
    /// Helper utilities for verifying command permissions per role.
    /// </summary>
    public static class RoleMatrix
    {
        public static bool CanExecuteCommand(PlayerRole role, ushort commandId)
        {
            return role != PlayerRole.Spectator;
        }
    }
}

