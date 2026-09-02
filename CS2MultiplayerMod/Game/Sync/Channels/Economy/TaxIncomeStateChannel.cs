using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Game.City;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Host-authoritative current taxable-income statistic buckets. The taxation panel calculates
    /// every displayed money amount from these four parameterized statistics, not from the tax-rate
    /// table itself. They are intentionally a separate, non-editable channel: players may propose
    /// rates through channel 6, but can never propose taxable income to the host.
    /// </summary>
    public sealed class TaxIncomeStateChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 23;
        public byte ChannelId => Id;

        private const int MaxEntries = 256;
        private const int MaxParameter = 127;
        private const double MaxMagnitude = 1e30;

        private CityStatisticsSystem _statistics;
        private bool _warned;
        private bool _hasAuthoritativeSnapshot;
        private World _world;
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "TaxIncome", "tax collection", "tax payments and taxable-income history",
            "tax authority", typeof(TaxSystem));

        private struct Entry
        {
            public StatisticType Type;
            public int Parameter;
            public double Value;
            public double TotalValue;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DoubleBits
        {
            [FieldOffset(0)] public double Double;
            [FieldOffset(0)] public long Long;
        }

        private CityStatisticsSystem Resolve(EntityManager em) =>
            _statistics ?? (_statistics =
                em.World.GetOrCreateSystemManaged<CityStatisticsSystem>());

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            CityStatisticsSystem statistics = Resolve(em);
            try
            {
                statistics.CompleteWriters();
                NativeParallelHashMap<CityStatisticsSystem.StatisticsKey, Entity> lookup =
                    statistics.GetLookup();
                NativeKeyValueArrays<CityStatisticsSystem.StatisticsKey, Entity> pairs =
                    lookup.GetKeyValueArrays(Allocator.Temp);
                var entries = new List<Entry>();
                try
                {
                    for (int i = 0; i < pairs.Length; i++)
                    {
                        CityStatisticsSystem.StatisticsKey key = pairs.Keys[i];
                        if (!IsTaxIncome(key.type) || key.parameter < 0 ||
                            key.parameter > MaxParameter) continue;

                        double value = 0d, total = 0d;
                        Entity entity = pairs.Values[i];
                        if (entity != Entity.Null && em.Exists(entity) &&
                            em.HasBuffer<CityStatistic>(entity))
                        {
                            DynamicBuffer<CityStatistic> buffer =
                                em.GetBuffer<CityStatistic>(entity, true);
                            if (buffer.Length > 0)
                            {
                                CityStatistic current = buffer[buffer.Length - 1];
                                value = current.m_Value;
                                total = current.m_TotalValue;
                            }
                        }
                        if (!IsValid(value) || !IsValid(total)) continue;
                        entries.Add(new Entry
                        {
                            Type = key.type,
                            Parameter = key.parameter,
                            Value = value,
                            TotalValue = total,
                        });
                    }
                }
                finally
                {
                    pairs.Dispose();
                }

                if (entries.Count > MaxEntries)
                    throw new InvalidOperationException("Tax-income statistic count exceeds its cap.");
                entries.Sort(Compare);
                writer.WriteShort((short)entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    writer.WriteByte((byte)entry.Type);
                    writer.WriteInt(entry.Parameter);
                    writer.WriteLong(new DoubleBits { Double = entry.Value }.Long);
                    writer.WriteLong(new DoubleBits { Double = entry.TotalValue }.Long);
                }
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce("capture", ex);
                return false;
            }
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            int count = reader.ReadShort();
            if (count < 0 || count > MaxEntries)
                throw new ProtocolException("Invalid tax-income statistic count " + count + ".");
            var entries = new Entry[count];
            var keys = new HashSet<long>();
            for (int i = 0; i < count; i++)
            {
                var type = (StatisticType)reader.ReadByte();
                int parameter = reader.ReadInt();
                double value = new DoubleBits { Long = reader.ReadLong() }.Double;
                double total = new DoubleBits { Long = reader.ReadLong() }.Double;
                if (!IsTaxIncome(type) || parameter < 0 || parameter > MaxParameter ||
                    !IsValid(value) || !IsValid(total))
                    throw new ProtocolException("Invalid taxable-income statistic entry.");
                long key = ((long)(int)type << 32) | (uint)parameter;
                if (!keys.Add(key))
                    throw new ProtocolException("Duplicate taxable-income statistic entry.");
                entries[i] = new Entry
                {
                    Type = type,
                    Parameter = parameter,
                    Value = value,
                    TotalValue = total,
                };
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in tax-income state.");

            CityStatisticsSystem statistics = Resolve(em);
            try
            {
                statistics.CompleteWriters();
                NativeParallelHashMap<CityStatisticsSystem.StatisticsKey, Entity> lookup =
                    statistics.GetLookup();
                for (int i = 0; i < entries.Length; i++)
                {
                    Entry wanted = entries[i];
                    Entity entity;
                    if (!lookup.TryGetValue(new CityStatisticsSystem.StatisticsKey(
                            wanted.Type, wanted.Parameter), out entity) ||
                        entity == Entity.Null || !em.Exists(entity)) continue;

                    if (!em.HasBuffer<CityStatistic>(entity)) continue;
                    DynamicBuffer<CityStatistic> buffer =
                        em.GetBuffer<CityStatistic>(entity, false);
                    if (buffer.Length == 0) buffer.Add(default(CityStatistic));
                    int last = buffer.Length - 1;
                    CityStatistic current = buffer[last];
                    current.m_Value = wanted.Value;
                    current.m_TotalValue = wanted.TotalValue;
                    buffer[last] = current;
                }
                _world = em.World;
                MultiplayerService service = Mod.Service;
                if (service != null && service.GameplaySyncReady)
                    _authority.Apply(em.World, service.Session);
                _hasAuthoritativeSnapshot = true;
            }
            catch (Exception ex)
            {
                _hasAuthoritativeSnapshot = false;
                _authority.Restore(em.World);
                WarnOnce("apply", ex);
            }
        }

        public void Pump(EntityManager em)
        {
            _world = em.World;
            MultiplayerService service = Mod.Service;
            if (_hasAuthoritativeSnapshot && service != null && service.GameplaySyncReady &&
                service.Session.Role == SessionRole.Client)
                _authority.Apply(em.World, service.Session);
            else
                _authority.Restore(em.World);
        }

        public void ResetPending()
        {
            _hasAuthoritativeSnapshot = false;
            if (_world != null) _authority.Restore(_world);
        }

        private static bool IsTaxIncome(StatisticType type) =>
            type == StatisticType.ResidentialTaxableIncome ||
            type == StatisticType.CommercialTaxableIncome ||
            type == StatisticType.IndustrialTaxableIncome ||
            type == StatisticType.OfficeTaxableIncome;

        private static bool IsValid(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) &&
            value >= -MaxMagnitude && value <= MaxMagnitude;

        private static int Compare(Entry left, Entry right)
        {
            int byType = ((int)left.Type).CompareTo((int)right.Type);
            return byType != 0 ? byType : left.Parameter.CompareTo(right.Parameter);
        }

        private void WarnOnce(string stage, Exception ex)
        {
            if (_warned) return;
            _warned = true;
            SyncLog.Warn(LogTopic.City, "Tax-income channel " + stage +
                " failed (logged once): " + ex.Message);
        }
    }
}
