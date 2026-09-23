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
    /// Cumulative life-event counters (deaths, births, moves, crime, mail), fed through the game's own
    /// statistics event pipeline so the buffers stay consistent.
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

        // What each counter will read once queued events are processed; the game drains that queue rarely
        // (never while paused), so a naive delta would be queued again every snapshot.
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

                    if (!_inFlightTarget.TryGetValue(type, out long target) || target == localValue)
                        target = localValue;

                    long delta = hostValue - target;
                    if (delta == 0) continue;

                    CityStatisticsSystem.SafeStatisticQueue queue = stats.GetSafeStatisticsQueue(out JobHandle deps);
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
