// SC2CostsTerrainTypesEditor.cs
//
// Integrated Costs + TerrainTypes painter for the unified SC2 Map Editor
// window's "Costs" tab (SC2MapEditorWindow.DrawCostsPanel). Replaces the
// previous stub (bridge button to the standalone CostsPainterWindow + two
// Debug.Log placeholders) with a real implementation, reusing every already-
// verified algorithm from CostsPainterWindow (brush paint, undo/redo,
// auto-gen bridge, island computation) instead of re-deriving them, and adds
// the two things the roadmap's "Costs + terrainTypes" item still needed:
//
//  - a TerrainTypes painter (paints BdfIO.Formats.Hfield.HfieldModel.
//    TerrainTypes, which previously had no paint UI anywhere in the project);
//  - one shared overlay switcher between "Costs block N" and "TerrainTypes"
//    views, instead of two separate rendering paths — both feed the exact
//    same SC2/CostsOverlay material via a freshly-built Color[] texture, only
//    the source of that Color[] differs.
//
// Global symmetry: CostsPainterWindow had its own private SC2SymmetryMode/
// Vector2 fields, disconnected from every other symmetry-aware tool in the
// project. This reads/writes the SAME SC2SymmetrySettings component other
// tools use (SC2Symmetry.cs's own doc comment already says costs/terrainTypes
// painting is SUPPOSED to share it) — found on the map root the unified
// window already tracks in SC2EditorContext.MapRoot, matching how markers/
// props symmetry (SymmetryToolWindow) locates it.
//
// Both costs.bytes and hfield.bytes are written on every Save() regardless
// of which target (Costs vs TerrainTypes) was actually edited — TerrainTypes
// physically lives inside hfield.bdf alongside Heightmap, and Heightmap here
// is passed through unchanged (only TerrainTypes is replaced), so writing
// both files unconditionally is simple and safe rather than tracking which
// one is "actually dirty".
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Costs;

public sealed class SC2CostsTerrainTypesEditor
{
    public enum PaintTarget { Costs, TerrainTypes }
    private enum CostsPaintMode { Impassable, Passable }

    private static readonly string[] BlockNames = { "Land_Small", "Land_Medium", "Land_Large", "Water_Small", "Water_Large" };

    // A palette id -> (label, color) preset list for TerrainTypes. Verified
    // against a real vanilla hfield.bdf — the only two values that actually
    // occur are 2 ("Default") and 3 ("Chasm_Bottom"); everything else is
    // unused. "Default" carries alpha 0 so the overlay shows the terrain's
    // own natural appearance with no tint there — only Chasm_Bottom gets a
    // visible red tint, matching how costs marks impassable cells.
    public sealed class TerrainTypePreset
    {
        public ushort Id;
        public string Label;
        public Color Color;
    }

    private List<TerrainTypePreset> _terrainTypePresets = BuildDefaultPresets();
    private int _activeTerrainTypePreset;

    private static List<TerrainTypePreset> BuildDefaultPresets() => new()
    {
        new TerrainTypePreset { Id = 2, Label = "Default", Color = new Color(0.5f, 0.5f, 0.5f, 0f) },
        new TerrainTypePreset { Id = 3, Label = "Chasm_Bottom", Color = new Color(1f, 0.1f, 0.1f, 0.55f) },
    };

    // ---- Loaded state ----
    private SC2MapRawData _data;
    private CostsModel _costsModel;
    private HfieldModel _hfield;
    private int _gridW, _gridH;
    private MeshCollider _terrainCollider;

    public bool IsLoaded => _costsModel != null && _hfield != null;

    // ---- Shared paint state ----
    public PaintTarget ActiveTarget = PaintTarget.Costs;
    private bool _painting;
    private int _brushRadius = 5;
    private Vector2? _lastPaintPos;

    // Costs-specific
    private CostsPaintMode _costsPaintMode = CostsPaintMode.Impassable;
    private bool _allBlocks = true;
    private int _activeBlock;

    // ---- Overlay (shared between both targets — see class doc comment) ----
    private Texture2D _overlayTex;
    private Color[] _overlayColors;
    private bool _overlayDirty;
    private Material _overlayMat;

