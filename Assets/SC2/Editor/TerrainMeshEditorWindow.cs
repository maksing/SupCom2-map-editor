// TerrainMeshEditorWindow.cs — SC2 > Edit Terrain Geometry
//
// Capabilities:
//  1. Transform individual terrain submesh layers (translate/scale/rotate)
//     with a BSP and hfield rebuild.
//  2. Replace textures on existing materials (TexturePaths).
//  3. Add a new layer with a new texture.
//  4. Recompute hfield from the changed terrain vertices.
//
// Pipeline:
//   Transform layer -> vertices updated -> BSP rebuild -> Serialize -> terrain.bytes
//   -> RegenerateHfield -> hfield.bytes  (both used at export time)

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Terrain;
using BdfIO.Formats.Hfield;
using SC2TerrainLayer = BdfIO.Formats.Terrain.TerrainLayer;

public class TerrainMeshEditorWindow : EditorWindow
{
    [MenuItem("SC2/Edit Terrain Geometry")]
    public static void ShowWindow() => GetWindow<TerrainMeshEditorWindow>("Terrain Editor");

    private SC2MapRawData? _rawData;
    private TerrainModel?  _model;
    private bool           _dirty;
    private Vector2        _scroll;
    private bool[]         _foldouts = System.Array.Empty<bool>();

    // ---- Transform panel ----
    private int     _transformLayer;
    private Vector3 _translate = Vector3.zero;
    private Vector3 _scale     = Vector3.one;
    private Vector3 _rotateEuler = Vector3.zero;

    // ---- Textures ----
    private int    _texLayer;
    private int    _texSlot;
    private string _texPathInput = "";

    // ---- New layer ----
    private Mesh?     _newMesh;
    private string    _newLayerName = "NewLayer";
    private string[]  _newTexPaths  = new string[13];

