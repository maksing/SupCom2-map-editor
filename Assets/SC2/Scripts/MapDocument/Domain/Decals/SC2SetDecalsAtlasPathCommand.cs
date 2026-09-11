using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Which of SC2DecalsDocument's 2 atlas texture-path strings a
    /// SC2SetDecalsAtlasPathCommand targets.</summary>
    public enum SC2DecalsAtlasSlot
    {
        Diffuse,
        Normal
    }

    /// <summary>
    /// Eighth real ISC2DocumentCommand (after Environment/Collision2/Costs/
    /// Hfield/Objects/Terrain/Scenario) — the first for Decals, and
    /// deliberately the only thing in this domain that IS meaningfully
    /// commandable yet: decals.win.bdf holds one already-baked mesh, not a
    /// list of individually-authored decals (SC2DecalsDocument's own doc
    /// comment: "there is no list of decals to expose as editable fields
    /// until something un-bakes this mesh back into that form, which
    /// doesn't exist"). The two atlas texture paths are real, genuinely
    /// editable strings independent of that gap, so this mirrors
    /// SC2SetEnvironmentTexturePathCommand's slot-enum shape exactly.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.Decals — the real flag
    /// SmartExport's dependency graph keys decals.win.bdf to.
    /// ExpandStaleFlags has no entry for Decals at all, confirming nothing
    /// else depends on it (same leaf reasoning as Environment/Collision2/
    /// Costs/Hfield).
    /// </summary>
    public sealed class SC2SetDecalsAtlasPathCommand : ISC2DocumentCommand
    {
        private readonly SC2DecalsAtlasSlot _slot;
        private readonly string _newValue;
        private string _previousValue;

        public SC2SetDecalsAtlasPathCommand(SC2DecalsAtlasSlot slot, string newValue)
        {
            _slot = slot;
            _newValue = newValue ?? "";
        }

        public string Name => $"Set Decals.{_slot}AtlasPath";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousValue = GetSlot(document.Decals, _slot);
            SetSlot(document.Decals, _slot, _newValue);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            SetSlot(document.Decals, _slot, _previousValue);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Decals;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Decals },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }

        private static string GetSlot(SC2DecalsDocument doc, SC2DecalsAtlasSlot slot) => slot switch
        {
            SC2DecalsAtlasSlot.Diffuse => doc.DiffuseAtlasPath,
            SC2DecalsAtlasSlot.Normal => doc.NormalAtlasPath,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };

        private static void SetSlot(SC2DecalsDocument doc, SC2DecalsAtlasSlot slot, string value)
        {
            switch (slot)
            {
                case SC2DecalsAtlasSlot.Diffuse: doc.DiffuseAtlasPath = value; break;
                case SC2DecalsAtlasSlot.Normal: doc.NormalAtlasPath = value; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }
    }
}
