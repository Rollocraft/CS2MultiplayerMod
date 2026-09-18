using Game.City;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the cumulative life-event counters - deaths, births, move-ins,
    /// move-aways, crime, mail, transport passengers and cargo - host -> clients, so
    /// both players' statistics panels show the same numbers between full-world resyncs.
    /// Mechanism: the host snapshots each counter's lifetime value and the client feeds it
    /// through the game's own event pipeline, the same path the deathcare/crime systems use,
    /// so the statistics buffers stay internally consistent and serializable.
    /// Only event-accumulated lifetime totals ride here. Gauges the simulation rewrites
    /// itself (population, money, happiness, current tourists) are deliberately excluded:
    /// forcing those through the event queue fights the writer, the same reason the
    /// population channel was retired.
    /// </summary>
    public sealed class StatisticsStateChannel : IStateChannel
    {
        public const byte Id = 10;
        public byte ChannelId => Id;

        /// <summary>
        /// Statistic parameter holding the event-accumulated lifetime total. All
        /// counters synced here are lifetime totals at this parameter, which is why
        /// the generic delta mechanism applies to every entry unchanged.
        /// </summary>
        private const int LifetimeParameter = 0;

        /// <summary>Upper bound for entries in one snapshot (the table holds 20).</summary>
        private const int MaxEntriesPerSnapshot = 64;

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
            // Transport ridership and cargo: lifetime boarding/load totals shown in the
            // transport info summaries. Same event-accumulated shape as the counters
            // above, read at parameter 0 (the total), so the generic delta mechanism
            // applies unchanged. The payload stays self-describing (count + type +
            // value), so peers without these entries simply exchange fewer of them.
            StatisticType.PassengerCountBus,
            StatisticType.PassengerCountSubway,
            StatisticType.PassengerCountTram,
            StatisticType.PassengerCountTrain,
            StatisticType.PassengerCountTaxi,
            StatisticType.PassengerCountAirplane,
            StatisticType.PassengerCountShip,
            StatisticType.PassengerCountFerry,
            StatisticType.CargoCountTruck,
            StatisticType.CargoCountTrain,
            StatisticType.CargoCountShip,
            StatisticType.CargoCountAirplane,
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
                    writer.WriteLong(stats.GetStatisticValueLong(Synced[i], LifetimeParameter));
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
            if (count < 0 || count > MaxEntriesPerSnapshot)
            {
                WarnOnce("apply", new System.IO.InvalidDataException(
                    "Implausible statistics entry count: " + count + "."));
                return;
            }
            try
            {
                for (int i = 0; i < count; i++)
                {
                    byte rawType = reader.ReadByte();
                    long hostValue = reader.ReadLong();
                    if (!System.Enum.IsDefined(typeof(StatisticType), (int)rawType)) continue;
                    var type = (StatisticType)rawType;
                    long localValue = stats.GetStatisticValueLong(type, LifetimeParameter);

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
                        m_Parameter = LifetimeParameter,
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
            SyncLog.Warn(LogTopic.City, "Statistics channel " + stage + " failed (logged once): " +
                ex.Message);
        }
    }
}
