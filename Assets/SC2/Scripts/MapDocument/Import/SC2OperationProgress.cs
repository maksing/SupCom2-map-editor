namespace SC2.MapEditor.Document
{
    /// <summary>Progress payload reported through IProgress&lt;T&gt; during
    /// Probe/Import — distinct from Session/SC2OperationState, which is the
    /// UI-facing mutable slot that observes these reports; this is
    /// just the data shape crossing the interface boundary.</summary>
    public sealed class SC2OperationProgress
    {
        public string Stage;
        public float Progress01;
        public string Message;
    }
}
