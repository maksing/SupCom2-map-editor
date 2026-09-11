using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Directory-backed ISC2BlobStore: "blobs/&lt;first 2 hex chars&gt;/&lt;full hash&gt;.bin".
    /// Writes go
    /// through a temp file + atomic move (never a partial file visible under
    /// the final hash-named path), and Put is a no-op if that hash's file
    /// already exists — the dedup the whole content-addressing scheme is
    /// for.
    /// </summary>
    public sealed class SC2DirectoryBlobStore : ISC2BlobStore
    {
        private readonly string _blobsRoot;

        public SC2DirectoryBlobStore(string blobsRoot)
        {
            _blobsRoot = blobsRoot;
            Directory.CreateDirectory(_blobsRoot);
        }

        private string PathFor(string hash) => Path.Combine(_blobsRoot, hash.Substring(0, 2), hash + ".bin");

        public bool Contains(string hash) => File.Exists(PathFor(hash));

        public Stream OpenRead(string hash)
        {
            var path = PathFor(hash);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Blob not found for hash '{hash}'.", path);
            return File.OpenRead(path);
        }

        public SC2BlobReference Put(Stream source)
        {
            // Hash while copying to a temp file in one pass, rather than
            // buffering the whole stream in memory first — matters once
            // this is handling multi-megabyte RNM masks/textures (JsonUtility
            // chokes on embedded byte[] arrays this large; a blob store only
            // helps if it doesn't reintroduce the same problem via a big
            // in-memory copy).
            var tempPath = Path.Combine(_blobsRoot, Guid.NewGuid().ToString("N") + ".tmp");
            long length;
            string hash;
            using (var sha256 = SHA256.Create())
            using (var tempFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            using (var hashingStream = new CryptoStream(tempFile, sha256, CryptoStreamMode.Write))
            {
                source.CopyTo(hashingStream);
                hashingStream.FlushFinalBlock();
                length = tempFile.Length;
                hash = SC2Hex.ToLowerHex(sha256.Hash!);
            }

            var finalPath = PathFor(hash);
            if (File.Exists(finalPath))
            {
                // Identical content already stored under this hash — the
                // dedup case. Discard the temp copy rather than overwrite;
                // immutable blobs are never rewritten once present.
                File.Delete(tempPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.Move(tempPath, finalPath);
            }

            return new SC2BlobReference(hash, length);
        }

        /// <summary>Walks the "&lt;first 2 hex
        /// chars&gt;/&lt;full hash&gt;.bin" shard layout Put writes into,
        /// yielding the hash portion of every stored blob's filename.</summary>
        public IEnumerable<string> EnumerateHashes()
        {
            if (!Directory.Exists(_blobsRoot))
                yield break;

            foreach (var shardDir in Directory.GetDirectories(_blobsRoot))
            {
                foreach (var file in Directory.GetFiles(shardDir, "*.bin"))
                    yield return Path.GetFileNameWithoutExtension(file);
            }
        }

        public bool TryDelete(string hash)
        {
            var path = PathFor(hash);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
    }
}
