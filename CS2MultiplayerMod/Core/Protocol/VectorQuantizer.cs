using System;
using System.Runtime.InteropServices;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Quantization routines for compressing 32-bit floating point coordinates and angles
    /// into 16-bit half precision values, halving cursor and position broadcast bandwidth.
    /// </summary>
    public static class VectorQuantizer
    {
        private const double TwoPi = 2.0 * Math.PI;
        private const float TwoPiF = (float)(2.0 * Math.PI);

        public static ushort FloatToHalf(float val)
        {
            return HalfHelper.SingleToHalf(val);
        }

        public static float HalfToFloat(ushort val)
        {
            return HalfHelper.HalfToSingle(val);
        }

        public static ushort QuantizeYaw(float radians)
        {
            // Normalize radians (-PI to PI) into 0 to 65535
            float normalized = (float)((radians % TwoPi + TwoPi) % TwoPi);
            return (ushort)(normalized / TwoPiF * 65535f);
        }

        public static float DequantizeYaw(ushort val)
        {
            return (float)(val / 65535f * TwoPiF);
        }

        // IEEE 754 half-precision float conversion helper with zero-allocation struct union
        private static class HalfHelper
        {
            [StructLayout(LayoutKind.Explicit)]
            private struct FloatIntUnion
            {
                [FieldOffset(0)] public float FloatVal;
                [FieldOffset(0)] public uint UIntVal;
            }

            public static ushort SingleToHalf(float val)
            {
                FloatIntUnion u = default;
                u.FloatVal = val;
                uint valBits = u.UIntVal;

                uint sign = (valBits >> 16) & 0x00008000;
                int exp = (int)((valBits >> 23) & 0x000000FF) - (127 - 15);
                uint mant = valBits & 0x007FFFFF;

                if (exp <= 0)
                {
                    if (exp < -10) return (ushort)sign;
                    mant = (mant | 0x00800000) >> (1 - exp);
                    return (ushort)(sign | ((mant + 0x00000FFF + ((mant >> 13) & 1)) >> 13));
                }
                else if (exp == 0xFF - (127 - 15))
                {
                    if (mant == 0) return (ushort)(sign | 0x7C00);
                    mant >>= 13;
                    return (ushort)(sign | 0x7C00 | mant | (mant == 0 ? 1u : 0u));
                }

                mant = mant + 0x00000FFF + ((mant >> 13) & 1);
                if ((mant & 0x00800000) != 0)
                {
                    mant = 0;
                    exp += 1;
                }
                if (exp > 30) return (ushort)(sign | 0x7C00);

                return (ushort)(sign | ((uint)exp << 10) | (mant >> 13));
            }

            public static float HalfToSingle(ushort val)
            {
                uint mant = (uint)(val & 0x03FF);
                uint exp = (uint)(val & 0x7C00);
                uint sign = (uint)(val & 0x8000) << 16;

                if (exp == 0x7C00)
                {
                    exp = 0xFF;
                }
                else if (exp != 0)
                {
                    exp = (exp >> 10) + (127 - 15);
                    mant <<= 13;
                }
                else if (mant != 0)
                {
                    exp = 127 - 15 + 1;
                    while ((mant & 0x0400) == 0)
                    {
                        mant <<= 1;
                        exp--;
                    }
                    mant = (mant & 0x03FF) << 13;
                }

                uint resultBits = sign | (exp << 23) | mant;
                FloatIntUnion u = default;
                u.UIntVal = resultBits;
                return u.FloatVal;
            }
        }
    }
}
