using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The map-wide "where can this building be built" colouring: one texel per height-grid cell. By default a cell is green when some
/// valid placement of a building of the given footprint covers it (the placement passes the game's ground check, SC2BuildSkirtMath,
/// does not overlap another building's footprint and stays on ground the Costs allow the building), so a red cell is ground no such building can occupy. Optionally a cell is
/// green when a building centred on the vertex at its lower corner is valid. The checks run on a background task over the heightmap the editor would export, and the
/// texture keeps showing the previous result until the new one is ready, so editing the terrain never stalls on it.
/// </summary>
public sealed class SC2BuildableOverlay : IDisposable
{
    private static readonly Color32 BuildableColor = new Color32(60, 220, 90, 110);
    private static readonly Color32 BlockedColor = new Color32(235, 60, 50, 110);

    /// <summary>What the ground check depends on. The ground check is the slow part, so it is kept apart from the buildings.</summary>
    private readonly struct GroundKey : IEquatable<GroundKey>
    {
        public readonly ushort[] Heights;
        public readonly int MapX, MapY;
        public readonly float SizeX, SizeZ, MaxGroundVariation;
        public readonly bool Flatten;

        public GroundKey(ushort[] heights, int mapX, int mapY, float sizeX, float sizeZ, bool flatten, float maxGroundVariation)
        {
            Heights = heights; MapX = mapX; MapY = mapY; SizeX = sizeX; SizeZ = sizeZ; Flatten = flatten; MaxGroundVariation = maxGroundVariation;
        }

        public bool Equals(GroundKey other) =>
            ReferenceEquals(Heights, other.Heights) && MapX == other.MapX && MapY == other.MapY
            && SizeX == other.SizeX && SizeZ == other.SizeZ && Flatten == other.Flatten && MaxGroundVariation == other.MaxGroundVariation;

        public override bool Equals(object obj) => obj is GroundKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(MapX, MapY, SizeX, SizeZ, Flatten, MaxGroundVariation);
    }

    private readonly struct Key : IEquatable<Key>
    {
        public readonly GroundKey Ground;
        public readonly int ObstaclesSignature;
        public readonly bool Coverage;
        public readonly bool HasAllowed;
        public readonly int AllowedSignature;

        public Key(GroundKey ground, int obstaclesSignature, bool coverage, bool hasAllowed, int allowedSignature)
        {
            Ground = ground; ObstaclesSignature = obstaclesSignature; Coverage = coverage; HasAllowed = hasAllowed; AllowedSignature = allowedSignature;
        }

        public bool Equals(Key other) =>
            Ground.Equals(other.Ground) && ObstaclesSignature == other.ObstaclesSignature && Coverage == other.Coverage
            && HasAllowed == other.HasAllowed && AllowedSignature == other.AllowedSignature;
        public override bool Equals(object obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Ground, ObstaclesSignature, Coverage, HasAllowed, AllowedSignature);
    }

    private sealed class Result
    {
        public byte[] Ground;
        public byte[] Final;
    }

    private Texture2D _texture;
    private Color32[] _pixels;
    private Task<Result> _task;
    private Key _taskKey;
    private Key _shownKey;
    private bool _hasShown;
    // The last finished ground check, reused while only the buildings change (a building being moved).
    private GroundKey _groundKey;
    private byte[] _groundMask;

    /// <summary>Returns the overlay texture (CostsGridSize x CostsGridSize, row = Z) for the given heightmap, building and other
    /// buildings, or null until the first result is ready. A change in any of them starts a new computation; the texture keeps
    /// its old contents until that finishes. <paramref name="obstaclesSignature"/> must change whenever <paramref name="obstacles"/> does.
    /// With <paramref name="coverage"/> a cell is green when a valid placement covers it; without, when a building centred on that
    /// vertex is valid. <paramref name="allowedCells"/> (null for no limit) says which cells the Costs allow the building to stand on,
    /// one per cell like <see cref="SC2BuildSkirtMath.WaterCells"/>; <paramref name="allowedSignature"/> must change whenever it does.</summary>
    public Texture Update(ushort[] heights, int mapX, int mapY, float footprintSizeX, float footprintSizeZ, bool flattenSkirt,
        float maxGroundVariation, SC2PlacedFootprint[] obstacles, int obstaclesSignature, bool coverage, byte[] allowedCells, int allowedSignature)
    {
        if (heights == null || mapX < 2 || mapY < 2) return null;

        if (_task != null && _task.IsCompleted)
        {
            if (_task.Status == TaskStatus.RanToCompletion)
            {
                _groundKey = _taskKey.Ground;
                _groundMask = _task.Result.Ground;
                Upload(_task.Result.Final, _taskKey.Ground);
                _shownKey = _taskKey;
                _hasShown = true;
            }
            else if (_task.IsFaulted)
            {
                Debug.LogException(_task.Exception);
                _shownKey = _taskKey; // not retried until something changes
                _hasShown = true;
            }
            _task = null;
        }

        var ground = new GroundKey(heights, mapX, mapY, footprintSizeX, footprintSizeZ, flattenSkirt, maxGroundVariation);
        var wanted = new Key(ground, obstaclesSignature, coverage, allowedCells != null, allowedSignature);
        if (_task == null && (!_hasShown || !_shownKey.Equals(wanted)))
        {
            _taskKey = wanted;
            var reuse = _groundMask != null && _groundKey.Equals(ground) ? _groundMask : null;
            _task = Task.Run(() =>
            {
                var groundMask = reuse ?? SC2BuildSkirtMath.BuildMask(heights, mapX, mapY, footprintSizeX, footprintSizeZ, flattenSkirt, maxGroundVariation);
                var centers = SC2BuildSkirtMath.BlockOverlapping(groundMask, mapX, mapY, footprintSizeX, footprintSizeZ, obstacles);
                if (allowedCells != null)
                    centers = SC2BuildSkirtMath.RestrictToAllowedCells(centers, mapX, mapY, footprintSizeX, footprintSizeZ, allowedCells);
                var final = coverage ? SC2BuildSkirtMath.CoverageMask(centers, mapX, mapY, footprintSizeX, footprintSizeZ) : centers;
                return new Result { Ground = groundMask, Final = final };
            });
        }
        return _texture;
    }

    private void Upload(byte[] mask, GroundKey key)
    {
        // One texel per cell: the vertex at the cell's lower corner stands for the cell.
        var width = key.MapX - 1;
        var height = key.MapY - 1;
        if (_texture == null || _texture.width != width || _texture.height != height)
        {
            if (_texture != null) UnityEngine.Object.DestroyImmediate(_texture);
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            _pixels = new Color32[width * height];
        }
        for (var z = 0; z < height; z++)
            for (var x = 0; x < width; x++)
                _pixels[z * width + x] = mask[z * key.MapX + x] != 0 ? BuildableColor : BlockedColor;
        _texture.SetPixels32(_pixels);
        _texture.Apply(false);
    }

    public void Dispose()
    {
        if (_texture != null) UnityEngine.Object.DestroyImmediate(_texture);
        _texture = null;
        _pixels = null;
        _task = null;
        _hasShown = false;
        _groundMask = null;
    }
}
