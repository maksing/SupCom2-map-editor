using System;

namespace BdfIO.Core
{
/// <summary>
/// Converts IEEE 754 half-precision (16-bit) <-> float without
/// <see cref="System.Half"/> — that type only exists in .NET 5+ and isn't
/// available in netstandard2.1, the target framework needed for
/// compatibility with Unity plugins. Used for UV1 in terrain.bdf
/// (TerrainVertex.Uv1U/Uv1V).
///
/// Standard bitwise conversion (round-to-nearest-even for FromFloat),
/// equivalent to <see cref="System.Half"/>; for a value read from half via
/// <see cref="ToFloat"/>, FromFloat(ToFloat(bits)) == bits (an exact
/// round-trip).
/// </summary>
public static class HalfConvert
{
    public static float ToFloat(ushort bits)
    {
        var sign = (bits >> 15) & 0x1;
        var exp = (bits >> 10) & 0x1F;
        var mant = bits & 0x3FF;

        int fbits;
        if (exp == 0)
        {
            if (mant == 0)
            {
                fbits = sign << 31; // ±0
            }
            else
            {
                // Subnormal half -> normalize.
                var e = -1;
                do
                {
                    e++;
                    mant <<= 1;
                } while ((mant & 0x400) == 0);

                mant &= 0x3FF;
                fbits = (sign << 31) | ((125 - e) << 23) | (mant << 13);
            }
        }
        else if (exp == 0x1F)
        {
            fbits = (sign << 31) | 0x7F800000 | (mant << 13); // Inf/NaN
        }
        else
        {
            fbits = (sign << 31) | ((exp + 112) << 23) | (mant << 13);
        }

        return BitConverter.Int32BitsToSingle(fbits);
    }

    public static ushort FromFloat(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        var sign = (bits >> 16) & 0x8000;
        var val = (bits & 0x7FFFFFFF) + 0x1000; // round-to-nearest

        if (val >= 0x47800000)
        {
            // Overflow -> Inf, or the input was already Inf/NaN.
            if ((bits & 0x7FFFFFFF) >= 0x47800000)
            {
                if (val < 0x7F800000) return (ushort)(sign | 0x7C00); // -> Inf
                return (ushort)(sign | 0x7C00 | (((bits & 0x7FFFFF) != 0) ? 0x0200 : 0)); // NaN
            }
            return (ushort)(sign | 0x7BFF); // Overflow -> max half value
        }

        if (val >= 0x38800000)
            return (ushort)(sign | ((val - 0x38000000) >> 13)); // Normal number

        if (val < 0x33000000)
            return (ushort)sign; // Underflow -> ±0

        // Subnormal
        val = (bits & 0x7FFFFFFF) >> 23;
        return (ushort)(sign | ((((bits & 0x7FFFFF) | 0x800000) + (0x800000 >> (val - 102))) >> (126 - val)));
    }
}
}