    private void OnGUI()
    {
        EditorGUILayout.LabelField("SC2 Terrain Editor", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        _rawData = (SC2MapRawData)EditorGUILayout.ObjectField(
            "SC2MapRawData", _rawData, typeof(SC2MapRawData), false);
        if (_rawData == null) { EditorGUILayout.HelpBox("Select a *_RawData.asset.", MessageType.Info); return; }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Load")) Load();
        EditorGUI.BeginDisabledGroup(!_dirty);
        var c = GUI.color; if (_dirty) GUI.color = new Color(1f, 0.8f, 0.3f);
        if (GUILayout.Button(_dirty ? "★ Save" : "Save")) Save();
        GUI.color = c;
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        if (_model == null) return;

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // ================================================================
        // 1. TRANSFORM LAYER
        // ================================================================
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("1. Transform layer", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("helpBox"))
        {
            var layerNames = LayerNames();
            _transformLayer = EditorGUILayout.Popup("Layer", _transformLayer, layerNames);

            EditorGUILayout.Space(3);
            _translate    = EditorGUILayout.Vector3Field("Translate (XYZ)", _translate);
            _scale        = EditorGUILayout.Vector3Field("Scale (XYZ)",     _scale);
            _rotateEuler  = EditorGUILayout.Vector3Field("Rotate Euler",    _rotateEuler);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_translate == Vector3.zero && _scale == Vector3.one && _rotateEuler == Vector3.zero);
            if (GUILayout.Button("Apply transform"))
            {
                TransformLayer(_transformLayer, _translate, _scale, _rotateEuler);
                _translate = Vector3.zero; _scale = Vector3.one; _rotateEuler = Vector3.zero;
            }
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Reset"))
            {
                _translate = Vector3.zero; _scale = Vector3.one; _rotateEuler = Vector3.zero;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "If the mesh becomes invisible or the geometry looks broken, " +
                "click \"Load\" again to discard unsaved changes " +
                "and start over from the clean terrain.bytes state.",
                MessageType.Info);

            EditorGUI.BeginDisabledGroup(_lastTransformBackup == null);
            if (GUILayout.Button("↩ Undo last transform"))
                UndoLastTransform();
            EditorGUI.EndDisabledGroup();
        }

        // ================================================================
        // 2. REPLACE TEXTURES
        // ================================================================
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("2. Change material textures", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("helpBox"))
        {
            var layerNames = LayerNames();
            _texLayer = EditorGUILayout.Popup("Material", _texLayer, layerNames);

            // Show the selected material's current textures
            if (_texLayer < _model.Materials.Count)
            {
                var mat = _model.Materials[_texLayer];
                EditorGUILayout.LabelField("Current textures:", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                for (var s = 0; s < 13; s++)
                    if (mat.TexturePaths[s] != null)
                        EditorGUILayout.LabelField($"[{s}] {mat.TexturePaths[s]}", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("Change slot:", EditorStyles.miniBoldLabel);
            _texSlot = EditorGUILayout.IntSlider("Slot [0..12]", _texSlot, 0, 12);

            // Drag & drop Texture2D
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Texture:", GUILayout.Width(SC2UiScale.Pxf(70f)));
            var droppedTex = (Texture2D)EditorGUILayout.ObjectField(
                null, typeof(Texture2D), false, GUILayout.Width(SC2UiScale.Pxf(200f)));
            if (droppedTex != null)
            {
                var ap = AssetDatabase.GetAssetPath(droppedTex).Replace('\\', '/');
                var root = SC2TerrainMaterialFactory.TextureRoot.Replace('\\', '/').TrimEnd('/') + "/";
                _texPathInput = ap.StartsWith(root)
                    ? "/" + ap[root.Length..]
                    : "/" + ap;
            }
            EditorGUILayout.EndHorizontal();

            _texPathInput = EditorGUILayout.TextField("Path (bdf)", _texPathInput);

            if (GUILayout.Button("Set texture") && !string.IsNullOrEmpty(_texPathInput))
                SetTexture(_texLayer, _texSlot, _texPathInput);

            EditorGUILayout.Space(3);
            // Quick slots
            if (_texLayer < _model.Materials.Count)
            {
                EditorGUILayout.LabelField("Quick slots:", EditorStyles.miniBoldLabel);
                var mat = _model.Materials[_texLayer];
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("[0] Diffuse1")) { _texSlot = 0; _texPathInput = mat.TexturePaths[0] ?? ""; }
                    if (GUILayout.Button("[1] Diffuse2")) { _texSlot = 1; _texPathInput = mat.TexturePaths[1] ?? ""; }
                    if (GUILayout.Button("[2] Normal1"))  { _texSlot = 2; _texPathInput = mat.TexturePaths[2] ?? ""; }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("[3] Normal2"))  { _texSlot = 3; _texPathInput = mat.TexturePaths[3] ?? ""; }
                    if (GUILayout.Button("[8] Ramp"))     { _texSlot = 8; _texPathInput = mat.TexturePaths[8] ?? ""; }
                    if (GUILayout.Button("[10] RNMMask")) { _texSlot = 10; _texPathInput = mat.TexturePaths[10] ?? ""; }
                }
            }
        }

        // ================================================================
        // 3. ADD NEW LAYER
        // ================================================================
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("3. Add new layer", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("helpBox"))
        {
            _newLayerName = EditorGUILayout.TextField("Material name", _newLayerName);
            _newMesh = (Mesh)EditorGUILayout.ObjectField("Mesh (submesh)", _newMesh, typeof(Mesh), false);

            EditorGUILayout.LabelField("New layer's textures:", EditorStyles.miniBoldLabel);
            string[] slotLabels = { "Diffuse1", "Diffuse2", "Normal1", "Normal2", "", "", "Spec1", "Spec2", "Ramp", "", "RNMMask", "EnvMap", "" };
            for (var s = 0; s < 13; s++)
            {
                if (string.IsNullOrEmpty(slotLabels[s])) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"[{s}] {slotLabels[s]}", GUILayout.Width(SC2UiScale.Pxf(100f)));
                var dropped = (Texture2D)EditorGUILayout.ObjectField(null, typeof(Texture2D), false, GUILayout.Width(SC2UiScale.Pxf(160f)));
                if (dropped != null)
                {
                    var ap = AssetDatabase.GetAssetPath(dropped).Replace('\\','/');
                    var root = SC2TerrainMaterialFactory.TextureRoot.Replace('\\','/').TrimEnd('/') + "/";
                    _newTexPaths[s] = ap.StartsWith(root) ? "/" + ap[root.Length..] : "/" + ap;
                }
                _newTexPaths[s] = EditorGUILayout.TextField(_newTexPaths[s] ?? "", GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.BeginDisabledGroup(_newMesh == null);
            if (GUILayout.Button("Add layer")) AddLayer();
            EditorGUI.EndDisabledGroup();
        }

        // ================================================================
        // 4. LAYER LIST
        // ================================================================
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField($"Layers ({_model.Layers.Count})", EditorStyles.boldLabel);
        if (_foldouts.Length != _model.Layers.Count) _foldouts = new bool[_model.Layers.Count];

        for (var i = 0; i < _model.Layers.Count; i++)
        {
            var layer = _model.Layers[i];
            var mat   = i < _model.Materials.Count ? _model.Materials[i] : null;
            int vs = layer.CumulativeVertices;
            int ve = i+1 < _model.Layers.Count ? _model.Layers[i+1].CumulativeVertices : _model.Vertices.Count;

            EditorGUILayout.BeginHorizontal("box");
            _foldouts[i] = EditorGUILayout.Foldout(_foldouts[i],
                $"[{i}] {mat?.Name ?? "?"} — {ve-vs} vertices", true);
            var oc = GUI.color; GUI.color = new Color(1f, 0.4f, 0.4f);
            if (GUILayout.Button("✕", GUILayout.Width(SC2UiScale.Pxf(24f))))
                if (EditorUtility.DisplayDialog("Delete?", $"Delete layer [{i}]?", "Yes", "No"))
                { RemoveLayer(i); break; }
            GUI.color = oc;
            EditorGUILayout.EndHorizontal();

            if (_foldouts[i] && mat != null)
            {
                EditorGUI.indentLevel++;
                for (var s = 0; s < 13; s++)
                    if (mat.TexturePaths[s] != null)
                        EditorGUILayout.LabelField($"  [{s}] {mat.TexturePaths[s]}", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }
        }

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Rebuild BSP (all layers)")) { TerrainBdf.RebuildAllBsp(_model!); _dirty = true; }

        EditorGUILayout.EndScrollView();
    }

    // ================================================================
    // Load / Save
    // ================================================================
    private void Load()
    {
        if (_rawData == null) return;
        var bytes = _rawData.GetTerrainData();
        if (bytes.Length == 0) { Debug.LogError("[TerrainEditor] terrain.bytes is empty"); return; }
        _model = TerrainBdf.Parse(new BdfContainer
        {
            Platform = _rawData.Platform, Data = bytes,
            OffsetTable = _rawData.GetTerrainOt(), NumFiles = _rawData.TerrainNumFiles,
            TrailingData = _rawData.TerrainTrailingData.Length > 0 ? _rawData.TerrainTrailingData : null
        });
        _foldouts = new bool[_model.Layers.Count];
        _dirty = false;
        Debug.Log($"[TerrainEditor] Loaded: {_model.Vertices.Count} vertices, {_model.Layers.Count} layers");
        Repaint();
    }

    private void Save()
    {
        if (_rawData == null || _model == null) return;

        // 1. Rebuild the BSP
        TerrainBdf.RebuildAllBsp(_model);

        // 2. Serialize the terrain
        var tc = TerrainBdf.SerializeToContainer(_model, _rawData.Platform);

        // 3. Write the files to disk
        WriteBytes("terrain.bytes", "terrain_ot.bytes", tc.Data, tc.OffsetTable);

        // 4. Update RawData's fields BEFORE AssetDatabase.Refresh
        _rawData.TerrainNumFiles     = tc.NumFiles;
        _rawData.TerrainTrailingData = tc.TrailingData ?? System.Array.Empty<byte>();

        // 5. Synchronous import — Unity must see the new .bytes files
        AssetDatabase.ImportAsset(
            GetAssetRelPath("terrain.bytes"), ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(
            GetAssetRelPath("terrain_ot.bytes"), ImportAssetOptions.ForceSynchronousImport);

        _rawData.TerrainBytes   = AssetDatabase.LoadAssetAtPath<TextAsset>(GetAssetRelPath("terrain.bytes"));
        _rawData.TerrainOtBytes = AssetDatabase.LoadAssetAtPath<TextAsset>(GetAssetRelPath("terrain_ot.bytes"));

        if (_rawData.TerrainBytes == null || _rawData.TerrainOtBytes == null)
            Debug.LogError("[TerrainEditor] Failed to load terrain.bytes after saving!");

        // 6. Recompute the hfield
        RegenerateHfield();

        // 7. Update the scene mesh
        RefreshSceneMesh();

        EditorUtility.SetDirty(_rawData);
        AssetDatabase.SaveAssets();
        _dirty = false;
        Debug.Log($"[TerrainEditor] Saved. terrain.bytes={tc.Data.Length}b, OT={tc.OffsetTable.Length} entries. BSP and hfield recomputed.");
    }

    private string GetAssetRelPath(string filename)
    {
        if (_rawData == null) return filename;
        var dir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_rawData))!
            .Replace('\\', '/');
        return dir + "/" + filename;
    }

    // ================================================================
    // Transform layer
    // ================================================================
    // Backs up the layer's vertices before transforming so it can be undone in one click
    private int? _lastTransformLayer;
    private TerrainVertex[]? _lastTransformBackup;

    private void TransformLayer(int layerIdx, Vector3 translate, Vector3 scale, Vector3 rotateEuler)
    {
        if (_model == null || layerIdx < 0 || layerIdx >= _model.Layers.Count) return;

        var layer = _model.Layers[layerIdx];
        int vs = layer.CumulativeVertices;
        int ve = layerIdx+1 < _model.Layers.Count
            ? _model.Layers[layerIdx+1].CumulativeVertices
            : _model.Vertices.Count;

        if (ve <= vs) return;

        // Backup for undo — saves the layer's RAW vertices before transforming
        _lastTransformLayer = layerIdx;
        _lastTransformBackup = new TerrainVertex[ve - vs];
        for (var i = vs; i < ve; i++) _lastTransformBackup[i - vs] = _model.Vertices[i];

        // Compute the layer's bbox center — scale/rotate must happen around
        // it, or the geometry "flies away" from its original spot for a nonzero pivot.
        var bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var i = vs; i < ve; i++)
        {
            var v = _model.Vertices[i];
            var p = new Vector3(v.PosX, v.PosY, v.PosZ);
            bmin = Vector3.Min(bmin, p);
            bmax = Vector3.Max(bmax, p);
        }
        var pivot = (bmin + bmax) * 0.5f;

        // Matrix around the pivot: T(pivot) * T(translate) * R * S * T(-pivot)
        var mat = Matrix4x4.Translate(pivot + translate)
                * Matrix4x4.Rotate(Quaternion.Euler(rotateEuler))
                * Matrix4x4.Scale(scale)
                * Matrix4x4.Translate(-pivot);

        var normalMat = Matrix4x4.Rotate(Quaternion.Euler(rotateEuler));
        var invScale = new Vector3(
            scale.x != 0 ? 1f / scale.x : 1f,
            scale.y != 0 ? 1f / scale.y : 1f,
            scale.z != 0 ? 1f / scale.z : 1f);

        for (var i = vs; i < ve; i++)
        {
            var v = _model.Vertices[i];
            var p = mat.MultiplyPoint3x4(new Vector3(v.PosX, v.PosY, v.PosZ));
            v.PosX = p.x; v.PosY = p.y; v.PosZ = p.z;

            var n = new Vector3(v.NormalX, v.NormalY, v.NormalZ);
            if (n.sqrMagnitude > 0.0001f)
            {
                n = Vector3.Scale(n, invScale);
                n = normalMat.MultiplyVector(n).normalized;
                v.NormalX = n.x; v.NormalY = n.y; v.NormalZ = n.z;
            }

            _model.Vertices[i] = v;
        }

        _dirty = true;

        // Update the scene mesh RIGHT AWAY, without waiting for "Save",
        // so the mapmaker sees the transform's result immediately.
        RefreshSceneMesh();

        var newBmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var newBmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var i = vs; i < ve; i++)
        {
            var v = _model.Vertices[i];
            var p = new Vector3(v.PosX, v.PosY, v.PosZ);
            newBmin = Vector3.Min(newBmin, p);
            newBmax = Vector3.Max(newBmax, p);
        }

