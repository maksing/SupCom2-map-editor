using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Global undo/redo stack of
    /// ISC2DocumentCommand. Deliberately a plain two-stack history (no
    /// grouping/coalescing/scoped-undo yet) — that structure applies once real
    /// commands exist to group; building it now, with
    /// nothing to group, would just be speculative API surface.
    /// </summary>
    public sealed class SC2CommandHistory
    {
        private readonly Stack<ISC2DocumentCommand> _undoStack = new();
        private readonly Stack<ISC2DocumentCommand> _redoStack = new();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public void Execute(SC2MapDocument document, ISC2DocumentCommand command)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (command == null) throw new ArgumentNullException(nameof(command));

            var changeSet = command.Do(document);
            _undoStack.Push(command);
            _redoStack.Clear();
            document.RaiseChanged(changeSet);
        }

        public void Undo(SC2MapDocument document)
        {
            if (_undoStack.Count == 0) return;
            var command = _undoStack.Pop();
            var changeSet = command.Undo(document);
            _redoStack.Push(command);
            document.RaiseChanged(changeSet);
        }

        public void Redo(SC2MapDocument document)
        {
            if (_redoStack.Count == 0) return;
            var command = _redoStack.Pop();
            var changeSet = command.Do(document);
            _undoStack.Push(command);
            document.RaiseChanged(changeSet);
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}
