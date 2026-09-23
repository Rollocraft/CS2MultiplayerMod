using Game.City;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Host charging for remote constructions: the game only charges the placing machine, and the
    /// client's own charge is overwritten by the next money snapshot. No-op unless connected as host.
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

        /// <summary>Calculate one net course's host-authoritative charge without mutating money.</summary>
        public static long CalculateNetCost(EntityManager em, Entity prefab, float length)
        {
            if (!em.HasComponent<PlaceableNetData>(prefab)) return 0;
            uint perCell = em.GetComponentData<PlaceableNetData>(prefab).m_DefaultConstructionCost;
            int cells = math.max(1, (int)math.round(length / 8f));
            return (long)perCell * cells;
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
                SyncLog.Detail(LogTopic.Pipeline, "Charged " + amount + " for remote build: " + what +
                    ".");
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
