using System;
using System.Collections.Generic;
using System.IO;

namespace SC2.MapEditor.Document
{
    /// <summary>One texture file kept inside the map's own share (its Blobs folder), so the map still has the texture on a machine
    /// that lacks the original: a texture imported from a file, or one an imported terrain.bdf names that only the author's project
    /// holds. <see cref="Key"/> is what a texture is looked up by (<see cref="SC2MapshareTextures.KeyOf"/>).</summary>
    public sealed class SC2MapshareTextureDocument
    {
        public string Key = "";
        public string Extension = ".dds";
        public SC2BlobReference Blob;

        public SC2MapshareTextureDocument Clone() => new SC2MapshareTextureDocument { Key = Key, Extension = Extension, Blob = Blob };
    }

    /// <summary>
    /// The map share as a last-resort place to look for a texture. Textures are found where they always were (the project's texture
    /// folders, the game archive, the file an import came from); only when none of those has one does a loader ask here.
    /// <see cref="Register"/> is called when a map is loaded, <see cref="Capture"/> when it is saved.
    /// </summary>
    public static class SC2MapshareTextures
    {
        public const string FilePrefix = "file:";

        private sealed class Held
        {
            public string Extension;
            public SC2BlobReference Blob;
            public ISC2BlobStore Store;
        }

        // What the map that is open right now carries, by key. Replaced whole on every load.
        private static readonly Dictionary<string, Held> Loaded = new Dictionary<string, Held>(StringComparer.Ordinal);

        /// <summary>The key a texture is stored and looked up by: a "file:" path (an imported texture) lower-cased as it is, and a game
        /// path ("/textures/Terrain/X/y_d.win.dds", "SC2Textures/textures/Terrain/X/y_d") without a leading slash, the SC2Textures
        /// folder, the extension or ".win", lower-cased, so the spellings one texture is asked for all meet.</summary>
        public static string KeyOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var p = path.Replace('\\', '/').Trim();
            if (p.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
                return FilePrefix + p.Substring(FilePrefix.Length).ToLowerInvariant();
            p = p.TrimStart('/').ToLowerInvariant();
            const string root = "sc2textures/";
            if (p.StartsWith(root, StringComparison.Ordinal)) p = p.Substring(root.Length);
            foreach (var ext in new[] { ".dds", ".png", ".tga" })
                if (p.EndsWith(ext, StringComparison.Ordinal)) { p = p.Substring(0, p.Length - ext.Length); break; }
            if (p.EndsWith(".win", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 4);
            return p;
        }

        /// <summary>Makes the textures a loaded map carries available to <see cref="TryRead"/>, replacing what an earlier map held.</summary>
        public static void Register(IEnumerable<SC2MapshareTextureDocument> items, ISC2BlobStore store)
        {
            Loaded.Clear();
            if (items == null || store == null) return;
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.Key) || item.Blob.IsEmpty) continue;
                Loaded[item.Key] = new Held { Extension = item.Extension, Blob = item.Blob, Store = store };
            }
        }

        public static bool Has(string path)
        {
            var key = KeyOf(path);
            return key.Length > 0 && Loaded.TryGetValue(key, out var held) && held.Store.Contains(held.Blob.ContentHash);
        }

        /// <summary>The bytes of the texture the open map carries under <paramref name="path"/>, with the extension they came with.</summary>
        public static bool TryRead(string path, out byte[] bytes, out string extension)
        {
            bytes = null;
            extension = ".dds";
            var key = KeyOf(path);
            if (key.Length == 0 || !Loaded.TryGetValue(key, out var held) || !held.Store.Contains(held.Blob.ContentHash)) return false;
            try
            {
                using var stream = held.Store.OpenRead(held.Blob.ContentHash);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
                extension = held.Extension;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>Puts the texture asked for as <paramref name="path"/> into <paramref name="list"/> (once per key) and its bytes into
        /// <paramref name="store"/>. <paramref name="bytes"/> is the file as this machine has it, or null when it is missing here; then
        /// what the loaded map carried under the same key is carried on instead, so a save on a machine without the original keeps it.</summary>
        public static void Capture(List<SC2MapshareTextureDocument> list, ISC2BlobStore store, string path, byte[] bytes, string extension)
        {
            if (list == null || store == null) return;
            var key = KeyOf(path);
            if (key.Length == 0) return;
            foreach (var existing in list)
                if (existing.Key == key) return;

            if (bytes == null && TryRead(path, out var carried, out var carriedExtension))
            {
                bytes = carried;
                extension = carriedExtension;
            }
            if (bytes == null) return;

            var blob = store.Put(new MemoryStream(bytes, writable: false));
            list.Add(new SC2MapshareTextureDocument { Key = key, Extension = string.IsNullOrEmpty(extension) ? ".dds" : extension, Blob = blob });
            Loaded[key] = new Held { Extension = list[list.Count - 1].Extension, Blob = blob, Store = store };
        }
    }
}
