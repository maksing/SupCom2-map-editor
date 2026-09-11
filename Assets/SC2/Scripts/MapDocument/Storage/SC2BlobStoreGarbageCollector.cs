using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// No compaction/GC/
    /// retention policy previously existed (see ISC2BlobStore's own doc comment). The blob
    /// store is a single directory SHARED across every saved map (one
    /// content-addressed pool, not one store per map), so GC can't just
    /// "keep whatever THIS map's manifest references" — every OTHER map's
    /// manifest sharing the same store would lose blobs it still needs.
    /// This does a real mark-and-sweep: mark every contentHash referenced by
    /// ANY of the given manifests (typically every *.sc2mapdoc/manifest.json
    /// under SC2RuntimeTerrainPersistence.SaveDirectory), then delete
    /// whatever the blob store holds that no manifest mentioned.
    ///
    /// The mark phase walks the raw parsed JSON tree rather than
    /// SC2MapDocumentSerializer.Deserialize + a typed field-by-field walk —
    /// every SC2BlobReference is written as the same {"contentHash": ...,
    /// "length": ...} shape (SC2MapDocumentSerializer.WriteBlobRef), so a
    /// generic "any object with both keys is a live reference" walk finds
    /// every one of them without needing to know (or keep up to date with)
    /// which of SC2MapDocument's ~30 blob-backed fields exist today.
    /// </summary>
    public static class SC2BlobStoreGarbageCollector
    {
        /// <summary>Returns the number of blobs actually deleted. A manifest
        /// that fails to parse (corrupt/partial write) is skipped rather
        /// than aborting the whole pass — one bad file must not prevent
        /// every OTHER map's blobs from being correctly marked live; an
        /// exception escaping the loop entirely would have meant a single
        /// corrupt manifest anywhere caused every live blob for every map to
        /// be swept. There is no way to protect the corrupt manifest's OWN
        /// referenced blobs specifically — a file that can't be parsed
        /// can't tell this pass what it references either — so those can
        /// still be collected; that's an accepted, narrow consequence (the
        /// next successful Save rewrites a valid manifest and regenerates
        /// whatever it needs from the live SC2TerrainData, which is the
        /// actual source of truth, not the blob store).</summary>
        public static int CollectUnreferencedBlobs(ISC2BlobStore blobStore, IEnumerable<string> manifestJsons)
        {
            if (blobStore == null) throw new ArgumentNullException(nameof(blobStore));
            if (manifestJsons == null) throw new ArgumentNullException(nameof(manifestJsons));

            var liveHashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var json in manifestJsons)
            {
                SC2JsonValue root;
                try
                {
                    root = SC2JsonValue.Parse(json);
                }
                catch (FormatException)
                {
                    continue;
                }
                CollectHashes(root, liveHashes);
            }

            var deleted = 0;
            foreach (var hash in new List<string>(blobStore.EnumerateHashes()))
            {
                if (liveHashes.Contains(hash)) continue;
                if (blobStore.TryDelete(hash)) deleted++;
            }
            return deleted;
        }

        private static void CollectHashes(SC2JsonValue node, HashSet<string> liveHashes)
        {
            if (node == null) return;

            switch (node.Kind)
            {
                case SC2JsonKind.Object:
                    if (node.TryGet("contentHash", out var hashValue) && node.TryGet("length", out _))
                    {
                        var hash = hashValue.AsString();
                        if (!string.IsNullOrEmpty(hash))
                            liveHashes.Add(hash);
                    }
                    foreach (var member in node.Members())
                        CollectHashes(member.Value, liveHashes);
                    break;

                case SC2JsonKind.Array:
                    foreach (var item in node.Items())
                        CollectHashes(item, liveHashes);
                    break;
            }
        }
    }
}
