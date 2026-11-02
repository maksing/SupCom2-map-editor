using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>One atomic edit of the mission graph: the whole graph is swapped, so every kind of edit (a node moved, a wire added, a
    /// stage renamed) undoes the same way.</summary>
    public sealed class SC2ReplaceMissionCommand : ISC2DocumentCommand
    {
        private readonly string _name;
        private readonly SC2MissionDocument _next;
        private SC2MissionDocument _previous;

        public SC2ReplaceMissionCommand(string name, SC2MissionDocument next)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "Edit Mission" : name;
            _next = next?.Clone() ?? throw new ArgumentNullException(nameof(next));
        }

        public string Name => _name;

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            _previous = document.Scenario.Mission.Clone();
            document.Scenario.Mission.CopyFrom(_next);
            return Change(document, _name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            document.Scenario.Mission.CopyFrom(_previous);
            return Change(document, _name + " (undo)");
        }

        private static SC2DocumentChangeSet Change(SC2MapDocument d, string name) => new()
        {
            BeforeRevision = d.Revision, AfterRevision = d.Revision + 1, CommandName = name,
            DirectDirtyFlags = SC2DirtyFlags.ScriptLua,
            ChangedDomains = new[] { SC2DomainId.Scenario },
            InvalidatedDomains = Array.Empty<SC2DomainId>(), TimestampUtc = DateTime.UtcNow
        };
    }
}
