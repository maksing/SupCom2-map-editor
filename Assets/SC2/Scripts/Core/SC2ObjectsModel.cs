using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural-workflow Objects data model — held
/// directly on SC2TerrainData so it's build-compatible the same way Costs/
/// Collision2/Environment already are (Scripts/Unified/ shells operating on
/// SC2TerrainData, persisted via SC2RuntimeTerrainPersistence in a standalone
/// build). This is a deliberate departure from the plan's original
/// assumption that Objects lives on the Editor-only SC2MapRawData scene
/// hierarchy (ScdImportWindow/SC2Prop/SC2Marker/SC2Graph GameObjects) — that
/// hierarchy has zero presence in a Build & Run player, so it cannot be made
/// to satisfy "must work in the build version" without this kind of change.
/// Plain POCO lists (not MonoBehaviours) keep every list here trivially
/// JSON-serializable for the runtime persistence bridge.
/// </summary>

[Serializable]
public class SC2ObjProp
{
    /// <summary>Stable identity for selection/undo — assigned once via
    /// SC2TerrainData.AllocateObjectId(), independent of list position (list
    /// indices shift on insert/delete/import, so index-based selection would
    /// silently point at the wrong row after any list mutation).</summary>
    public int Id;
    public string BlueprintPath = "";
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.identity;
    public float Scale = 1f;
    public uint Flags;
    /// <summary>Render priority 0..1 (mapobjs.bdf's "Unknown9"): vanilla maps give trees a spread of values
    /// and everything else 1. 1 = always drawn first-class, lower = the first to drop out.</summary>
    public float RenderPriority = 1f;
    /// <summary>True for every prop written since RenderPriority existed. A saved prop without it (older saves,
    /// where JSON may not run field initializers) reads back false and is set to full priority on load.</summary>
    public bool RenderPriorityStored = true;
    /// <summary>Bit 0 of <see cref="Flags"/> is set when the prop does NOT cast a shadow onto the terrain
    /// (Flags 0 = shadow, 1 = no shadow; 92% of vanilla props are 0). The other bits are left as they are.</summary>
    public const uint NoShadowFlag = 1u;
    /// <summary>The prop's own geometry is rasterized into the heightmap that hfield.bdf (and so the pathing, costs and collision
    /// built from it) is made of, so a building a unit must climb or walk around is part of the ground it stands on.
    /// Off by default: a prop is only a decoration.</summary>
    public bool IncludeInHeightmap;

    /// <summary>A copy of this prop standing at <paramref name="position"/>.</summary>
    public SC2ObjProp WithPosition(Vector3 position)
    {
        var copy = (SC2ObjProp)MemberwiseClone();
        copy.Position = position;
        return copy;
    }

    public bool CastsShadow
    {
        get => (Flags & NoShadowFlag) == 0;
        set => Flags = value ? (Flags & ~NoShadowFlag) : (Flags | NoShadowFlag);
    }
    /// <summary>Height-anchoring model: when
    /// true, Position.y is treated as HeightOffset above the terrain height
    /// sampled at (Position.x, Position.z) and recomputed by "Reproject" —
    /// when false, Position.y is used as-is (absolute).</summary>
    public bool AnchorToTerrain = true;
    public float HeightOffset;
    /// <summary>With <see cref="AnchorToTerrain"/>: Y follows the nearest of the surfaces under the object, imported model or runtime terrain (see SC2HeightMode.Model)
    /// instead of the terrain heightmap. Without it, this flag is ignored.</summary>
    public bool AnchorToModel;
    /// <summary>With <see cref="AnchorToTerrain"/>: over water the object rests on the water surface instead of the seabed
    /// ("above water" / "under water" placement). Ignored without the anchor and on a map without water.</summary>
    public bool OnWaterSurface;
}

