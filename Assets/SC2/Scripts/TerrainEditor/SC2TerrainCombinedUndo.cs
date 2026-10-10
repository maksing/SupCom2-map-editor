using SC2.MapEditor.Document;

/// <summary>
/// Terrain has two independent, both-correct undo systems that grew up for
/// different reasons: SC2TerrainUndoStack (whole-state snapshots covering
/// Geometry/Paint/Materials/Pairs together, the ONLY thing that has ever
/// undone brush sculpting — see its own doc comment) and SC2CommandHistory
/// (the ISC2DocumentCommand stack, used by every other domain's UI
/// cutover so far). This class unifies both under one Undo/Redo button
/// rather than picking one.
///
/// This class used to hand-roll its own
/// Stack&lt;Source&gt; marker stack — the exact same shape SC2ObjectsShell and
/// SC2CostsShell each independently re-implemented. It's now a thin wrapper
/// over the shared SC2ShellUndoCoordinator, so the marker-stack logic itself
/// lives in exactly one place; this class's only remaining job is building
/// the three Terrain-specific channels (legacy snapshot, RNM blend opaque
/// data, command history) and keeping its own public API identical so
/// SC2TerrainShell's call sites need no changes at all.
/// </summary>
public sealed class SC2TerrainCombinedUndo
{
    private readonly SC2TerrainUndoStack _legacy;
    private readonly SC2CommandHistory _commands;
    private readonly SC2ShellUndoCoordinator _coordinator = new();

    // RNM blend paint (SC2PaintMode's "Blend" sub-mode) gets its own opaque
    // data channel, deliberately separate from SC2TerrainUndoStack.CaptureExtra/
    // ApplyExtra: that hook fires on EVERY checkpoint across the whole shell
    // (Symmetry, rename, sculpt, ...), and the active pair's BlendRT is a
    // 2048×2048 RGBA texture (~16MB) — capturing that on every single
    // checkpoint, not just RNM strokes, would be real, unacceptable per-action
    // cost. This channel only captures/restores when an RnmBlend step
    // specifically is being recorded/undone/redone.
    private readonly System.Collections.Generic.Stack<object> _rnmUndoData = new();
    private readonly System.Collections.Generic.Stack<object> _rnmRedoData = new();

    /// <summary>Captures the currently-active blend pair's state as an
    /// opaque object (see SC2PaintMode.CaptureActiveBlendState) — set by
    /// SC2TerrainShell alongside SC2TerrainUndoStack.CaptureExtra/ApplyExtra.
    /// Either may be left null (RNM blend undo becomes unavailable, same
    /// degrade-not-throw pattern as ExecuteCommand with no Session).</summary>
    public System.Func<object> CaptureRnmBlend;
    public System.Action<object> ApplyRnmBlend;

    /// <param name="commands">May be null (e.g. no SC2MapSession wired yet) —
    /// ExecuteCommand becomes a no-op and only the legacy stack is used, so
    /// this degrades to today's behavior rather than throwing.</param>
    public SC2TerrainCombinedUndo(SC2TerrainUndoStack legacy, SC2CommandHistory commands)
    {
        _legacy = legacy;
        _commands = commands;
    }

    public bool CanUndo => _coordinator.CanUndo;
    public bool CanRedo => _coordinator.CanRedo;

    /// <summary>See SC2ShellUndoCoordinator.LastActionTimestamp.</summary>
    public long LastActionTimestamp => _coordinator.LastActionTimestamp;

    /// <summary>See SC2ShellUndoCoordinator.LastRedoTimestamp.</summary>
    public long LastRedoTimestamp => _coordinator.LastRedoTimestamp;

