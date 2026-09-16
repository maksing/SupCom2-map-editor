using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Unlike Terrain's Materials/
    /// Pairs (which needed real Id fields added before they had stable
    /// identity), Objects already has it: every element type
    /// (SC2ObjProp/Marker/Graph/Army/UnitNode/Zone/Chain/SymmetryGroup, all
    /// in Scripts/Core/SC2ObjectsModel.cs) carries a stable `int Id` assigned
    /// via SC2TerrainData.AllocateObjectId(), independent of list position —
    /// this document reuses those types directly rather than defining parallel
    /// Document-specific ones.
    ///
    /// Known gaps NOT fixed by introducing this document — still using
    /// position/name-based linking in two places:
    /// - SC2ObjUnitNode.ParentIndex is still a list index, not a ParentId;
    /// - SC2ObjChain.MarkerNames links by marker Name (for Lua round-trip),
    ///   not by stable marker Id.
    /// Both are live-editing-code changes, out of scope for a read-only
    /// snapshot bridge — same reasoning as the deferred Delete Pair fix
    /// elsewhere in this codebase.
    /// </summary>
    public sealed class SC2ObjectsDocument
    {
        public List<SC2ObjProp> Props = new();
        public List<SC2ObjMarker> Markers = new();
        public List<SC2ObjGraph> Graphs = new();
        public List<SC2ObjArmy> Armies = new();
        public List<SC2ObjUnitNode> Units = new();
        public List<SC2ObjZone> Zones = new();
        public List<SC2ObjChain> Chains = new();
        public List<SC2ObjSymmetryGroup> SymmetryGroups = new();
        /// <summary>Reference-only units/buildings (SC2ObjReferenceUnit) —
        /// round-tripped through the map project like every other list here,
        /// but never read by SC2ObjectsExport (see that class's own
        /// doc comment on this list).</summary>
        public List<SC2ObjReferenceUnit> ReferenceUnits = new();
        public int NextObjectId;

        /// <summary>The real original _save.lua/
        /// _scenario.lua text (SC2TerrainData.ObjSaveLuaTextOriginal/
        /// ObjScenarioLuaTextOriginal) — SC2ObjectsExport.RegenerateSaveLua/
        /// RegenerateScenarioLua patch known blocks into this exact text at
        /// export time. Without it, reconstructing SC2TerrainData from only
        /// this document (the persistence Stage 2 reverse bridge) would
        /// regenerate both files from a bare skeleton instead of the real
        /// original — real data loss for any imported (non-from-scratch)
        /// map. Not redundant with SC2ScenarioDocument.OriginalScenarioLua/
        /// OriginalSaveLua — those come from a different bridge/source, used
        /// for KopiLua parsing, not export-time text patching.</summary>
        public string SaveLuaTextOriginal = "";
        public string ScenarioLuaTextOriginal = "";

        /// <summary>The exact original mapobjs.win.bdf bytes,
        /// populated at import (SC2MapImportOrchestrator) — real original-
        /// bytes source for Smart Export's Objects passthrough, same "keep
        /// the as-imported form for round-trip" pattern as SC2DecalsDocument.
        /// OriginalBdf/SC2CostsDocument.OriginalBdf. Whole-file, not the
        /// individual Props/Markers/etc. lists, so re-export matches the
        /// real vanilla file byte-for-byte when nothing has actually
        /// changed since import.</summary>
        public SC2BlobReference OriginalBdf;
    }
}
