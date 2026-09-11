using System;
using System.Collections.Generic;
using System.IO;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class BlobStoreGarbageCollectorTests
{
    private static SC2DirectoryBlobStore NewStore(out string root)
    {
        root = Directory.CreateTempSubdirectory("sc2gcblobs_").FullName;
        return new SC2DirectoryBlobStore(root);
    }

    private static SC2JsonValue BlobRefJson(string contentHash)
    {
        var o = SC2JsonValue.NewObject();
        o.Set("contentHash", SC2JsonValue.Of(contentHash));
        o.Set("length", SC2JsonValue.Of(1L));
        return o;
    }

    private static string ManifestWithOneBlobRef(string contentHash)
    {
        var root = SC2JsonValue.NewObject();
        root.Set("someBlob", BlobRefJson(contentHash));
        return root.Write();
    }

    private static string ManifestWithTwoBlobRefsInAnArray(string hashA, string hashB)
    {
        var itemA = SC2JsonValue.NewObject();
        itemA.Set("id", SC2JsonValue.Of("a"));
        itemA.Set("maskBytes", BlobRefJson(hashA));
        var itemB = SC2JsonValue.NewObject();
        itemB.Set("id", SC2JsonValue.Of("b"));
        itemB.Set("maskBytes", BlobRefJson(hashB));

        var maskSets = SC2JsonValue.NewArray();
        maskSets.Add(itemA);
        maskSets.Add(itemB);

        var root = SC2JsonValue.NewObject();
        root.Set("maskSets", maskSets);
        return root.Write();
    }

    [Fact]
    public void EnumerateHashes_ReturnsEveryStoredBlobsHash()
    {
        var store = NewStore(out var root);
        try
        {
            var refA = store.Put(new MemoryStream(new byte[] { 1, 2, 3 }));
            var refB = store.Put(new MemoryStream(new byte[] { 4, 5, 6 }));

            var hashes = new HashSet<string>(store.EnumerateHashes());

            Assert.Equal(2, hashes.Count);
            Assert.Contains(refA.ContentHash, hashes);
            Assert.Contains(refB.ContentHash, hashes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryDelete_RemovesTheBlob_ReturnsTrue()
    {
        var store = NewStore(out var root);
        try
        {
            var reference = store.Put(new MemoryStream(new byte[] { 1, 2, 3 }));
            Assert.True(store.Contains(reference.ContentHash));

            Assert.True(store.TryDelete(reference.ContentHash));

            Assert.False(store.Contains(reference.ContentHash));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryDelete_AlreadyGone_ReturnsFalse()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.False(store.TryDelete("0000000000000000000000000000000000000000000000000000000000000000"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CollectUnreferencedBlobs_DeletesBlobsNoManifestMentions()
    {
        var store = NewStore(out var root);
        try
        {
            var live = store.Put(new MemoryStream(new byte[] { 1, 2, 3 }));
            var orphan = store.Put(new MemoryStream(new byte[] { 4, 5, 6 }));

            var manifest = ManifestWithOneBlobRef(live.ContentHash);

            var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(store, new[] { manifest });

            Assert.Equal(1, deleted);
            Assert.True(store.Contains(live.ContentHash));
            Assert.False(store.Contains(orphan.ContentHash));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CollectUnreferencedBlobs_KeepsBlobsReferencedByAnyOfMultipleManifests()
    {
        var store = NewStore(out var root);
        try
        {
            var mapABlob = store.Put(new MemoryStream(new byte[] { 1 }));
            var mapBBlob = store.Put(new MemoryStream(new byte[] { 2 }));

            var manifestA = ManifestWithOneBlobRef(mapABlob.ContentHash);
            var manifestB = ManifestWithOneBlobRef(mapBBlob.ContentHash);

            var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(store, new[] { manifestA, manifestB });

            Assert.Equal(0, deleted);
            Assert.True(store.Contains(mapABlob.ContentHash));
            Assert.True(store.Contains(mapBBlob.ContentHash));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CollectUnreferencedBlobs_FindsHashesNestedInsideArrays()
    {
        var store = NewStore(out var root);
        try
        {
            var maskA = store.Put(new MemoryStream(new byte[] { 1 }));
            var maskB = store.Put(new MemoryStream(new byte[] { 2 }));

            var manifest = ManifestWithTwoBlobRefsInAnArray(maskA.ContentHash, maskB.ContentHash);

            var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(store, new[] { manifest });

            Assert.Equal(0, deleted);
            Assert.True(store.Contains(maskA.ContentHash));
            Assert.True(store.Contains(maskB.ContentHash));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CollectUnreferencedBlobs_UnparsableManifest_DoesNotAbortOrAffectOtherManifestsBlobs()
    {
        var store = NewStore(out var root);
        try
        {
            // A manifest that fails to parse can't have its own references
            // recovered (there is no way to know what a corrupt file was
            // pointing at), but it must not throw out of the loop and abort
            // the whole pass either — every OTHER, validly-parsed manifest's
            // blobs must still be correctly kept alive regardless of where
            // in the list the bad one sits.
            var live = store.Put(new MemoryStream(new byte[] { 1, 2, 3 }));

            var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(
                store, new[] { "{not valid json", ManifestWithOneBlobRef(live.ContentHash) });

            Assert.Equal(0, deleted);
            Assert.True(store.Contains(live.ContentHash));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CollectUnreferencedBlobs_NoManifestsAtAll_DeletesEverything()
    {
        var store = NewStore(out var root);
        try
        {
            store.Put(new MemoryStream(new byte[] { 1 }));
            store.Put(new MemoryStream(new byte[] { 2 }));

            var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(store, Array.Empty<string>());

            Assert.Equal(2, deleted);
            Assert.Empty(store.EnumerateHashes());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CollectUnreferencedBlobs_ThrowsOnNullArguments()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.Throws<ArgumentNullException>(() => SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(null, Array.Empty<string>()));
            Assert.Throws<ArgumentNullException>(() => SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(store, null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
