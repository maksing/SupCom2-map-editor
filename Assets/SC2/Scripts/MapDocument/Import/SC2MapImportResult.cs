namespace SC2.MapEditor.Document
{
    /// <summary>The importer
    /// returns a document, not just a scene GameObject (contrast with the
    /// legacy SC2RuntimeScdImporter.Result.Root). Building a preview scene
    /// from this document remains a separate step, out of scope here.</summary>
    public sealed class SC2MapImportResult
    {
        public SC2MapDocument Document;
        public SC2ValidationReport Diagnostics;
    }
}
