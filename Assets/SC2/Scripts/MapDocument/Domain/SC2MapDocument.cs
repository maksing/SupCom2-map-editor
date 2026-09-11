using System;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The pure-data root. No UI
    /// state lives here (that's SC2MapSession, Session/SC2MapSession.cs).
    ///
    /// The remaining domain sub-document (Assets)
    /// is still deliberately NOT added — it still needs the same "actually
    /// migrate off legacy SC2TerrainData fields" work every other domain
    /// already got (real commands + dirty/stale wiring + validation, not
    /// just a read-only bridge) before adding an empty/unused field here
    /// would be worth the sync burden. Environment went first because
    /// SC2SetEnvironmentTexturePathCommand needed somewhere real to mutate;
    /// Collision2/Costs/Hfield/Objects/Terrain followed for the same
    /// reason, in roughly smallest-first order (not the plan's own Objects-
    /// first ordering). Scenario/Decals went last — both had real read-only
    /// bridges since earlier in this project, but genuinely little to
    /// command yet (Decals in particular: "there is no list of decals to
    /// expose as editable fields", SC2DecalsDocument's own doc comment), so
    /// their first commands are both string-field-by-slot, same shape as
    /// Environment's.
    /// </summary>
    public sealed class SC2MapDocument
    {
        public const int CurrentFormatVersion = 1;

        public string DocumentId { get; } = Guid.NewGuid().ToString("N");
        public int FormatVersion = CurrentFormatVersion;
        public long Revision { get; private set; }

        public SC2MapIdentity Identity = new();
        public SC2MapSource Source = new();
        public SC2MapEntryPoint EntryPoint = new();
        public SC2ArchiveDocument Archive = new();
        public SC2EnvironmentDocument Environment = new();
        public SC2CollisionDocument Collision2 = new();
        public SC2CostsDocument Costs = new();
        public SC2HfieldDocument Hfield = new();
        public SC2ObjectsDocument Objects = new();
        public SC2TerrainDocument Terrain = new();
        public SC2ScenarioDocument Scenario = new();
        public SC2DecalsDocument Decals = new();

        public event Action<SC2DocumentChangeSet> Changed;

        /// <summary>
        /// Bumps Revision and raises Changed. Nothing calls this outside
        /// tests today; it exists
        /// now so Session/command code written in later stages has a single
        /// real notification path to wire against instead of inventing one
        /// ad hoc later.
        /// </summary>
        public void RaiseChanged(SC2DocumentChangeSet changeSet)
        {
            if (changeSet == null) throw new ArgumentNullException(nameof(changeSet));
            Revision++;
            Changed?.Invoke(changeSet);
        }
    }
}
