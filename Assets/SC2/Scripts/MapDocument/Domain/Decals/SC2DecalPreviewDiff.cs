using System.Collections.Generic;
using System.Text;

namespace SC2.MapEditor.Document
{
    /// <summary>Decides which decal placements need their preview fragment rebuilt after a
    /// document edit. A fragment is expensive (surface collection plus clipping against
    /// every nearby terrain triangle), so an edit must only rebuild the placements whose
    /// own data changed, not all of them.</summary>
    public static class SC2DecalPreviewDiff
    {
        public sealed class Result
        {
            public readonly HashSet<string> Rebuild = new();
            public readonly List<string> Removed = new();
        }

        /// <summary>Everything a fragment depends on besides its own placement: the atlas size
        /// and each entry's atlas rectangle. A change here invalidates every fragment.</summary>
        public static string EntriesSignature(SC2DecalsDocument document)
        {
            var sb = new StringBuilder();
            sb.Append(document.AtlasWidth).Append('x').Append(document.AtlasHeight);
            foreach (var e in document.Entries)
            {
                if (e == null) continue;
                sb.Append('|').Append(e.Id).Append(':').Append(e.X).Append(':').Append(e.Y)
                  .Append(':').Append(e.Width).Append(':').Append(e.Height);
            }
            return sb.ToString();
        }

        /// <summary>True when the two placements would produce the same preview fragment
        /// (every authored field, including the group they belong to).</summary>
        public static bool SamePlacement(SC2DecalPlacement a, SC2DecalPlacement b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.Id == b.Id && a.Name == b.Name && a.AtlasEntryId == b.AtlasEntryId
                && a.Rotation == b.Rotation && a.Width == b.Width && a.Length == b.Length
                && a.ProjectionDepth == b.ProjectionDepth && a.HeightOffset == b.HeightOffset
                && a.AlignToSurface == b.AlignToSurface && a.Enabled == b.Enabled
                && a.SymmetryGroupId == b.SymmetryGroupId && a.SymmetrySlotIndex == b.SymmetrySlotIndex
                && SameVector(a.Position, b.Position) && SameVector(a.SurfaceNormal, b.SurfaceNormal);
        }

        private static bool SameVector(float[] a, float[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <param name="builtFrom">Per placement id, the placement data its current fragment
        /// was built from.</param>
        public static Result Compute(SC2DecalsDocument document, IReadOnlyDictionary<string, SC2DecalPlacement> builtFrom)
        {
            var result = new Result();
            var live = new HashSet<string>();
            foreach (var p in document.Placements)
            {
                if (p?.Id == null) continue;
                live.Add(p.Id);
                if (!builtFrom.TryGetValue(p.Id, out var source) || !SamePlacement(source, p))
                    result.Rebuild.Add(p.Id);
            }
            foreach (var id in builtFrom.Keys)
                if (!live.Contains(id)) result.Removed.Add(id);
            return result;
        }
    }
}
