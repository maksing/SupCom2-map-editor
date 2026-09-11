using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Progress/status for a
    /// single long-running operation (import probe, export, smart-export
    /// pass). One at a time by design: this editor has no concurrent
    /// long-running operations today, so a single mutable slot is simpler
    /// than a queue and can grow into one later if that ever changes.
    /// </summary>
    public sealed class SC2OperationState
    {
        public bool IsRunning;
        public string OperationName = "";
        public float Progress01;
        public string StatusMessage = "";
        public readonly List<string> Warnings = new();
        public readonly List<string> Errors = new();

        public void Begin(string operationName)
        {
            IsRunning = true;
            OperationName = operationName;
            Progress01 = 0f;
            StatusMessage = "";
            Warnings.Clear();
            Errors.Clear();
        }

        public void Report(float progress01, string statusMessage)
        {
            Progress01 = progress01;
            StatusMessage = statusMessage ?? "";
        }

        public void Finish()
        {
            IsRunning = false;
            Progress01 = 1f;
        }

        /// <summary>Fully clears back to the "nothing has happened yet"
        /// state — distinct from Finish(), which marks a completed operation
        /// but deliberately keeps its final message/warnings/errors visible
        /// for the user to read.</summary>
        public void Reset()
        {
            IsRunning = false;
            OperationName = "";
            Progress01 = 0f;
            StatusMessage = "";
            Warnings.Clear();
            Errors.Clear();
        }
    }
}
