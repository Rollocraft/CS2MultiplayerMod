using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// The city loan, editable through the game's ChangeLoan on the host. Clients set the balance
    /// directly: the money channel already includes the transaction.
    /// </summary>
    public sealed class LoanStateChannel : IStateChannel
    {
        public const byte Id = 12;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<global::Game.Simulation.Loan>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;

            // Frame indices differ between machines; only the amount is shared state.
            writer.WriteInt(em.GetComponentData<global::Game.Simulation.Loan>(_query.GetSingletonEntity()).m_Amount);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int amount = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;

            Entity entity = _query.GetSingletonEntity();
            var loan = em.GetComponentData<global::Game.Simulation.Loan>(entity);
            if (loan.m_Amount == amount) return;

            if (Mod.Service != null && Mod.Service.Session.Role == SessionRole.Client)
            {
                // ChangeLoan clamps against local cash and queues; land the authoritative amount without another
                // transaction.
                loan.m_Amount = amount;
                loan.m_LastModified = em.World
                    .GetOrCreateSystemManaged<global::Game.Simulation.SimulationSystem>().frameIndex;
                em.SetComponentData(entity, loan);
                return;
            }

            em.World.GetOrCreateSystemManaged<global::Game.Tools.LoanSystem>().ChangeLoan(amount);
        }
    }
}