    /// <summary>Call BEFORE mutating SC2TerrainData directly — mirrors
    /// SC2TerrainUndoStack.RecordBeforeChange's own calling convention, so
    /// every existing SC2TerrainShell.RecordUndoCheckpoint call site needs
    /// no changes at all.</summary>
    public void RecordLegacyCheckpoint(SC2TerrainData data, string label)
    {
        _legacy.RecordBeforeChange(data, label);
        _coordinator.Record(new SC2DelegateUndoChannel(
            tryUndo: () => _legacy.Undo(data),
            tryRedo: () => _legacy.Redo(data)));
    }

    /// <summary>Call BEFORE painting an RNM blend stroke — mirrors
    /// RecordLegacyCheckpoint's "record before, not after" convention. A
    /// no-op if CaptureRnmBlend was never wired.</summary>
    public void RecordRnmBlendCheckpoint()
    {
        if (CaptureRnmBlend == null)
            return;

        _rnmUndoData.Push(CaptureRnmBlend());
        if (_rnmUndoData.Count > SC2ShellUndoCoordinator.MaxSteps)
        {
            // Every step keeps a copy of the mask state, so the oldest one has to go when the step limit is passed.
            var newestFirst = _rnmUndoData.ToArray();
            _rnmUndoData.Clear();
            for (var i = SC2ShellUndoCoordinator.MaxSteps - 1; i >= 0; i--) _rnmUndoData.Push(newestFirst[i]);
        }
        _rnmRedoData.Clear();
        _coordinator.Record(new SC2DelegateUndoChannel(
            tryUndo: () =>
            {
                if (CaptureRnmBlend == null || ApplyRnmBlend == null) return false;
                _rnmRedoData.Push(CaptureRnmBlend());
                ApplyRnmBlend(_rnmUndoData.Pop());
                return true;
            },
            tryRedo: () =>
            {
                if (CaptureRnmBlend == null || ApplyRnmBlend == null) return false;
                _rnmUndoData.Push(CaptureRnmBlend());
                ApplyRnmBlend(_rnmRedoData.Pop());
                return true;
            }));
    }

    /// <summary>Executes a real ISC2DocumentCommand against the session's
    /// document and records it as the newest combined-undo step. Returns
    /// false (does nothing) if no SC2CommandHistory was wired in.</summary>
    public bool ExecuteCommand(SC2MapDocument document, ISC2DocumentCommand command)
    {
        if (_commands == null)
            return false;

        _commands.Execute(document, command);
        _coordinator.Record(new SC2DelegateUndoChannel(
            tryUndo: () => { if (_commands == null || !_commands.CanUndo) return false; _commands.Undo(document); return true; },
            tryRedo: () => { if (_commands == null || !_commands.CanRedo) return false; _commands.Redo(document); return true; }));
        return true;
    }

    /// <param name="syncFromDocument">Invoked after a command-sourced Undo so
    /// the caller can copy the document's new field values back onto the
    /// live SC2TerrainData objects the GUI reads directly (Materials/Pairs
    /// have no working-copy of their own — see SC2MaterialsPairsMode). Kept
    /// as a parameter here (rather than folded into ExecuteCommand's channel
    /// closure) since Undo/Redo are the only two places that ever need it —
    /// ExecuteCommand itself never calls it.</param>
    public bool Undo(SC2TerrainData data, SC2MapDocument document, System.Action syncFromDocument)
    {
        if (!_coordinator.Undo()) return false;
        syncFromDocument?.Invoke();
        return true;
    }

    public bool Redo(SC2TerrainData data, SC2MapDocument document, System.Action syncFromDocument)
    {
        if (!_coordinator.Redo()) return false;
        syncFromDocument?.Invoke();
        return true;
    }

    /// <summary>Mirrors SC2TerrainUndoStack.Reset's own call site
    /// (SC2TerrainShell.SetData) — clears both underlying stacks and the
    /// combined marker order together, since a new SC2TerrainData means
    /// neither stack's recorded snapshots/commands are meaningful anymore.</summary>
    public void Reset()
    {
        _coordinator.Clear();
        _rnmUndoData.Clear();
        _rnmRedoData.Clear();
        _legacy.Reset();
        _commands?.Clear();
    }
}
