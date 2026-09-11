using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>One
    /// named RNM mask, its bytes moved to the shared blob store instead of
    /// living inline on every pair that references it (legacy
    /// SC2MaterialPair.RnmMaskBytes duplicates the same bytes on every pair
    /// sharing a mask name — pairs can share an
    /// RnmMaskName). Id defaults to the legacy RnmMaskName so existing masks
    /// keep a recognizable identity; kept as a separate field from Name so a
    /// future rename doesn't need to renumber every pair's RnmSetId.</summary>
    public sealed class SC2RnmMaskSetDocument
    {
        public string Id = "";
        public string Name = "";
        public int Width;
        public int Height;
        public SC2BlobReference MaskBytes;

        /// <summary>Baked RNM lighting (SC2RnmBakeService) for this mask name:
        /// the three basis-direction RGB slices plus the sun-occlusion alpha.
        /// Like MaskBytes these live in the blob store and are shared by every
        /// pair using this mask name. Empty until a bake has run.
        ///
        /// These have to travel with the document because Load prefers the
        /// document package over the legacy JSON — without them a save/reload
        /// silently reverted the map to the neutral 128 placeholder with alpha
        /// 255, which the game reads as sunOcclusion = 1.0, i.e. entirely
        /// unattenuated specular (real vanilla masks average ~40-46% there).</summary>
        public SC2BlobReference BakedRgb0;
        public SC2BlobReference BakedRgb1;
        public SC2BlobReference BakedRgb2;
        public SC2BlobReference BakedShadowAlpha;
    }

    public sealed class SC2RnmDocument
    {
        public List<SC2RnmMaskSetDocument> MaskSets = new();

        public SC2RnmMaskSetDocument Find(string id)
        {
            foreach (var set in MaskSets)
                if (set.Id == id)
                    return set;
            return null;
        }
    }
}
