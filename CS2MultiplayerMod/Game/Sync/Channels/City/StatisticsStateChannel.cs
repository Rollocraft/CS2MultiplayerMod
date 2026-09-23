using System.Collections.Generic;
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
    /// Cumulative event counters (deaths, births, moves, crime, mail, transport passengers and cargo), fed
    /// through the game's own statistics event pipeline so the buffers stay consistent.
    /// </summary>
    public sealed class StatisticsStateChannel : IStateChannel
    {
        public const byte Id = 10;
        public byte ChannelId => Id;

        private const int MaxEntriesPerSnapshot = 64;

        private static readonly StatisticType[] Totals =
        {
            StatisticType.DeathRate,          // "deaths" — cumulative count of citizen deaths
            StatisticType.BirthRate,
            StatisticType.CitizensMovedIn,
            StatisticType.CitizensMovedAway,
            StatisticType.CrimeCount,
            StatisticType.EscapedArrestCount,
            StatisticType.CollectedMail,
            StatisticType.DeliveredMail,
            StatisticType.CargoCountTruck,
            StatisticType.CargoCountTrain,
            StatisticType.CargoCountShip,
            StatisticType.CargoCountAirplane,
        };

        // Boardings are counted per PassengerType: parameter 0 citizens, 1 tourists.
        private static readonly StatisticType[] Passengers =
        {
            StatisticType.PassengerCountBus,
            StatisticType.PassengerCountSubway,
            StatisticType.PassengerCountTram,
            StatisticType.PassengerCountTrain,
            StatisticType.PassengerCountTaxi,
            StatisticType.PassengerCountAirplane,
            StatisticType.PassengerCountShip,
            StatisticType.PassengerCountFerry,
        };

        private static readonly int[] Synced = BuildTable();
        private static readonly HashSet<int> SyncedKeys = new HashSet<int>(Synced);

        private CityStatisticsSystem _stats;
        private bool _warned;

        // The game commits queued events only when it samples, so until the sample count or the counter
        // moves, the last correction is still queued and the counter is headed for its target.
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();

        private struct Pending
        {
            public long Target;
            public long LocalAtEnqueue;
            public int Sample;
        }

        private static int Key(StatisticType type, int parameter) => ((int)type << 8) | parameter;
        private static StatisticType TypeOf(int key) => (StatisticType)(key >> 8);
        private static int ParameterOf(int key) => key & 0xFF;

        private static int[] BuildTable()
        {
            var keys = new List<int>();
            foreach (StatisticType type in Totals) keys.Add(Key(type, 0));
            foreach (StatisticType type in Passengers)
            {
                keys.Add(Key(type, (int)PassengerType.Citizen));
                keys.Add(Key(type, (int)PassengerType.Tourist));
            }
            return keys.ToArray();
        }

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
                    StatisticType type = TypeOf(Synced[i]);
                    int parameter = ParameterOf(Synced[i]);
                    writer.WriteByte((byte)type);
                    writer.WriteByte((byte)parameter);
                    writer.WriteLong(stats.GetStatisticValueLong(type, parameter));
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
            if (count > MaxEntriesPerSnapshot)
            {
                WarnOnce("apply", new System.IO.InvalidDataException(
                    "Implausible statistics entry count: " + count + "."));
                return;
            }
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var type = (StatisticType)reader.ReadByte();
                    int parameter = reader.ReadByte();
                    long hostValue = reader.ReadLong();
                    int key = Key(type, parameter);
                    if (!SyncedKeys.Contains(key)) continue;

                    // Reconciling against the committed value also takes back this machine's own events,
                    // which it simulates as well (every ride is counted on both sides).
                    long localValue = stats.GetStatisticValueLong(type, parameter);
                    long baseline = localValue;
                    if (_pending.TryGetValue(key, out Pending pending) &&
                        pending.Sample == stats.sampleCount && pending.LocalAtEnqueue == localValue)
                        baseline = pending.Target;

                    long delta = hostValue - baseline;
                    if (delta == 0) continue;

                    CityStatisticsSystem.SafeStatisticQueue queue = stats.GetSafeStatisticsQueue(out JobHandle deps);
                    deps.Complete();
                    queue.Enqueue(new StatisticsEvent
                    {
                        m_Statistic = type,
                        m_Parameter = parameter,
                        m_Change = delta,
                    });
                    _pending[key] = new Pending
                    {
                        Target = hostValue,
                        LocalAtEnqueue = localValue,
                        Sample = stats.sampleCount,
                    };
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
