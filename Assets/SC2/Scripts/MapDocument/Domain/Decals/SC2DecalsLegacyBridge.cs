using System;
using System.IO;
using BdfIO.Core;
using BdfIO.Formats.Decals;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Read-only
    /// legacy-view snapshot, same pattern as every other domain bridge.
    /// Like Scenario (and unlike Terrain/Environment/Collision2/
    /// Costs/Hfield/Objects), the "legacy" source here isn't an existing
    /// field on SC2TerrainData — nothing in SC2TerrainData.cs holds decal
    /// data at all; SC2DecalsBdfImporter.Import reads decals.win.bdf
    /// straight into a scene GameObject/Mesh and never stores a DecalsModel
    /// anywhere queryable. So this bridge parses the raw BDF bytes itself,
    /// via the already-real, already-validated BdfIO.Formats.Decals.DecalsBdf
    /// (Parse + Serialize, round-trip-tested against the real vanilla
    /// corpus — see BdfFormatRoundTripTests.Decals_ParsesAndRoundTripsStructurally).
    /// </summary>
    public static class SC2DecalsLegacyBridge
    {
        public static SC2DecalsDocument BuildFromLegacy(byte[] decalsBdfBytes, ISC2BlobStore blobStore)
        {
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));

            var doc = new SC2DecalsDocument
            {
                // A freshly-imported legacy decals.bdf has no authored
                // Entries/Placements of its own yet — just the original
                // baked bytes (OriginalBdf below). IsEditable flips to true
                // the first time the user actually authors something via
                // SC2DecalsShell (see SC2ReplaceDecalsStateCommand), so it
                // reads as "has this document been touched through the
                // editor," not a statement that no editor exists.
                IsEditable = false,
                KeepOriginalBakedLayer = true,
            };

            if (decalsBdfBytes == null || decalsBdfBytes.Length == 0)
            {
                doc.State = SC2DerivedState.Missing;
                return doc;
            }

            doc.OriginalBdf = blobStore.Put(new MemoryStream(decalsBdfBytes));

            try
            {
                var container = BdfContainer.Read(decalsBdfBytes, DecalsBdf.Padding);
                var model = DecalsBdf.Parse(container);

                doc.HasDecals = model.HasDecals;
                doc.VertexCount = model.Vertices.Count;
                doc.IndexCount = model.Indices.Count;
                doc.DiffuseAtlasPath = model.DiffuseAtlasPath;
                doc.NormalAtlasPath = model.NormalAtlasPath;
                doc.SpatialState = model.HasDecals ? SC2DecalsSpatialState.Original : SC2DecalsSpatialState.Missing;
                doc.State = SC2DerivedState.Current;
            }
            catch (Exception)
            {
                // decals.bdf's own container/parse layer is already
                // defensive against malformed input (DecalsBdf.Parse
                // returns an empty model rather than throwing on a
                // too-short buffer) — this catch is a safety net for
                // whatever BdfContainer.Read itself might still throw on
                // genuinely corrupt bytes, matching
                // SC2ScenarioLegacyBridge's "one bad file can't crash an
                // otherwise-working import" posture. The original bytes are
                // already safely in OriginalBdf regardless.
                doc.State = SC2DerivedState.Invalid;
            }

            return doc;
        }
    }
}
