using System;
using System.Collections.Generic;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Replaces the whole lobby start-position list
    /// (SC2ScenarioDocument.LobbyStartPositions — the minimap markers of
    /// _scenario.lua's StartPositions) in one step, so dragging an army
    /// together with its symmetric partners is a single undo entry, the same
    /// whole-state shape as SC2ReplaceDecalsStateCommand.
    ///
    /// Only ScenarioLua is dirtied: _save.lua's ARMY markers (the in-game
    /// spawns, Objects domain) are deliberately not touched by this edit.
    /// </summary>
    public sealed class SC2SetLobbyStartPositionsCommand : ISC2DocumentCommand
    {
        private readonly List<SC2LobbyStartPosition> _next;
        private List<SC2LobbyStartPosition> _previous;

        public SC2SetLobbyStartPositionsCommand(IEnumerable<SC2LobbyStartPosition> next)
        {
            _next = SC2LobbyStartPosition.CloneList(next);
        }

        public string Name => "Set Lobby Start Positions";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previous = SC2LobbyStartPosition.CloneList(document.Scenario.LobbyStartPositions);
            document.Scenario.LobbyStartPositions = SC2LobbyStartPosition.CloneList(_next);
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Scenario.LobbyStartPositions = SC2LobbyStartPosition.CloneList(_previous);
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName) => new()
        {
            BeforeRevision = document.Revision,
            AfterRevision = document.Revision + 1,
            CommandName = commandName,
            DirectDirtyFlags = SC2DirtyFlags.ScenarioLua,
            ChangedDomains = new[] { SC2DomainId.Scenario },
            InvalidatedDomains = Array.Empty<SC2DomainId>(),
            TimestampUtc = DateTime.UtcNow,
        };
    }
}
