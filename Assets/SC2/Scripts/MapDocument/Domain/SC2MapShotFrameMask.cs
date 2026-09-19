using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The alpha "frame" every vanilla _PC_mapshot.dds carries: a feathered
    /// edge (7 px of quadratic falloff), softly rounded top-left/top-right/
    /// bottom-right corners and a diagonally cut bottom-left corner.
    /// Extracted pixel-for-pixel from the alpha channel of the real
    /// SC2_MP_001_PC_mapshot.dds (448x206); the mask is reproduced exactly at
    /// that size and, at any other size, the same edge profile is used with the
    /// corner patches only where the image is big enough to hold them.
    ///
    /// The colour channels inside the frame are black in the vanilla file too
    /// (see <see cref="ApplyToBgra"/>).
    ///
    /// Everything here is expressed in DDS row order (row 0 = top of the
    /// picture); callers holding Unity's bottom-row-0 buffers pass
    /// <c>bottomRowFirst: true</c> to <see cref="ApplyToBgra"/>.
    /// </summary>
    public static class SC2MapShotFrameMask
    {
        public const int FeatherPixels = 7;
        public const int CornerSize = 14;
        public const int CutCornerSize = 30;

        // Alpha at distance 0..6 px from an edge; 255 from 7 px inward.
        private static readonly byte[] EdgeProfile = { 4, 12, 24, 40, 60, 84, 112 };

        // Top-left corner patch (CornerSize x CornerSize); the top-right and
        // bottom-right corners are its mirror images.
        private static readonly byte[] RoundedCorner =
        {
            0, 0, 0, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 4,
            0, 1, 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 11, 12,
            0, 1, 2, 4, 6, 8, 10, 13, 16, 18, 20, 22, 23, 24,
            1, 2, 4, 6, 9, 13, 17, 22, 27, 31, 34, 36, 38, 39,
            1, 3, 6, 9, 14, 20, 26, 34, 40, 46, 50, 54, 57, 59,
            1, 4, 8, 13, 20, 27, 37, 47, 56, 64, 71, 76, 80, 82,
            2, 5, 10, 17, 26, 37, 49, 63, 75, 85, 94, 101, 106, 110,
            2, 7, 13, 22, 34, 47, 63, 255, 255, 255, 255, 255, 255, 255,
            3, 8, 16, 27, 40, 56, 75, 255, 255, 255, 255, 255, 255, 255,
            3, 9, 18, 31, 46, 64, 85, 255, 255, 255, 255, 255, 255, 255,
            3, 10, 20, 34, 50, 71, 94, 255, 255, 255, 255, 255, 255, 255,
            4, 11, 22, 36, 54, 76, 101, 255, 255, 255, 255, 255, 255, 255,
            4, 11, 23, 38, 57, 80, 106, 255, 255, 255, 255, 255, 255, 255,
            4, 12, 24, 39, 59, 82, 110, 255, 255, 255, 255, 255, 255, 255
        };

        // Bottom-left diagonal cut (CutCornerSize x CutCornerSize), stored
        // top-to-bottom, anchored to the image's bottom-left corner.
        private static readonly byte[] CutCorner =
        {
            4, 12, 24, 40, 59, 83, 111, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            4, 12, 23, 39, 59, 82, 110, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            4, 11, 22, 38, 57, 80, 108, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            3, 10, 21, 36, 54, 77, 104, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            3, 9, 19, 33, 51, 73, 98, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            3, 8, 17, 30, 47, 67, 92, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            2, 7, 15, 26, 41, 60, 83, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            2, 6, 12, 22, 35, 52, 72, 97, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            1, 4, 10, 18, 29, 43, 61, 83, 106, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            1, 3, 7, 13, 22, 34, 50, 68, 89, 112, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            1, 2, 5, 10, 16, 26, 39, 55, 73, 93, 115, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 1, 3, 7, 12, 19, 29, 42, 57, 75, 95, 116, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 1, 2, 4, 7, 13, 20, 30, 43, 58, 76, 96, 117, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 96, 117, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 96, 117, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 96, 117, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 96, 117, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 96, 117, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 96, 116, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 59, 76, 95, 115, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 58, 75, 93, 112, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 31, 43, 57, 73, 89, 106, 255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 21, 30, 42, 55, 68, 83, 97, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 20, 29, 39, 50, 61, 72, 83, 92, 98, 104, 108, 110, 111,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 8, 13, 19, 26, 34, 43, 52, 60, 67, 73, 77, 80, 82, 83,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 7, 12, 16, 22, 29, 35, 41, 47, 51, 54, 57, 59, 59,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 4, 7, 10, 13, 18, 22, 26, 30, 33, 36, 38, 39, 40,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 5, 7, 10, 12, 15, 17, 19, 21, 22, 23, 24,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 12,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 4
        };

        private static int Profile(int distanceToEdge) =>
            distanceToEdge < FeatherPixels ? EdgeProfile[distanceToEdge] : 255;

        /// <summary>Frame alpha for the pixel at (x, yFromTop) of a
        /// width x height mapshot.</summary>
        public static byte AlphaAt(int x, int yFromTop, int width, int height)
        {
            if (x < 0 || yFromTop < 0 || x >= width || yFromTop >= height) return 0;
            if (width >= CutCornerSize * 2 && height >= CutCornerSize * 2)
            {
                var fromRight = width - 1 - x;
                var fromBottom = height - 1 - yFromTop;
                if (x < CutCornerSize && fromBottom < CutCornerSize)
                    return CutCorner[(CutCornerSize - 1 - fromBottom) * CutCornerSize + x];
                var cx = Math.Min(x, fromRight);
                var cy = Math.Min(yFromTop, fromBottom);
                if (cx < CornerSize && cy < CornerSize)
                    return RoundedCorner[cy * CornerSize + cx];
            }
            return (byte)Math.Min(Profile(Math.Min(x, width - 1 - x)), Profile(Math.Min(yFromTop, height - 1 - yFromTop)));
        }

        /// <summary>Stamps the frame onto a 4-byte-per-pixel buffer (BGRA or
        /// RGBA — alpha is byte 3 either way): alpha is overwritten with the
        /// mask, and every pixel the mask leaves even partly transparent is
        /// also blacked out. The vanilla file has exactly that — all 9096 of
        /// its alpha &lt; 255 pixels are RGB (0,0,0), so the frame fades the
        /// picture into black rather than into a see-through copy of the
        /// image. Idempotent.</summary>
        public static void ApplyToBgra(byte[] pixels, int width, int height, bool bottomRowFirst)
        {
            if (pixels == null || pixels.Length != width * height * 4) return;
            for (var row = 0; row < height; row++)
            {
                var yFromTop = bottomRowFirst ? height - 1 - row : row;
                for (var x = 0; x < width; x++)
                {
                    var i = (row * width + x) * 4;
                    var alpha = AlphaAt(x, yFromTop, width, height);
                    pixels[i + 3] = alpha;
                    if (alpha < 255) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                }
            }
        }
    }
}
