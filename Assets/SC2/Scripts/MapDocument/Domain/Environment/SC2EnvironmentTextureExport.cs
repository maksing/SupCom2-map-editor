using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Per-slot export options for the 5 Environment texture paths (indexed by
    /// SC2EnvironmentTextureSlot). The path strings themselves
    /// (SC2EnvironmentDocument.EnvironmentMap etc.) always hold a clean,
    /// loadable real game path — the texture picker resolves a browser pick to
    /// that immediately and forgets where it came from — so provenance has to
    /// live next to them:
    ///   SourceTags[slot]  the "archive:&lt;entryPath&gt;" tag of the texture the
    ///                     user picked, or "" (typed path, custom file, unset);
    ///   ForceExport[slot] the per-texture "copy into map on export" opt-in —
    ///                     default false, meaning a vanilla-archive texture is
    ///                     referenced by its real game path only (see
    ///                     SC2VanillaArchiveClassifier).
    /// </summary>
    public sealed class SC2EnvironmentTextureExport
    {
        public const int SlotCount = 5;

        public string[] SourceTags = { "", "", "", "", "" };
        public bool[] ForceExport = new bool[SlotCount];

        public string GetTag(SC2EnvironmentTextureSlot slot) => Index(slot) < SourceTags.Length ? SourceTags[Index(slot)] ?? "" : "";
        public bool GetForce(SC2EnvironmentTextureSlot slot) => Index(slot) < ForceExport.Length && ForceExport[Index(slot)];

        public void Set(SC2EnvironmentTextureSlot slot, string tag, bool force)
        {
            EnsureSize();
            SourceTags[Index(slot)] = tag ?? "";
            ForceExport[Index(slot)] = force;
        }

        public SC2EnvironmentTextureExport Clone()
        {
            var clone = new SC2EnvironmentTextureExport();
            clone.CopyFrom(this);
            return clone;
        }

        public void CopyFrom(SC2EnvironmentTextureExport other)
        {
            // Build into locals first: `other` may be `this` (EnsureSize).
            var tags = new string[SlotCount];
            var force = new bool[SlotCount];
            for (var i = 0; i < SlotCount; i++)
            {
                tags[i] = other != null && other.SourceTags != null && i < other.SourceTags.Length ? other.SourceTags[i] ?? "" : "";
                force[i] = other != null && other.ForceExport != null && i < other.ForceExport.Length && other.ForceExport[i];
            }
            SourceTags = tags;
            ForceExport = force;
        }

        public bool ContentEquals(SC2EnvironmentTextureExport other)
        {
            if (other == null) return false;
            for (var i = 0; i < SlotCount; i++)
            {
                var a = i < SourceTags.Length ? SourceTags[i] ?? "" : "";
                var b = i < other.SourceTags.Length ? other.SourceTags[i] ?? "" : "";
                if (!string.Equals(a, b, StringComparison.Ordinal)) return false;
                var fa = i < ForceExport.Length && ForceExport[i];
                var fb = i < other.ForceExport.Length && other.ForceExport[i];
                if (fa != fb) return false;
            }
            return true;
        }

        private void EnsureSize()
        {
            if (SourceTags == null || SourceTags.Length != SlotCount ||
                ForceExport == null || ForceExport.Length != SlotCount)
                CopyFrom(this);
        }

        private static int Index(SC2EnvironmentTextureSlot slot) => (int)slot;
    }
}
