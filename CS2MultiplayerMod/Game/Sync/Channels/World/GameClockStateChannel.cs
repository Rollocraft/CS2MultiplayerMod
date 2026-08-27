using System;
using Unity.Entities;
using Game.Common;
using Game.Simulation;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the in-game calendar/clock (date and time of day) so both players read
    /// the same date and see the same day/night cycle. The game derives its clock from
    /// <c>frameIndex - TimeData.m_FirstFrame</c>, so instead of forcing the client's
    /// frame counter (which schedules the whole simulation and must never jump), the
    /// client re-anchors its own <see cref="TimeData.m_FirstFrame"/> so that its elapsed
    /// frames equal the host's. A small tolerance avoids rewriting the anchor every
    /// snapshot over network jitter.
    /// </summary>
    public sealed class GameClockStateChannel : IStateChannel
    {
        public const byte Id = 14;
        public byte ChannelId => Id;

        /// <summary>~1 in-game minute of drift allowed before re-anchoring the clock.</summary>
        private const long DriftToleranceFrames = 64;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<TimeData>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            SimulationSystem simulation = em.World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation == null) return false;

            TimeData time = em.GetComponentData<TimeData>(_query.GetSingletonEntity());
            writer.WriteInt((int)simulation.frameIndex);
            writer.WriteInt((int)time.m_FirstFrame);
            writer.WriteInt(time.m_StartingYear);
            writer.WriteByte(time.m_StartingMonth);
            writer.WriteByte(time.m_StartingHour);
            writer.WriteByte(time.m_StartingMinutes);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            uint hostFrame = (uint)reader.ReadInt();
            uint hostFirstFrame = (uint)reader.ReadInt();
            int startingYear = reader.ReadInt();
            byte startingMonth = reader.ReadByte();
            byte startingHour = reader.ReadByte();
            byte startingMinutes = reader.ReadByte();

            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return;
            SimulationSystem simulation = em.World.GetExistingSystemManaged<SimulationSystem>();
            if (simulation == null) return;

            long hostElapsed = (long)hostFrame - hostFirstFrame;
            if (hostElapsed < 0) hostElapsed = 0;

            // Anchor our clock so OUR elapsed frames equal the host's right now.
            long desiredFirstFrame = (long)simulation.frameIndex - hostElapsed;
            if (desiredFirstFrame < 0) desiredFirstFrame = 0;

            Entity entity = _query.GetSingletonEntity();
            TimeData time = em.GetComponentData<TimeData>(entity);

            bool baseDiffers = time.m_StartingYear != startingYear ||
                               time.m_StartingMonth != startingMonth ||
                               time.m_StartingHour != startingHour ||
                               time.m_StartingMinutes != startingMinutes;
            if (!baseDiffers && Math.Abs(time.m_FirstFrame - desiredFirstFrame) <= DriftToleranceFrames) return;

            time.m_FirstFrame = (uint)desiredFirstFrame;
            time.m_StartingYear = startingYear;
            time.m_StartingMonth = startingMonth;
            time.m_StartingHour = startingHour;
            time.m_StartingMinutes = startingMinutes;
            em.SetComponentData(entity, time);
        }
    }
}
