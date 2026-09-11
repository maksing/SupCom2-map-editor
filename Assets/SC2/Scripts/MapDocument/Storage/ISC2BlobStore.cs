using System.Collections.Generic;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>Content-addressed reference — the blob store is the only
    /// thing that resolves ContentHash to actual bytes, so this struct is
    /// cheap to embed in the document manifest without duplicating data.</summary>
    public readonly struct SC2BlobReference
    {
        public readonly string ContentHash;
        public readonly long Length;

        public SC2BlobReference(string contentHash, long length)
        {
            ContentHash = contentHash;
            Length = length;
        }

        public bool IsEmpty => string.IsNullOrEmpty(ContentHash);
        public static SC2BlobReference Empty => default;
    }

    /// <summary>
    /// Keeps large binary data
    /// (RNM masks, textures, original archive entry bytes) out of the JSON
    /// manifest. Content-addressed by SHA-256, so identical bytes are
    /// automatically deduplicated — this falls out of the hash-then-check
    /// approach almost for free. See SC2BlobStoreGarbageCollector for the
    /// compaction/retention pass.
    /// </summary>
    public interface ISC2BlobStore
    {
        bool Contains(string hash);
        Stream OpenRead(string hash);
        SC2BlobReference Put(Stream source);

        /// <summary>Every content hash currently
        /// stored, needed by SC2BlobStoreGarbageCollector's sweep phase.
        /// SC2DirectoryBlobStore is the only implementation today.</summary>
        IEnumerable<string> EnumerateHashes();

        /// <summary>Deletes the blob for <paramref name="hash"/> if present.
        /// Returns false (not an error) if it was already gone — GC callers
        /// treat "nothing to delete" and "deleted it" the same way.</summary>
        bool TryDelete(string hash);
    }
}
