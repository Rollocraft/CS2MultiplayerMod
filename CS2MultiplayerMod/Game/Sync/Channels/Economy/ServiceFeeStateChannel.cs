using System;
using System.Collections.Generic;
using Game.City;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the service-fee sliders (electricity/water price etc.): the
    /// <see cref="ServiceFee"/> buffer on the city entity, keyed by
    /// <see cref="PlayerResource"/> which is a stable enum (same on every machine).
    /// Player-editable - every player may move the sliders; the host arbitrates.
    /// </summary>
    public sealed class ServiceFeeStateChannel : IStateChannel
    {
        public const byte Id = 8;
        public byte ChannelId => Id;

        // Native slider limits are substantially smaller. Keep a generous forward-compatible
        // ceiling while rejecting non-finite or overflow-sized editable payloads.
        private const float MaxFee = 1000000f;

        private EntityQuery _cityQuery;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _cityQuery = em.CreateEntityQuery(ComponentType.ReadWrite<PlayerMoney>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_cityQuery.CalculateEntityCount() != 1) return false;
            Entity city = _cityQuery.GetSingletonEntity();
            if (!em.HasBuffer<ServiceFee>(city)) return false;

            DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city, true);
            if (fees.Length > (int)PlayerResource.Count) return false;
            var entries = new List<(byte resource, float fee)>(fees.Length);
            var resources = new HashSet<byte>();
            for (int i = 0; i < fees.Length; i++)
            {
                int resource = (int)fees[i].m_Resource;
                float fee = fees[i].m_Fee;
                if (resource < 0 || resource >= (int)PlayerResource.Count ||
                    !IsValidFee(fee) || !resources.Add((byte)resource)) return false;
                entries.Add(((byte)resource, fee));
            }
            entries.Sort((left, right) => left.resource.CompareTo(right.resource));

            writer.WriteByte((byte)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                writer.WriteByte(entries[i].resource);
                writer.WriteFloat(entries[i].fee);
            }
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int count = reader.ReadByte();
            if (count > (int)PlayerResource.Count)
                throw new ProtocolException("Invalid service-fee count " + count + ".");
            var wanted = new (byte resource, float fee)[count];
            var resources = new HashSet<byte>();
            for (int i = 0; i < count; i++)
            {
                byte resource = reader.ReadByte();
                float fee = WireGuard.ReadFinite(reader);
                if (resource >= (byte)PlayerResource.Count || !IsValidFee(fee) ||
                    !resources.Add(resource))
                    throw new ProtocolException("Invalid or duplicate service-fee entry.");
                wanted[i] = (resource, fee);
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in service-fee state.");

            if (_cityQuery.CalculateEntityCount() != 1) return;
            Entity city = _cityQuery.GetSingletonEntity();
            if (!em.HasBuffer<ServiceFee>(city)) return;

            DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city);
            if (fees.Length != count)
                throw new ProtocolException("Service-fee table length differs from this game build (" +
                    count + " on wire, " + fees.Length + " locally).");

            // Prove the complete table matches before changing any entry. A forged partial or
            // foreign-build table can therefore never leave the host half-updated.
            var localResources = new HashSet<byte>();
            for (int f = 0; f < fees.Length; f++)
            {
                int localResource = (int)fees[f].m_Resource;
                if (localResource < 0 || localResource >= (int)PlayerResource.Count ||
                    !resources.Contains((byte)localResource) ||
                    !localResources.Add((byte)localResource))
                    throw new ProtocolException(
                        "Service-fee resources differ from this game build.");
            }

            for (int i = 0; i < count; i++)
            {
                for (int f = 0; f < fees.Length; f++)
                {
                    if ((byte)fees[f].m_Resource != wanted[i].resource) continue;
                    if (!fees[f].m_Fee.Equals(wanted[i].fee))
                    {
                        ServiceFee fee = fees[f];
                        fee.m_Fee = wanted[i].fee;
                        fees[f] = fee;
                    }
                    break;
                }
            }
        }

        private static bool IsValidFee(float fee) =>
            !float.IsNaN(fee) && !float.IsInfinity(fee) && fee >= 0f && fee <= MaxFee;
    }
}
