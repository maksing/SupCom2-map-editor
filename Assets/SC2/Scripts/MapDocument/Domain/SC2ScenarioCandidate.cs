using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// One scenario.lua found
    /// during Probe, before the user has chosen which map to import. A
    /// multi-map container (several campaign missions in one .scd) produces
    /// several of these; exactly one is ever imported into a SC2MapDocument —
    /// automatically importing "the first one found" is deliberately disallowed.
    /// </summary>
    public sealed class SC2ScenarioCandidate
    {
        public string ScenarioEntryPath;
        public string DisplayName;
        public string MapPath;
        public string SavePath;
        public string ScriptPath;
        public string BdfStem;
        /// <summary>The directory (within whichever reader resolved this
        /// candidate) that actually contains "{BdfStem}.terrain.win.bdf" —
        /// set by SC2ScdEntryPointResolver.FindAllScenarioCandidates. NOT
        /// always the same as MapPath's own directory prefix: a real game
        /// install's scenario.lua encodes paths relative to the archive
        /// ROOT (e.g. "maps/SC2_MP_001/..."), but a reader opened directly
        /// on one map's own already-extracted folder has entries relative
        /// to THAT folder instead (e.g. "SC2_MP_001.terrain.win.bdf", no
        /// prefix) — the resolver falls back to a filename-only search when
        /// the prefixed path isn't found, exactly like the older filesystem-
        /// based SC2ScdEntryPointResolver.Resolve() already did. Consumers
        /// (the import orchestrator) must use THIS field, not re-derive a
        /// directory from MapPath themselves.</summary>
        public string BdfDirectory;
        public bool CanImport;
        /// <summary>True when this candidate's own scenario.lua entry was
        /// compiled Lua bytecode (i.e. it came from lua.scd) rather than
        /// plain text (uncompiled_lua.scd). A real game install ships both
        /// for the same map; confirmed the uncompiled/text copy is the one
        /// authoritative for what the real game's own UI displays, so
        /// consumers that need to pick one of several duplicate candidates
        /// for the same map (e.g. a simple map picker) should prefer
        /// !IsCompiledBytecode over IsCompiledBytecode.</summary>
        public bool IsCompiledBytecode;
        /// <summary>Which archive (ISC2SourceContainerReader.SourcePath —
        /// e.g. the real .scd file/folder path) this candidate's own
        /// scenario.lua entry actually came from, resolved via
        /// SC2MultiSourceContainerReader.FindOwnerSourcePath when the
        /// reader is a composite spanning several archives (the normal
        /// case for a real game install), or the reader's own SourcePath
        /// for a single-archive reader. Lets a map picker show/trace which
        /// specific .scd a given map was found in — useful when several
        /// custom map packs are installed alongside vanilla content.</summary>
        public string SourceArchivePath;
        public List<SC2ValidationMessage> Messages = new();
    }
}
