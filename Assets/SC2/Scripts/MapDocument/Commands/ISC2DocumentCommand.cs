namespace SC2.MapEditor.Document
{
    /// <summary>
    /// One undoable edit to a
    /// SC2MapDocument. Do/Undo each return the SC2DocumentChangeSet describing
    /// that direction's transition, so SC2CommandHistory can hand it straight
    /// to SC2MapDocument.RaiseChanged without the command needing to know
    /// about revision numbers itself.
    /// </summary>
    public interface ISC2DocumentCommand
    {
        string Name { get; }
        SC2DocumentChangeSet Do(SC2MapDocument document);
        SC2DocumentChangeSet Undo(SC2MapDocument document);
    }
}