[Serializable]
public class SC2ObjMarker
{
    public int Id;
    public string Name = "";
    public string Type = "";
    public Vector3 Position;
    /// <summary>Most marker types store a QUATERNION orientation; a few
    /// (Camera Info) store a plain VECTOR3. Kept as Euler-equivalent Vector3
    /// here for both cases; IsQuaternion
    /// records which representation the source file actually used, so the
    /// writer round-trips the correct wrapper.</summary>
    public Vector3 OrientationEuler;
    public bool OrientationIsQuaternion = true;
    public string ColorHex = "ffffffff";
    public string Prop = "";
    /// <summary>Extra per-type lua fields (amount/size/resource, zoom/fov,
    /// EventName, Cloud* etc.) not covered by the fixed fields above.</summary>
    public List<SC2LuaField> ExtraFields = new();
    public bool AnchorToTerrain = true;
    public float HeightOffset;
    /// <summary>With <see cref="AnchorToTerrain"/>: Y follows the nearest of the surfaces under the object, imported model or runtime terrain (see SC2HeightMode.Model)
    /// instead of the terrain heightmap. Without it, this flag is ignored.</summary>
    public bool AnchorToModel;
    /// <summary>With <see cref="AnchorToTerrain"/>: over water the object rests on the water surface instead of the seabed
    /// ("above water" / "under water" placement). Ignored without the anchor and on a map without water.</summary>
    public bool OnWaterSurface;
}

[Serializable]
public class SC2ObjGraph
{
    public int Id;
    public string Name = "";
    /// <summary>
    /// Legacy pairs of indices into SC2TerrainData.ObjMarkers. They remain in
    /// saved data solely so older runtime terrain JSON can be migrated without
    /// losing graph edges. New code must use MarkerIdsA/MarkerIdsB.
    /// </summary>
    public List<int> EntriesA = new();
    public List<int> EntriesB = new();
    /// <summary>
    /// Stable marker identities for each edge endpoint. List indexes are not
    /// stable when a marker is added, deleted, or sorted, which used to make a
    /// graph silently connect the wrong nodes after an Objects edit.
    /// </summary>
    public List<int> MarkerIdsA = new();
    public List<int> MarkerIdsB = new();
    public bool UsesMarkerIds;
    /// <summary>Land/Air/Naval/Amphibious — cosmetic only (viewport edge
    /// color, quick filter); the real graph "type" is whatever the
    /// underlying path-node markers' Type already encodes.</summary>
    public string GraphKind = "Land";
}

[Serializable]
public class SC2ObjAlliance
{
    public string OtherArmy = "";
    public string Stance = "ENEMY"; // ENEMY / ALLY / NEUTRAL — only ENEMY confirmed in real data
}

[Serializable]
public class SC2ObjArmy
{
    public int Id;
    public string Name = "";
    public string Personality = "";
    public string Plans = "";
    public float Color = 1f;
    public int Faction;
    public float Mass;
    public float Energy;
    public List<SC2ObjAlliance> Alliances = new();
    /// <summary>Placing an Army marker is one linked action creating both
    /// this metadata entry and its ARMY_*
    /// start-position marker together, with a real link — SC2ObjMarker.Id,
    /// not the previous name-matching-only convention (still the fallback
    /// for armies created the old way / imported from a map that predates
    /// this field: 0 means "not yet linked", and every export/UI lookup
    /// that cares about the association still also matches by name).</summary>
    public int MarkerId;
}

/// <summary>One node of an army's pre-placed-Units tree — stored flat with
/// a parent index instead of a real nested tree,
/// which keeps it trivially serializable and easy to edit as a list while
/// still being reconstructible as a hierarchy for display (see
/// SC2ObjectsShell.BuildUnitChildren).</summary>
[Serializable]
public class SC2ObjUnitNode
{
    public int Id;
    public string ArmyName = "";
    public string Name = "";
    public int ParentIndex = -1; // index into SC2TerrainData.ObjUnits, -1 = root of that army's tree
    public bool IsGroup;
    public bool IsWreckage; // groups only — marks a special "WRECKAGE" group holding dead units' reclaim
    public string Type = ""; // blueprint code (e.g. "uca0104"), leaves only
    public Vector3 Position;
    public Vector3 OrientationEuler;
    public bool AnchorToTerrain = true;
    public float HeightOffset;
    /// <summary>With <see cref="AnchorToTerrain"/>: Y follows the nearest of the surfaces under the object, imported model or runtime terrain (see SC2HeightMode.Model)
    /// instead of the terrain heightmap. Without it, this flag is ignored.</summary>
    public bool AnchorToModel;
    /// <summary>With <see cref="AnchorToTerrain"/>: over water the object rests on the water surface instead of the seabed
    /// ("above water" / "under water" placement). Ignored without the anchor and on a map without water.</summary>
    public bool OnWaterSurface;
}

