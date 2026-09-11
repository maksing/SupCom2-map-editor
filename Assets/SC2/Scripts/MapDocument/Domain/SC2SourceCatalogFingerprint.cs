using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// If the
    /// source directory or archive changed between Probe and Import, the
    /// importer must not silently use stale indexes. This is a CATALOG
    /// fingerprint (sorted normalized path + length only), not a content
    /// hash: Probe must stay cheap even against a large multi-map .scd, so
    /// it hashes what a container listing already gives it for free rather
    /// than reading every entry's bytes just to prove nothing changed. Good
    /// enough to detect "the file set or a file's size changed"; not a
    /// security-grade integrity hash of file contents (SC2ArchiveEntry.Sha256
    /// and SC2BlobReference.ContentHash are that, computed per-entry only for
    /// the files actually imported).
    /// </summary>
    public static class SC2SourceCatalogFingerprint
    {
        public static string Compute(IReadOnlyList<SC2SourceEntryInfo> entries)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries.OrderBy(e => e.NormalizedPath, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(entry.NormalizedPath.ToLowerInvariant());
                sb.Append('|');
                sb.Append(entry.UncompressedLength);
                sb.Append('\n');
            }
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            using var sha256 = SHA256.Create();
            return SC2Hex.ToLowerHex(sha256.ComputeHash(bytes));
        }
    }
}
