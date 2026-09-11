using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>One atomic decal edit or brush stroke.</summary>
    public sealed class SC2ReplaceDecalsStateCommand : ISC2DocumentCommand
    {
        private readonly string _name;
        private readonly SC2DecalsDocument _next;
        private SC2DecalsDocument _previous;

        public SC2ReplaceDecalsStateCommand(string name, SC2DecalsDocument next)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "Edit Decals" : name;
            _next = next?.Clone() ?? throw new ArgumentNullException(nameof(next));
        }

        public string Name => _name;
        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            _previous = document.Decals.Clone();
            document.Decals.CopyFrom(_next);
            document.Decals.IsEditable = true;
            document.Decals.SpatialState = SC2DecalsSpatialState.Stale;
            return Change(document, _name);
        }
        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            document.Decals.CopyFrom(_previous);
            return Change(document, _name + " (undo)");
        }
        private static SC2DocumentChangeSet Change(SC2MapDocument d, string name) => new()
        {
            BeforeRevision = d.Revision, AfterRevision = d.Revision + 1, CommandName = name,
            DirectDirtyFlags = SC2DirtyFlags.Decals,
            ChangedDomains = new[] { SC2DomainId.Decals },
            InvalidatedDomains = Array.Empty<SC2DomainId>(), TimestampUtc = DateTime.UtcNow
        };
    }
}
