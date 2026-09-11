using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Per-triangle pair
    /// assignment, replacing the legacy short[] TrianglePairIndex whose
    /// values are raw indices into SC2TerrainData.Pairs (the exact identity
    /// scheme a confirmed cross-pair-reset bug traced to: an
    /// index is reused as identity, so deleting/reordering pairs silently
    /// repoints unrelated triangles).
    ///
    /// The per-triangle blob still stores small integers (a string per
    /// triangle would be enormous at million-triangle grids) — but they're
    /// SLOTS into SlotIds, not indices into the live Pairs list. A deleted
    /// pair tombstones its slot (SlotIds[i] = null) rather than the list
    /// being compacted, so every triangle's slot number stays valid forever;
    /// SlotIds[slot] is what actually resolves to a stable pair Id, and that
    /// mapping only ever grows or tombstones, never reshuffles. Exactly the
    /// same "stable id table + compact index only at the final serialization
    /// step" shape SC2ObjectGraphRefs already uses for marker graph edges.
    /// </summary>
    public sealed class SC2TrianglePairMap
    {
        /// <summary>int16 blob, one entry per triangle: -1 = unassigned,
        /// otherwise an index into SlotIds.</summary>
        public SC2BlobReference SlotIndexPerTriangleInt16;

        /// <summary>SlotIds[i] is the stable SC2MaterialPairDocument.Id for
        /// slot i, or null/empty if that slot has been tombstoned (its pair
        /// was deleted). Append-only: a new pair gets a new slot at the end;
        /// slots are never removed or renumbered.</summary>
        public List<string> SlotIds = new();
    }
}
