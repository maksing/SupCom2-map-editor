using System;
using System.Collections.Generic;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// SC2CommandHistory's own doc comment flagged this exact gap on purpose
    /// ("no grouping/coalescing yet... apply once real commands exist to
    /// group") — the Objects UI cutover is the first case that actually needs
    /// it: dragging a multi-item selection (Props/Markers/Units together)
    /// must undo as ONE step, not one step per dragged item, the same way the
    /// existing legacy PushUndo() already treats a whole drag/batch action as
    /// a single snapshot.
    ///
    /// Wraps N ISC2DocumentCommand instances and runs them as one atomic step
    /// from SC2CommandHistory's perspective: Do() executes every sub-command
    /// forward, in order; Undo() undoes them in REVERSE order (matters if a
    /// later sub-command's Do() could depend on an earlier one already having
    /// applied — not true for today's per-Id position commands, but cheap
    /// correctness to keep for whatever gets grouped here next). Dirty
    /// flags/changed/invalidated domains are unioned across every
    /// sub-command so nothing SmartExport depends on gets silently dropped
    /// just because it was merged into a batch.
    /// </summary>
    public sealed class SC2CompositeCommand : ISC2DocumentCommand
    {
        private readonly IReadOnlyList<ISC2DocumentCommand> _commands;

        public SC2CompositeCommand(string name, IReadOnlyList<ISC2DocumentCommand> commands)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _commands = commands ?? throw new ArgumentNullException(nameof(commands));
            if (_commands.Count == 0) throw new ArgumentException("Composite command needs at least one sub-command.", nameof(commands));
        }

        public string Name { get; }

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var dirty = SC2DirtyFlags.None;
            var changed = new List<SC2DomainId>();
            var invalidated = new List<SC2DomainId>();
            var beforeRevision = document.Revision;

            foreach (var command in _commands)
            {
                var changeSet = command.Do(document);
                dirty |= changeSet.DirectDirtyFlags;
                MergeInto(changed, changeSet.ChangedDomains);
                MergeInto(invalidated, changeSet.InvalidatedDomains);
            }

            return Build(beforeRevision, document.Revision, Name, dirty, changed, invalidated);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var dirty = SC2DirtyFlags.None;
            var changed = new List<SC2DomainId>();
            var invalidated = new List<SC2DomainId>();
            var beforeRevision = document.Revision;

            for (var i = _commands.Count - 1; i >= 0; i--)
            {
                var changeSet = _commands[i].Undo(document);
                dirty |= changeSet.DirectDirtyFlags;
                MergeInto(changed, changeSet.ChangedDomains);
                MergeInto(invalidated, changeSet.InvalidatedDomains);
            }

            return Build(beforeRevision, document.Revision, Name + " (undo)", dirty, changed, invalidated);
        }

        private static void MergeInto(List<SC2DomainId> target, IReadOnlyList<SC2DomainId> source)
        {
            if (source == null) return;
            foreach (var domain in source)
                if (!target.Contains(domain))
                    target.Add(domain);
        }

        private static SC2DocumentChangeSet Build(long beforeRevision, long afterRevision, string commandName,
            SC2DirtyFlags dirty, List<SC2DomainId> changed, List<SC2DomainId> invalidated)
        {
            return new SC2DocumentChangeSet
            {
                BeforeRevision = beforeRevision,
                AfterRevision = afterRevision,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = changed,
                InvalidatedDomains = invalidated,
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
