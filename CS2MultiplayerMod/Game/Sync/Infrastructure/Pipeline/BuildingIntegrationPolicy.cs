namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    internal static class BuildingIntegrationPolicy
    {
        // Only the consumers the prefab's archetype declares are expected.
        public static bool HasExpectedConsumers(bool expectsElectricity, bool expectsWater,
            bool hasElectricity, bool hasWater) =>
                (!expectsElectricity || hasElectricity) && (!expectsWater || hasWater);
    }
}
