using Game.City;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the cumulative life-event counters - deaths, births, move-ins,
    /// move-aways, crime, mail - host -> clients, so both players' statistics panels show
    /// the same numbers between full-world resyncs.
    /// Mechanism: the host snapshots each counter's lifetime value and the client feeds it
    /// through the game's own event pipeline, the same path the deathcare/crime systems use,
    /// so the statistics buffers stay internally consistent and serializable.
    /// </summary>
    public sealed class StatisticsStateChannel : IStateChannel
    {
        public const byte Id = 10;
        public byte ChannelId => Id;

        private static readonly StatisticType[] Synced =
        {
            StatisticType.DeathRate,          // "deaths" — cumulative count of citizen deaths
            StatisticType.BirthRate,
            StatisticType.CitizensMovedIn,
            StatisticType.CitizensMovedAway,
            StatisticType.CrimeCount,
            StatisticType.EscapedArrestCount,
            StatisticType.CollectedMail,
            StatisticType.DeliveredMail,
        };

        private CityStatisticsSystem _stats;
        private bool _warned;

        private CityStatisticsSystem Resolve(EntityManager em) =>
            _stats ?? (_stats = em.World.GetOrCreateSystemManaged<CityStatisticsSystem>());

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            CityStatisticsSystem stats = Resolve(em);
            try
            {
                writer.WriteByte((byte)Synced.Length);
                for (int i = 0; i < Synced.Length; i++)
                {
                    writer.WriteByte((byte)Synced[i]);
                    writer.WriteLong(stats.GetStatisticValueLong(Synced[i], 0));
                }
                return true;
            }
            catch (System.Exception ex)
            {
                WarnOnce("capture", ex);
                return false;
            }
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            CityStatisticsSystem stats = Resolve(em);
            int count = reader.ReadByte();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var type = (StatisticType)reader.ReadByte();
                    long hostValue = reader.ReadLong();
                    long delta = hostValue - stats.GetStatisticValueLong(type, 0);
                    if (delta == 0) continue;

                    JobHandle deps;
                    CityStatisticsSystem.SafeStatisticQueue queue = stats.GetSafeStatisticsQueue(out deps);
                    deps.Complete();
                    queue.Enqueue(new StatisticsEvent
                    {
                        m_Statistic = type,
                        m_Parameter = 0,
                        m_Change = delta,
                    });
                }
            }
            catch (System.Exception ex)
            {
                // Drain the remaining payload is unnecessary — channel payloads are
                // per-message, the next snapshot starts fresh.
                WarnOnce("apply", ex);
            }
        }

        private void WarnOnce(string stage, System.Exception ex)
        {
            if (_warned) return;
            _warned = true;
            Mod.log.Warn("[MP] Statistics channel " + stage + " failed (logged once): " + ex.Message);
        }
    }
}
