using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Sets one Environment texture slot's export provenance tag and
    /// "copy into map on export" opt-in (SC2EnvironmentTextureExport). Same
    /// shape and dirty-flag reasoning as SC2SetEnvironmentTexturePathCommand
    /// (Info is the flag SmartExport keys info.bdf to; nothing else depends
    /// on it), so a toggle or a fresh pick is one undoable step.
    /// </summary>
    public sealed class SC2SetEnvironmentTextureExportCommand : ISC2DocumentCommand
    {
        private readonly SC2EnvironmentTextureSlot _slot;
        private readonly string _newTag;
        private readonly bool _newForce;
        private string _previousTag;
        private bool _previousForce;

        public SC2SetEnvironmentTextureExportCommand(SC2EnvironmentTextureSlot slot, string newTag, bool newForce)
        {
            _slot = slot;
            _newTag = newTag ?? "";
            _newForce = newForce;
        }

        public string Name => $"Set Environment.{_slot} Export";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var export = document.Environment.TextureExport ??= new SC2EnvironmentTextureExport();
            _previousTag = export.GetTag(_slot);
            _previousForce = export.GetForce(_slot);
            export.Set(_slot, _newTag, _newForce);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var export = document.Environment.TextureExport ??= new SC2EnvironmentTextureExport();
            export.Set(_slot, _previousTag, _previousForce);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName) =>
            new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = SC2DirtyFlags.Info,
                ChangedDomains = new[] { SC2DomainId.Environment },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
    }
}
