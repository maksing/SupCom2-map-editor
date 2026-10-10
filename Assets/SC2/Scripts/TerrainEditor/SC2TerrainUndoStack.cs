using System;
using System.Collections.Generic;

/// <summary>
/// Snapshot-based Undo/Redo for SC2TerrainData, independent of UnityEditor.Undo.
///
/// Why this exists: UnityEditor.Undo does not exist outside the Editor — the
/// UnityEditorRuntimeShim's "Undo" class (used when compiling for standalone
/// Build &amp; Run) is a set of empty no-op methods, since the real Undo system
/// is Editor-only. Every `Undo.RecordObject(...)` call scattered across the
/// terrain editor therefore silently did nothing in a build: Ctrl+Z had zero
/// effect and there was no way to recover from a bad brush stroke or an
/// "Assign ALL to this pair" / "Clear All" mistake. On top of that, brush
/// sculpting (Raise/Lower/Smooth/Flatten/SetHeight/Noise) never called
/// Undo.RecordObject at all, even in the Editor — so height sculpting was not
/// undoable there either.
///
/// This stack captures full copies of the fields that actually define the
/// terrain (heights, per-triangle pair assignment, materials, pair metadata)
/// and works identically in the Editor and in a build. Grid resolution is
/// fixed at a constant triangle budget regardless of map size (see
/// SC2TerrainShell.kTargetTriCount, 524_288 tris/
/// 512x512 quads), so Heights (~1.0MB, 513x513 floats) and
/// TrianglePairIndex (~1.0MB, 524_288 shorts) snapshots are still small
/// enough that plain full copies are fine — no diffing needed, and the full
/// MaxDepth-step history stays modest (a few hundred MB at the 100-step limit).
///
/// Scope limitation (documented, not accidental): RNM blend-mask bytes are
/// intentionally NOT captured. A single pair's RNM mask can be a 2048x2048
/// RGBA32 texture (~16MB), and painting happens directly on a GPU
/// RenderTexture — undoing that would need per-tile GPU diffing, which is a
/// separate feature. RNM blend painting therefore remains outside this undo
/// stack, same as pair "Assign" brush painting's GPU-side texture (its CPU
/// mirror, TrianglePairIndex, *is* covered).
/// </summary>
public sealed class SC2TerrainUndoStack
{
    private const int MaxDepth = SC2.MapEditor.Document.SC2ShellUndoCoordinator.MaxSteps;

    private sealed class Snapshot
    {
        public string Label;
        public bool IsInitialized;
        public int GridWidth, GridHeight;
        public float CellSize;
        public float[] Heights;
        public short[] TrianglePairIndex;
        public List<SC2Material> Materials;
        public List<SC2MaterialPair> Pairs;
        // Confirmed real via a dedicated MapDocument save/restore + Undo/
        // Redo audit: the Water tab, ImportOnlyMode, and PlayableAreaName
        // all call RecordUndoCheckpoint (via the same shell methods every
        // other field here uses), but none of these fields were captured —
        // a real "false Undo step": the history counter grows, but the
        // value silently doesn't change back. Added the specific fields the
        // audit named as reachable via a real UI control with this exact
        // symptom; this snapshot is still a deliberately hand-picked subset
        // of SC2TerrainData (see the class doc comment), not a full-field
        // clone — a genuinely comprehensive audit of every editable field
        // is its own separate pass.
        public bool ImportOnlyMode;
        public int WaterPlaneSize;
        public float WaterUvScale;
        public string[] WaterTexturePaths;
        public bool WaterDepthAutoGenerate;
        public int WaterDepthResolution;
        public float WaterDepthMargin;
        public float WaterDepthFalloffPower;
        public float WaterDepthMinAlpha;
        public float WaterDepthLandBleedTexels;
        public bool WaterDepthPaintBorder;
        public bool WaterDepthImageBorderOutline;
        public string PlayableAreaName;
        // Same audit, same false-Undo-step symptom: these are also reachable
        // via a real UI control (Terrain BSP options, Collision2's custom-mesh
        // toggle, Costs' playable-water toggle and grid-size picker) whose
        // checkpoints didn't actually restore them.
        public bool SingleBspNodePerLayer;
        public bool VanillaCandidateBspSplit;
        public List<string> Collision2ExcludedMaterialNames;
        public bool PlayableWater;
        public bool UseCustomCollision2;
        public byte[] CustomCollision2Bytes;
        public int CostsGridSize;
        // Background terrain: the nested BackgroundTerrainData
        // instance itself is fully regenerated deterministically from
        // these three scalars + the live terrain (SC2BackgroundTerrain.
        // Generate), so it's never itself snapshotted — only the one thing
        // regeneration can't recover, the background's own single pair's
        // user-tuned SimplifyRatio, is captured/restored alongside them.
        public bool BackgroundTerrainEnabled;
        public int BackgroundTerrainSize;
        public float BackgroundTerrainUvScale;
        public float BackgroundSimplifyRatio;
        public bool GlobalUvScaleEnabled;
        public float GlobalUvScale;
        public float Uv1HeightWeight;
        public object Extra;
    }

    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();
    private SC2TerrainData _trackedData;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string NextUndoLabel => _undo.Count > 0 ? _undo.Peek().Label : null;
    public string NextRedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>Renames the newest undo entry - for an edit that turns out to belong to a joint step with another stack
    /// (see SC2TerrainShell.SeamStrokeLabel) after its checkpoint was already recorded.</summary>
    public void RelabelNextUndo(string label)
    {
        if (_undo.Count > 0) _undo.Peek().Label = label;
    }

