using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What the checks say about putting one building at one spot.</summary>
public readonly struct SC2BuildCheck
{
    public readonly SC2SkirtReport Ground;
    public readonly bool OverlapsBuilding;
    public readonly bool BlockedByCosts;

    public SC2BuildCheck(SC2SkirtReport ground, bool overlapsBuilding, bool blockedByCosts)
    {
        Ground = ground; OverlapsBuilding = overlapsBuilding; BlockedByCosts = blockedByCosts;
    }

    public bool CanBuild => Ground.Buildable && !OverlapsBuilding && !BlockedByCosts;
}

/// <summary>
/// Which ground the Costs (and, for a water building, the water) allow a building: the cells its Costs block passes, and for a water
/// building only the ones under the water surface. The result is kept and only rebuilt when something it depends on changed, because
/// telling that walks every cell of the block.
/// </summary>
public sealed class SC2BuildRestriction
{
    /// <summary>One byte per cell (index z * (mapX - 1) + x), 1 = allowed; null when Costs do not limit the building.</summary>
    public byte[] AllowedCells { get; private set; }
    public int Signature { get; private set; }
    public SC2BuildCostsRule Rule { get; private set; }
    public string BlockName { get; private set; } = "";

    private static SC2CostsBlockData FindBlock(SC2TerrainData data, string name)
    {
        foreach (var block in data.CostsBlocks)
            if (string.Equals(block.Name, name, StringComparison.OrdinalIgnoreCase)) return block;
        return null;
    }

    private static int HashBytes(byte[] bytes)
    {
        unchecked
        {
            var hash = 2166136261u;
            for (var i = 0; i < bytes.Length; i++) hash = (hash ^ bytes[i]) * 16777619u;
            return (int)hash;
        }
    }

    /// <summary>Brings the restriction up to date for the building described by <paramref name="intel"/> on a map whose heightmap is
    /// <paramref name="heights"/>. <paramref name="waterSurface"/> is only asked for a water building.</summary>
    public void Refresh(SC2TerrainData data, SC2ReferenceUnitIntel intel, ushort[] heights, int mapX, int mapY, Func<float?> waterSurface)
    {
        var cells = mapX - 1;
        var waterBlock = FindBlock(data, "Land_Medium_Water_Surface");
        var rule = SC2BuildSkirtMath.SelectCostsRule(intel.HasBuildOnLayerCaps, intel.BuildOnLand, intel.BuildOnSeabed, intel.BuildOnWater, waterBlock != null);
        SC2CostsBlockData block = null;
        if (rule == SC2BuildCostsRule.LandMedium) block = FindBlock(data, "Land_Medium");
        else if (rule == SC2BuildCostsRule.LandMediumWaterSurface || rule == SC2BuildCostsRule.WaterSurfaceOnly) block = waterBlock;
        Rule = rule;
        BlockName = block?.Name ?? "";

        var blockUsable = block != null && block.Navmesh != null && block.Navmesh.Length == cells * cells;
        if (mapX != mapY || cells <= 0 || rule == SC2BuildCostsRule.None || (rule != SC2BuildCostsRule.Impossible && !blockUsable))
        {
            AllowedCells = null;
            Signature = 0;
            return;
        }

        float? water = rule == SC2BuildCostsRule.WaterSurfaceOnly ? waterSurface?.Invoke() : null;
        var signature = HashCode.Combine((int)rule, cells, blockUsable ? HashBytes(block.Navmesh) : 0, water.HasValue ? water.Value : float.NaN,
            water.HasValue ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(heights) : 0);
        if (AllowedCells != null && signature == Signature && AllowedCells.Length == cells * cells) return;

        var waterCells = water.HasValue ? SC2BuildSkirtMath.WaterCells(heights, mapX, mapY, water.Value) : null;
        var allowed = new byte[cells * cells];
        for (var i = 0; i < allowed.Length; i++)
        {
            var passes = rule != SC2BuildCostsRule.Impossible;
            if (passes && blockUsable) passes = block.Navmesh[i] != 255;
            if (passes && rule == SC2BuildCostsRule.WaterSurfaceOnly) passes = waterCells != null && waterCells[i] != 0;
            allowed[i] = passes ? (byte)1 : (byte)0;
        }
        AllowedCells = allowed;
        Signature = signature;
    }

    /// <summary>Why the Costs refuse the building, for a message.</summary>
    public string DescribeBlock()
    {
        switch (Rule)
        {
            case SC2BuildCostsRule.Impossible: return "this map has no water to build it on";
            case SC2BuildCostsRule.WaterSurfaceOnly: return $"{BlockName} blocks it, or the ground is not under water";
            default: return $"{BlockName} blocks it";
        }
    }
}

