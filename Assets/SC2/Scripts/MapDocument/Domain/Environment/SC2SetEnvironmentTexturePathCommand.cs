using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Which of SC2EnvironmentDocument's 5 texture-path strings a
    /// SC2SetEnvironmentTexturePathCommand targets.</summary>
    public enum SC2EnvironmentTextureSlot
    {
        EnvironmentMap,
        SkyMap,
        TexturePath3,
        ColorCorrectionCube,
        FogOfWarCorrectionCube
    }

    /// <summary>
    /// The first real ISC2DocumentCommand in this project — see
    /// ISC2DocumentCommand's own doc comment ("No real commands implement
    /// this yet"). Covers SC2EnvironmentDocument's 5 texture-path string
    /// fields, deliberately the simplest slice of Environment to
    /// command-ify: each is one discrete text-field edit. The other ~45
    /// continuously-dragged sliders/colors on the same document
    /// (SC2EnvironmentShell's Sun/Shadow/Water/Fog/Glow/Camera tabs) stay on
    /// the shell's existing per-frame-diff local undo stack for now — see
    /// SC2MapDocument_Etap4_Environment_Status.md for why that's a
    /// deliberate scope cut, not an oversight: command-ifying a continuous
    /// drag needs drag-start/drag-end transaction semantics this first
    /// command doesn't need to solve yet.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.Info (not a separate
    /// "Environment" flag) because that's the flag SmartExport's own
    /// dependency graph already keys the info.bdf file to
    /// (SC2ExportDependencyGraph.CreateDefaultFileMasks) — Environment's
    /// legacy data IS info.bdf's EnvironmentHeader + texture paths, so this
    /// reuses the real, already-tested mapping instead of inventing a
    /// parallel one. ExpandStaleFlags has no entry for Info, confirming
    /// nothing else depends on it (Environment is a leaf/cosmetic domain).
    /// </summary>
    public sealed class SC2SetEnvironmentTexturePathCommand : ISC2DocumentCommand
    {
        private readonly SC2EnvironmentTextureSlot _slot;
        private readonly string _newValue;
        private string _previousValue;

        public SC2SetEnvironmentTexturePathCommand(SC2EnvironmentTextureSlot slot, string newValue)
        {
            _slot = slot;
            _newValue = newValue ?? "";
        }

        public string Name => $"Set Environment.{_slot}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousValue = GetSlot(document.Environment, _slot);
            SetSlot(document.Environment, _slot, _newValue);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            SetSlot(document.Environment, _slot, _previousValue);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Info;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Environment },
                // SC2ExportDependencyGraph.ExpandStaleFlags(Info) == Info —
                // nothing else becomes stale, so no other domain is listed.
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }

        private static string GetSlot(SC2EnvironmentDocument doc, SC2EnvironmentTextureSlot slot) => slot switch
        {
            SC2EnvironmentTextureSlot.EnvironmentMap => doc.EnvironmentMap,
            SC2EnvironmentTextureSlot.SkyMap => doc.SkyMap,
            SC2EnvironmentTextureSlot.TexturePath3 => doc.TexturePath3,
            SC2EnvironmentTextureSlot.ColorCorrectionCube => doc.ColorCorrectionCube,
            SC2EnvironmentTextureSlot.FogOfWarCorrectionCube => doc.FogOfWarCorrectionCube,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

        private static void SetSlot(SC2EnvironmentDocument doc, SC2EnvironmentTextureSlot slot, string value)
        {
            switch (slot)
            {
                case SC2EnvironmentTextureSlot.EnvironmentMap: doc.EnvironmentMap = value; break;
                case SC2EnvironmentTextureSlot.SkyMap: doc.SkyMap = value; break;
                case SC2EnvironmentTextureSlot.TexturePath3: doc.TexturePath3 = value; break;
                case SC2EnvironmentTextureSlot.ColorCorrectionCube: doc.ColorCorrectionCube = value; break;
                case SC2EnvironmentTextureSlot.FogOfWarCorrectionCube: doc.FogOfWarCorrectionCube = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }
    }
}