    /// <summary>Same Stopwatch.GetTimestamp()-based monotonic clock
    /// SC2ShellUndoCoordinator's own LastActionTimestamp/LastRedoTimestamp
    /// use, so SC2TerrainShell can compare "most recent action" against
    /// this stack too. Without this, the global Ctrl+Z dispatcher
    /// (SC2GameViewEditorUI.UndoTerrainEdit) had no way to know this stack
    /// held anything at all — SC2TerrainShell.CanUndo only ever checked
    /// CombinedUndo, so a session that only ever edited the background
    /// (never the core) reported "Nothing to undo" despite this stack
    /// genuinely having a real entry waiting.</summary>
    public long LastActionTimestamp { get; private set; }
    public long LastRedoTimestamp { get; private set; }

    /// <summary>Optional hook for state that lives outside SC2TerrainData but
    /// should still undo/redo in lockstep with a checkpoint — Symmetry,
    /// active preview mode, and active sculpt tool + brush parameters all
    /// live on SC2TerrainShell/SC2GeometryMode, not SC2TerrainData (see
    /// SC2TerrainShell.CaptureAuxState/ApplyAuxState). Captured/restored as
    /// an opaque object so this class doesn't need to know its shape;
    /// either or both may be left null (no auxiliary state tracked).</summary>
    public Func<object> CaptureExtra;
    public Action<object> ApplyExtra;

    /// <summary>Call BEFORE mutating data, to push the pre-edit state as the undo target.</summary>
    public void RecordBeforeChange(SC2TerrainData data, string label)
    {
        if (data == null)
            return;

        if (_trackedData != data)
        {
            _undo.Clear();
            _redo.Clear();
            _trackedData = data;
        }

        _undo.Push(Capture(data, label));
        LastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        TrimToMaxDepth(_undo);
        _redo.Clear();
    }

    public bool Undo(SC2TerrainData data)
    {
        if (data == null || _trackedData != data || _undo.Count == 0)
            return false;

        _redo.Push(Capture(data, _undo.Peek().Label));
        LastRedoTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var snapshot = _undo.Pop();
        Apply(data, snapshot);
        ApplyExtra?.Invoke(snapshot.Extra);
        return true;
    }