/// <summary>A real game unit/building placed PURELY as an in-editor visual
/// scale reference (real mesh + Radar/Sonar/Vision/weapon-range circles read
/// from its own .bp blueprint) — never written to mapobjs.bdf/_save.lua/
/// _scenario.lua. Deliberately its own list (SC2TerrainData.ObjReferenceUnits)
/// rather than a flag on SC2ObjUnitNode, so "never exported" is a structural
/// property of SC2ObjectsExport.cs simply never reading this list, not a
/// flag that every export code path has to remember to check.</summary>
[Serializable]
public class SC2ObjReferenceUnit
{
    public int Id;
    /// <summary>SC2GameAsset.Id (e.g. "uua0101") — same lowercase-blueprint-
    /// code convention SC2ObjUnitNode.Type already uses, resolved back to a
    /// full SC2GameAsset via SC2UnitPropBrowser.FindUnitAsset.</summary>
    public string BlueprintId = "";
    public string DisplayName = "";
    public Vector3 Position;
    public Vector3 OrientationEuler;
    public bool AnchorToTerrain = true;
    public float HeightOffset;
    /// <summary>Per-instance declutter toggles — a mapper placing more than
    /// one reference unit for different purposes (one for radar coverage,
    /// another for weapon range) needs to be able to quiet the circles they
    /// don't currently care about on each one independently.</summary>
    public bool ShowRadar = true;
    public bool ShowSonar = true;
    public bool ShowVision = true;
    public bool ShowWeapons = true;
    /// <summary>Colours the whole map by whether this building could be built there (see SC2BuildSkirtMath). Only one reference
    /// unit shows it at a time.</summary>
    public bool ShowBuildable;
}

[Serializable]
public class SC2ObjZone
{
    public int Id;
    public string Name = "";
    public float X1, Z1, X2, Z2;
    /// <summary>User-editable label, defaults to a name-heuristic guess —
    /// "Playable"/"Trigger"/"Unknown".</summary>
    public string Role = "Unknown";

    public static string GuessRole(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";
        var n = name.ToUpperInvariant();
        if (n.Contains("PLAYABLE_AREA") || System.Text.RegularExpressions.Regex.IsMatch(n, @"^AREA_?\d+$"))
            return "Playable";
        return "Trigger";
    }
}

[Serializable]
public class SC2ObjChain
{
    public int Id;
    public string Name = "";
    public List<string> MarkerNames = new();
}

/// <summary>One member of a live symmetry-linked group (practical-
/// recommendations #3) — SlotIndex is this member's position in whatever
/// SC2TerrainData.GetSymmetricPositions(...) returns for the current
/// Symmetry mode (0 = the "canonical" slot the others all mirror from).
/// Kept separate from SC2ObjMarker/SC2ObjProp themselves so a group can be
/// broken/rebuilt without touching the objects' own fields.</summary>
[Serializable]
public class SC2ObjSymmetryMember
{
    public int ObjectId;
    public int SlotIndex;
}

/// <summary>Kind is "Marker" or "Prop" — a group only ever links objects of
/// the same kind (see SC2ObjectsShell.LinkWithSymmetry).</summary>
[Serializable]
public class SC2ObjSymmetryGroup
{
    public int Id;
    public string Kind = "Marker";
    public List<SC2ObjSymmetryMember> Members = new();
}