        Debug.Log($"[TerrainEditor] Layer [{layerIdx}] transformed: translate={translate} scale={scale} rotate={rotateEuler} (pivot={pivot}).\n" +
                  $"  BBox before: min={bmin} max={bmax}\n" +
                  $"  BBox after:  min={newBmin} max={newBmax}\n" +
                  $"  {ve-vs} vertices. Backup saved — \"Undo\" is available.");
        Repaint();
    }

    private void UndoLastTransform()
    {
        if (_model == null || _lastTransformLayer == null || _lastTransformBackup == null) return;
        var layerIdx = _lastTransformLayer.Value;
        var layer = _model.Layers[layerIdx];
        int vs = layer.CumulativeVertices;
        for (var i = 0; i < _lastTransformBackup.Length; i++)
            _model.Vertices[vs + i] = _lastTransformBackup[i];

        _lastTransformLayer = null;
        _lastTransformBackup = null;
        _dirty = true;
        RefreshSceneMesh();
        Debug.Log($"[TerrainEditor] Layer [{layerIdx}]'s transform undone.");
        Repaint();
    }

    // ================================================================
    // Textures
    // ================================================================
    private void SetTexture(int layerIdx, int slot, string path)
    {
        if (_model == null || layerIdx >= _model.Materials.Count) return;
        _model.Materials[layerIdx].TexturePaths[slot] = path;
        _dirty = true;

        // Update the Unity material in the scene immediately
        RefreshMaterial(layerIdx);
        Debug.Log($"[TerrainEditor] Material [{layerIdx}] slot [{slot}] = {path}");
        Repaint();
    }

    private void RefreshMaterial(int layerIdx)
    {
        var go = GameObject.Find("terrain");
        if (go == null || _model == null) return;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        var mats = mr.sharedMaterials;
        if (layerIdx < mats.Length)
        {
            var newMat = SC2TerrainMaterialFactory.Create(_model.Materials[layerIdx]);
            mats[layerIdx] = newMat;
            mr.sharedMaterials = mats;
        }
    }

    // ================================================================
    // Add / remove layer
    // ================================================================
    private void AddLayer()
    {
        if (_model == null || _newMesh == null) return;

        var newLayer = new SC2TerrainLayer
        {
            CumulativeVertices = _model.Vertices.Count,
            CumulativeIndices  = _model.Triangles.Count * 3
        };
        _model.Layers.Add(newLayer);

        var newMat = new TerrainMaterial { Name = _newLayerName };
        for (var s = 0; s < 13; s++)
            if (!string.IsNullOrEmpty(_newTexPaths[s]))
                newMat.TexturePaths[s] = _newTexPaths[s];
        _model.Materials.Add(newMat);

        var verts = _newMesh.vertices;
        var uv1   = _newMesh.uv;
        var uv2   = _newMesh.uv2;
        var meshNormals = _newMesh.normals;
        int baseIdx = newLayer.CumulativeVertices;

        // [+20..32) is the packed Normal/Tangent/Binormal, which the shader
        // actually reads — garbage there breaks specular (see TerrainVertex's
        // own comment; this is not ignored by the game and not a constant
        // across the vanilla map corpus). Tangent/Binormal are computed from
        // real derivatives of UV1 — here that's the mesh's own UV, so they're
        // taken directly rather than through the main exporter's planar
        // assumption.
        var editorTan = new Vector3[verts.Length];
        var editorBin = new Vector3[verts.Length];
        var meshTris = _newMesh.triangles;
        for (var t = 0; t + 2 < meshTris.Length; t += 3)
        {
            int a = meshTris[t], b1 = meshTris[t + 1], c1 = meshTris[t + 2];
            if (a >= verts.Length || b1 >= verts.Length || c1 >= verts.Length) continue;
            if (a >= uv1.Length || b1 >= uv1.Length || c1 >= uv1.Length) continue;
            var e1 = verts[b1] - verts[a];
            var e2 = verts[c1] - verts[a];
            var d1 = uv1[b1] - uv1[a];
            var d2 = uv1[c1] - uv1[a];
            var den = d1.x * d2.y - d2.x * d1.y;
            if (Mathf.Abs(den) < 1e-12f) continue;
            var f = 1f / den;
            var tv = new Vector3(f * (d2.y * e1.x - d1.y * e2.x), f * (d2.y * e1.y - d1.y * e2.y), f * (d2.y * e1.z - d1.y * e2.z));
            var bv = new Vector3(f * (d1.x * e2.x - d2.x * e1.x), f * (d1.x * e2.y - d2.x * e1.y), f * (d1.x * e2.z - d2.x * e1.z));
            editorTan[a] += tv; editorTan[b1] += tv; editorTan[c1] += tv;
            editorBin[a] += bv; editorBin[b1] += bv; editorBin[c1] += bv;
        }

        for (var i = 0; i < verts.Length; i++)
        {
            var n = i < meshNormals.Length ? meshNormals[i] : Vector3.up;
            if (n.sqrMagnitude < 1e-10f) n = Vector3.up;
            n = n.normalized;
            var tan = editorTan[i] - n * Vector3.Dot(editorTan[i], n);
            tan = tan.sqrMagnitude > 1e-12f ? tan.normalized : Vector3.Cross(Vector3.forward, n).normalized;
            // Stored binormal runs against +V (measured 99.62% on the corpus).
            var bin = editorBin[i].sqrMagnitude > 1e-12f
                ? (-editorBin[i]).normalized
                : Vector3.Cross(n, tan).normalized;

            _model.Vertices.Add(new TerrainVertex
            {
                PosX = verts[i].x, PosY = verts[i].y, PosZ = verts[i].z,
                Uv1U = i < uv1.Length ? uv1[i].x * 1024f : 0f,
                Uv1V = i < uv1.Length ? uv1[i].y * 1024f : 0f,
                Uv2U = i < uv2.Length ? uv2[i].x : 0f,
                Uv2V = i < uv2.Length ? uv2[i].y : 0f,
                NormalX = n.x, NormalY = n.y, NormalZ = n.z,
                TangentX = tan.x, TangentY = tan.y, TangentZ = tan.z,
                BinormalX = bin.x, BinormalY = bin.y, BinormalZ = bin.z,
            });
        }

        var idx = _newMesh.triangles;
        for (var t = 0; t < idx.Length; t += 3)
            _model.Triangles.Add(new TerrainTriangle
            {
                V0 = (uint)(baseIdx + idx[t]),
                V1 = (uint)(baseIdx + idx[t+1]),
                V2 = (uint)(baseIdx + idx[t+2])
            });

        // Reset the new-layer fields
        _newLayerName = "NewLayer";
        _newTexPaths  = new string[13];
        _newMesh      = null;

        _foldouts = new bool[_model.Layers.Count];
        _dirty = true;
        Debug.Log($"[TerrainEditor] Layer added: {verts.Length} vertices.");
        Repaint();
    }

    private void RemoveLayer(int idx)
    {
        if (_model == null || idx < 0 || idx >= _model.Layers.Count) return;

        var layer = _model.Layers[idx];
        int vs = layer.CumulativeVertices;
        int ve = idx+1 < _model.Layers.Count ? _model.Layers[idx+1].CumulativeVertices : _model.Vertices.Count;
        int ts = layer.CumulativeIndices / 3;
        int te = idx+1 < _model.Layers.Count ? _model.Layers[idx+1].CumulativeIndices / 3 : _model.Triangles.Count;
        int dv = ve - vs, di = (te - ts) * 3;

        _model.Vertices.RemoveRange(vs, dv);
        _model.Triangles.RemoveRange(ts, te - ts);
        _model.Layers.RemoveAt(idx);
        if (idx < _model.Materials.Count) _model.Materials.RemoveAt(idx);

        for (var i = idx; i < _model.Layers.Count; i++)
        {
            _model.Layers[i].CumulativeVertices -= dv;
            _model.Layers[i].CumulativeIndices  -= di;
        }
        for (var t = ts; t < _model.Triangles.Count; t++)
        {
            var tri = _model.Triangles[t];
            tri.V0 -= (uint)dv; tri.V1 -= (uint)dv; tri.V2 -= (uint)dv;
            _model.Triangles[t] = tri;
        }

        _foldouts = new bool[_model.Layers.Count];
        _dirty = true;
        Repaint();
    }

    // ================================================================
    // Recompute hfield from the terrain vertices
    // ================================================================
    private void RegenerateHfield()
    {
        if (_rawData == null || _model == null) return;

        const int MAP_W = 1025; const int MAP_H = 1025;
        // hfield grid: 1025x1025 points = (mapX-1)x(mapY-1) = 1024x1024 cells
        // terrain world space: X,Z in the range [0, 1024]

        // Rasterization lives in SC2HfieldProjectionService — a single
        // implementation shared with SC2TerrainExporter (procedural export),
        // so the two entry points can't drift apart on the algorithm. The
        // height at every grid point EXACTLY matches the terrain mesh's
        // surface (barycentric interpolation across triangles, not rounding
        // vertex coordinates), and water layers are excluded from the
        // computation — see the service's own comments.

        // Read the original hfield for terrain_types (passthrough)
        var origHfield = _rawData.GetHfieldData();
        ushort[] terrainTypes;
        if (origHfield.Length >= 28 + MAP_W * MAP_H * 2 + 1024 * 1024 * 2)
        {
            terrainTypes = new ushort[1024 * 1024];
            System.Buffer.BlockCopy(origHfield, 28 + MAP_W * MAP_H * 2, terrainTypes, 0, 1024 * 1024 * 2);
        }
        else terrainTypes = new ushort[1024 * 1024];

        var hmodel = SC2HfieldProjectionService.Project(_model, terrainTypes, MAP_W, MAP_H);

        var hfContainer = HfieldBdf.Create(hmodel, _rawData.Platform);
        WriteBytes("hfield.bytes", "hfield_ot.bytes", hfContainer.Data, hfContainer.OffsetTable);
        AssetDatabase.ImportAsset(GetAssetRelPath("hfield.bytes"),    ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(GetAssetRelPath("hfield_ot.bytes"), ImportAssetOptions.ForceSynchronousImport);
        _rawData.HfieldBytes   = AssetDatabase.LoadAssetAtPath<TextAsset>(GetAssetRelPath("hfield.bytes"));
        _rawData.HfieldOtBytes = AssetDatabase.LoadAssetAtPath<TextAsset>(GetAssetRelPath("hfield_ot.bytes"));

        Debug.Log("[TerrainEditor] hfield recomputed from the terrain vertices.");
    }

    // ================================================================
    // Scene mesh refresh
    // ================================================================
    private void RefreshSceneMesh()
    {
        if (_model == null) return;
        var go = GameObject.Find("terrain");
        if (go == null) return;
        var mf = go.GetComponent<MeshFilter>();
        var mr = go.GetComponent<MeshRenderer>();
        if (mf != null)
        {
            var mesh = TerrainMeshBuilder.BuildMesh(_model);
            mf.sharedMesh = mesh;
            var col = go.GetComponent<MeshCollider>();
            if (col != null) col.sharedMesh = mesh;
        }
        if (mr != null)
        {
            mr.sharedMaterials = _model.Materials
                .Select(m => SC2TerrainMaterialFactory.Create(m))
                .ToArray();
        }
        EditorUtility.SetDirty(go);
    }

    // ================================================================
    // Helpers
    // ================================================================
    private string[] LayerNames() => _model == null
        ? System.Array.Empty<string>()
        : _model.Layers.Select((_, i) =>
            $"[{i}] {(i < _model.Materials.Count ? _model.Materials[i].Name : "?")}").ToArray();

    private void WriteBytes(string name, string otName, byte[] data, uint[] ot)
    {
        if (_rawData == null) return;
        // GetAssetPath returns "Assets/SC2Maps/MapName/..."
        // Convert to an absolute path via Application.dataPath
        var assetDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_rawData))!
            .Replace('\\', '/');
        // "Assets/..." -> absolute path
        var absDir = assetDir.StartsWith("Assets")
            ? System.IO.Path.Combine(Application.dataPath, assetDir["Assets/".Length..])
            : assetDir;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(absDir, name), data);
        var otB = new byte[ot.Length*4]; System.Buffer.BlockCopy(ot, 0, otB, 0, otB.Length);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(absDir, otName), otB);
    }

}
#endif
