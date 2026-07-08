using Game.City;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Host-side treasury charging for constructions that arrive over the wire. The game
    /// only charges the machine whose tool placed the object, so a remote player's build
    /// would otherwise be free city-wide. The host - the money authority - charges the
    /// shared treasury when realizing a remote command; the building client's own local
    /// charge is overwritten by the next money snapshot, so everyone converges on exactly
    /// one charge. All methods are no-ops unless this machine is the connected host.
    /// </summary>
    public static class ConstructionCharger
    {
        /// <summary>Standalone object/building: <see cref="PlaceableObjectData.m_ConstructionCost"/>.</summary>
        public static void ChargeObject(EntityManager em, Entity prefab, string name)
        {
            if (em.HasComponent<PlaceableObjectData>(prefab))
                Charge(em, em.GetComponentData<PlaceableObjectData>(prefab).m_ConstructionCost, name);
        }

        /// <summary>Service-building extension: upgrade cost, falling back to placement cost.</summary>
        public static void ChargeUpgrade(EntityManager em, Entity prefab, string name)
        {
            if (em.HasComponent<ServiceUpgradeData>(prefab))
                Charge(em, em.GetComponentData<ServiceUpgradeData>(prefab).m_UpgradeCost, name);
            else
                ChargeObject(em, prefab, name);
        }

        /// <summary>
        /// Road segment: per-cell cost x cell count. The game prices nets per 8 m cell;
        /// length/8 reproduces that closely but not exactly (elevation multipliers are
        /// not applied) - flagged in the log line for in-game tuning.
        /// </summary>
        public static void ChargeNet(EntityManager em, Entity prefab, float length, string name)
        {
            if (!em.HasComponent<PlaceableNetData>(prefab)) return;
            uint perCell = em.GetComponentData<PlaceableNetData>(prefab).m_DefaultConstructionCost;
            int cells = math.max(1, (int)math.round(length / 8f));
            Charge(em, (long)perCell * cells, name + " x" + cells + " cells (8m approximation)");
        }

        /// <summary>A price already known in money terms (e.g. a remote map tile purchase).</summary>
        public static void ChargeAmount(EntityManager em, long amount, string what) =>
            Charge(em, amount, what);

        private static void Charge(EntityManager em, long amount, string what)
        {
            if (amount <= 0 || !IsChargingHost()) return;

            EntityQuery query = em.CreateEntityQuery(ComponentType.ReadWrite<PlayerMoney>());
            try
            {
                if (query.CalculateEntityCount() == 0) return;
                Entity city = query.GetSingletonEntity();
                PlayerMoney money = em.GetComponentData<PlayerMoney>(city);
                if (money.m_Unlimited) return;

                money.Subtract((int)math.min(amount, int.MaxValue));
                em.SetComponentData(city, money);
                Mod.Verbose("[MP] Charged " + amount + " for remote build: " + what + ".");
            }
            finally
            {
                query.Dispose();
            }
        }

        private static bool IsChargingHost()
        {
            MultiplayerService service = Mod.Service;
            return service != null &&
                   service.GameplaySyncReady &&
                   service.Session.Role == SessionRole.Host;
        }
    }
}
