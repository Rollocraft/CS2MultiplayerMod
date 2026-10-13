using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace CS2MultiplayerMod.Core.Protocol.Messages
{
    public sealed class StateDigest
    {
        public byte ChannelId;
        public byte[] Hash;
        public static byte[] Compute(byte[] data)
        {
            using (var hash = SHA256.Create()) return hash.ComputeHash(data);
        }
        public static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    public enum StateRepairStage : byte { Snapshot, Applied, Failed, Declined }

    /// <summary>A bounded repair transaction for independently replaceable state channels.</summary>
    public sealed class StateRepairMessage : INetMessage
    {
        public const int MaxChannels = 16;
        public const int MaxChannelBytes = 4096;
        public long RequestId;
        public StateRepairStage Stage;
        public StateSnapshotMessage[] Snapshots = Array.Empty<StateSnapshotMessage>();
        public MessageType Type => MessageType.StateRepair;

        public void Write(NetworkWriter writer)
        {
            Validate();
            writer.WriteLong(RequestId);
            writer.WriteByte((byte)Stage);
            writer.WriteByte((byte)Snapshots.Length);
            foreach (var snapshot in Snapshots)
            {
                writer.WriteByte(snapshot.ChannelId);
                writer.WriteInt(snapshot.Data.Length);
                writer.WriteBytes(snapshot.Data, 0, snapshot.Data.Length);
            }
        }

        public void Read(NetworkReader reader)
        {
            RequestId = reader.ReadLong();
            Stage = (StateRepairStage)reader.ReadByte();
            int count = reader.ReadByte();
            if (count > MaxChannels) throw new ProtocolException("Too many repair channels.");
            Snapshots = new StateSnapshotMessage[count];
            for (int i = 0; i < count; i++)
            {
                byte channel = reader.ReadByte();
                int length = reader.ReadInt();
                if (length <= 0 || length > MaxChannelBytes || length > reader.Remaining)
                    throw new ProtocolException("Invalid repair payload length.");
                Snapshots[i] = new StateSnapshotMessage(channel, reader.ReadBytes(length));
            }
            Validate();
        }

        private void Validate()
        {
            if (RequestId <= 0 || Stage > StateRepairStage.Declined || Snapshots == null ||
                Snapshots.Length > MaxChannels || (Stage != StateRepairStage.Snapshot && Snapshots.Length != 0))
                throw new ProtocolException("Invalid state repair transaction.");
            var seen = new HashSet<byte>();
            foreach (var snapshot in Snapshots)
                if (snapshot == null || !seen.Add(snapshot.ChannelId) || snapshot.Data == null ||
                    snapshot.Data.Length == 0 || snapshot.Data.Length > MaxChannelBytes)
                    throw new ProtocolException("Invalid state repair channel.");
        }
    }
}
