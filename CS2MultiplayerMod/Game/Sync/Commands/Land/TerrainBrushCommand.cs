using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Applied terraform brush samples, replayed through the game's brush pipeline. The TOOL prefab
    /// (<c>Brush.m_Tool</c>) selects the operation, the BRUSH prefab (<c>PrefabRef</c>) the texture.
    /// Each sample carries its full applied <c>Brush</c>: re-running line subdivision would drift.
    /// Samples sharing tool and brush are batched.
    /// </summary>
    public sealed class TerrainBrushCommand : ISimulationCommand
    {
        public const ushort Id = 6;

        /// <summary>Most samples one command may carry (a fast small-brush drag is far below this).</summary>
        public const int MaxSamples = 256;

        /// <summary>Bytes each sample occupies on the wire (14 floats).</summary>
        private const int BytesPerSample = 14 * 4;

        /// <summary>A full <see cref="MaxSamples"/> batch plus two max-length names, rounded up.</summary>
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
            // The source's frame delta for this sample (see ReceiverStrength); unused by material/resource.
            public float DeltaTime;
        }

        // The height pass steps by clamp(delta, MinApplyDelta, MaxApplyDelta); strengths below CurveKnee
        // are bent by a steep power curve first.
        private const float MinApplyDelta = 0.05f;
        private const float MaxApplyDelta = 1f;
        private const float CurveKnee = 0.002f;
        private const float CurveExponent = 0.055f;

        /// <summary>
        /// The strength that moves this machine's ground as far as the source's pass did, given each
        /// side's clamped delta; a raw delta ratio does not. <paramref name="doubledWhenNegative"/> is the
        /// soften tool, which applies a negative strength at twice its magnitude.
        /// </summary>
        public static float ReceiverStrength(float strength, float sourceDelta, float receiverDelta,
            bool doubledWhenNegative)
        {
            float source = ApplyDelta(sourceDelta);
            float receiver = ApplyDelta(receiverDelta);
            if (source == receiver || strength == 0f) return strength;

            bool doubled = doubledWhenNegative && strength < 0f;
            float applied = doubled ? -strength * 2f : strength;
            float matched = InverseCurve(Curve(applied) * source / receiver);
            return doubled ? -matched * 0.5f : matched;
        }

        private static float ApplyDelta(float delta)
        {
            if (float.IsNaN(delta)) return MinApplyDelta;
            return Math.Min(MaxApplyDelta, Math.Max(MinApplyDelta, delta));
        }

        private static float Curve(float strength)
        {
            float magnitude = Math.Abs(strength);
            if (magnitude >= CurveKnee) return strength;
            return Math.Sign(strength) * CurveKnee * (float)Math.Pow(magnitude / CurveKnee, CurveExponent);
        }

        private static float InverseCurve(float applied)
        {
            float magnitude = Math.Abs(applied);
            if (magnitude >= CurveKnee) return applied;
            return Math.Sign(applied) * CurveKnee * (float)Math.Pow(magnitude / CurveKnee, 1.0 / CurveExponent);
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
            if (count <= 0 || count > MaxSamples)
                throw new ProtocolException("Terrain sample count " + count +
                                            " outside [1," + MaxSamples + "].");
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
                writer.WriteFloat(s.DeltaTime);
            }
        }

        public void Read(NetworkReader reader)
        {
            ToolPrefabName = WireGuard.ReadName(reader);
            BrushPrefabName = WireGuard.ReadName(reader);
            int count = WireGuard.ReadCount(reader, BytesPerSample, MaxSamples);
            if (count == 0) throw new ProtocolException("Empty terrain brush command.");
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
                    DeltaTime = WireGuard.ReadFinite(reader),
                };
                // A map-sized brush, absurd strength or non-positive opacity is hostile or a cancelled preview.
                if (s.Size <= 0f || s.Size > 10000f || s.Strength < -1000f || s.Strength > 1000f)
                    throw new ProtocolException("Implausible brush parameters (size " + s.Size +
                                                ", strength " + s.Strength + ").");
                if (s.Opacity <= 0f || s.Opacity > 1f)
                    throw new ProtocolException("Brush opacity " + s.Opacity + " outside (0,1].");
                if (s.DeltaTime <= 0f || s.DeltaTime > 10f)
                    throw new ProtocolException("Brush source delta " + s.DeltaTime + " outside (0,10].");
                samples[i] = s;
            }
            Samples = samples;
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in terrain brush command: " +
                                            reader.Remaining + ".");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(MaxEncodedBytes);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Terrain command body " + writer.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            return writer.ToArray();
        }

        public static TerrainBrushCommand Decode(byte[] body)
        {
            if (body == null) throw new ProtocolException("Null terrain command body.");
            if (body.Length > MaxEncodedBytes)
                throw new ProtocolException("Terrain command body " + body.Length +
                                            " exceeds the " + MaxEncodedBytes + "-byte cap.");
            var command = new TerrainBrushCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