    // ---- Undo/redo (snapshots whichever target is active at the time) ----
    private sealed class Snapshot
    {
        public PaintTarget Target;
        public byte[][] CostsNavmesh;
        public ushort[] TerrainTypes;
    }
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();
    private const int MaxUndoSteps = 30;

    public int UndoCount => _undoStack.Count;
    public int RedoCount => _redoStack.Count;

    /// <summary>When true, Save() also dumps PNGs of exactly what got written
    /// into costs.bdf (per-block navmesh + islandmap, plus the TerrainTypes
    /// grid) next to costs.bytes, for visual verification — see
    /// SC2CostsDebugImageWriter.</summary>
    public bool DebugSaveCostsImages;

    // ==================================================================
    // Load / Save
    // ==================================================================

    public void Load(SC2MapRawData data)
    {
        _data = data;
        if (_data == null) return;

        var costsC = new BdfContainer { Platform = _data.Platform, Data = _data.GetCostsData(), OffsetTable = _data.GetCostsOt(), NumFiles = 1 };
        _costsModel = _data.GetCostsData().Length > 0 ? CostsBdf.Parse(costsC) : new CostsModel();

        var hfC = new BdfContainer { Platform = _data.Platform, Data = _data.GetHfieldData(), OffsetTable = _data.GetHfieldOt(), NumFiles = 1 };
        // TerrainTypesElementSize: HfieldBdf.Parse auto-detects it from the
        // real file's byte layout when one already exists (preserves
        // whatever the original map used). A from-scratch hfield (no bytes
        // yet) explicitly requests 1 (8-bit) to match the verified vanilla
        // format — HfieldBdf.Serialize still double-checks every value fits
        // a byte before honoring this.
        _hfield = _data.GetHfieldData().Length > 0
            ? HfieldBdf.Parse(hfC)
            : new HfieldModel { MapX = _data.HfieldMapX, MapY = _data.HfieldMapY, TerrainTypesElementSize = 1 };

        _gridW = (int)_hfield.MapX - 1;
        _gridH = (int)_hfield.MapY - 1;
        if (_gridW <= 0 || _gridH <= 0) { _gridW = (int)_data.HfieldMapX - 1; _gridH = (int)_data.HfieldMapY - 1; }

        // Passthrough-safe defaults: a from-scratch/empty costs.bdf gets 5
        // blocks matching BlockNames, all-passable, so painting has somewhere
        // to write immediately instead of throwing on an empty Blocks list.
        while (_costsModel.Blocks.Count < BlockNames.Length)
        {
            var nav = new byte[Mathf.Max(1, _gridW * _gridH)];
            for (var i = 0; i < nav.Length; i++) nav[i] = 1;
            // Islandmap is 1 byte/cell (8-bit index, per CostsBdf.cs's
            // verified format), NOT 4 — this used to allocate 4x, a leftover
            // from before SC2IslandMap.Compute was fixed to return the
            // correct 8-bit format; RecomputeAllIslands() overwrites this
            // placeholder anyway, but the size should still be right from
            // the start (it briefly IS the real Islandmap array if Save()
            // is somehow called before any paint stroke recomputes islands).
            _costsModel.Blocks.Add(new CostsBlock { BlockIndex = (uint)_costsModel.Blocks.Count, Navmesh = nav, Islandmap = new byte[nav.Length], LongInts = System.Array.Empty<uint>() });
        }
        if (_hfield.TerrainTypes == null || _hfield.TerrainTypes.Length != _gridW * _gridH)
        {
            // 0 isn't a real/known TerrainTypes id — default to
            // SC2TerrainData.DefaultTerrainTypeId ("Default", verified
            // vanilla value 2).
            _hfield.TerrainTypes = new ushort[Mathf.Max(1, _gridW * _gridH)];
            for (var i = 0; i < _hfield.TerrainTypes.Length; i++) _hfield.TerrainTypes[i] = SC2TerrainData.DefaultTerrainTypeId;
        }

        var terrainGo = FindTerrainGo();
        if (terrainGo != null) AddTerrainCollider(terrainGo);

        RebuildOverlayTexture();
        SC2Log.Log($"[SC2CostsTerrainTypes] Loaded: {_costsModel.Blocks.Count} costs block(s), grid {_gridW}x{_gridH}.");
    }

