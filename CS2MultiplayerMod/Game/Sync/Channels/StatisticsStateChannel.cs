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

        // What each counter will read once the events we already queued have been processed.
        // The game drains that queue from its own statistics job, which runs minutes apart (and
        // not at all while paused) - so the naive "host value minus current value" delta gets
        // re-queued every snapshot and applies dozens of times over. Tracking the in-flight
        // target instead makes each snapshot queue only the part not already on its way.
        private readonly System.Collections.Generic.Dictionary<StatisticType, long> _inFlightTarget =
            new System.Collections.Generic.Dictionary<StatisticType, long>();

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
                    long localValue = stats.GetStatisticValueLong(type, 0);

                    // Where this counter is headed: the value it will hold once the events already
                    // queued are processed. Once the local value has caught up to that target the
                    // queue has drained and the target is simply the current value again.
                    long target;
                    if (!_inFlightTarget.TryGetValue(type, out target) || target == localValue)
                        target = localValue;

                    long delta = hostValue - target;
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
                    _inFlightTarget[type] = hostValue;
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
