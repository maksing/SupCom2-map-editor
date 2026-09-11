#nullable disable
namespace SC2.MapEditor.SmartExport
{
    public enum SC2ExportFileMode
    {
        OriginalPassthrough = 0,
        Patched,
        Regenerated,
        Missing,
        SkippedBecauseUnchanged,
        BlockedByValidationError
    }
}

