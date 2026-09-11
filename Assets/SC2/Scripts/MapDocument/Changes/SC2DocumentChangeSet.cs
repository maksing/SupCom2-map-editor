using System;
using System.Collections.Generic;
using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>Which domain sub-document a change touched — mirrors the
    /// SC2MapDocument fields, not the underlying file formats (that's
    /// what SC2DirtyFlags/SC2ExportFileKind, already proven in SmartExport,
    /// are for).</summary>
    public enum SC2DomainId
    {
        Identity,
        Source,
        EntryPoint,
        Archive,
        Terrain,
        Hfield,
        Costs,
        Collision2,
        Objects,
        Environment,
        Scenario,
        Decals,
        Assets
    }

    /// <summary>
    /// Reuses the already-real,
    /// already-tested SC2DirtyFlags/dependency-graph from SmartExport
    /// (Runtime/SmartExport/) rather than inventing a parallel enum, since
    /// this change set's whole job is to feed that same expansion logic
    /// (SC2ExportDependencyGraph.ExpandStaleFlags).
    /// </summary>
    public sealed class SC2DocumentChangeSet
    {
        public long BeforeRevision;
        public long AfterRevision;
        public string CommandName;
        public SC2DirtyFlags DirectDirtyFlags;
        public IReadOnlyList<SC2DomainId> ChangedDomains;
        public IReadOnlyList<SC2DomainId> InvalidatedDomains;
        public DateTime TimestampUtc;
    }
}
