using System;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Seventh real ISC2DocumentCommand (after Environment/Collision2/Costs/
    /// Hfield/Objects/Terrain) — the first for Scenario. Mirrors
    /// SC2SetEnvironmentTexturePathCommand's shape exactly: a single string
    /// field on the domain's KnownFields, with no "find by Id" step needed
    /// since a document has exactly one ScenarioInfo, not a list.
    ///
    /// DirectDirtyFlags uses SC2DirtyFlags.ScenarioLua — the real flag
    /// SmartExport's dependency graph keys _scenario.lua to. ExpandStaleFlags
    /// never lists ScenarioLua as a SOURCE (only as something Objects
    /// changes cascade INTO, via SC2ScenarioLuaWriter's StartPositions
    /// regeneration) — nothing depends on ScenarioInfo.name specifically, so
    /// InvalidatedDomains is empty here.
    /// </summary>
    public sealed class SC2SetScenarioNameCommand : ISC2DocumentCommand
    {
        private readonly string _newName;
        private string _previousName;

        public SC2SetScenarioNameCommand(string newName)
        {
            _newName = newName ?? "";
        }

        public string Name => "Set Scenario.Name";

        public SC2DocumentChangeSet Do(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            _previousName = document.Scenario.KnownFields.Name;
            document.Scenario.KnownFields.Name = _newName;
            return BuildChangeSet(document, Name);
        }

        public SC2DocumentChangeSet Undo(SC2MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.Scenario.KnownFields.Name = _previousName;
            return BuildChangeSet(document, Name + " (undo)");
        }

        private static SC2DocumentChangeSet BuildChangeSet(SC2MapDocument document, string commandName)
        {
            const SC2DirtyFlags dirty = SC2DirtyFlags.ScenarioLua;
            return new SC2DocumentChangeSet
            {
                BeforeRevision = document.Revision,
                AfterRevision = document.Revision + 1,
                CommandName = commandName,
                DirectDirtyFlags = dirty,
                ChangedDomains = new[] { SC2DomainId.Scenario },
                InvalidatedDomains = Array.Empty<SC2DomainId>(),
                TimestampUtc = DateTime.UtcNow,
            };
        }
    }
}
