using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Result of ProbeScdAsync.
    /// Deliberately does NOT hold an open ISC2SourceContainerReader: Import
    /// must re-open the source and re-check SourceFingerprint
    /// rather than trust a handle that's been sitting open while the user
    /// picks a scenario in a modal, so this only keeps SourcePath +
    /// ContainerKind (enough to re-open) plus the already-extracted
    /// candidate list.
    /// </summary>
    public sealed class SC2ContainerProbeResult
    {
        public string SourcePath;
        public SC2SourceContainerKind ContainerKind;
        public string SourceFingerprint;
        public IReadOnlyList<SC2ScenarioCandidate> ScenarioCandidates;
        public SC2ValidationReport Diagnostics;
    }
}
