namespace SC2.MapEditor.Document
{
    /// <summary>
    /// One step in the manifest
    /// migration chain. Operates on the raw JSON tree (not the typed
    /// SC2MapDocument), on purpose: a migration exists precisely because the
    /// typed model has already moved on and can no longer represent the old
    /// shape, so it has to be reshaped as data first. SC2MapDocumentSerializer
    /// applies migrations in a FromVersion-ordered chain until the tree
    /// reaches SC2MapDocument.CurrentFormatVersion, then maps the result into
    /// the typed model exactly once.
    /// </summary>
    public interface ISC2DocumentMigration
    {
        int FromVersion { get; }
        void Apply(SC2JsonValue root);
    }
}
