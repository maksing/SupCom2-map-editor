using System;

namespace BdfIO.Core
{
/// <summary>
/// Zero-padding mode around a zlib block.
///
/// All three modes align to 32 bytes using <c>rem = position % 32</c>:
///  - Align32:      pad = (32 - rem) % 32  — range [0,31], 0 if already aligned.
///  - MinPad32:      pad = 32 - rem         — range [1,32], NEVER 0 (adds a
///                   full 32 bytes even when already aligned).
///  - Align32Min8:   like Align32 but never less than 8 — if plain Align32
///                   padding would land at 0..7, adds another 32 bytes. Used
///                   only by formats that store 8 bytes of TrailingData
///                   inside this padding (currently terrain.bdf's
///                   GeometryHeaderStart). Read() and Write() must use the
///                   same mode: the formula is deterministic and doesn't
///                   depend on whether TrailingData is actually present, so
///                   Read() always lands on the same offset Write() computed.
///
/// Applied both before a zlib block (after the header+offset table) and
/// after it (to end of file), with the same mode.
///
/// collision2 / mapobjs / info / decals -> Align32
/// terrain -> Align32Min8
/// hfield / costs -> MinPad32
/// </summary>
public enum BdfPadding
{
    Align32,
    MinPad32,
    Align32Min8
}

public static class BdfPaddingExtensions
{
    /// <summary>
    /// Computes how many padding bytes to add after <paramref name="position"/>
    /// to reach the start of the next block.
    /// </summary>
    public static int ComputePad(this BdfPadding padding, long position)
    {
        var rem = (int)(position % 32);
        return padding switch
        {
            BdfPadding.Align32 => (32 - rem) % 32,
            BdfPadding.MinPad32 => 32 - rem,
            BdfPadding.Align32Min8 => ((32 - rem) % 32) is var a && a < 8 ? a + 32 : a,
            _ => throw new ArgumentOutOfRangeException(nameof(padding))
        };
    }
}
}
