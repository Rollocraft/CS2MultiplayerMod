using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// One bounded page of host-authoritative, property-wide rents. A page is an absolute set of
    /// independent corrections rather than an ordered delta: losing one delays those properties
    /// until the next rolling sweep, but can never make a later page unsafe to apply.
    /// </summary>
    public sealed class PropertyRentSnapshot
    {
        public const int MaxEntries = 96;
        public const int MaxPagesPerSweep = 4096;
        public const int MaxEncodedBytes = 48 * 1024;
        // Far above any plausible in-game daily rent while still preventing a corrupt host from
        // feeding a near-int.MaxValue charge into the economy systems.
        public const int MaxRent = 100000000;

        public uint SweepId;
        public int PageIndex;
        public bool EndOfSweep;
        public readonly List<PropertyRentEntry> Entries = new List<PropertyRentEntry>();

        public void Write(NetworkWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (SweepId == 0) throw new ProtocolException("Property-rent sweep id must be non-zero.");
            if (PageIndex < 0 || PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Property-rent page index is outside its cap.");
            if (Entries.Count > MaxEntries)
                throw new ProtocolException("Property-rent page exceeds its entry cap.");

            writer.WriteInt(unchecked((int)SweepId));
            writer.WriteShort((short)PageIndex);
            writer.WriteBool(EndOfSweep);
            writer.WriteShort((short)Entries.Count);
            var identities = new HashSet<PropertyRentIdentity>();
            for (int i = 0; i < Entries.Count; i++)
            {
                PropertyRentEntry entry = Entries[i];
                Validate(entry);
                if (!identities.Add(entry.Identity))
                    throw new ProtocolException("Duplicate property identity in rent page.");
                writer.WriteString(entry.PrefabName);
                writer.WriteFloat(entry.AnchorX);
                writer.WriteFloat(entry.AnchorY);
                writer.WriteFloat(entry.AnchorZ);
                writer.WriteInt(entry.Rent);
            }
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Property-rent page exceeds its encoded-byte cap.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(4096);
            Write(writer);
            return writer.ToArray();
        }

        public static PropertyRentSnapshot Read(NetworkReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (reader.Remaining > MaxEncodedBytes)
                throw new ProtocolException("Property-rent page exceeds its encoded-byte cap.");

            var snapshot = new PropertyRentSnapshot
            {
                SweepId = unchecked((uint)reader.ReadInt()),
                PageIndex = reader.ReadShort(),
                EndOfSweep = ReadStrictBool(reader),
            };
            if (snapshot.SweepId == 0)
                throw new ProtocolException("Property-rent sweep id must be non-zero.");
            if (snapshot.PageIndex < 0 || snapshot.PageIndex >= MaxPagesPerSweep)
                throw new ProtocolException("Property-rent page index is outside its cap.");

            int count = WireGuard.ReadCount(reader, 20, MaxEntries);
            var identities = new HashSet<PropertyRentIdentity>();
            for (int i = 0; i < count; i++)
            {
                var entry = new PropertyRentEntry
                {
                    PrefabName = WireGuard.ReadName(reader),
                    AnchorX = WireGuard.ReadCoordinate(reader),
                    AnchorY = WireGuard.ReadCoordinate(reader),
                    AnchorZ = WireGuard.ReadCoordinate(reader),
                    Rent = reader.ReadInt(),
                };
                Validate(entry);
                if (!identities.Add(entry.Identity))
                    throw new ProtocolException("Duplicate property identity in rent page.");
                snapshot.Entries.Add(entry);
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in property-rent page.");
            return snapshot;
        }

        public static PropertyRentSnapshot Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null property-rent page.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Property-rent page exceeds its encoded-byte cap.");
            return Read(new NetworkReader(body));
        }

        /// <summary>
        /// Shared validation for both the wire codec and host capture. Capture calls this before an
        /// entry reaches the page so one broken local prefab/transform is skipped rather than making
        /// <see cref="Write"/> throw and abort every other city-state channel in that snapshot.
        /// </summary>
        public static bool IsValidEntry(PropertyRentEntry entry)
        {
            if (string.IsNullOrEmpty(entry.PrefabName) ||
                entry.PrefabName.Length > WireGuard.MaxNameLength) return false;
            for (int i = 0; i < entry.PrefabName.Length; i++)
                if (char.IsControl(entry.PrefabName[i])) return false;
            return IsValidCoordinate(entry.AnchorX) && IsValidCoordinate(entry.AnchorY) &&
                   IsValidCoordinate(entry.AnchorZ) && entry.Rent >= 0 && entry.Rent <= MaxRent;
        }

        private static bool ReadStrictBool(NetworkReader reader)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new ProtocolException("Invalid property-rent page flag.");
            return value != 0;
        }

        private static void Validate(PropertyRentEntry entry)
        {
            if (!IsValidEntry(entry))
                throw new ProtocolException("Invalid property-rent entry.");
        }

        private static bool IsValidCoordinate(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value) &&
            value >= -WireGuard.MaxCoordinate && value <= WireGuard.MaxCoordinate;
    }

    /// <summary>Portable property identity plus the one property-wide rent vanilla computes.</summary>
    public struct PropertyRentEntry
    {
        public string PrefabName;
        public float AnchorX;
        public float AnchorY;
        public float AnchorZ;
        public int Rent;

        public PropertyRentIdentity Identity =>
            new PropertyRentIdentity(PrefabName, AnchorX, AnchorY, AnchorZ);
    }

    /// <summary>
    /// Prefab entity ids and building entity ids are machine-local. The prefab's stable name and
    /// its world anchor are the same portable identity used by growable-building realization.
    /// </summary>
    public struct PropertyRentIdentity : IEquatable<PropertyRentIdentity>
    {
        public readonly string PrefabName;
        public readonly float AnchorX;
        public readonly float AnchorY;
        public readonly float AnchorZ;

        public PropertyRentIdentity(string prefabName, float anchorX, float anchorY, float anchorZ)
        {
            PrefabName = prefabName;
            AnchorX = anchorX;
            AnchorY = anchorY;
            AnchorZ = anchorZ;
        }

        public bool Equals(PropertyRentIdentity other) =>
            string.Equals(PrefabName, other.PrefabName, StringComparison.Ordinal) &&
            AnchorX.Equals(other.AnchorX) && AnchorY.Equals(other.AnchorY) &&
            AnchorZ.Equals(other.AnchorZ);

        public override bool Equals(object obj) =>
            obj is PropertyRentIdentity && Equals((PropertyRentIdentity)obj);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = PrefabName != null ? PrefabName.GetHashCode() : 0;
                hash = hash * 397 ^ AnchorX.GetHashCode();
                hash = hash * 397 ^ AnchorY.GetHashCode();
                return hash * 397 ^ AnchorZ.GetHashCode();
            }
        }
    }
}
