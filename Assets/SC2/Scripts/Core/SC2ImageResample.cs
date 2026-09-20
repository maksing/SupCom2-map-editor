using UnityEngine;

/// <summary>Resizing of an imported picture to a fixed target size (the minimap and the map-select preview
/// have exact sizes) without stretching it: the picture is scaled to cover the target and the overflow is
/// cropped evenly on both sides.</summary>
public static class SC2ImageResample
{
    /// <summary>The part of a <paramref name="srcW"/> x <paramref name="srcH"/> picture that covers a
    /// <paramref name="dstW"/> x <paramref name="dstH"/> target with the same aspect ratio, centred:
    /// (x, y, width, height) in source pixels.</summary>
    public static (float x, float y, float w, float h) CoverRect(int srcW, int srcH, int dstW, int dstH)
    {
        var srcAspect = srcW / (float)srcH;
        var dstAspect = dstW / (float)dstH;
        if (srcAspect > dstAspect)
        {
            var w = srcH * dstAspect;
            return ((srcW - w) * 0.5f, 0f, w, srcH);
        }
        var h = srcW / dstAspect;
        return (0f, (srcH - h) * 0.5f, srcW, h);
    }

    /// <summary>Bilinear resample of 4-bytes-per-pixel rows to the target size, covering it (see
    /// <see cref="CoverRect"/>). Row order is preserved (row 0 stays row 0).</summary>
    public static byte[] CoverResizeRgba(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var result = new byte[dstW * dstH * 4];
        var (rx, ry, rw, rh) = CoverRect(srcW, srcH, dstW, dstH);
        for (var y = 0; y < dstH; y++)
        {
            var sy = ry + (y + 0.5f) / dstH * rh - 0.5f;
            var y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, srcH - 1);
            var y1 = Mathf.Min(y0 + 1, srcH - 1);
            var ty = Mathf.Clamp01(sy - y0);
            for (var x = 0; x < dstW; x++)
            {
                var sx = rx + (x + 0.5f) / dstW * rw - 0.5f;
                var x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, srcW - 1);
                var x1 = Mathf.Min(x0 + 1, srcW - 1);
                var tx = Mathf.Clamp01(sx - x0);
                for (var c = 0; c < 4; c++)
                {
                    var top = Mathf.Lerp(src[(y0 * srcW + x0) * 4 + c], src[(y0 * srcW + x1) * 4 + c], tx);
                    var bottom = Mathf.Lerp(src[(y1 * srcW + x0) * 4 + c], src[(y1 * srcW + x1) * 4 + c], tx);
                    result[(y * dstW + x) * 4 + c] = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(top, bottom, ty)), 0, 255);
                }
            }
        }
        return result;
    }
}
