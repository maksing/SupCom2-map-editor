using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Generalizes the "Legacy/Command marker stack"
    /// pattern that SC2TerrainCombinedUndo, SC2ObjectsShell, and SC2CostsShell
    /// each independently re-implemented (identical shape, three separate
    /// copies) to let one shell's Undo/Redo button walk backwards through
    /// BOTH a legacy full-snapshot stack AND SC2CommandHistory, in true
    /// chronological order, without either system needing to know the other
    /// exists.
    ///
    /// A "channel" is whichever underlying undo system owns one recorded
    /// step — a legacy snapshot stack, SC2CommandHistory, or (Terrain's RNM
    /// blend paint) a third opaque-data channel. The coordinator itself only
    /// remembers WHICH channel owns each step and in what order; it never
    /// touches the channels' own data.
    /// </summary>
    public interface ISC2UndoChannel
    {
        /// <summary>Undoes this channel's own most recent recorded step.
        /// Returns false (leaving all state unchanged) if the channel can't
        /// actually undo right now — e.g. SC2CommandHistory.CanUndo is
        /// false despite a Command marker being on top, which can happen if
        /// something external cleared history since this step was recorded.</summary>
        bool TryUndo();
        bool TryRedo();
    }

    /// <summary>Domain-agnostic marker-stack coordinator — same class used by
    /// every shell instead of each hand-copying its own Stack&lt;UndoSource&gt;.</summary>
    public sealed class SC2ShellUndoCoordinator
    {
        private readonly struct Marker
        {
            public readonly ISC2UndoChannel Channel;
            public readonly long Timestamp;
            public Marker(ISC2UndoChannel channel, long timestamp) { Channel = channel; Timestamp = timestamp; }
        }

        private readonly Stack<Marker> _undoMarkers = new();
        private readonly Stack<Marker> _redoMarkers = new();

        public bool CanUndo => _undoMarkers.Count > 0;
        public bool CanRedo => _redoMarkers.Count > 0;

        /// <summary>Step counts, for shells whose UI shows "Undo (N)" — see
        /// SC2ObjectsShell/SC2CostsShell. Not every shell needs this
        /// (Collision2/Scenario/Environment only show enabled/disabled).</summary>
        public int UndoCount => _undoMarkers.Count;
        public int RedoCount => _redoMarkers.Count;

        /// <summary>Monotonic timestamp (System.Diagnostics.
        /// Stopwatch.GetTimestamp(), immune to clock changes) of whatever is
        /// currently on top of the undo stack, default 0 meaning "nothing to
        /// undo". Lets the global Ctrl+Z handler compare every shell's most
        /// recent pending action and dispatch to whichever is actually
        /// newest. Fixed a bug where marker stacks sat on top of the shared
        /// history without staying in sync with it: this
        /// used to be a single field bumped only by Record() and never
        /// touched by Undo()/Redo() — after undoing this coordinator's own
        /// most recent step, it kept reporting that already-undone step's
        /// (now stale) timestamp instead of the NEW top of its stack, which
        /// could make a shell that had genuinely used up its "most recent"
        /// claim keep winning the global dispatch race anyway, skipping a
        /// chronologically-earlier action from another domain sitting on the
        /// shared SC2CommandHistory. Deriving this live from the stack's
        /// actual top makes it self-correcting after every Undo/Redo, for
        /// every shell built on this one coordinator (Terrain/Objects/Costs)
        /// at once.</summary>
        public long LastActionTimestamp => _undoMarkers.Count > 0 ? _undoMarkers.Peek().Timestamp : 0;

        /// <summary>Same fix, mirrored for Redo dispatch — but NOT simply
        /// "the redo stack's original recorded timestamp", since Redo must
        /// replay in exact reverse of whatever was just undone (most-
        /// recently-undone first), not in original-recording order. Undo()/
        /// Redo() below re-stamp each marker with a FRESH timestamp when it
        /// crosses from one stack to the other, so this always reflects "how
        /// recently was this specific step undone" rather than "how long ago
        /// was it first recorded" — see Undo()'s own comment for why that
        /// distinction matters for cross-domain redo ordering.</summary>
        public long LastRedoTimestamp => _redoMarkers.Count > 0 ? _redoMarkers.Peek().Timestamp : 0;

        /// <summary>Call AFTER the channel has already captured/applied
        /// whatever it needs to remember this step (e.g. after
        /// SC2TerrainUndoStack.RecordBeforeChange or SC2CommandHistory.
        /// Execute) — mirrors every existing shell's own "record, then push
        /// a marker" call order.</summary>
        public void Record(ISC2UndoChannel channel)
        {
            _undoMarkers.Push(new Marker(channel, System.Diagnostics.Stopwatch.GetTimestamp()));
            _redoMarkers.Clear();
        }

        public bool Undo()
        {
            if (_undoMarkers.Count == 0) return false;
            var marker = _undoMarkers.Pop();
            if (!marker.Channel.TryUndo())
            {
                _undoMarkers.Push(marker);
                return false;
            }
            // Re-stamped fresh (not marker.Timestamp) so LastRedoTimestamp
            // reflects "just undone now", not this step's original creation
            // time — required for correct redo ordering across domains that
            // were interleaved with steps recorded long before this one; see
            // LastRedoTimestamp's own doc comment.
            _redoMarkers.Push(new Marker(marker.Channel, System.Diagnostics.Stopwatch.GetTimestamp()));
            return true;
        }

        public bool Redo()
        {
            if (_redoMarkers.Count == 0) return false;
            var marker = _redoMarkers.Pop();
            if (!marker.Channel.TryRedo())
            {
                _redoMarkers.Push(marker);
                return false;
            }
            // Symmetric with Undo() above — re-stamped fresh so a further
            // Undo (after this Redo) correctly treats this step as the most
            // recent action again, not as if it still dated from whenever it
            // was originally recorded.
            _undoMarkers.Push(new Marker(marker.Channel, System.Diagnostics.Stopwatch.GetTimestamp()));
            return true;
        }

        public void Clear()
        {
            _undoMarkers.Clear();
            _redoMarkers.Clear();
        }
    }

    /// <summary>Thin stateless adapter — wraps whatever a call site closes
    /// over (a shell's own legacy snapshot system OR SC2CommandHistory.Undo/
    /// Redo against the current Session.Document) as a pair of Func&lt;bool&gt;
    /// delegates. One shared class instead of a separate typed channel per
    /// underlying system, since every existing call site already has
    /// everything it needs in scope to build the right closure inline — see
    /// SC2TerrainCombinedUndo/SC2ObjectsShell/SC2CostsShell for the concrete
    /// Legacy- and Command-sourced closures each one builds.</summary>
    public sealed class SC2DelegateUndoChannel : ISC2UndoChannel
    {
        private readonly Func<bool> _tryUndo;
        private readonly Func<bool> _tryRedo;

        public SC2DelegateUndoChannel(Func<bool> tryUndo, Func<bool> tryRedo)
        {
            _tryUndo = tryUndo;
            _tryRedo = tryRedo;
        }

        public bool TryUndo() => _tryUndo();
        public bool TryRedo() => _tryRedo();
    }
}
