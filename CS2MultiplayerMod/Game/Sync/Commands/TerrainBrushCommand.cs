using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "A player applied these terraform brush samples." Terraforming is replicated as the stream
    /// of applied brush samples the game itself produces: the terrain tool emits a
    /// <c>CreationDefinition + BrushDefinition</c> line, <c>GenerateBrushesSystem</c> expands it into
    /// one or more <c>Temp + Brush</c> samples, and the ApplyTool pass applies each and tags it
    /// <c>Applied</c>. The receiver replays the samples through that same brush pipeline - see
    /// <see cref="Systems.TerrainSyncSystem"/>.
    ///
    /// Two prefabs travel: the terraforming TOOL prefab (<c>Brush.m_Tool</c>) selects
    /// shift/level/slope/soften and height/material/resource; the BRUSH prefab
    /// (<c>PrefabRef.m_Prefab</c>) supplies the texture/archetype. Each sample carries the complete
    /// applied <c>Brush</c> state, since level/slope need target+start and line subdivision assigns
    /// opacity per sample - re-running subdivision on the receiver would drift. Consecutive samples
    /// sharing a tool+brush are batched into one command; the periodic world resync trues residual
    /// GPU/float drift.
    /// </summary>
    public sealed class TerrainBrushCommand : ISimulationCommand
    {
        public const ushort Id = 6;

        /// <summary>Most samples one command may carry (a fast small-brush drag is far below this).</summary>
        public const int MaxSamples = 256;

        /// <summary>Bytes each sample occupies on the wire (13 floats).</summary>
        private const int BytesPerSample = 13 * 4;

        /// <summary>
        /// Hard cap on an encoded terrain command: a full <see cref="MaxSamples"/> batch plus two
        /// max-length prefab names and the count, rounded up. Oversized bodies never enter the inbox.
        /// </summary>
        public const int MaxEncodedBytes = 16 * 1024;

        /// <summary>One applied brush sample - the complete <c>Brush</c> state the receiver replays.</summary>
        public struct Sample
        {
            public float PosX, PosY, PosZ;
            public float TargetX, TargetY, TargetZ;
            public float StartX, StartY, StartZ;
            public float Size;
            public float Angle;
            public float Strength;
            public float Opacity;
        }

        public string ToolPrefabName;
        public string BrushPrefabName;
        public Sample[] Samples;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(ToolPrefabName);
            writer.WriteString(BrushPrefabName);
            int count = Samples != null ? Samples.Length : 0;
            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++)
            {
                Sample s = Samples[i];
                writer.WriteFloat(s.PosX); writer.WriteFloat(s.PosY); writer.WriteFloat(s.PosZ);
                writer.WriteFloat(s.TargetX); writer.WriteFloat(s.TargetY); writer.WriteFloat(s.TargetZ);
                writer.WriteFloat(s.StartX); writer.WriteFloat(s.StartY); writer.WriteFloat(s.StartZ);
                writer.WriteFloat(s.Size);
                writer.WriteFloat(s.Angle);
                writer.WriteFloat(s.Strength);
                writer.WriteFloat(s.Opacity);
            }
        }

        public void Read(NetworkReader reader)
        {
            ToolPrefabName = WireGuard.ReadName(reader);
            BrushPrefabName = WireGuard.ReadName(reader);
            int count = WireGuard.ReadCount(reader, BytesPerSample, MaxSamples);
            var samples = new Sample[count];
            for (int i = 0; i < count; i++)
            {
                var s = new Sample
                {
                    PosX = WireGuard.ReadCoordinate(reader),
                    PosY = WireGuard.ReadCoordinate(reader),
                    PosZ = WireGuard.ReadCoordinate(reader),
                    TargetX = WireGuard.ReadCoordinate(reader),
                    TargetY = WireGuard.ReadCoordinate(reader),
                    TargetZ = WireGuard.ReadCoordinate(reader),
                    StartX = WireGuard.ReadCoordinate(reader),
                    StartY = WireGuard.ReadCoordinate(reader),
                    StartZ = WireGuard.ReadCoordinate(reader),
                    Size = WireGuard.ReadFinite(reader),
                    Angle = WireGuard.ReadFinite(reader),
                    Strength = WireGuard.ReadFinite(reader),
                    Opacity = WireGuard.ReadFinite(reader),
                };
                // A brush the size of the map, absurd strength, or a zero/negative opacity is an
                // attack or a cancelled preview, not an edit.
                if (s.Size < 0f || s.Size > 10000f || s.Strength < -1000f || s.Strength > 1000f)
                    throw new ProtocolException("Implausible brush parameters (size " + s.Size +
                                                ", strength " + s.Strength + ").");
                if (s.Opacity <= 0f || s.Opacity > 1f)
                    throw new ProtocolException("Brush opacity " + s.Opacity + " outside (0,1].");
                samples[i] = s;
            }
            Samples = samples;
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(MaxEncodedBytes);
            Write(writer);
            return writer.ToArray();
        }

        public static TerrainBrushCommand Decode(byte[] body)
        {
            if (body != null && body.Length > MaxEncodedBytes)
                throw new ProtocolException("Terrain command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new TerrainBrushCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