/// <summary>Whether a building can be built at a spot: the ground check of the game, no overlap with other buildings' footprints, and the
/// Costs.</summary>
public static class SC2BuildabilityChecker
{
    /// <summary>The footprints of everything on the map that has one and that a new building cannot be put on top of: props, units
    /// (not dead units' wreckage) and reference buildings other than <paramref name="exceptReferenceUnitId"/>.</summary>
    public static SC2PlacedFootprint[] GatherObstacles(SC2TerrainData data, SC2UnitPropBrowser browser, int exceptReferenceUnitId, out int signature)
    {
        var found = new List<SC2PlacedFootprint>();
        void Add(SC2GameAsset asset, Vector3 position, float yawDegrees)
        {
            if (asset == null || asset.FootprintSizeX <= 0f || asset.FootprintSizeZ <= 0f) return;
            found.Add(new SC2PlacedFootprint(position.x, position.z, asset.FootprintSizeX, asset.FootprintSizeZ, yawDegrees));
        }

        foreach (var prop in data.ObjProps)
            Add(browser.FindPropAsset(prop.BlueprintPath), prop.Position, prop.Rotation.eulerAngles.y);
        for (var i = 0; i < data.ObjUnits.Count; i++)
        {
            var unit = data.ObjUnits[i];
            if (unit.IsGroup || IsWreckage(data, i)) continue;
            Add(browser.FindUnitAsset(unit.Type), unit.Position, unit.OrientationEuler.y);
        }
        foreach (var other in data.ObjReferenceUnits)
            if (other.Id != exceptReferenceUnitId) Add(browser.FindUnitAsset(other.BlueprintId), other.Position, other.OrientationEuler.y);

        var hash = new HashCode();
        foreach (var f in found) { hash.Add(f.X); hash.Add(f.Z); hash.Add(f.SizeX); hash.Add(f.SizeZ); hash.Add(f.YawDegrees); }
        signature = hash.ToHashCode();
        return found.ToArray();
    }

    /// <summary>Whether the unit sits under a WRECKAGE group: dead units' reclaim is not a standing building.</summary>
    private static bool IsWreckage(SC2TerrainData data, int unitIndex)
    {
        var parent = data.ObjUnits[unitIndex].ParentIndex;
        for (var depth = 0; parent >= 0 && parent < data.ObjUnits.Count && depth < 16; depth++)
        {
            if (data.ObjUnits[parent].IsWreckage) return true;
            parent = data.ObjUnits[parent].ParentIndex;
        }
        return false;
    }

    /// <summary>All three checks for the building described by <paramref name="intel"/> centred on (x, z).</summary>
    public static SC2BuildCheck Check(SC2ReferenceUnitIntel intel, ushort[] heights, int mapX, int mapY, SC2PlacedFootprint[] obstacles,
        SC2BuildRestriction restriction, float x, float z)
    {
        var rect = SC2BuildSkirtMath.SkirtAround(x, z, intel.FootprintSizeX, intel.FootprintSizeZ);
        var ground = SC2BuildSkirtMath.Evaluate(heights, mapX, mapY, rect, intel.FlattenSkirt, intel.MaxGroundVariation);
        var overlaps = SC2BuildSkirtMath.FindOverlap(x, z, intel.FootprintSizeX, intel.FootprintSizeZ, obstacles) >= 0;
        var blocked = !SC2BuildSkirtMath.FootprintCellsAllowed(restriction?.AllowedCells, mapX - 1, mapY - 1, x, z, intel.FootprintSizeX, intel.FootprintSizeZ);
        return new SC2BuildCheck(ground, overlaps, blocked);
    }

    /// <summary>The reasons a spot is refused, in words (no leading verdict): the numbers of the ground check, an overlapping building, the
    /// Costs. Empty when the building can be built.</summary>
    public static string DescribeRefusal(in SC2BuildCheck check, SC2BuildRestriction restriction)
    {
        string N(float value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        var ground = check.Ground;
        if (!ground.InsideGrid) return "the skirt reaches outside the map";
        var parts = new List<string>();
        if (!ground.Buildable)
            parts.Add(ground.Levelled
                ? $"ground not level enough: edge {N(ground.MinHeight)}..{N(ground.MaxHeight)}, level {N(ground.Pivot)}, off by {N(ground.Deviation)} (limit {N(ground.Tolerance)})"
                : $"ground not level enough: skirt {N(ground.MinHeight)}..{N(ground.MaxHeight)}, spread {N(ground.Deviation)} (limit {N(ground.Tolerance)})");
        if (check.OverlapsBuilding) parts.Add("overlaps another building");
        if (check.BlockedByCosts) parts.Add(restriction != null ? restriction.DescribeBlock() : "Costs block it");
        return string.Join("; ", parts);
    }
}