    public bool Redo(SC2TerrainData data)
    {
        if (data == null || _trackedData != data || _redo.Count == 0)
            return false;

        _undo.Push(Capture(data, _redo.Peek().Label));
        LastActionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var snapshot = _redo.Pop();
        Apply(data, snapshot);
        ApplyExtra?.Invoke(snapshot.Extra);
        return true;
    }

    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
        _trackedData = null;
    }

    private Snapshot Capture(SC2TerrainData data, string label)
    {
        var materials = new List<SC2Material>(data.Materials?.Count ?? 0);
        if (data.Materials != null)
            foreach (var m in data.Materials)
                materials.Add(new SC2Material
                {
                    Name = m.Name,
                    Color = m.Color,
                    Diffuse = m.Diffuse,
                    Normal = m.Normal,
                    Mask = m.Mask
                });

        var pairs = new List<SC2MaterialPair>(data.Pairs?.Count ?? 0);
        if (data.Pairs != null)
            foreach (var p in data.Pairs)
                pairs.Add(new SC2MaterialPair
                {
                    Name = p.Name,
                    Mat1Index = p.Mat1Index,
                    Mat2Index = p.Mat2Index,
                    PairColor = p.PairColor,
                    RnmMaskName = p.RnmMaskName,
                    ShaderFlags = CloneFlags(p.ShaderFlags),
                    // RNM mask bytes/texture intentionally shared by reference, not
                    // snapshotted — see the class-level doc comment.
                    RnmMaskBytes = p.RnmMaskBytes,
                    RnmMaskTexture = p.RnmMaskTexture,
                    MaskWidth = p.MaskWidth,
                    MaskHeight = p.MaskHeight,
                    TilingScale = p.TilingScale,
                    SimplifyRatio = p.SimplifyRatio,
                    State = p.State,
                    TextureScroll = p.TextureScroll,
                    AlphaTestThreshold = p.AlphaTestThreshold,
                    ShaderFile = p.ShaderFile,
                    ShaderPrefix = p.ShaderPrefix,
                    RampPath = p.RampPath,
                    RampSourceTag = p.RampSourceTag,
                    RampForceExport = p.RampForceExport,
                    EnvironmentPath = p.EnvironmentPath,
                    EnvironmentSourceTag = p.EnvironmentSourceTag,
                    EnvironmentForceExport = p.EnvironmentForceExport,
                    // This clone list predated
                    // RNM lighting bake and was never extended
                    // for it — any Undo/Redo (Apply replaces data.Pairs
                    // wholesale with a Capture()'d snapshot) silently wiped
                    // every pair's baked lighting back to empty, which
                    // ExportRnmMaskByName then reads as "never baked" and
                    // falls back to the neutral/opaque placeholder. Shared by
                    // reference, not deep-cloned, same as RnmMaskBytes/
                    // RnmMaskTexture above — see this class's own doc comment.
                    RnmBakedRgb0 = p.RnmBakedRgb0,
                    RnmBakedRgb1 = p.RnmBakedRgb1,
                    RnmBakedRgb2 = p.RnmBakedRgb2,
                    RnmBakedShadowAlpha = p.RnmBakedShadowAlpha
                });

        return new Snapshot
        {
            Label = label,
            IsInitialized = data.IsInitialized,
            GridWidth = data.GridWidth,
            GridHeight = data.GridHeight,
            CellSize = data.CellSize,
            Heights = data.Heights != null ? (float[])data.Heights.Clone() : System.Array.Empty<float>(),
            TrianglePairIndex = data.TrianglePairIndex != null ? (short[])data.TrianglePairIndex.Clone() : System.Array.Empty<short>(),
            Materials = materials,
            Pairs = pairs,
            ImportOnlyMode = data.ImportOnlyMode,
            WaterPlaneSize = data.WaterPlaneSize,
            WaterUvScale = data.WaterUvScale,
            WaterTexturePaths = data.WaterTexturePaths != null ? (string[])data.WaterTexturePaths.Clone() : System.Array.Empty<string>(),
            WaterDepthAutoGenerate = data.WaterDepthAutoGenerate,
            WaterDepthResolution = data.WaterDepthResolution,
            WaterDepthMargin = data.WaterDepthMargin,
            WaterDepthFalloffPower = data.WaterDepthFalloffPower,
            WaterDepthMinAlpha = data.WaterDepthMinAlpha,
            WaterDepthLandBleedTexels = data.WaterDepthLandBleedTexels,
            WaterDepthPaintBorder = data.WaterDepthPaintBorder,
            WaterDepthImageBorderOutline = data.WaterDepthImageBorderOutline,
            PlayableAreaName = data.PlayableAreaName,
            SingleBspNodePerLayer = data.SingleBspNodePerLayer,
            VanillaCandidateBspSplit = data.VanillaCandidateBspSplit,
            Collision2ExcludedMaterialNames = data.Collision2ExcludedMaterialNames != null ? new List<string>(data.Collision2ExcludedMaterialNames) : new List<string>(),
            PlayableWater = data.PlayableWater,
            UseCustomCollision2 = data.UseCustomCollision2,
            CustomCollision2Bytes = data.CustomCollision2Bytes != null ? (byte[])data.CustomCollision2Bytes.Clone() : null,
            CostsGridSize = data.CostsGridSize,
            BackgroundTerrainEnabled = data.BackgroundTerrainEnabled,
            BackgroundTerrainSize = data.BackgroundTerrainSize,
            BackgroundTerrainUvScale = data.BackgroundTerrainUvScale,
            BackgroundSimplifyRatio = data.BackgroundTerrainData != null && data.BackgroundTerrainData.Pairs.Count > 0
                ? data.BackgroundTerrainData.Pairs[0].SimplifyRatio : 0f,
            GlobalUvScaleEnabled = data.GlobalUvScaleEnabled,
            GlobalUvScale = data.GlobalUvScale,
            Uv1HeightWeight = data.Uv1HeightWeight,
            Extra = CaptureExtra?.Invoke()
        };
    }

    private static List<SC2ShaderFlag> CloneFlags(List<SC2ShaderFlag> src)
    {
        var list = new List<SC2ShaderFlag>(src?.Count ?? 0);
        if (src != null)
            foreach (var f in src)
                list.Add(new SC2ShaderFlag { Key = f.Key, Value = f.Value });
        return list;
    }

    private static void Apply(SC2TerrainData data, Snapshot snapshot)
    {
        data.IsInitialized = snapshot.IsInitialized;
        data.GridWidth = snapshot.GridWidth;
        data.GridHeight = snapshot.GridHeight;
        data.CellSize = snapshot.CellSize;
        data.Heights = (float[])snapshot.Heights.Clone();
        data.TrianglePairIndex = (short[])snapshot.TrianglePairIndex.Clone();
        data.Materials = snapshot.Materials;
        data.Pairs = snapshot.Pairs;
        data.ImportOnlyMode = snapshot.ImportOnlyMode;
        data.WaterPlaneSize = snapshot.WaterPlaneSize;
        data.WaterUvScale = snapshot.WaterUvScale;
        data.WaterTexturePaths = (string[])snapshot.WaterTexturePaths.Clone();
        data.WaterDepthAutoGenerate = snapshot.WaterDepthAutoGenerate;
        data.WaterDepthResolution = snapshot.WaterDepthResolution;
        data.WaterDepthMargin = snapshot.WaterDepthMargin;
        data.WaterDepthFalloffPower = snapshot.WaterDepthFalloffPower;
        data.WaterDepthMinAlpha = snapshot.WaterDepthMinAlpha;
        data.WaterDepthLandBleedTexels = snapshot.WaterDepthLandBleedTexels;
        data.WaterDepthPaintBorder = snapshot.WaterDepthPaintBorder;
        data.WaterDepthImageBorderOutline = snapshot.WaterDepthImageBorderOutline;
        data.PlayableAreaName = snapshot.PlayableAreaName;
        data.SingleBspNodePerLayer = snapshot.SingleBspNodePerLayer;
        data.VanillaCandidateBspSplit = snapshot.VanillaCandidateBspSplit;
        data.Collision2ExcludedMaterialNames = new List<string>(snapshot.Collision2ExcludedMaterialNames);
        data.PlayableWater = snapshot.PlayableWater;
        data.UseCustomCollision2 = snapshot.UseCustomCollision2;
        data.CustomCollision2Bytes = snapshot.CustomCollision2Bytes != null ? (byte[])snapshot.CustomCollision2Bytes.Clone() : null;
        data.CostsGridSize = snapshot.CostsGridSize;
        data.BackgroundTerrainEnabled = snapshot.BackgroundTerrainEnabled;
        data.BackgroundTerrainSize = snapshot.BackgroundTerrainSize;
        data.BackgroundTerrainUvScale = snapshot.BackgroundTerrainUvScale;
        if (data.BackgroundTerrainData != null && data.BackgroundTerrainData.Pairs.Count > 0)
            data.BackgroundTerrainData.Pairs[0].SimplifyRatio = snapshot.BackgroundSimplifyRatio;
        data.GlobalUvScaleEnabled = snapshot.GlobalUvScaleEnabled;
        data.GlobalUvScale = snapshot.GlobalUvScale;
        data.Uv1HeightWeight = snapshot.Uv1HeightWeight;
        SC2BackgroundRuntimeSync.MarkDirty();
        foreach (var p in data.Pairs)
            p?.InvalidateTriCount();
        data.InvalidateGeometryCache();
    }

    private static void TrimToMaxDepth(Stack<Snapshot> stack)
    {
        if (stack.Count <= MaxDepth)
            return;

        // Stack<T> has no RemoveOldest; rebuild without the bottom (oldest) entry.
        var items = stack.ToArray(); // index 0 = top (newest)
        stack.Clear();
        for (var i = items.Length - 2; i >= 0; i--)
            stack.Push(items[i]);
    }
}
