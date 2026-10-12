namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class NativeReplacementPolicy
    {
        // Delete beats replacement and lane cancellation beats both; only a real replacement needs the original.
        public static bool RequiresOriginal(bool edge, bool lane, bool delete, bool cancel,
            bool replace, bool combine)
        {
            if (delete || (lane && cancel)) return false;
            return (edge && (replace || combine)) || (lane && replace);
        }
    }
}