    public void Save()
    {
        if (_data == null || _costsModel == null || _hfield == null) return;

        RecomputeAllIslands();

        var (costsData, costsOt) = CostsBdf.Serialize(_costsModel, _data.Platform);
        var (hfieldData, hfieldOt) = HfieldBdf.Serialize(_hfield, _data.Platform);

        var assetDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_data));
        WriteBytesPair(assetDir, "costs.bytes", "costs_ot.bytes", costsData, costsOt);
        WriteBytesPair(assetDir, "hfield.bytes", "hfield_ot.bytes", hfieldData, hfieldOt);

        if (DebugSaveCostsImages)
            SaveDebugImages(assetDir);

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        SC2Log.Log($"[SC2CostsTerrainTypes] Saved costs.bytes ({_costsModel.Blocks.Count} blocks) and hfield.bytes (TerrainTypes updated, Heightmap passthrough).");
    }

    /// <summary>Dumps exactly what was just serialized above as PNGs — navmesh
    /// (grayscale, raw 1/5/255 value as intensity), islandmap (grayscale, raw
    /// 8-bit island index 0..254), and TerrainTypes (grayscale, raw id value
    /// e.g. 2/3) — all as the literal bytes that go into costs.bdf/hfield.bdf,
    /// into a "CostsDebug" subfolder next to costs.bytes.</summary>
    private void SaveDebugImages(string assetDir)
    {
        var outputDir = System.IO.Path.Combine(assetDir, "CostsDebug");
        var navBlocks = new System.Collections.Generic.List<(string, byte[])>();
        var islandBlocks = new System.Collections.Generic.List<(string, byte[])>();
        for (var i = 0; i < _costsModel.Blocks.Count; i++)
        {
            var name = i < BlockNames.Length ? BlockNames[i] : $"Block_{i}";
            navBlocks.Add((name, _costsModel.Blocks[i].Navmesh));
            islandBlocks.Add((name, _costsModel.Blocks[i].Islandmap));
        }
        SC2CostsDebugImageWriter.WriteNavmeshPngs(navBlocks, _gridW, _gridH, outputDir);
        SC2CostsDebugImageWriter.WriteIslandmapPngs(islandBlocks, _gridW, _gridH, outputDir);
        SC2CostsDebugImageWriter.WriteTerrainTypesPng(_hfield.TerrainTypes, _gridW, _gridH, outputDir);

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        SC2Log.Log($"[SC2CostsTerrainTypes] Debug images written to {outputDir}");
    }

    private static void WriteBytesPair(string assetDir, string dataFile, string otFile, byte[] data, uint[] ot)
    {
        var p = System.IO.Path.Combine(assetDir, dataFile);
        var pp = System.IO.Path.Combine(assetDir, otFile);
        System.IO.File.WriteAllBytes(p, data);
        var otB = new byte[ot.Length * 4];
        System.Buffer.BlockCopy(ot, 0, otB, 0, otB.Length);
        System.IO.File.WriteAllBytes(pp, otB);
    }

    // ==================================================================
    // Auto-gen (bridges to SC2CostsAutoGen — see that file's own comment:
    // GenerateNavmesh is currently a placeholder that returns all-passable,
    // not real slope/depth-based generation; kept honest here rather than
    // implying it does more than it does).
    // ==================================================================

    public void AutoGenerateCosts()
    {
        if (_costsModel == null || _hfield == null) return;
        RecordUndo();
        int[] thresholds = {
            SC2CostsAutoGen.UnitSizeThresholds.LandSmall, SC2CostsAutoGen.UnitSizeThresholds.LandMedium,
            SC2CostsAutoGen.UnitSizeThresholds.LandLarge, SC2CostsAutoGen.UnitSizeThresholds.LandMedium,
            SC2CostsAutoGen.UnitSizeThresholds.LandLarge
        };
        for (var b = 0; b < _costsModel.Blocks.Count; b++)
            _costsModel.Blocks[b].Navmesh = SC2CostsAutoGen.GenerateNavmesh(_hfield, b < thresholds.Length ? thresholds[b] : thresholds[0]);
        RecomputeAllIslands();
        RebuildOverlayTexture();
    }

    /// <summary>Recomputes islandmap/islandList for every block without touching
    /// the navmesh's passable/impassable painting — for the Islands/Validation
    /// subtab's "Recompute Islands" button (e.g. after a manual navmesh edit
    /// made outside this tool, or just to refresh counts).</summary>
    public void AutoGenerateIslandsOnly()
    {
        if (!IsLoaded) return;
        RecordUndo();
        RecomputeAllIslands();
    }

    private void RecomputeAllIslands()
    {
        foreach (var block in _costsModel.Blocks)
        {
            var impassable = SC2CostsAutoGen.NavmeshToImpassable(block.Navmesh);
            SC2CostsAutoGen.ApplyImpassableToNavmesh(impassable, block.Navmesh, _gridW, _gridH);
            // 8-bit index map (0=impassable, 1..254=island), per CostsBdf.cs's
            // verified format comment — SC2IslandMap.Compute used to return a
            // uint32-per-cell color array that got Buffer.BlockCopy'd straight
            // in here, 4x the real costs.bdf format's byte size.
            var (islandmap, islandListU32) = SC2IslandMap.Compute(block.Navmesh, _gridW, _gridH);
            block.Islandmap = islandmap;
            block.LongInts = islandListU32;
        }
    }

    /// <summary>Island count per block — for the Islands/Validation subtab.</summary>
    public int CountIslands(int blockIdx)
    {
        if (_costsModel == null || blockIdx < 0 || blockIdx >= _costsModel.Blocks.Count) return 0;
        return _costsModel.Blocks[blockIdx].LongInts.Length / 4;
    }

    // ==================================================================
    // Global symmetry — reads the same SC2SymmetrySettings component every
    // other symmetry-aware tool (markers/props) uses, instead of a private
    // per-window override.
    // ==================================================================

    public static SC2SymmetrySettings FindSymmetrySettings(GameObject mapRoot) =>
        mapRoot != null ? mapRoot.GetComponentInChildren<SC2SymmetrySettings>() : null;

    public static SC2SymmetrySettings EnsureSymmetrySettings(GameObject mapRoot)
    {
        if (mapRoot == null) return null;
        var existing = mapRoot.GetComponentInChildren<SC2SymmetrySettings>();
        if (existing != null) return existing;
        return mapRoot.AddComponent<SC2SymmetrySettings>();
    }

    // ==================================================================
    // Painting
    // ==================================================================

    public void SetPainting(bool on, GameObject mapRoot)
    {
        _painting = on;
        SetTerrainTransparent(on);
        SceneView.RepaintAll();
    }

    public bool IsPainting => _painting;
    public int BrushRadius { get => _brushRadius; set => _brushRadius = Mathf.Clamp(value, 1, 128); }
    public CostsPaintModePublic CostsMode
    {
        get => (CostsPaintModePublic)_costsPaintMode;
        set => _costsPaintMode = (CostsPaintMode)value;
    }
    public enum CostsPaintModePublic { Impassable, Passable }
    public bool AllBlocks { get => _allBlocks; set => _allBlocks = value; }
    public int ActiveBlock { get => _activeBlock; set => _activeBlock = Mathf.Clamp(value, 0, Mathf.Max(0, BlockCount - 1)); }
    public int BlockCount => _costsModel?.Blocks.Count ?? 0;
    public static string[] AllBlockNames => BlockNames;
    public List<TerrainTypePreset> TerrainTypePresets => _terrainTypePresets;
    public int ActiveTerrainTypePreset { get => _activeTerrainTypePreset; set => _activeTerrainTypePreset = Mathf.Clamp(value, 0, _terrainTypePresets.Count - 1); }

    public void OnSceneGUI(SceneView sv, GameObject mapRoot)
    {
        DrawOverlay();

        if (!_painting || !IsLoaded || _terrainCollider == null) return;

        var e = Event.current;
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (!_terrainCollider.Raycast(ray, out var hit, float.MaxValue)) return;

        Handles.color = ActiveTarget == PaintTarget.Costs
            ? (_costsPaintMode == CostsPaintMode.Impassable ? new Color(1f, 0.1f, 0.1f, 0.5f) : new Color(0.1f, 1f, 0.1f, 0.5f))
            : new Color(_terrainTypePresets[_activeTerrainTypePreset].Color.r, _terrainTypePresets[_activeTerrainTypePreset].Color.g, _terrainTypePresets[_activeTerrainTypePreset].Color.b, 0.6f);
        Handles.DrawSolidDisc(hit.point, Vector3.up, _brushRadius);
        sv.Repaint();

        var symmetry = FindSymmetrySettings(mapRoot);
        var symMode = symmetry != null ? symmetry.Mode : SC2SymmetryMode.None;
        var symCenter = symmetry != null ? symmetry.Center + new Vector2(512f, 512f) : new Vector2(512f, 512f);

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            RecordUndo();
            _lastPaintPos = new Vector2(hit.point.x, hit.point.z);
            PaintLine(_lastPaintPos.Value, _lastPaintPos.Value, symMode, symCenter);
            RebuildOverlayTexture();
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && e.button == 0)
        {
            var cur = new Vector2(hit.point.x, hit.point.z);
            var from = _lastPaintPos ?? cur;
            PaintLine(from, cur, symMode, symCenter);
            _lastPaintPos = cur;
            RebuildOverlayTexture();
            e.Use();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            _lastPaintPos = null;
        }
    }

    private void PaintLine(Vector2 from, Vector2 to, SC2SymmetryMode symMode, Vector2 symCenter)
    {
        var dist = Vector2.Distance(from, to);
        var step = Mathf.Max(1f, _brushRadius * 0.5f);
        var steps = Mathf.Max(1, Mathf.CeilToInt(dist / step));
        for (var i = 0; i <= steps; i++)
        {
            var t = steps > 0 ? (float)i / steps : 0f;
            var p = Vector2.Lerp(from, to, t);
            PaintCircle(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), symMode, symCenter);
        }
    }

    private void PaintCircle(int cx, int cz, SC2SymmetryMode symMode, Vector2 symCenter)
    {
        var r2 = _brushRadius * _brushRadius;

        if (ActiveTarget == PaintTarget.Costs)
        {
            byte value = _costsPaintMode == CostsPaintMode.Impassable ? (byte)255 : (byte)1;
            var blocks = _allBlocks
                ? Enumerable.Range(0, _costsModel.Blocks.Count)
                : Enumerable.Repeat(Mathf.Clamp(_activeBlock, 0, _costsModel.Blocks.Count - 1), 1);

            foreach (var b in blocks)
            {
                var navmesh = _costsModel.Blocks[b].Navmesh;
                ForEachSymmetricCellInBrush(cx, cz, r2, symMode, symCenter, cell =>
                {
                    var idx = cell.y * _gridW + cell.x;
                    navmesh[idx] = value;
                });
            }
        }
        else
        {
            var id = _terrainTypePresets[_activeTerrainTypePreset].Id;
            ForEachSymmetricCellInBrush(cx, cz, r2, symMode, symCenter, cell =>
            {
                _hfield.TerrainTypes[cell.y * _gridW + cell.x] = id;
            });
        }
    }

    private void ForEachSymmetricCellInBrush(int cx, int cz, int r2, SC2SymmetryMode symMode, Vector2 symCenter, System.Action<Vector2Int> apply)
    {
        for (var dy = -_brushRadius; dy <= _brushRadius; dy++)
        for (var dx = -_brushRadius; dx <= _brushRadius; dx++)
        {
            if (dx * dx + dy * dy > r2) continue;
            var cells = SC2Symmetry.GetSymmetricCells(new Vector2Int(cx + dx, cz + dy), symCenter, _gridW, symMode);
            foreach (var cell in cells)
            {
                if (cell.x < 0 || cell.x >= _gridW || cell.y < 0 || cell.y >= _gridH) continue;
                apply(cell);
            }
        }
        _overlayDirty = true;
    }

    // ==================================================================
    // Overlay — ONE shared texture/material for both Costs and TerrainTypes.
    // Switching ActiveTarget just changes what RebuildOverlayTexture reads
    // from; the render path (DrawOverlay) never branches on target.
    // ==================================================================

    private void RebuildOverlayTexture()
    {
        if (!IsLoaded) return;
        if (_overlayTex == null || _overlayTex.width != _gridW || _overlayTex.height != _gridH)
        {
            if (_overlayTex != null) Object.DestroyImmediate(_overlayTex);
            _overlayTex = new Texture2D(Mathf.Max(1, _gridW), Mathf.Max(1, _gridH), TextureFormat.RGBA32, false, true)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _overlayColors = new Color[Mathf.Max(1, _gridW * _gridH)];
        }

        if (ActiveTarget == PaintTarget.Costs)
        {
            var block = _activeBlock >= 0 && _activeBlock < _costsModel.Blocks.Count ? _costsModel.Blocks[_activeBlock] : null;
            var navmesh = block?.Navmesh;
            var tint = BlockNames.Length > 0 ? BlockTintFor(_activeBlock) : Color.red;
            for (var i = 0; i < _overlayColors.Length; i++)
                _overlayColors[i] = navmesh != null && i < navmesh.Length && navmesh[i] == 255 ? tint : Color.clear;
        }
        else
        {
            // The game itself treats anything that isn't Chasm_Bottom (id 3)
            // as Default — there's no separate "unknown id" case in-game, so
            // the overlay matches that: only id 3 tints red, everything else
            // (including id 2 and any other value) is alpha 0 / natural.
            var chasmColor = _terrainTypePresets.First(p => p.Id == 3).Color;
            for (var i = 0; i < _overlayColors.Length; i++)
            {
                var id = i < _hfield.TerrainTypes.Length ? _hfield.TerrainTypes[i] : SC2TerrainData.DefaultTerrainTypeId;
                _overlayColors[i] = id == 3 ? chasmColor : Color.clear;
            }
        }

        _overlayTex.SetPixels(_overlayColors);
        _overlayTex.Apply(false);

        if (_overlayMat == null)
        {
            // See SC2CostsShell.RebuildOverlayTexture's comment — same
            // Resources.Load-first resolution, now that the shader lives
            // under Resources/SC2CostsOverlay.shader instead of Shaders/.
            var shader = Resources.Load<Shader>("SC2CostsOverlay") ?? Shader.Find("SC2/CostsOverlay") ?? SC2FallbackShader.Get();
            _overlayMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
        _overlayMat.mainTexture = _overlayTex;
        _overlayDirty = false;
    }

    private static readonly Color[] BlockTints =
    {
        new Color(1f, 0.1f, 0.1f, 0.55f), new Color(1f, 0.5f, 0.1f, 0.55f),
        new Color(1f, 0.9f, 0f, 0.55f), new Color(0.2f, 0.4f, 1f, 0.55f),
        new Color(0f, 0.2f, 1f, 0.55f),
    };
    private static Color BlockTintFor(int block) => BlockTints[block >= 0 && block < BlockTints.Length ? block : 0];

    private void DrawOverlay()
    {
        if (_overlayDirty) RebuildOverlayTexture();
        if (_overlayMat == null || _overlayTex == null) return;
        var terrainGo = FindTerrainGo();
        if (terrainGo == null) return;
        var mf = terrainGo.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null) return;
        var matrix = terrainGo.transform.localToWorldMatrix;
        for (var sub = 0; sub < mf.sharedMesh.subMeshCount; sub++)
            Graphics.DrawMesh(mf.sharedMesh, matrix, _overlayMat, 0, null, sub);
    }

    public void SetActiveTarget(PaintTarget target)
    {
        if (ActiveTarget == target) return;
        ActiveTarget = target;
        RebuildOverlayTexture();
    }

    public void SetActiveBlock(int block)
    {
        _activeBlock = Mathf.Clamp(block, 0, Mathf.Max(0, BlockCount - 1));
        if (ActiveTarget == PaintTarget.Costs) RebuildOverlayTexture();
    }

    public void SetActiveTerrainTypePreset(int preset)
    {
        _activeTerrainTypePreset = Mathf.Clamp(preset, 0, _terrainTypePresets.Count - 1);
    }

    // ==================================================================
    // Undo / redo
    // ==================================================================

    private void RecordUndo()
    {
        if (!IsLoaded) return;
        _undoStack.Push(new Snapshot
        {
            Target = ActiveTarget,
            CostsNavmesh = _costsModel.Blocks.Select(b => (byte[])b.Navmesh.Clone()).ToArray(),
            TerrainTypes = (ushort[])_hfield.TerrainTypes.Clone()
        });
        if (_undoStack.Count > MaxUndoSteps)
        {
            var tmp = _undoStack.ToArray();
            _undoStack.Clear();
            foreach (var s in tmp.Take(MaxUndoSteps)) _undoStack.Push(s);
        }
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (!IsLoaded || _undoStack.Count == 0) return;
        _redoStack.Push(Capture());
        Apply(_undoStack.Pop());
    }

    public void Redo()
    {
        if (!IsLoaded || _redoStack.Count == 0) return;
        _undoStack.Push(Capture());
        Apply(_redoStack.Pop());
    }

    private Snapshot Capture() => new Snapshot
    {
        Target = ActiveTarget,
        CostsNavmesh = _costsModel.Blocks.Select(b => (byte[])b.Navmesh.Clone()).ToArray(),
        TerrainTypes = (ushort[])_hfield.TerrainTypes.Clone()
    };

    private void Apply(Snapshot s)
    {
        for (var i = 0; i < _costsModel.Blocks.Count && i < s.CostsNavmesh.Length; i++)
            _costsModel.Blocks[i].Navmesh = s.CostsNavmesh[i];
        _hfield.TerrainTypes = s.TerrainTypes;
        ActiveTarget = s.Target;
        RebuildOverlayTexture();
    }

    // ==================================================================
    // Terrain transparency (paint-mode visual aid) — same approach as
    // CostsPainterWindow: temporarily blend the terrain material so the
    // overlay reads clearly while painting.
    // ==================================================================

    private static readonly Dictionary<Material, (int src, int dst, int zw, int q)> _matSave = new();

    private static void SetTerrainTransparent(bool on)
    {
        var go = FindTerrainGo();
        if (go == null) return;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        if (on)
        {
            _matSave.Clear();
            foreach (var m in mr.sharedMaterials)
            {
                if (m == null) continue;
                _matSave[m] = (m.GetInt("_SrcBlend"), m.GetInt("_DstBlend"), m.GetInt("_ZWrite"), m.renderQueue);
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.renderQueue = 3000;
                if (m.HasProperty("_PainterAlpha")) m.SetFloat("_PainterAlpha", 0.4f);
            }
        }
        else
        {
            foreach (var m in mr.sharedMaterials)
            {
                if (m == null || !_matSave.TryGetValue(m, out var s)) continue;
                m.SetInt("_SrcBlend", s.src);
                m.SetInt("_DstBlend", s.dst);
                m.SetInt("_ZWrite", s.zw);
                m.renderQueue = s.q;
                if (m.HasProperty("_PainterAlpha")) m.SetFloat("_PainterAlpha", 1f);
            }
            _matSave.Clear();
        }
    }

    private void AddTerrainCollider(GameObject go)
    {
        _terrainCollider = go.GetComponent<MeshCollider>();
        if (_terrainCollider == null) _terrainCollider = go.AddComponent<MeshCollider>();
        var mf = go.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) _terrainCollider.sharedMesh = mf.sharedMesh;
    }

    private static GameObject FindTerrainGo() => GameObject.Find("terrain");

    public void Dispose()
    {
        SetTerrainTransparent(false);
        if (_overlayTex != null) { Object.DestroyImmediate(_overlayTex); _overlayTex = null; }
        if (_overlayMat != null) { Object.DestroyImmediate(_overlayMat); _overlayMat = null; }
    }
}
#endif
