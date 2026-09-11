using System;

namespace SC2.MapEditor.Textures
{
    public readonly struct SC2TextureVirtualRange
    {
        public readonly int Columns;
        public readonly int FirstIndex;
        public readonly int LastIndexExclusive;
        public readonly int TotalRows;
        public readonly float TotalHeight;

        public SC2TextureVirtualRange(int columns, int first, int last, int rows, float height)
        {
            Columns = columns;
            FirstIndex = first;
            LastIndexExclusive = last;
            TotalRows = rows;
            TotalHeight = height;
        }
    }

    public static class SC2TextureVirtualGrid
    {
        public static SC2TextureVirtualRange Calculate(int itemCount, float viewportWidth, float scrollY, float viewportHeight, float cellWidth, float cellHeight, int overscanRows = 1)
        {
            if (itemCount <= 0 || cellWidth <= 0f || cellHeight <= 0f)
                return new SC2TextureVirtualRange(1, 0, 0, 0, 0f);

            var columns = Math.Max(1, (int)Math.Floor(viewportWidth / cellWidth));
            var rows = (itemCount + columns - 1) / columns;
            var firstRow = Math.Max(0, (int)Math.Floor(Math.Max(0f, scrollY) / cellHeight) - Math.Max(0, overscanRows));
            var lastRow = Math.Min(rows, (int)Math.Ceiling((Math.Max(0f, scrollY) + Math.Max(0f, viewportHeight)) / cellHeight) + Math.Max(0, overscanRows));
            return new SC2TextureVirtualRange(columns, Math.Min(itemCount, firstRow * columns), Math.Min(itemCount, lastRow * columns), rows, rows * cellHeight);
        }
    }
}
