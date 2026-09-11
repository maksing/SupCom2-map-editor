using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Objects' "Graphs" slice — renames a graph by stable Id,
    /// same shape as every other single-string-field-by-Id command in this
    /// project. Doesn't touch EntriesA/EntriesB/MarkerIdsA/MarkerIdsB (the
    /// graph's edges) — those are a structurally different, bigger edit
    /// (add/remove edge) left for later.</summary>
    public sealed class SC2SetGraphNameCommand : ISC2DocumentCommand
    {
        private readonly int _graphId;
        private readonly string _newName;
        private string _previousName;

        public SC2SetGraphNameCommand(int graphId, string newName)
        {
            _graphId = graphId;
            _newName = newName ?? "";
        }

        public string Name => $"Rename Graph #{_graphId}";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var graph = FindGraph(document);
            _previousName = graph.Name;
            graph.Name = _newName;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            FindGraph(document).Name = _previousName;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private SC2ObjGraph FindGraph(SC2MapDocument document)
        {
            foreach (var graph in document.Objects.Graphs)
                if (graph.Id == _graphId)
                    return graph;
            throw new InvalidOperationException($"Graph #{_graphId} not found.");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.Objects;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Objects },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
