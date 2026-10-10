// SC2ReferenceUnitIntel.cs
//
// Reads the handful of .bp fields a placed reference unit needs to draw its
// scale-reference overlay (Intel.RadarRadius/SonarRadius/VisionRadius, each
// Weapons[] entry's Label/MaxRadius/MinRadius/FireTargetLayerCaps) — nothing
// else in the blueprint is relevant here. SC2BlueprintLoader.ParseFile
// already turns either a real shipped Lua 5.1 BYTECODE .bp (confirmed via
// the \x1bLuaQ header on real gamedata) or a plain-text "uncompiled" dev
// blueprint into a uniform SC2Blueprint — this file only needs to walk that
// already-parsed table, mirroring the exact GetTable(...).Items / per-item
// .TableValue.Fields idiom SC2GameAssetCatalog.CreateAsset's own
// ExtractMaterialSets already uses for a structurally identical "array of
// sub-tables" read.

using System.Collections.Generic;

public sealed class SC2ReferenceUnitWeapon
{
    public string Label = "";
    public float MaxRadius;
    public float MinRadius; // 0 = no inner ring
    /// <summary>Raw Key/Value pairs from the weapon's own FireTargetLayerCaps
    /// table — Value is empty when the source was a bare `Key = true` flag
    /// rather than `Key = 'SomeString'` (both forms are real, confirmed
    /// against actual game data), so the caller can render either faithfully
    /// without this reader guessing at deeper game-rule semantics.</summary>
    public List<(string Key, string Value)> FireTargetLayerCaps = new();
}

public sealed class SC2ReferenceUnitIntel
{
    public float RadarRadius;  // 0 = field absent
    public float SonarRadius;
    public float VisionRadius;
    public List<SC2ReferenceUnitWeapon> Weapons = new();

    /// <summary>The blueprint's `Footprint = { SizeX, SizeZ }`, the skirt the building is built on; 0 = no footprint.</summary>
    public float FootprintSizeX;
    public float FootprintSizeZ;
    /// <summary>`Physics.FlattenSkirt`: the game levels the ground under the building when construction starts.</summary>
    public bool FlattenSkirt = SC2BuildSkirtMath.DefaultFlattenSkirt;
    /// <summary>`Physics.MaxGroundVariation`: how uneven the ground may be under the skirt.</summary>
    public float MaxGroundVariation = SC2BuildSkirtMath.DefaultMaxGroundVariation;

    public bool HasBuildSkirt => FootprintSizeX > 0f && FootprintSizeZ > 0f;

    /// <summary>`Physics.BuildOnLayerCaps`: the layers the building can be built on. False when the blueprint has no such table.</summary>
    public bool HasBuildOnLayerCaps;
    public bool BuildOnLand;
    public bool BuildOnSeabed;
    public bool BuildOnWater;
}

public static class SC2ReferenceUnitIntelReader
{
    public static SC2ReferenceUnitIntel Read(SC2Blueprint blueprint)
    {
        var result = new SC2ReferenceUnitIntel();
        var table = blueprint?.Table;
        if (table == null) return result;

        var intel = table.GetTable("Intel");
        if (intel != null)
        {
            result.RadarRadius = intel.GetSingle("RadarRadius");
            result.SonarRadius = intel.GetSingle("SonarRadius");
            result.VisionRadius = intel.GetSingle("VisionRadius");
        }

        var footprint = table.GetTable("Footprint");
        if (footprint != null)
        {
            result.FootprintSizeX = footprint.GetSingle("SizeX");
            result.FootprintSizeZ = footprint.GetSingle("SizeZ");
        }
        var physics = table.GetTable("Physics");
        if (physics != null)
        {
            result.FlattenSkirt = physics.GetBool("FlattenSkirt", SC2BuildSkirtMath.DefaultFlattenSkirt);
            result.MaxGroundVariation = physics.GetSingle("MaxGroundVariation", SC2BuildSkirtMath.DefaultMaxGroundVariation);
            var layerCaps = physics.GetTable("BuildOnLayerCaps");
            if (layerCaps != null)
            {
                result.HasBuildOnLayerCaps = true;
                result.BuildOnLand = layerCaps.GetBool("LAYER_Land");
                result.BuildOnSeabed = layerCaps.GetBool("LAYER_Seabed");
                result.BuildOnWater = layerCaps.GetBool("LAYER_Water");
            }
        }

        // Confirmed field name is "Weapons" (plural) against real game data
        // — e.g. gamedata.scd/uncompiled/Units/UEF/UUA0101/UUA0101_unit.bp.
        var weapons = table.GetTable("Weapons");
        if (weapons != null)
        {
            foreach (var item in weapons.Items)
            {
                if (item.Kind != SC2BlueprintValueKind.Table) continue;
                var w = item.TableValue;
                var maxRadius = w.GetSingle("MaxRadius");
                // Non-attack pseudo-weapons (e.g. 'DeathImpact') carry no
                // MaxRadius at all — nothing to draw for those.
                if (maxRadius <= 0f) continue;

                var weapon = new SC2ReferenceUnitWeapon
                {
                    Label = w.GetString("Label") ?? "",
                    MaxRadius = maxRadius,
                    MinRadius = w.GetSingle("MinRadius"),
                };
                var caps = w.GetTable("FireTargetLayerCaps");
                if (caps != null)
                {
                    foreach (var field in caps.Fields)
                    {
                        var value = field.Value.Kind == SC2BlueprintValueKind.Boolean ? "" : field.Value.AsString();
                        weapon.FireTargetLayerCaps.Add((field.Key, value ?? ""));
                    }
                }
                result.Weapons.Add(weapon);
            }
        }

        return result;
    }
}
