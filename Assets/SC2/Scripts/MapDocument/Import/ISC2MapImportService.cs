using System;
using System.Threading;
using System.Threading.Tasks;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The shared Editor/Build
    /// import contract. Two-phase by design: ProbeScdAsync never
    /// imports anything by itself, so an "import the first scenario found"
    /// shortcut is impossible to reach through this API — the caller MUST
    /// have a probe result and an explicit selectedScenarioEntryPath in hand
    /// before ImportSelectedScenarioAsync will do anything.
    /// </summary>
    public interface ISC2MapImportService
    {
        Task<SC2ContainerProbeResult> ProbeScdAsync(
            string scdFileOrDirectoryPath,
            IProgress<SC2OperationProgress> progress,
            CancellationToken cancellationToken);

        Task<SC2MapImportResult> ImportSelectedScenarioAsync(
            SC2ContainerProbeResult probe,
            string selectedScenarioEntryPath,
            SC2ImportOptions options,
            IProgress<SC2OperationProgress> progress,
            CancellationToken cancellationToken);
    }
}
