using System;
using System.Collections.Generic;

/// <summary>
/// DXT DDS encoder — extracted from SC2TerrainExporter (it was a private
/// method there, used only for RNM mask export) so it can also be used by
/// SC2RuntimeTextureExporter to re-encode terrain textures directly from
/// live pixel data in a standalone build, where there is no source asset
/// file to copy (see SC2RuntimeTextureExporter's doc comment for why).
///
/// The RGB color block previously just averaged all 16 pixels into a single
/// flat color and wrote it as both DXT endpoints — every 4x4 block lost all
/// internal gradient/detail, which is why re-exported textures looked
/// visibly lower quality/blocky than the source. This now does real
/// two-endpoint block compression: principal-axis (PCA) fit via power
/// iteration to find the two endpoint colors, then per-pixel nearest-color
/// matching against the resulting 4-color palette — the same "range fit"
/// approach used by common fast DXT encoders (e.g. stb_dxt's default mode).
/// </summary>
public static class SC2DdsEncoder
{
    /// <summary>
    /// Single-level DXT5, no mip chain — used only for RNM lighting masks
    /// (SC2TerrainExporter.ExportRnmMaskByName), which are read directly by
    /// the shader as one basis texture and were never mip-mapped. Material
    /// textures (Diffuse/Normal/Mask) should use BuildMaterialDds instead —
    /// see its doc comment.
    /// </summary>
    public static byte[] BuildDxt5(int w, int h, byte[] rgb, byte[] alpha)
    {
        var bX = w / 4; var bY = h / 4; var pxBytes = bX * bY * 16;
        var r = new byte[128 + pxBytes];
        r[0] = (byte)'D'; r[1] = (byte)'D'; r[2] = (byte)'S'; r[3] = (byte)' ';
        W4(r, 4, 124); W4(r, 8, 0x000A1007); W4(r, 12, (uint)h); W4(r, 16, (uint)w);
        W4(r, 20, (uint)pxBytes); W4(r, 24, 1); W4(r, 28, 1);
        W4(r, 76, 32); W4(r, 80, 4);
        r[84] = (byte)'D'; r[85] = (byte)'X'; r[86] = (byte)'T'; r[87] = (byte)'5';
        W4(r, 108, 0x1000);
        var off = 128;
        var blockR = new int[16]; var blockG = new int[16]; var blockB = new int[16];
        for (var by = 0; by < bY; by++)
        for (var bx = 0; bx < bX; bx++)
        {
            int aMax = 0, aMin = 255;
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            { var a = alpha[(by * 4 + py) * w + (bx * 4 + px)]; if (a > aMax) aMax = a; if (a < aMin) aMin = a; }
            r[off] = (byte)aMax; r[off + 1] = (byte)aMin;
            if (aMax != aMin)
            {
                var pal = new int[8]; pal[0] = aMax; pal[1] = aMin;
                for (var k = 1; k < 7; k++) pal[k + 1] = (aMax * (7 - k) + aMin * k) / 7;
                ulong bits = 0; var pi2 = 0;
                for (var py = 0; py < 4; py++)
                for (var px = 0; px < 4; px++)
                {
                    var av = alpha[(by * 4 + py) * w + (bx * 4 + px)];
                    var best = 0; var bd = int.MaxValue;
                    for (var k = 0; k < 8; k++) { var d = Math.Abs(av - pal[k]); if (d < bd) { bd = d; best = k; } }
                    bits |= (ulong)best << (pi2 * 3); pi2++;
                }
                for (var b = 0; b < 6; b++) r[off + 2 + b] = (byte)((bits >> (b * 8)) & 0xFF);
            }
            off += 8;

            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var pi = (by * 4 + py) * w + (bx * 4 + px);
                var li = py * 4 + px;
                blockR[li] = rgb[pi * 3]; blockG[li] = rgb[pi * 3 + 1]; blockB[li] = rgb[pi * 3 + 2];
            }
            EncodeColorBlock(r, off, blockR, blockG, blockB);
            off += 8;
        }
        return r;
    }

    private static void EncodeColorBlock(byte[] r, int off, int[] br, int[] bg, int[] bb)
    {
        double meanR = 0, meanG = 0, meanB = 0;
        for (var i = 0; i < 16; i++) { meanR += br[i]; meanG += bg[i]; meanB += bb[i]; }
        meanR /= 16; meanG /= 16; meanB /= 16;

        // Covariance matrix (symmetric, 3x3) of the 16 pixel colors.
        double cRR = 0, cRG = 0, cRB = 0, cGG = 0, cGB = 0, cBB = 0;
        for (var i = 0; i < 16; i++)
        {
            var dr = br[i] - meanR; var dg = bg[i] - meanG; var db = bb[i] - meanB;
            cRR += dr * dr; cRG += dr * dg; cRB += dr * db;
            cGG += dg * dg; cGB += dg * db; cBB += db * db;
        }

        // Principal axis via power iteration — a handful of iterations is
        // enough to converge for a 4x4 block's covariance matrix.
        double vr = 1, vg = 1, vb = 1;
        for (var iter = 0; iter < 6; iter++)
        {
            var nr = cRR * vr + cRG * vg + cRB * vb;
            var ng = cRG * vr + cGG * vg + cGB * vb;
            var nb = cRB * vr + cGB * vg + cBB * vb;
            var len = Math.Sqrt(nr * nr + ng * ng + nb * nb);
            if (len < 1e-9) { vr = 1; vg = 1; vb = 1; break; } // flat block — axis doesn't matter
            vr = nr / len; vg = ng / len; vb = nb / len;
        }

        // Project every pixel onto the axis; the extreme projections become
        // the two endpoint colors (clamped back into the mean +/- axis line,
        // then to the valid byte range).
        double tMin = double.MaxValue, tMax = double.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var t = (br[i] - meanR) * vr + (bg[i] - meanG) * vg + (bb[i] - meanB) * vb;
            if (t < tMin) tMin = t;
            if (t > tMax) tMax = t;
        }

        int c0r, c0g, c0b, c1r, c1g, c1b;
        if (tMax - tMin < 1e-6)
        {
            // Flat block (all 16 pixels identical, or a degenerate axis) —
            // both endpoints are just the mean color.
            c0r = c1r = Clamp255((int)Math.Round(meanR));
            c0g = c1g = Clamp255((int)Math.Round(meanG));
            c0b = c1b = Clamp255((int)Math.Round(meanB));
        }
        else
        {
            c0r = Clamp255((int)Math.Round(meanR + tMax * vr));
            c0g = Clamp255((int)Math.Round(meanG + tMax * vg));
            c0b = Clamp255((int)Math.Round(meanB + tMax * vb));
            c1r = Clamp255((int)Math.Round(meanR + tMin * vr));
            c1g = Clamp255((int)Math.Round(meanG + tMin * vg));
            c1b = Clamp255((int)Math.Round(meanB + tMin * vb));
        }

        var c0_565 = Encode565(c0r, c0g, c0b);
        var c1_565 = Encode565(c1r, c1g, c1b);

        // DXT interpolated-color (non punch-through-alpha) mode requires
        // c0 > c1 as raw 16-bit values; if they end up equal or reversed,
        // force ordering (nudging apart if exactly equal) so the decoder
        // always picks the 4-color palette, not the 3-color+black one.
        if (c0_565 <= c1_565)
        {
            if (c0_565 == c1_565)
            {
                if (c0_565 < 0xFFFF) c0_565++;
                else c1_565--;
            }
            else
            {
                (c0_565, c1_565) = (c1_565, c0_565);
            }
        }

        r[off] = (byte)(c0_565 & 0xFF); r[off + 1] = (byte)(c0_565 >> 8);
        r[off + 2] = (byte)(c1_565 & 0xFF); r[off + 3] = (byte)(c1_565 >> 8);

        Decode565(c0_565, out var p0r, out var p0g, out var p0b);
        Decode565(c1_565, out var p1r, out var p1g, out var p1b);
        var p2r = (2 * p0r + p1r) / 3; var p2g = (2 * p0g + p1g) / 3; var p2b = (2 * p0b + p1b) / 3;
        var p3r = (p0r + 2 * p1r) / 3; var p3g = (p0g + 2 * p1g) / 3; var p3b = (p0b + 2 * p1b) / 3;

        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var d0 = Dist2(br[i], bg[i], bb[i], p0r, p0g, p0b);
            var d1 = Dist2(br[i], bg[i], bb[i], p1r, p1g, p1b);
            var d2 = Dist2(br[i], bg[i], bb[i], p2r, p2g, p2b);
            var d3 = Dist2(br[i], bg[i], bb[i], p3r, p3g, p3b);
            var best = 0; var bd = d0;
            if (d1 < bd) { bd = d1; best = 1; }
            if (d2 < bd) { bd = d2; best = 2; }
            if (d3 < bd) { bd = d3; best = 3; }
            indices |= (uint)best << (i * 2);
        }
        r[off + 4] = (byte)(indices & 0xFF);
        r[off + 5] = (byte)((indices >> 8) & 0xFF);
        r[off + 6] = (byte)((indices >> 16) & 0xFF);
        r[off + 7] = (byte)((indices >> 24) & 0xFF);
    }

    private static int Dist2(int r0, int g0, int b0, int r1, int g1, int b1)
    {
        var dr = r0 - r1; var dg = g0 - g1; var db = b0 - b1;
        return dr * dr + dg * dg + db * db;
    }

    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    private static ushort Encode565(int r8, int g8, int b8) =>
        (ushort)(((r8 >> 3) << 11) | ((g8 >> 2) << 5) | (b8 >> 3));

    private static void Decode565(int c, out int r8, out int g8, out int b8)
    {
        var r5 = (c >> 11) & 0x1F; var g6 = (c >> 5) & 0x3F; var b5 = c & 0x1F;
        r8 = (r5 << 3) | (r5 >> 2);
        g8 = (g6 << 2) | (g6 >> 4);
        b8 = (b5 << 3) | (b5 >> 2);
    }

    private static void W4(byte[] b, int o, uint v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }

    private struct MipLevel { public int W, H; public byte[] Rgb; public byte[] Alpha; }

    /// <summary>
    /// Full-quality DDS for material textures (Diffuse/Normal/Mask — NOT the
    /// RNM lighting masks, which keep using BuildDxt5 above as a single
    /// unmipped basis texture read directly by the shader). Generates a full
    /// mip chain (box-filtered down to 1x1, like any normal game texture)
    /// and auto-selects DXT1 (opaque, half the size) vs DXT5 (has an alpha
    /// block) per-texture depending on whether the source actually uses its
    /// alpha channel — masks/normal maps with alpha get DXT5, plain opaque
    /// diffuse textures get DXT1.
    /// </summary>
    public static byte[] BuildMaterialDds(int w, int h, byte[] rgb, byte[] alpha)
    {
        var hasAlpha = false;
        for (var i = 0; i < alpha.Length; i++)
            if (alpha[i] != 255) { hasAlpha = true; break; }

        var mips = GenerateMipChain(w, h, rgb, alpha);
        var levels = new byte[mips.Count][];
        for (var i = 0; i < mips.Count; i++)
            levels[i] = EncodeLevel(mips[i], hasAlpha);

        var totalSize = 0;
        foreach (var lvl in levels) totalSize += lvl.Length;

        const int DDSD_MIPMAPCOUNT_AND_LINEARSIZE_AND_BASE = 0x000A1007; // CAPS|HEIGHT|WIDTH|PIXELFORMAT|LINEARSIZE|MIPMAPCOUNT
        const uint DDSCAPS_COMPLEX = 0x8;
        const uint DDSCAPS_TEXTURE = 0x1000;
        const uint DDSCAPS_MIPMAP  = 0x400000;

        var result = new byte[128 + totalSize];
        result[0] = (byte)'D'; result[1] = (byte)'D'; result[2] = (byte)'S'; result[3] = (byte)' ';
        W4(result, 4, 124);
        W4(result, 8, DDSD_MIPMAPCOUNT_AND_LINEARSIZE_AND_BASE);
        W4(result, 12, (uint)h);
        W4(result, 16, (uint)w);
        W4(result, 20, (uint)levels[0].Length); // dwPitchOrLinearSize: size of the top mip level
        W4(result, 24, 1);
        W4(result, 28, (uint)mips.Count); // dwMipMapCount
        W4(result, 76, 32);
        W4(result, 80, 4); // DDPF_FOURCC
        var fourCc = hasAlpha ? "DXT5" : "DXT1";
        result[84] = (byte)fourCc[0]; result[85] = (byte)fourCc[1]; result[86] = (byte)fourCc[2]; result[87] = (byte)fourCc[3];
        W4(result, 108, DDSCAPS_TEXTURE | DDSCAPS_COMPLEX | DDSCAPS_MIPMAP);

        var off = 128;
        foreach (var lvl in levels)
        {
            Buffer.BlockCopy(lvl, 0, result, off, lvl.Length);
            off += lvl.Length;
        }
        return result;
    }

    /// <summary>
    /// Box-filtered mip chain from a full-res RGB+alpha buffer down to 1x1,
    /// halving (rounding up) each dimension per level, clamping to the edge
    /// pixel when sampling an odd-sized source.
    /// </summary>
    private static List<MipLevel> GenerateMipChain(int w, int h, byte[] rgb, byte[] alpha)
    {
        var list = new List<MipLevel> { new MipLevel { W = w, H = h, Rgb = rgb, Alpha = alpha } };
        var cw = w; var ch = h; var crgb = rgb; var calpha = alpha;
        while (cw > 1 || ch > 1)
        {
            var nw = Math.Max(1, cw / 2);
            var nh = Math.Max(1, ch / 2);
            var nrgb = new byte[nw * nh * 3];
            var nalpha = new byte[nw * nh];
            for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                var sx0 = Math.Min(cw - 1, x * 2); var sx1 = Math.Min(cw - 1, x * 2 + 1);
                var sy0 = Math.Min(ch - 1, y * 2); var sy1 = Math.Min(ch - 1, y * 2 + 1);
                int rSum = 0, gSum = 0, bSum = 0, aSum = 0;
                void Acc(int sx, int sy)
                {
                    var si = sy * cw + sx;
                    rSum += crgb[si * 3]; gSum += crgb[si * 3 + 1]; bSum += crgb[si * 3 + 2];
                    aSum += calpha[si];
                }
                Acc(sx0, sy0); Acc(sx1, sy0); Acc(sx0, sy1); Acc(sx1, sy1);
                var di = y * nw + x;
                nrgb[di * 3] = (byte)(rSum / 4); nrgb[di * 3 + 1] = (byte)(gSum / 4); nrgb[di * 3 + 2] = (byte)(bSum / 4);
                nalpha[di] = (byte)(aSum / 4);
            }
            list.Add(new MipLevel { W = nw, H = nh, Rgb = nrgb, Alpha = nalpha });
            cw = nw; ch = nh; crgb = nrgb; calpha = nalpha;
        }
        return list;
    }

    /// <summary>
    /// Encodes one mip level to DXT1 (8 bytes/block: just a color block) or
    /// DXT5 (16 bytes/block: alpha block + color block). Levels smaller than
    /// 4x4 (down near the bottom of the chain) are still encoded as whole
    /// 4x4 blocks, clamping to the edge pixel for the padding — the standard
    /// way block-compressed formats handle non-multiple-of-4 mip sizes.
    /// </summary>
    private static byte[] EncodeLevel(MipLevel m, bool dxt5)
    {
        var bx = (m.W + 3) / 4; var by = (m.H + 3) / 4;
        var blockBytes = dxt5 ? 16 : 8;
        var data = new byte[bx * by * blockBytes];
        var br = new int[16]; var bg = new int[16]; var bb = new int[16]; var ba = new byte[16];
        var off = 0;
        for (var byi = 0; byi < by; byi++)
        for (var bxi = 0; bxi < bx; bxi++)
        {
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var sx = Math.Min(m.W - 1, bxi * 4 + px);
                var sy = Math.Min(m.H - 1, byi * 4 + py);
                var si = sy * m.W + sx; var li = py * 4 + px;
                br[li] = m.Rgb[si * 3]; bg[li] = m.Rgb[si * 3 + 1]; bb[li] = m.Rgb[si * 3 + 2]; ba[li] = m.Alpha[si];
            }
            if (dxt5) { EncodeAlphaBlock(data, off, ba); off += 8; }
            EncodeColorBlock(data, off, br, bg, bb);
            off += 8;
        }
        return data;
    }

    /// <summary>Single-level DXT1 (BC1, color block only, no alpha block),
    /// no mip chain — the exact convention a real vanilla &lt;MapName&gt;.
    /// minimap.win.dds uses, confirmed by inspecting several real files
    /// (1024x1024, fourCC "DXT1", dwMipMapCount=1, dwCaps=TEXTURE|MIPMAP|
    /// COMPLEX even though there's only the one level — a real encoder's
    /// leftover habit, matched here byte-for-byte rather than "corrected").
    /// Reuses EncodeLevel's existing color-block compression (its own alpha
    /// array is unused when dxt5=false, so a dummy one is fine here).</summary>
    public static byte[] BuildDxt1SingleMip(int w, int h, byte[] rgb)
    {
        var level = new MipLevel { W = w, H = h, Rgb = rgb, Alpha = new byte[w * h] };
        var body = EncodeLevel(level, dxt5: false);

        const uint DDSD_CAPS_HEIGHT_WIDTH_PIXELFORMAT_LINEARSIZE_MIPMAPCOUNT = 0x000A1007;
        const uint DDSCAPS_COMPLEX = 0x8;
        const uint DDSCAPS_TEXTURE = 0x1000;
        const uint DDSCAPS_MIPMAP = 0x400000;

        var result = new byte[128 + body.Length];
        result[0] = (byte)'D'; result[1] = (byte)'D'; result[2] = (byte)'S'; result[3] = (byte)' ';
        W4(result, 4, 124);
        W4(result, 8, DDSD_CAPS_HEIGHT_WIDTH_PIXELFORMAT_LINEARSIZE_MIPMAPCOUNT);
        W4(result, 12, (uint)h);
        W4(result, 16, (uint)w);
        W4(result, 20, (uint)body.Length);
        W4(result, 24, 0); // dwDepth — real vanilla files leave this 0, not 1
        W4(result, 28, 1); // dwMipMapCount
        W4(result, 76, 32); // ddspf.dwSize
        W4(result, 80, 0x5); // DDPF_ALPHAPIXELS|DDPF_FOURCC — matches real files exactly (DXT1's own block encoding ignores this either way)
        result[84] = (byte)'D'; result[85] = (byte)'X'; result[86] = (byte)'T'; result[87] = (byte)'1';
        W4(result, 108, DDSCAPS_TEXTURE | DDSCAPS_COMPLEX | DDSCAPS_MIPMAP);
        Buffer.BlockCopy(body, 0, result, 128, body.Length);
        return result;
    }

    /// <summary>Uncompressed 32-bit BGRA, no mips — the exact convention a
    /// real vanilla &lt;MapName&gt;_PC_mapshot.dds uses (confirmed against
    /// several real files: no FourCC, ddspf.dwFlags=DDPF_RGB|
    /// DDPF_ALPHAPIXELS, dwRGBBitCount=32, R=0x00ff0000 G=0x0000ff00
    /// B=0x000000ff A=0xff000000 — exactly Unity's own byte order for a
    /// texture read back as RGBA32 with R/B swapped, i.e. plain B,G,R,A
    /// bytes per pixel, row-major, no padding). `bgra` must be exactly
    /// w*h*4 bytes.</summary>
    public static byte[] BuildBgra32(int w, int h, byte[] bgra)
    {
        if (bgra == null) throw new ArgumentNullException(nameof(bgra));
        if (bgra.Length != w * h * 4) throw new ArgumentException("bgra must be exactly w*h*4 bytes.", nameof(bgra));

        const uint DDSD_CAPS_HEIGHT_WIDTH_PIXELFORMAT_LINEARSIZE = 0x00081007;
        const uint DDSCAPS_TEXTURE = 0x1000;

        var result = new byte[128 + bgra.Length];
        result[0] = (byte)'D'; result[1] = (byte)'D'; result[2] = (byte)'S'; result[3] = (byte)' ';
        W4(result, 4, 124);
        W4(result, 8, DDSD_CAPS_HEIGHT_WIDTH_PIXELFORMAT_LINEARSIZE);
        W4(result, 12, (uint)h);
        W4(result, 16, (uint)w);
        W4(result, 20, (uint)bgra.Length); // dwPitchOrLinearSize: total pixel-data size (real files use the "linear size" reading here, not a per-row pitch)
        W4(result, 24, 0); // dwDepth
        W4(result, 28, 0); // dwMipMapCount — real files leave this 0 (not even 1) for a single-level uncompressed image
        W4(result, 76, 32); // ddspf.dwSize
        W4(result, 80, 0x41); // DDPF_ALPHAPIXELS|DDPF_RGB
        W4(result, 88, 32); // dwRGBBitCount
        W4(result, 92, 0x00FF0000); // R mask
        W4(result, 96, 0x0000FF00); // G mask
        W4(result, 100, 0x000000FF); // B mask
        W4(result, 104, 0xFF000000); // A mask
        W4(result, 108, DDSCAPS_TEXTURE);
        Buffer.BlockCopy(bgra, 0, result, 128, bgra.Length);
        return result;
    }

    private static void EncodeAlphaBlock(byte[] r, int off, byte[] ba)
    {
        int aMax = 0, aMin = 255;
        for (var i = 0; i < 16; i++) { if (ba[i] > aMax) aMax = ba[i]; if (ba[i] < aMin) aMin = ba[i]; }
        r[off] = (byte)aMax; r[off + 1] = (byte)aMin;
        if (aMax == aMin) return;

        var pal = new int[8]; pal[0] = aMax; pal[1] = aMin;
        for (var k = 1; k < 7; k++) pal[k + 1] = (aMax * (7 - k) + aMin * k) / 7;
        ulong bits = 0;
        for (var i = 0; i < 16; i++)
        {
            var av = ba[i];
            var best = 0; var bd = int.MaxValue;
            for (var k = 0; k < 8; k++) { var d = Math.Abs(av - pal[k]); if (d < bd) { bd = d; best = k; } }
            bits |= (ulong)best << (i * 3);
        }
        for (var b = 0; b < 6; b++) r[off + 2 + b] = (byte)((bits >> (b * 8)) & 0xFF);
    }
}
