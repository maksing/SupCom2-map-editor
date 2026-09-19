using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Helpers for the imported-layer RNM masks
    /// (<see cref="SC2TerrainDocument.ImportedRnm"/>): names, resolutions and
    /// reading/writing a mask's baked byte arrays through the blob store.</summary>
    public static class SC2ImportedRnmMasks
    {
        public static readonly int[] Resolutions = { 512, 1024, 2048, 4096 };
        public const int DefaultResolution = 1024;
        public const string DefaultBaseName = "imported";

        public static SC2RnmMaskSetDocument Find(SC2RnmDocument rnm, string name)
        {
            if (rnm == null || string.IsNullOrEmpty(name)) return null;
            foreach (var set in rnm.MaskSets)
                if (string.Equals(set.Name, name, StringComparison.Ordinal)) return set;
            return null;
        }

        /// <summary>"imported01", "imported02", … — the first one no mask uses yet.</summary>
        public static string NextName(IEnumerable<SC2RnmMaskSetDocument> existing)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in existing) taken.Add(set.Name ?? "");
            for (var i = 1; ; i++)
            {
                var candidate = DefaultBaseName + i.ToString("00");
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        /// <summary>The name reduced to what is safe in a file name / game path
        /// (letters, digits, '_' and '-'); anything else becomes '_'.</summary>
        public static string SafeStem(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return DefaultBaseName;
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim())
                sb.Append(char.IsLetterOrDigit(c) && c < 128 || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        /// <summary>The on-disk / in-game stem of a mask: <c>imp_&lt;name&gt;</c>
        /// (files <c>imp_&lt;name&gt;_0/_1/_2.win.dds</c>) — the prefix keeps it
        /// apart from the core terrain's own masks.</summary>
        public static string ExportStem(string name) => "imp_" + SafeStem(name);

        /// <summary>True once a bake has produced all three RGB slices and the
        /// shadow alpha at this mask's current resolution.</summary>
        public static bool HasBake(SC2RnmMaskSetDocument set)
        {
            if (set == null || set.Width <= 0 || set.Height <= 0) return false;
            var n = (long)set.Width * set.Height;
            return set.BakedRgb0.Length == n * 3 && set.BakedRgb1.Length == n * 3 &&
                   set.BakedRgb2.Length == n * 3 && set.BakedShadowAlpha.Length == n;
        }

        public static byte[] Read(ISC2BlobStore store, SC2BlobReference reference)
        {
            if (store == null || reference.IsEmpty || !store.Contains(reference.ContentHash)) return null;
            using var stream = store.OpenRead(reference.ContentHash);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        public static SC2BlobReference Put(ISC2BlobStore store, byte[] bytes) =>
            store != null && bytes != null && bytes.Length > 0
                ? store.Put(new MemoryStream(bytes))
                : SC2BlobReference.Empty;

        /// <summary>The slice-1 blend alpha of a mask (its RGBA <c>MaskBytes</c>'
        /// alpha channel), one byte per texel, or null when not stored.</summary>
        public static byte[] ReadBlendAlpha(ISC2BlobStore store, SC2RnmMaskSetDocument set)
        {
            var rgba = Read(store, set.MaskBytes);
            var n = set.Width * set.Height;
            if (rgba == null || rgba.Length != n * 4) return null;
            var alpha = new byte[n];
            for (var i = 0; i < n; i++) alpha[i] = rgba[i * 4 + 3];
            return alpha;
        }
    }

    /// <summary>
    /// Replaces the whole imported-layer RNM mask list
    /// (SC2TerrainDocument.ImportedRnm) in one step — adding, renaming,
    /// resizing or deleting a mask, and storing a bake result, are each one
    /// undo entry (same whole-state shape as SC2ReplaceDecalsStateCommand).
    /// DirectDirtyFlags is TerrainMaterials: the masks change the RNM Mask
    /// path of the imported terrain.bdf materials that use them.
    /// </summary>
    public sealed class SC2SetImportedRnmMasksCommand : ISC2DocumentCommand
    {
        private readonly List<SC2RnmMaskSetDocument> _next;
        private List<SC2RnmMaskSetDocument> _previous;

        public SC2SetImportedRnmMasksCommand(IEnumerable<SC2RnmMaskSetDocument> next)
        {
            _next = new List<SC2RnmMaskSetDocument>();
            foreach (var set in next) _next.Add(set.Clone());
        }

        public string Name => "Set Imported RNM Masks";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previous = document.Terrain.ImportedRnm.CloneMaskSets();
            document.Terrain.ImportedRnm.MaskSets = CloneAll(_next);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Terrain.ImportedRnm.MaskSets = CloneAll(_previous);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static List<SC2RnmMaskSetDocument> CloneAll(List<SC2RnmMaskSetDocument> source)
        {
            var list = new List<SC2RnmMaskSetDocument>(source.Count);
            foreach (var set in source) list.Add(set.Clone());
            return list;
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName) => new()
        {
            BeforeRevision = document.Revision,
            AfterRevision = document.Revision + 1,
            CommandName = commandName,
            DirectDirtyFlags = SC2DirtyFlags.TerrainMaterials,
            ChangedDomains = new[] { SC2DomainId.Terrain },
            InvalidatedDomains = Array.Empty<SC2DomainId>(),
            TimestampUtc = DateTime.UtcNow,
        };
    }
}
