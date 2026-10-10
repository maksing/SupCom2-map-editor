using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    public enum SC2CostsProfile
    {
        LandOnly,
        PlayableWater,
        ImportedCustom
    }

    /// <summary>No real
    /// island-connectivity validation exists anywhere in the legacy
    /// procedural workflow today, so this is a minimal, honest placeholder:
    /// enough shape for a future validation pass to fill in, not populated
    /// by SC2CostsLegacyBridge.</summary>
    public sealed class SC2IslandValidationResult
    {
        public bool Checked;
        public bool HasUnreachableIslands;
        public int IslandCount;
    }

    /// <summary>The legacy procedural workflow's Costs tab is pure manual brush
    /// painting with no auto-generation algorithm at all (SC2CostsAutoGen
    /// exists in this codebase but isn't wired to SC2TerrainData's Costs
    /// tab), so there is nothing real to configure yet.</summary>
    public sealed class SC2CostsGenerationSettings
    {
    }

    /// <summary>Id is derived
    /// from the legacy block's Name (SC2CostsLegacyBridge) — SC2CostsBlockData
    /// has no separate stable identity field the way SC2Material/SC2MaterialPair
    /// now do, but block names ("Land_Small" etc.) are already the de facto
    /// stable key: nothing in this codebase renames a cost block, and the
    /// same 5 names are the real BDF field names costs.bdf itself uses.</summary>
    public sealed class SC2CostsLayerDocument
    {
        public string Id = "";
        public string FieldName = "";
        public string DisplayName = "";

        /// <summary>Left empty by SC2CostsLegacyBridge: the legacy model
        /// doesn't separate an auto-generated base from manual paint
        /// overrides — a real schema would keep the two separate so a user
        /// could re-run generation without losing manual work, but
        /// SC2CostsBlockData.Navmesh is
        /// already the final, combined result of manual painting with no
        /// separate generation step at all.</summary>
        public SC2BlobReference BaseGeneratedGrid;
        public SC2BlobReference PaintOverrides;

        /// <summary>The one real, populated field: a direct blob of the
        /// legacy Navmesh bytes (1=passable, 5=border, 255=impassable).</summary>
        public SC2BlobReference FinalGrid;

        public SC2IslandValidationResult Islands = new();

        /// <summary>Blocks 1+ only: the cells of this block's automatic outline the user marked as unneeded with the Outline brush
        /// (SC2CostsBlockData.OutlineOpenMask, 1 = marked). Live-editing bookkeeping, never exported. Empty when no cell is marked,
        /// and in a document written before this field existed.</summary>
        public SC2BlobReference OutlineOpenMask;
    }

    /// <summary>
    /// Built today only by
    /// SC2CostsLegacyBridge as a read-only snapshot — nothing
    /// writes live edits through this yet.
    /// </summary>
    public sealed class SC2CostsDocument
    {
        public SC2CostsProfile Profile;
        public List<SC2CostsLayerDocument> Layers = new();
        public SC2CostsGenerationSettings Generation = new();
        public long SourceHfieldRevision;
        public SC2DerivedState State;

        /// <summary>The exact original costs.win.bdf bytes,
        /// populated at import (SC2MapImportOrchestrator) — real original-
        /// bytes source for Smart Export's Costs passthrough, same "keep the
        /// as-imported form for round-trip" pattern as SC2DecalsDocument.
        /// OriginalBdf. Whole-file, not per-layer, so re-export matches the
        /// real vanilla file byte-for-byte rather than re-serializing from
        /// the (lossy — Islandmap/LongInts aren't round-tripped) per-layer
        /// Navmesh grids.</summary>
        public SC2BlobReference OriginalBdf;

        /// <summary>The cells of the shore-hint block the user painted by hand (SC2TerrainData.CostsShoreHintOverrideMask, 1 = pinned),
        /// which the automatic resync of the Costs blocks must leave alone. Live-editing bookkeeping, never exported. Empty when no
        /// cell is pinned, and in a document written before this field existed.</summary>
        public SC2BlobReference ShoreHintOverrideMask;

        public SC2CostsLayerDocument Find(string id)
        {
            foreach (var l in Layers)
                if (l.Id == id)
                    return l;
            return null;
        }
    }
}
