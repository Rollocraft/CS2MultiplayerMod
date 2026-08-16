using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the city loan (<see cref="global::Game.Simulation.Loan"/> on the City
    /// entity) as a player-editable channel: any player can take or repay a loan and the
    /// host arbitrates. Applying goes through the game's own
    /// <see cref="global::Game.Tools.LoanSystem.ChangeLoan"/> so the money delta, interest
    /// and creditworthiness bookkeeping all happen exactly as for a local loan change.
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
            _query = em.CreateEntityQuery(ComponentType.ReadOnly<global::Game.Simulation.Loan>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;

            // m_LastModified (a frame index) diverges between machines and would make
            // every snapshot look like an edit — only the amount is the shared state.
            writer.WriteInt(em.GetComponentData<global::Game.Simulation.Loan>(_query.GetSingletonEntity()).m_Amount);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int amount = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;

            int current = em.GetComponentData<global::Game.Simulation.Loan>(_query.GetSingletonEntity()).m_Amount;
            if (current == amount) return;

            em.World.GetOrCreateSystemManaged<global::Game.Tools.LoanSystem>().ChangeLoan(amount);
        }
    }
}
