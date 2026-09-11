#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class TerrainLayerVisibilityWindow : EditorWindow
{
    [MenuItem("SC2/Scene Visibility")]
    public static void ShowWindow() => GetWindow<TerrainLayerVisibilityWindow>("SC2 Visibility");

    private bool   _terrainSplit;
    private readonly List<(string Name, GameObject Go)> _terrainLayers = new();
    private string _markerFilter = "";
    private string _graphFilter  = "";
    private string _propFilter   = "";
    private int    _activeCosts  = 0;
    private Vector2 _scroll;

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // ===== TERRAIN =====
        H("Terrain");
        var tGo = Find("terrain");
        if (tGo != null)
        {
            if (!_terrainSplit)
            {
                RRow("terrain (all)", tGo);
                if (GUILayout.Button("Split into layers")) SplitTerrain(tGo);
            }
            else
            {
                foreach (var (n, go) in _terrainLayers)
                    if (go != null) RRow($"  {n}", go);
                EditorGUILayout.BeginHorizontal();
                if (Btn("All on"))  foreach (var (_,g) in _terrainLayers) if (g) SetRend(g, true);
                if (Btn("All off")) foreach (var (_,g) in _terrainLayers) if (g) SetRend(g, false);
                if (Btn("Restore"))   MergeTerrain(tGo);
                EditorGUILayout.EndHorizontal();
            }
        }
        Sp();

        // ===== HEIGHTMAP / COLLISION2 =====
        H("Heightmap / Collision2");
        RRow("Heightmap",  Find("Heightmap"));
        RRow("Collision2", Find("Collision2"));
        Sp();

        // ===== COSTS (radio) =====
        H("Costs (one active)");
        var costsRoot = Find("Costs");
        if (costsRoot != null)
        {
            if (Btn("All off", 80)) { _activeCosts = -1; SetCostsBlock(costsRoot, -1); }
            for (var i = 0; i < costsRoot.transform.childCount; i++)
            {
                var child = costsRoot.transform.GetChild(i).gameObject;
                var mr = child.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                EditorGUILayout.BeginHorizontal();
                bool was = mr.enabled;
                bool now = EditorGUILayout.Toggle(was, GUILayout.Width(SC2UiScale.Pxf(20f)));
                EditorGUILayout.LabelField(child.name);
                EditorGUILayout.EndHorizontal();
                if (now != was) { _activeCosts = now ? i : -1; SetCostsBlock(costsRoot, _activeCosts); }
            }
        }
        Sp();

        // ===== PROPS filtered by .bp name =====
        H("Props");
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Filter .bp:", GUILayout.Width(SC2UiScale.Pxf(65f)));
        _propFilter = EditorGUILayout.TextField(_propFilter);
        if (Btn("×", 22)) { _propFilter = ""; GUI.FocusControl(null); }
        EditorGUILayout.EndHorizontal();

        var propsRoot = Find("Props");
        if (propsRoot != null)
        {
            // "All" buttons
            EditorGUILayout.BeginHorizontal();
            if (Btn("All on",  70)) SetAll(propsRoot, true);
            if (Btn("All off", 70)) SetAll(propsRoot, false);
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(_propFilter))
            {
                // One combined checkbox
                bool allOn = IsAllActive(propsRoot);
                EditorGUILayout.BeginHorizontal();
                bool newOn = EditorGUILayout.Toggle(allOn, GUILayout.Width(SC2UiScale.Pxf(20f)));
                EditorGUILayout.LabelField($"All props ({propsRoot.transform.childCount})");
                EditorGUILayout.EndHorizontal();
                if (newOn != allOn) SetAll(propsRoot, newOn);
            }
            else
            {
                // Grouped by bp file name
                var groups = new SortedDictionary<string, List<GameObject>>();
                for (var i = 0; i < propsRoot.transform.childCount; i++)
                {
                    var child = propsRoot.transform.GetChild(i).gameObject;
                    var prop  = child.GetComponent<SC2Prop>();
                    var bp    = prop != null
                        ? System.IO.Path.GetFileNameWithoutExtension(prop.BlueprintPath)
                        : child.name;
                    if (!bp.Contains(_propFilter, System.StringComparison.OrdinalIgnoreCase)) continue;
                    if (!groups.ContainsKey(bp)) groups[bp] = new List<GameObject>();
                    groups[bp].Add(child);
                }
                if (groups.Count == 0)
                    EditorGUILayout.LabelField("  No props match the filter");
                foreach (var (bp, objs) in groups)
                {
                    bool on = objs.All(o => o.activeSelf);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(8);
                    bool nw = EditorGUILayout.Toggle(on, GUILayout.Width(SC2UiScale.Pxf(20f)));
                    EditorGUILayout.LabelField($"{bp} ({objs.Count})");
                    EditorGUILayout.EndHorizontal();
                    if (nw != on) foreach (var o in objs) o.SetActive(nw);
                }
            }
        }
        Sp();

        // ===== MARKERS filtered by MarkerType =====
        H("Markers");
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Type:", GUILayout.Width(SC2UiScale.Pxf(35f)));
        _markerFilter = EditorGUILayout.TextField(_markerFilter);
        if (Btn("×", 22)) { _markerFilter = ""; GUI.FocusControl(null); }
        EditorGUILayout.EndHorizontal();

        var markRoot = Find("Markers");
        if (markRoot != null)
        {
            // All on/off buttons — via SetActive
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            if (Btn("All on",  70)) SetAll(markRoot, true);
            if (Btn("All off", 70)) SetAll(markRoot, false);
            EditorGUILayout.EndHorizontal();

            // Grouped by MarkerType
            var byType = new SortedDictionary<string, List<GameObject>>();
            for (var i = 0; i < markRoot.transform.childCount; i++)
            {
                var child = markRoot.transform.GetChild(i).gameObject;
                var mk = child.GetComponent<SC2Marker>();
                var t  = mk != null && !string.IsNullOrEmpty(mk.MarkerType)
                    ? mk.MarkerType : "(no type)";
                if (!string.IsNullOrEmpty(_markerFilter) &&
                    !t.Contains(_markerFilter, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!byType.ContainsKey(t)) byType[t] = new List<GameObject>();
                byType[t].Add(child);
            }

            if (byType.Count == 0 && !string.IsNullOrEmpty(_markerFilter))
                EditorGUILayout.LabelField("  No markers match the filter");

            foreach (var (type, objs) in byType)
            {
                bool on = objs.All(o => o.activeSelf);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(8);
                bool nw = EditorGUILayout.Toggle(on, GUILayout.Width(SC2UiScale.Pxf(20f)));
                EditorGUILayout.LabelField($"{type} ({objs.Count})");
                EditorGUILayout.EndHorizontal();
                if (nw != on) foreach (var o in objs) o.SetActive(nw);
            }
        }
        Sp();

        // ===== GRAPHS filtered =====
        H("Graphs");
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Filter:", GUILayout.Width(SC2UiScale.Pxf(50f)));
        _graphFilter = EditorGUILayout.TextField(_graphFilter);
        if (Btn("×", 22)) { _graphFilter = ""; GUI.FocusControl(null); }
        EditorGUILayout.EndHorizontal();

        var graphRoot = Find("Graphs");
        if (graphRoot != null)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(8);
            if (Btn("All on",  70)) SetAll(graphRoot, true);
            if (Btn("All off", 70)) SetAll(graphRoot, false);
            EditorGUILayout.EndHorizontal();

            for (var i = 0; i < graphRoot.transform.childCount; i++)
            {
                var child = graphRoot.transform.GetChild(i).gameObject;
                if (!string.IsNullOrEmpty(_graphFilter) &&
                    !child.name.Contains(_graphFilter, System.StringComparison.OrdinalIgnoreCase)) continue;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(8);
                bool was = child.activeSelf;
                bool now = EditorGUILayout.Toggle(was, GUILayout.Width(SC2UiScale.Pxf(20f)));
                EditorGUILayout.LabelField(child.name);
                EditorGUILayout.EndHorizontal();
                if (now != was) child.SetActive(now);
            }
        }
        Sp();

        // ===== ENVIRONMENT =====
        H("Environment");
        RRow("Environment", Find("Environment"));

        EditorGUILayout.EndScrollView();
    }

    // ---- Terrain split ----
    private void SplitTerrain(GameObject go)
    {
        var mf = go.GetComponent<MeshFilter>();
        var mr = go.GetComponent<MeshRenderer>();
        if (mf == null || mr == null || mf.sharedMesh == null) return;
        var mesh = mf.sharedMesh; var mats = mr.sharedMaterials;
        mr.enabled = false; _terrainLayers.Clear();
        for (var i = 0; i < mesh.subMeshCount; i++)
        {
            var nm = i < mats.Length && mats[i] ? mats[i].name : $"mat_{i}";
            var child = new GameObject($"tl_{i}_{nm}");
            child.transform.SetParent(go.transform, false);
            var sub = new Mesh { name = nm, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            sub.vertices = mesh.vertices; sub.normals = mesh.normals;
            sub.uv = mesh.uv; sub.uv2 = mesh.uv2; sub.subMeshCount = 1;
            sub.SetIndices(mesh.GetIndices(i), MeshTopology.Triangles, 0);
            sub.RecalculateBounds();
            child.AddComponent<MeshFilter>().sharedMesh = sub;
            child.AddComponent<MeshRenderer>().sharedMaterial = i < mats.Length ? mats[i] : null;
            _terrainLayers.Add((nm, child));
        }
        _terrainSplit = true;
    }

    private void MergeTerrain(GameObject go)
    {
        foreach (var (_,c) in _terrainLayers) if (c) DestroyImmediate(c);
        _terrainLayers.Clear(); _terrainSplit = false;
        var mr = go.GetComponent<MeshRenderer>(); if (mr) mr.enabled = true;
    }

    // ---- Helpers ----
    private static void H(string s) => EditorGUILayout.LabelField(s, EditorStyles.boldLabel);
    private static void Sp() => GUILayout.Space(5);
    private static bool Btn(string s, int w = 0) =>
        w > 0 ? GUILayout.Button(s, GUILayout.Width(w)) : GUILayout.Button(s);

    private static void RRow(string label, GameObject? go)
    {
        if (go == null) return;
        var r = go.GetComponent<Renderer>(); if (!r) return;
        EditorGUILayout.BeginHorizontal();
        bool v = EditorGUILayout.Toggle(r.enabled, GUILayout.Width(SC2UiScale.Pxf(20f)));
        EditorGUILayout.LabelField(label);
        EditorGUILayout.EndHorizontal();
        if (v != r.enabled) r.enabled = v;
    }

    // SetActive for every child object
    private static void SetAll(GameObject root, bool active)
    {
        for (var i = 0; i < root.transform.childCount; i++)
            root.transform.GetChild(i).gameObject.SetActive(active);
    }

    private static bool IsAllActive(GameObject root)
    {
        for (var i = 0; i < root.transform.childCount; i++)
            if (!root.transform.GetChild(i).gameObject.activeSelf) return false;
        return root.transform.childCount > 0;
    }

    // SetRend for terrain layers (they have a MeshRenderer)
    private static void SetRend(GameObject go, bool enabled)
    {
        var r = go.GetComponent<Renderer>(); if (r) r.enabled = enabled;
    }

    private static void SetCostsBlock(GameObject root, int activeIdx)
    {
        for (var i = 0; i < root.transform.childCount; i++)
        {
            var mr = root.transform.GetChild(i).GetComponent<MeshRenderer>();
            if (mr) mr.enabled = (i == activeIdx);
        }
    }

    private static GameObject? Find(string n) => GameObject.Find(n);
}
#endif
