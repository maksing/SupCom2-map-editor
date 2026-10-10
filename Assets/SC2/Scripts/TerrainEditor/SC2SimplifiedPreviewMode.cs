using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public sealed class SC2SimplifiedPreviewMode
{
    public SC2TerrainData Data;

    private SC2TerrainShell _win;
    private GameObject _go;
    private MeshFilter _filter;
    private MeshRenderer _renderer;
    private Mesh _mesh;
    private GameObject _wireGo;
    private MeshFilter _wireFilter;
    private MeshRenderer _wireRenderer;
    private Mesh _wireMesh;
    private Material _wireMaterial;
    private Material[] _materials;
    private SC2TerrainBuildResult _lastBuild;
    // The background's simplified build and its world offset, when the background is on (null otherwise).
    private SC2TerrainBuildResult _lastBackgroundBuild;
    private SC2TerrainData _lastBackgroundData;
    private Vector3 _lastBackgroundOffset;
    private double _buildMs;
    private bool _dirty = true;
    private bool _showWireframe;

    public SC2SimplifiedPreviewMode(SC2TerrainShell win, SC2TerrainData data)
    {
        _win = win;
        Data = data;
    }

    /// <summary>See SC2GeometryToolState — same reasoning, folded into
    /// SC2TerrainShell.CaptureAuxState/ApplyAuxState so toggling Wireframe is
    /// undo-able like everything else editable in this shell.</summary>
    public bool CaptureState() => _showWireframe;

    public void ApplyState(bool wireframe)
    {
        _showWireframe = wireframe;
        UpdateWireframeVisibility();
    }

    public void DrawPanel()
    {
        EditorGUILayout.LabelField("Simplified Geometry Preview", EditorStyles.boldLabel);
        var nextWire = EditorGUILayout.ToggleLeft("Wireframe", _showWireframe);
        if (nextWire != _showWireframe)
        {
            _win.RecordUndoCheckpoint("Toggle Wireframe");
            _showWireframe = nextWire;
            UpdateWireframeVisibility();
        }

        if (_win.IsPairCpuDataStale)
            SC2TerrainShell.DrawUnifiedNote("CPU pair data is pending GPU readback. Rebuild/export will force a sync.");

        var sourceTris = _lastBuild != null ? _lastBuild.SourceTriangleCount : 0;
        var builtTris = _lastBuild != null ? _lastBuild.BuiltTriangleCount : 0;
        var builtVerts = _lastBuild != null ? _lastBuild.BuiltVertexCount : 0;
        var locked = _lastBuild != null ? _lastBuild.LockedVertexCount : 0;
        var components = _lastBuild != null ? _lastBuild.ComponentCount : 0;
        var skipped = _lastBuild != null ? _lastBuild.SkippedComponentCount : 0;
        var problems = _lastBuild != null ? _lastBuild.ProblemTriangleCount : 0;

        EditorGUILayout.LabelField($"Source: {sourceTris:N0} tris", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"After simplification: {builtTris:N0} tris, {builtVerts:N0} verts, {_buildMs:F0} ms", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"Locked: {locked:N0} verts", EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"Components: {components:N0}, Raw: {skipped:N0}, Rejected: {problems:N0}", EditorStyles.miniLabel);

        EditorGUILayout.Space(4);
        for (var i = 0; i < Data.Pairs.Count; i++)
        {
            var p = Data.Pairs[i];
            SC2TerrainShell.DrawPairCardHeader(Data.PairColor(i), p.Name, PairTriangles(_lastBuild, i));
            var next = SC2TerrainShell.DrawCompactSlider("Simplify", p.SimplifyRatio, 0f, 1f, $"simplified-{i}",
                onValueChangeStart: () => _win.RecordUndoCheckpoint("Simplify Ratio"));
            if (!Mathf.Approximately(next, p.SimplifyRatio))
            {
                p.SimplifyRatio = next;
                MarkDirty();
                EditorUtility.SetDirty(Data);
            }
            SC2TerrainShell.EndUnifiedPanelBox();
        }

        var background = _win.ProceduralBackgroundData;
        if (background != null && background.Pairs.Count > 0)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Background", EditorStyles.boldLabel);
            if (_lastBackgroundBuild != null)
                EditorGUILayout.LabelField(
                    $"Source: {_lastBackgroundBuild.SourceTriangleCount:N0} tris, preview: {_lastBackgroundBuild.BuiltTriangleCount:N0} tris, {_lastBackgroundBuild.BuiltVertexCount:N0} verts",
                    EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Exported as about {SC2BackgroundTerrain.EstimateExportTriangles(background):N0} tris (1.0 = {SC2BackgroundTerrain.ExportTriangleBudget:N0})", EditorStyles.miniLabel);
            for (var i = 0; i < background.Pairs.Count; i++)
            {
                var p = background.Pairs[i];
                SC2TerrainShell.DrawPairCardHeader(background.PairColor(i), p.Name, PairTriangles(_lastBackgroundBuild, i));
                _win.DrawBackgroundDetailSlider(background, p, $"simplified-bg-{i}", null, MarkDirty);
                SC2TerrainShell.EndUnifiedPanelBox();
            }
        }

        EditorGUILayout.Space(8);
        if (GUILayout.Button("Rebuild Preview", GUILayout.Height(SC2UiScale.Pxf(30f))))
            Rebuild();
        EditorGUI.BeginDisabledGroup(!_dirty);
        if (GUILayout.Button("Apply Pending", GUILayout.Height(SC2UiScale.Pxf(30f))))
            Rebuild();
        EditorGUI.EndDisabledGroup();

        if (_dirty)
            SC2TerrainShell.DrawUnifiedNote("Preview is stale. Rebuild to see current simplify settings.");
    }

    /// <summary>"292k → 42k tris" for one pair of a build: its triangles before and after simplification (a trailing * once the
    /// settings changed after that build); empty before the first build.</summary>
    private string PairTriangles(SC2TerrainBuildResult build, int pairIndex)
    {
        if (build == null) return "";
        var source = 0;
        var built = 0;
        foreach (var layer in build.Layers)
        {
            if (layer.PairIndex != pairIndex) continue;
            source += layer.SourceTriangleCount;
            built += layer.Mesh.Triangles != null ? layer.Mesh.Triangles.Length / 3 : 0;
        }
        return SC2TerrainShell.TrianglesBeforeAfter(source, built, _dirty);
    }

    public void MarkDirty()
    {
        _dirty = true;
        _win.MarkSimplifiedCountsStale();
    }

    public void Rebuild()
    {
        if (Data == null || !Data.IsInitialized) return;

        _win.FlushPaintToCPU();
        EnsureObject();
        _win.SetBasePreviewVisiblePublic(false);

        // Same outer-perimeter lock the real exporter uses (see its own
        // comment) — this preview exists specifically to show what the
        // simplified, export-shaped mesh looks like, so it must protect the
        // map's own edge from simplification the same way export does.
        var shore = SC2ShoreDetail.For(Data, _win.Session);
        SC2TerrainBuildResult BuildCore() => SC2TerrainLayerBuilder.Build(Data, sortByTriangleCount: false, null,
            SC2TerrainSimplifier.FindOuterPerimeterVertices(Data), shoreDetail: shore);

        var background = _win.ProceduralBackgroundData;
        if (background == null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _lastBuild = BuildCore();
            _lastBackgroundBuild = null;
            _lastBackgroundData = null;
            BuildMesh(_lastBuild);
            Finish(sw.Elapsed.TotalMilliseconds);
            return;
        }

        // The background's simplification takes seconds: it runs on a worker thread behind the busy plate, and the meshes
        // are built from its result on the main thread.
        SC2BusyOverlay.RunInBackground("Building the simplified preview",
            "Simplifying the core and the background the way the export does. This can take a while.", () =>
            {
                var core = Data;
                return () =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    SC2BusyOverlay.Report("Simplifying the core");
                    var coreBuild = BuildCore();
                    SC2BusyOverlay.Report("Simplifying the background");
                    var backgroundBuild = SC2BackgroundTerrain.BuildForExportOrPreview(core, out var offset);
                    var elapsed = sw.Elapsed.TotalMilliseconds;
                    return () =>
                    {
                        _lastBuild = coreBuild;
                        _lastBackgroundBuild = backgroundBuild;
                        _lastBackgroundData = background;
                        _lastBackgroundOffset = offset;
                        BuildMesh(_lastBuild);
                        Finish(elapsed);
                    };
                };
            });
    }

    private void Finish(double buildMs)
    {
        _buildMs = buildMs;
        _dirty = false;
        _win.StoreSimplifiedCounts(_lastBuild, _lastBackgroundBuild);
        SceneView.RepaintAll();
        _win.Repaint();
    }

    public void SetVisible(bool visible)
    {
        if (_go != null) _go.SetActive(visible);
        if (_wireGo != null) _wireGo.SetActive(visible && _showWireframe);
    }

    public void Destroy()
    {
        if (_go != null) Object.DestroyImmediate(_go);
        if (_mesh != null) Object.DestroyImmediate(_mesh);
        if (_wireGo != null) Object.DestroyImmediate(_wireGo);
        if (_wireMesh != null) Object.DestroyImmediate(_wireMesh);
        if (_wireMaterial != null) Object.DestroyImmediate(_wireMaterial);
        SC2PreviewMaterialFactory.Destroy(_materials);
        _go = null;
        _filter = null;
        _renderer = null;
        _mesh = null;
        _wireGo = null;
        _wireFilter = null;
        _wireRenderer = null;
        _wireMesh = null;
        _wireMaterial = null;
        _materials = null;
    }

    private void EnsureObject()
    {
        if (_go != null) return;
        _go = new GameObject("SC2SimplifiedPreview") { hideFlags = HideFlags.DontSave };
        _filter = _go.AddComponent<MeshFilter>();
        _renderer = _go.AddComponent<MeshRenderer>();
        _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _renderer.receiveShadows = false;

        _wireGo = new GameObject("SC2SimplifiedWireframe") { hideFlags = HideFlags.DontSave };
        _wireGo.transform.SetParent(_go.transform, false);
        _wireFilter = _wireGo.AddComponent<MeshFilter>();
        _wireRenderer = _wireGo.AddComponent<MeshRenderer>();
        _wireRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _wireRenderer.receiveShadows = false;
        var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
        _wireMaterial = new Material(shader) { hideFlags = HideFlags.DontSave, color = new Color(1f, 1f, 0f, 0.85f) };
        _wireRenderer.sharedMaterial = _wireMaterial;
        UpdateWireframeVisibility();
    }

    private void BuildMesh(SC2TerrainBuildResult build)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var subTris = new List<int[]>();
        var mats = new List<Material>();

        AddBuildLayers(build, Data, Vector3.zero, "Simplified", verts, normals, subTris, mats);
        // The background's layers are in its own local space; its offset places them in the world.
        if (_lastBackgroundBuild != null && _lastBackgroundData != null)
            AddBuildLayers(_lastBackgroundBuild, _lastBackgroundData, _lastBackgroundOffset, "SimplifiedBackground", verts, normals, subTris, mats);

        if (_mesh != null) Object.DestroyImmediate(_mesh);
        SC2PreviewMaterialFactory.Destroy(_materials);

        _mesh = new Mesh
        {
            name = "SC2SimplifiedPreviewMesh",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        _mesh.SetVertices(verts);
        if (normals.Count == verts.Count) _mesh.SetNormals(normals);
        _mesh.subMeshCount = Mathf.Max(1, subTris.Count);
        if (subTris.Count == 0)
            _mesh.SetTriangles(System.Array.Empty<int>(), 0);
        else
            for (var i = 0; i < subTris.Count; i++)
                _mesh.SetTriangles(subTris[i], i);
        _mesh.RecalculateBounds();
        if (normals.Count != verts.Count) _mesh.RecalculateNormals();

        _filter.sharedMesh = _mesh;
        BuildWireframeMesh(verts, subTris);
        _materials = mats.Count > 0
            ? mats.ToArray()
            : new[] { SC2PreviewMaterialFactory.MakeGrey("EmptySimplifiedPreview") };
        _renderer.sharedMaterials = _materials;
    }

    private void AddBuildLayers(SC2TerrainBuildResult build, SC2TerrainData data, Vector3 worldOffset, string materialName,
        List<Vector3> verts, List<Vector3> normals, List<int[]> subTris, List<Material> mats)
    {
        foreach (var layer in build.Layers)
        {
            var simplified = layer.Mesh;
            if (simplified.Triangles == null || simplified.Triangles.Length == 0)
                continue;

            var offset = verts.Count;
            foreach (var v in simplified.Vertices) verts.Add(v + worldOffset);
            normals.AddRange(simplified.Normals);

            var tri = new int[simplified.Triangles.Length];
            for (var i = 0; i < tri.Length; i++)
                tri[i] = simplified.Triangles[i] + offset;
            subTris.Add(tri);
            mats.Add(SC2PreviewMaterialFactory.MakePairColorMaterial(data, layer.PairIndex, materialName));

            AddProblemOverlay(layer, data, worldOffset, verts, normals, subTris, mats);
        }
    }

    private void BuildWireframeMesh(List<Vector3> verts, List<int[]> subTris)
    {
        if (_wireFilter == null) return;
        if (_wireMesh != null) Object.DestroyImmediate(_wireMesh);
        _wireMesh = new Mesh
        {
            name = "SC2SimplifiedWireframeMesh",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        var lines = new List<int>();
        foreach (var tris in subTris)
        {
            for (var i = 0; i + 2 < tris.Length; i += 3)
            {
                var a = tris[i];
                var b = tris[i + 1];
                var c = tris[i + 2];
                lines.Add(a); lines.Add(b);
                lines.Add(b); lines.Add(c);
                lines.Add(c); lines.Add(a);
            }
        }
        _wireMesh.SetVertices(verts);
        _wireMesh.SetIndices(lines.ToArray(), MeshTopology.Lines, 0);
        _wireMesh.RecalculateBounds();
        _wireFilter.sharedMesh = _wireMesh;
        UpdateWireframeVisibility();
    }

    private void UpdateWireframeVisibility()
    {
        if (_wireGo != null)
            _wireGo.SetActive(_showWireframe && _go != null && _go.activeSelf);
    }

    private void AddProblemOverlay(
        SC2TerrainBuiltLayer layer,
        SC2TerrainData data,
        Vector3 worldOffset,
        List<Vector3> verts,
        List<Vector3> normals,
        List<int[]> subTris,
        List<Material> mats)
    {
        var problem = layer.Mesh.ProblemOrigTriMap;
        if (problem == null || problem.Length == 0) return;

        data.InvalidateGeometryCache();
        var sourceVerts = data.Vertices;
        var sourceTris = data.Triangles;
        var overlayTris = new int[problem.Length * 3];
        var triCursor = 0;
        for (var i = 0; i < problem.Length; i++)
        {
            var sourceTri = problem[i];
            if (sourceTri < 0 || sourceTri * 3 + 2 >= sourceTris.Length) continue;

            var offset = verts.Count;
            verts.Add(sourceVerts[sourceTris[sourceTri * 3]] + worldOffset + Vector3.up * 0.035f);
            verts.Add(sourceVerts[sourceTris[sourceTri * 3 + 1]] + worldOffset + Vector3.up * 0.035f);
            verts.Add(sourceVerts[sourceTris[sourceTri * 3 + 2]] + worldOffset + Vector3.up * 0.035f);
            normals.Add(Vector3.up);
            normals.Add(Vector3.up);
            normals.Add(Vector3.up);
            overlayTris[triCursor++] = offset;
            overlayTris[triCursor++] = offset + 1;
            overlayTris[triCursor++] = offset + 2;
        }

        if (triCursor == 0) return;
        if (triCursor != overlayTris.Length)
        {
            var packed = new int[triCursor];
            System.Array.Copy(overlayTris, packed, triCursor);
            overlayTris = packed;
        }
        subTris.Add(overlayTris);
        mats.Add(SC2PreviewMaterialFactory.MakeDebugRed($"SimplifiedProblems_{layer.PairIndex}"));
    }

    public bool HandleKeyboardShortcut(UnityEngine.Event e) => false;
}
