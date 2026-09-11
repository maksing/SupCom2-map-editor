using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using SC2.MapEditor.Document;
using SC2.MapEditor.Textures;

// ── ShaderSetup entry ─────────────────────────────────────────────────────────
// ════════════════════════════════════════════════════════════════════════════
// SC2MaterialsMode
// ════════════════════════════════════════════════════════════════════════════
public class SC2MaterialsMode
{
    public SC2TerrainData Data;
    private SC2TerrainShell _win;

    private static readonly Color[] kDefaultColors =
    {
        new(0.20f,0.72f,0.30f), new(0.30f,0.50f,1.00f), new(0.92f,0.20f,0.20f),
        new(1.00f,0.72f,0.10f), new(0.72f,0.30f,1.00f), new(0.20f,0.90f,0.90f),
        new(1.00f,0.42f,0.10f), new(0.52f,0.90f,0.20f), new(0.90f,0.20f,0.72f),
    };

    public SC2MaterialsMode(SC2TerrainShell win, SC2TerrainData data) { _win=win; Data=data; }

    /// <summary>Keyed by SC2Material.Id (stable across list reindexing —
    /// same reasoning as RelinkPairsToMaterials using Ids, not indices) so a
    /// material's collapsed/expanded state survives adding/removing other
    /// materials. Absent from the set = expanded (matches this panel's
    /// previous always-expanded behavior, so existing maps don't suddenly
    /// look "empty" the first time this loads).</summary>
    private readonly HashSet<string> _collapsedMaterialIds = new();

    public void DrawPanel()
    {
        Data.EnsureStableIds();

        if (GUILayout.Button("+ New Material", GUILayout.Height(SC2UiScale.Pxf(26f))))
        {
            _win.RecordUndoCheckpointFor(Data, "Add Material");
            Data.Materials.Add(new SC2Material
            {
                Name  = $"Material {Data.Materials.Count+1}",
                Color = kDefaultColors[Data.Materials.Count % kDefaultColors.Length]
            });
            Data.EnsureSerializableState();
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }

        for (var i = 0; i < Data.Materials.Count; i++)
            DrawMaterialBlock(i);

        if (GUI.changed) { EditorUtility.SetDirty(Data); _win.Repaint(); }
    }

    private void DrawMaterialBlock(int i)
    {
        var mat = Data.Materials[i];
        SC2TerrainShell.BeginUnifiedPanelBox();

        var expanded = !_collapsedMaterialIds.Contains(mat.Id);
        EditorGUILayout.BeginHorizontal();
        var newExpanded = EditorGUILayout.Foldout(expanded, "", true, EditorStyles.foldout);
        if (newExpanded != expanded)
        {
            if (newExpanded) _collapsedMaterialIds.Remove(mat.Id);
            else _collapsedMaterialIds.Add(mat.Id);
        }
        var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
        EditorGUI.DrawRect(swatch, mat.Color);
        var newMaterialName = EditorGUILayout.TextField(mat.Name, GUILayout.MinWidth(44), GUILayout.ExpandWidth(true));
        if (newMaterialName != mat.Name)
        {
            // Rename had no undo checkpoint at all before this (unlike Color,
            // just below) — routed through the real ISC2DocumentCommand
            // instead of _win.RecordUndoCheckpoint so it also updates
            // SC2MapDocument.Terrain, not just SC2TerrainData.
            // Gated on Data==_win.Data — this mode class is now
            // also reused for the decorative background terrain
            // (SC2TerrainShell._bgMatMode, Data==BackgroundTerrainData
            // there), which has no representation in Session.Document.Terrain
            // at all (by design, never persisted there). Without this guard,
            // renaming a background material would silently ADD a phantom
            // entry to the real map's document material list via
            // EnsureMaterialDocument.
            if (_win.Data == Data)
            {
                EnsureMaterialDocument(mat);
                _win.ExecuteCommand(new SC2SetMaterialNameCommand(mat.Id, newMaterialName));
            }
            mat.Name = newMaterialName;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        var materialColor = EditorGUILayout.ColorField(new GUIContent("", $"material-color-{i}"), mat.Color, false, false, false, GUILayout.Width(SC2UiScale.Pxf(42f)), GUILayout.Height(SC2UiScale.Pxf(20f)));
        if (materialColor != mat.Color)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Material Color");
            mat.Color = materialColor;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        if (GUILayout.Button("x", GUILayout.Width(SC2UiScale.Pxf(20f))) &&
            EditorUtility.DisplayDialog("Delete", $"Delete '{mat.Name}'?", "Yes", "Cancel"))
        {
            _win.RecordUndoCheckpointFor(Data, "Remove Material");
            Data.Materials.RemoveAt(i);
            // Re-resolve every pair's Mat1Index/Mat2Index from its stable
            // Mat1Id/Mat2Id against the NEW Materials list, rather than
            // manually shifting indices > i. A pair that referenced the
            // just-deleted material's Id correctly becomes unassigned (its
            // Id no longer resolves); every other pair keeps pointing at
            // the SAME material regardless of how the list reindexed — this
            // is exactly the confirmed cross-pair reference-reset bug class
            // the audit found in the old shift-in-place arithmetic.
            RelinkPairsToMaterials();
            EditorUtility.SetDirty(Data); _win.MarkDirtyAndRepaint();
            EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical();
            return;
        }
        EditorGUILayout.EndHorizontal();

        if (newExpanded)
        {
            // siblingHint: whichever OTHER slot on this same material
            // already has a texture — lets the browser suggest this
            // slot's counterpart by real vanilla suffix convention
            // ("_d"/"_n"/"_s"/"_m") instead of leaving the user to search
            // 1000+ textures for the matching set by hand (see
            // SC2TextureCatalog.TryFindSiblingMatch). Diffuse is preferred
            // as the hint since it's the slot most commonly picked first.
            DrawTextureField(i, "Diffuse", ref mat.Diffuse, ref mat.DiffuseOverridePath, mat.Normal != null ? mat.Normal : mat.Mask);
            DrawTextureField(i, "Normal", ref mat.Normal, ref mat.NormalOverridePath, mat.Diffuse != null ? mat.Diffuse : mat.Mask);
            DrawTextureField(i, "Mask", ref mat.Mask, ref mat.MaskOverridePath, mat.Diffuse != null ? mat.Diffuse : mat.Normal);
        }
        SC2TerrainShell.EndUnifiedPanelBox();
        EditorGUILayout.Space(2);
    }

    /// <summary>Thin wrapper — the actual logic lives in
    /// SC2MaterialPairLinker (Scripts/TerrainEditor/SC2MaterialPairLinker.cs),
    /// which has no dependency on SC2TerrainShell/UnityEditor and so can sit
    /// in SC2.Core.asmdef where an EditMode test can call it directly.</summary>
    public void RelinkPairsToMaterials() => SC2MaterialPairLinker.RelinkPairsToMaterials(Data);

    /// <summary>Nothing currently keeps SC2MapDocument.Terrain.Materials
    /// synced with live SC2TerrainData.Materials (that full-sync job is out
    /// of scope for this rename-only slice — see the Terrain UI cutover
    /// status doc). Rather than requiring that larger sync to exist first,
    /// this lazily creates the matching document entry the very first time
    /// a material is renamed, keyed by the same stable Id both sides
    /// already share, so SC2SetMaterialNameCommand always has somewhere
    /// real to record Undo/Redo against.</summary>
    private void EnsureMaterialDocument(SC2Material mat)
    {
        var terrain = _win.Session?.Document?.Terrain;
        if (terrain == null)
            return;
        if (terrain.FindMaterial(mat.Id) == null)
            terrain.Materials.Add(new SC2MaterialDocument { Id = mat.Id, Name = mat.Name });
    }

    private void DrawTextureField(int materialIndex, string label, ref Texture2D texture, ref string overridePath, Texture2D siblingHint = null)
    {
        var previous = texture;
        var previousPath = overridePath;
        // Checkpoint BEFORE the field is actually reassigned below — texture
        // is a ref straight into mat.Diffuse/Normal/Mask, so recording after
        // the assignment would snapshot the already-new value as the undo
        // target (same "record before, not after" rule as everywhere else).
        var pathDraft = overridePath ?? "";
        // With no override set yet, the field
        // showed blank instead of the path the texture would actually
        // resolve to — fall back to the catalog's already-known resource
        // path for display (never written into overridePath unless the user
        // actually edits it).
        var fallbackPath = texture != null ? SC2TerrainMaterialFactory.TryGetResourcePath(texture) : null;
        var next = SC2TerrainShell.DrawTexturePickerWithPath(label, texture, "Material" + materialIndex + ":" + label, ref pathDraft, out var pickedViaBrowser, fallbackPath, siblingHint) as Texture2D;
        if (next != previous)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Material " + label + " Texture");
            var material = Data.Materials[materialIndex];
            // See the rename handler's own comment above for why this is
            // gated on Data==_win.Data (background materials have no
            // document representation to lazily create).
            if (_win.Data == Data)
            {
                EnsureMaterialDocument(material);
                _win.ExecuteCommand(new SC2SetMaterialTextureCommand(material.Id, TextureSlot(label), BuildTextureReference(next)));
            }
            // A fresh browser pick used to ALWAYS clear
            // the override path, on the assumption Unity's own asset
            // reference is enough to persist — true only for a real
            // Resources-backed asset (a plain resourcePath). A Custom- or
            // archive-sourced pick resolves to an in-memory Texture2D with
            // no real project asset/GUID (SC2TextureBrowser.ResolveRealAsset
            // via SC2CustomTextureImporter.TryLoadAny/TryLoadFromArchive) —
            // Unity CANNOT serialize a reference to that object at all, so
            // clearing the one thing (the override path) that could have
            // re-resolved it on the next load silently lost the texture the
            // moment the map was reopened. Keep the override path (set to
            // the just-picked texture's own resolved path) for anything that
            // isn't a plain Resources-backed asset; only clear it for a real
            // asset pick, where it would just be redundant.
            if (pickedViaBrowser)
            {
                var resolvedPath = next != null ? SC2TerrainMaterialFactory.TryGetResourcePath(next) : null;
                var isRealAsset = string.IsNullOrEmpty(resolvedPath) ||
                    (!resolvedPath.StartsWith("file:", StringComparison.Ordinal) &&
                     !resolvedPath.StartsWith("archive:", StringComparison.Ordinal));
                pathDraft = isRealAsset ? "" : resolvedPath;
            }
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        if (pathDraft != previousPath)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Material " + label + " Path");
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        texture = next;
        overridePath = pathDraft;
    }

    private static SC2MaterialTextureSlot TextureSlot(string label)
    {
        if (string.Equals(label, "Normal", StringComparison.OrdinalIgnoreCase)) return SC2MaterialTextureSlot.Normal;
        if (string.Equals(label, "Mask", StringComparison.OrdinalIgnoreCase)) return SC2MaterialTextureSlot.Mask;
        return SC2MaterialTextureSlot.Diffuse;
    }

    private static SC2TextureReference BuildTextureReference(Texture2D texture)
    {
        if (texture == null) return new SC2TextureReference();
        var path = SC2TerrainMaterialFactory.TryGetResourcePath(texture) ?? "";
        var custom = path.StartsWith("file:", StringComparison.Ordinal);
        var identityPath = custom ? path.Substring(5) : path;
        var catalogEntry = custom
            ? SC2TextureCatalog.Instance.Find(SC2TextureCatalogEntry.BuildStableId(SC2TextureSource.Custom, identityPath))
            : SC2TextureCatalog.Instance.FindByResourcePath(identityPath);
        return new SC2TextureReference
        {
            Id = catalogEntry?.Id ?? (identityPath.Length > 0 ? (custom ? "custom:" : "resource:") + identityPath.ToLowerInvariant() : ""),
            Name = texture.name ?? "",
            ResourcePath = custom ? "" : identityPath,
            GamePath = custom ? identityPath : catalogEntry?.GamePath ?? "",
            Source = custom ? "Custom" : catalogEntry?.Source.ToString() ?? "Vanilla",
            ContentHash = catalogEntry?.ContentHash ?? "",
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// SC2PairsMode
// ════════════════════════════════════════════════════════════════════════════
public class SC2PairsMode
{
    public SC2TerrainData Data;
    private SC2TerrainShell _win;
    private int     _sel = -1;

    /// <summary>Row-highlight rects, one frame stale — see DrawRow's comment
    /// for why: GUILayoutUtility.GetLastRect() is only safe to call right
    /// after EndHorizontal (once that group is genuinely the "last"
    /// completed layout entry), which is AFTER the row's own content already
    /// drew. Caching last frame's rect and drawing the highlight from it
    /// BEFORE this frame's content keeps the highlight correctly layered
    /// behind the swatches/labels/buttons instead of painted over them.</summary>
    private readonly Dictionary<int, Rect> _rowRects = new();
    private int _presetPickIndex;

    // Default SC2 shader flags — the real, complete 12-flag set confirmed by
    // a direct probe of all 56 vanilla maps' terrain.bdf ShaderSetup entries
    // (no other flag names exist anywhere in the corpus). ALPHA_TEST's
    // Value here still only tracks presence (true/false), same as every
    // other flag — its real numeric threshold lives separately on
    // SC2MaterialPair.AlphaTestThreshold and gets a dedicated inline slider
    // in DrawDetails instead of a plain checkbox+label row.
    private static readonly (string key, bool def)[] kShaderFlags =
    {
        ("AMBIENT_LIGHTING",     true),
        ("DIFFUSE_LIGHTING",     true),
        ("SPECULAR_LIGHTING",    true),
        ("RNM_LIGHTING",         true),
        ("BLENDING",             true),
        ("FOG",                  true),
        ("DYNAMIC_SHADOWS",      true),
        ("REFLECT",              true),
        ("CHROMATIC_DISPERSION", true),
        ("GLOW",                 false),
        ("ENVIRONMENT_MAPPING",  false),
        ("INFINITE_DISTANCE",    false),
        ("ALPHA_TEST",           false),
    };

    // Real State values confirmed by the same 56-map probe (WaterState
    // excluded — water pairs are authored via SC2WaterPlane, a wholly
    // separate path from SC2MaterialsPairsMode).
    private static readonly string[] kStateOptions =
    {
        "None", "TerrainAlphaBlendState", "TerrainAdditiveState", "TerrainOpaqueState",
    };

    public SC2PairsMode(SC2TerrainShell win, SC2TerrainData data) { _win=win; Data=data; }
    public int SelectedPairIdx => _sel;

    public void DrawPanel()
    {
        Data.EnsureStableIds();

        if (Data.Materials.Count < 2)
        { SC2TerrainShell.DrawUnifiedNote("Create at least 2 materials first."); return; }
        if (Data.Pairs.Count >= 72)
            SC2TerrainShell.DrawUnifiedNote("Limit: 72 pairs.");

        EditorGUI.BeginDisabledGroup(Data.Pairs.Count >= 72);
        if (GUILayout.Button("+ New Pair", GUILayout.Height(SC2UiScale.Pxf(26f))))
        {
            _win.RecordUndoCheckpointFor(Data, "Add Pair");
            var mat1Index = 0;
            var mat2Index = Mathf.Min(1, Data.Materials.Count-1);
            var np = new SC2MaterialPair
            {
                Name       = $"Pair {Data.Pairs.Count+1}",
                Mat1Index  = mat1Index,
                Mat2Index  = mat2Index,
                Mat1Id     = Data.Materials[mat1Index].Id,
                Mat2Id     = Data.Materials[mat2Index].Id,
                ShaderFlags = DefaultFlags(),
            };
            Data.Pairs.Add(np);
            _sel = Data.Pairs.Count-1;
            // If this is the first pair, auto-assign every triangle to it
            if (Data.Pairs.Count == 1) Data.AutoAssignFirstPair();
            Data.EnsureSerializableState();
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(4);
        for (var i = 0; i < Data.Pairs.Count; i++) DrawRow(i);
        if (_sel >= 0 && _sel < Data.Pairs.Count) { EditorGUILayout.Space(6); DrawDetails(_sel); }
        if (GUI.changed) { EditorUtility.SetDirty(Data); _win.Repaint(); }
    }

    private static int CountTris(short[] idx, int pairIdx)
    {
        var n=0;
        foreach(var v in idx) if(v==pairIdx)n++;
        return n;
    }

    private void DrawRow(int i)
    {
        var pair = Data.Pairs[i];
        var c1 = pair.PairColor;
        var c2 = pair.PairColor * 0.7f;

        // Drawn from LAST frame's rect (see _rowRects's doc comment) so the
        // highlight paints behind this frame's row content, not on top of it.
        if (_sel == i && Event.current.type == EventType.Repaint && _rowRects.TryGetValue(i, out var lastRect))
            EditorGUI.DrawRect(lastRect, new Color(0.22f, 0.38f, 0.55f));

        EditorGUILayout.BeginHorizontal(GUILayout.Height(SC2UiScale.Pxf(28f)));

        var swatch1 = GUILayoutUtility.GetRect(SC2UiScale.Pxf(12f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(12f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
        EditorGUI.DrawRect(swatch1, c1);
        var swatch2 = GUILayoutUtility.GetRect(SC2UiScale.Pxf(12f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(12f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
        EditorGUI.DrawRect(swatch2, c2);

        EditorGUILayout.LabelField(
                Data.PairTriCounts != null && i < Data.PairTriCounts.Length
                    ? $"{Data.PairTriCounts[i]}t"
                    : (Data.TrianglePairIndex.Length > 0 ? $"{CountTris(Data.TrianglePairIndex, i)}t" : "?t"),
                GUILayout.Width(SC2UiScale.Pxf(44f)));
        var pairNameStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };
        EditorGUILayout.LabelField(pair.Name, pairNameStyle, GUILayout.MinWidth(32), GUILayout.ExpandWidth(true));

        if (GUILayout.Button(_sel == i ? "v" : ">", GUILayout.Width(SC2UiScale.Pxf(22f)))) _sel = _sel==i ? -1 : i;
        if (GUILayout.Button("x", GUILayout.Width(SC2UiScale.Pxf(20f))) &&
            EditorUtility.DisplayDialog("Delete", $"Delete '{pair.Name}'?", "Yes", "Cancel"))
        {
            _win.RecordUndoCheckpointFor(Data, "Remove Pair");
            // Clear pair from index array and shift higher indices down
            for (var t=0;t<Data.TrianglePairIndex.Length;t++)
            {
                if (Data.TrianglePairIndex[t]==i) Data.TrianglePairIndex[t]=-1;
                else if (Data.TrianglePairIndex[t]>i) Data.TrianglePairIndex[t]--;
            }
            Data.Pairs.RemoveAt(i);
            if (_sel >= Data.Pairs.Count) _sel = Data.Pairs.Count-1;
            EditorUtility.SetDirty(Data); _win.MarkDirtyAndRepaint();
        }
        EditorGUILayout.EndHorizontal();
        // GetLastRect() here (right after EndHorizontal, not from BeginHorizontal's
        // own return value) is the safe way to get a just-closed group's rect
        // with plain GUILayout - see UnityEditorRuntimeShim.cs's BeginHorizontal
        // comment for why the old technique (capturing it at Begin-time) doesn't
        // work outside the real Editor. Cached for NEXT frame's highlight draw
        // above, not drawn here, since drawing it now would paint over the
        // content this same row just drew.
        if (Event.current.type == EventType.Repaint)
            _rowRects[i] = GUILayoutUtility.GetLastRect();
    }

    private void DrawDetails(int i)
    {
        var pair = Data.Pairs[i];
        var newPairName = EditorGUILayout.TextField("Name", pair.Name);
        if (newPairName != pair.Name)
        {
            // Same gap as Material rename — Pair rename had no undo
            // checkpoint at all (unlike PairColor, just below). Same
            // Data==_win.Data guard as SC2MaterialsMode's rename handler —
            // see that comment for why (this mode is also reused for the
            // background terrain's own pair, which has no document entry).
            if (_win.Data == Data)
            {
                EnsurePairDocument(pair);
                _win.ExecuteCommand(new SC2SetMaterialPairNameCommand(pair.Id, newPairName));
            }
            pair.Name = newPairName;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        var pairColor = EditorGUILayout.ColorField(new GUIContent("Pair Color", $"pair-color-{i}"), pair.PairColor, false, false, false, GUILayout.Height(SC2UiScale.Pxf(24f)));
        if (pairColor != pair.PairColor)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Pair Color");
            pair.PairColor = pairColor;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        var newRnmMaskName = EditorGUILayout.TextField("RNM Mask", pair.RnmMaskName);
        if (newRnmMaskName != pair.RnmMaskName)
        {
            _win.RecordUndoCheckpointFor(Data, "Change RNM Mask Name");
            pair.RnmMaskName = newRnmMaskName;
        }

        var globalUvActive = _win.Data != null && _win.Data.GlobalUvScaleEnabled;
        EditorGUI.BeginDisabledGroup(globalUvActive);
        var newTilingScale = Mathf.Max(0.01f, EditorGUILayout.FloatField("Tiling Scale", pair.TilingScale));
        if (newTilingScale != pair.TilingScale)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Tiling Scale");
            pair.TilingScale = newTilingScale;
        }
        EditorGUI.EndDisabledGroup();
        if (globalUvActive)
            SC2TerrainShell.DrawUnifiedNote("Overridden by Global UV Scale (Background tab) — currently " +
                $"{_win.Data.GlobalUvScale:0.##} world units for every pair, core and background alike.");

        pair.SimplifyRatio = SC2TerrainShell.DrawCompactSlider("Simplify", pair.SimplifyRatio, 0f, 1f, $"pair-{i}",
            onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change Simplify Ratio"));

        EditorGUILayout.Space(4);
        var newMat1Index = MatPicker(i, "D1", pair.Mat1Index);
        var newMat2Index = MatPicker(i, "D2", pair.Mat2Index);
        if (newMat1Index != pair.Mat1Index || newMat2Index != pair.Mat2Index)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Pair Material Link");
            var newMat1Id = newMat1Index >= 0 && newMat1Index < Data.Materials.Count ? Data.Materials[newMat1Index].Id : "";
            var newMat2Id = newMat2Index >= 0 && newMat2Index < Data.Materials.Count ? Data.Materials[newMat2Index].Id : "";
            if (_win.Session?.Document != null && _win.Data == Data)
            {
                // Same lazy-creation reasoning as the Name rename above —
                // nothing else keeps this pair's document entry in sync.
                // Gated on Data==_win.Data — see the rename handler's own
                // comment for why (background pairs have no document entry).
                EnsurePairDocument(pair);
                _win.ExecuteCommand(new SC2SetMaterialPairLinkCommand(pair.Id, newMat1Id, newMat2Id));
            }
            pair.Mat1Index = newMat1Index;
            pair.Mat2Index = newMat2Index;
        }
        pair.Mat1Id = pair.Mat1Index >= 0 && pair.Mat1Index < Data.Materials.Count ? Data.Materials[pair.Mat1Index].Id : "";
        pair.Mat2Id = pair.Mat2Index >= 0 && pair.Mat2Index < Data.Materials.Count ? Data.Materials[pair.Mat2Index].Id : "";

        EditorGUILayout.Space(6);
        // ── Shader flags ──────────────────────────────────────────────────
        EditorGUILayout.LabelField("Shader Flags", EditorStyles.boldLabel);
        if (pair.ShaderFlags == null || pair.ShaderFlags.Count == 0)
            pair.ShaderFlags = DefaultFlags();

        EditorGUI.indentLevel++;
        foreach (var flag in pair.ShaderFlags)
        {
            EditorGUILayout.BeginHorizontal();
            var newFlagValue = EditorGUILayout.Toggle(flag.Value, GUILayout.Width(SC2UiScale.Pxf(20f)));
            if (newFlagValue != flag.Value)
            {
                _win.RecordUndoCheckpointFor(Data, "Change Shader Flag");
                flag.Value = newFlagValue;
            }
            EditorGUILayout.LabelField(flag.Key);
            if (flag.Key == "ALPHA_TEST" && flag.Value)
            {
                // Real per-material threshold (terrain.hlsl:
                // clip((a*255)-(ALPHA_TEST+1))) — observed range 4..128
                // across the 56-map corpus, stored separately from the
                // flag's own true/false presence.
                var newThreshold = GUILayout.HorizontalSlider(pair.AlphaTestThreshold, 0f, 255f, GUILayout.MinWidth(80));
                if (!Mathf.Approximately(newThreshold, pair.AlphaTestThreshold))
                {
                    _win.RecordUndoCheckpointFor(Data, "Change Alpha Test Threshold");
                    pair.AlphaTestThreshold = newThreshold;
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUI.indentLevel--;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("All ON", GUILayout.Height(SC2UiScale.Pxf(20f))))
        {
            _win.RecordUndoCheckpointFor(Data, "Shader Flags All ON");
            foreach (var f in pair.ShaderFlags) f.Value = true;
        }
        if (GUILayout.Button("Reset", GUILayout.Height(SC2UiScale.Pxf(20f))))
        {
            _win.RecordUndoCheckpointFor(Data, "Reset Shader Flags");
            pair.ShaderFlags = DefaultFlags();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);
        // ── Material Presets ──────────────────────────────────────────────
        // Applies ONLY to this pair's own fields (ShaderFile/ShaderPrefix/
        // State/ShaderFlags/TextureScroll) — deliberately never touches
        // Mat1Index/Mat2Index or the linked SC2TerrainData.Materials'
        // texture paths, so a user's chosen Diffuse/Normal/Mask is never
        // silently overwritten by picking a preset (see SC2MaterialPresets'
        // own doc comment). Applied in place onto the existing 12-entry
        // ShaderFlags list (set each flag's Value by key lookup) rather
        // than replacing the list object — the same duplicate-flag safety
        // reasoning as the reverse-sync write-back for Imported terrain.
        EditorGUILayout.LabelField("Material Preset", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        var presetNames = new string[SC2MaterialPreset.All.Count];
        for (var pi = 0; pi < presetNames.Length; pi++) presetNames[pi] = SC2MaterialPreset.All[pi].Name;
        _presetPickIndex = EditorGUILayout.Popup(Mathf.Clamp(_presetPickIndex, 0, presetNames.Length - 1), presetNames, SC2UiScale.PopupStyle());
        if (GUILayout.Button("Apply", GUILayout.Width(SC2UiScale.Pxf(60f))))
        {
            var preset = SC2MaterialPreset.All[_presetPickIndex];
            _win.RecordUndoCheckpointFor(Data, "Apply Material Preset");
            if (pair.ShaderFlags == null || pair.ShaderFlags.Count == 0)
                pair.ShaderFlags = DefaultFlags();
            foreach (var flag in pair.ShaderFlags)
                if (preset.Flags.TryGetValue(flag.Key, out var presetValue))
                    flag.Value = presetValue;
            pair.State = preset.State;
            pair.TextureScroll = preset.TextureScroll;
            pair.ShaderFile = preset.ShaderFile;
            pair.ShaderPrefix = preset.ShaderPrefix;
            if (preset.Flags.TryGetValue("ALPHA_TEST", out var alphaTestOn) && alphaTestOn)
                pair.AlphaTestThreshold = preset.AlphaTestThreshold;
        }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(SC2MaterialPreset.All[Mathf.Clamp(_presetPickIndex, 0, presetNames.Length - 1)].Description))
            EditorGUILayout.HelpBox(SC2MaterialPreset.All[Mathf.Clamp(_presetPickIndex, 0, presetNames.Length - 1)].Description, MessageType.None);

        EditorGUILayout.Space(6);
        // ── State + Texture Scroll ───────────────────────────────────────
        // Real terrain.bdf TerrainMaterial fields (56-map corpus survey) —
        // pure export-time metadata for the resulting .bdf, since Pairs'
        // own live preview renders through a completely different shader
        // (SC2TerrainRuntime's Hidden/SC2/TerrainView), not SC2/Terrain.
        var stateIndex = Mathf.Max(0, Array.IndexOf(kStateOptions, string.IsNullOrEmpty(pair.State) ? "None" : pair.State));
        var newStateIndex = EditorGUILayout.Popup("State", stateIndex, kStateOptions, SC2UiScale.PopupStyle());
        if (newStateIndex != stateIndex)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Material State");
            pair.State = kStateOptions[newStateIndex] == "None" ? "" : kStateOptions[newStateIndex];
        }
        // Plain EditorGUILayout.Vector2Field here used to show "1" for a
        // component the user had just cleared to empty and clicked away
        // from (a real, confirmed Unity numeric-field quirk: an
        // unparseable/blank text commits as 1, not 0 or the prior value) —
        // SC2TerrainShell.DrawCompactSlider already exists specifically to
        // work around this class of numeric-field jank elsewhere in this
        // codebase (see its own doc comment), so reused here instead of a
        // second one-off fix. keySuffix includes the pair's own index so
        // two different pairs' X/Y fields never share cached text state.
        EditorGUILayout.LabelField("Texture Scroll", EditorStyles.miniLabel);
        var newScrollX = SC2TerrainShell.DrawCompactSlider("X", pair.TextureScroll.x, -10f, 10f, "PairScroll" + i);
        var newScrollY = SC2TerrainShell.DrawCompactSlider("Y", pair.TextureScroll.y, -10f, 10f, "PairScroll" + i);
        var newScroll = new Vector2(newScrollX, newScrollY);
        if (newScroll != pair.TextureScroll)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Texture Scroll");
            pair.TextureScroll = newScroll;
        }
    }

    /// <summary>Same lazy-creation reasoning as
    /// SC2MaterialsMode.EnsureMaterialDocument — nothing keeps
    /// SC2MapDocument.Terrain.MaterialPairs synced with live
    /// SC2TerrainData.Pairs yet, so this creates the matching document
    /// entry the first time a pair is renamed.</summary>
    private void EnsurePairDocument(SC2MaterialPair pair)
    {
        var terrain = _win.Session?.Document?.Terrain;
        if (terrain == null)
            return;
        if (terrain.FindPair(pair.Id) == null)
            terrain.MaterialPairs.Add(new SC2MaterialPairDocument { Id = pair.Id, Name = pair.Name });
    }

    private int MatPicker(int pairIndex, string label, int cur)
    {
        EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
        EditorGUILayout.BeginHorizontal();
        if (cur >= 0 && cur < Data.Materials.Count)
        {
            var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), SC2UiScale.Pxf(18f), GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(SC2UiScale.Pxf(18f)));
            EditorGUI.DrawRect(swatch, Data.Materials[cur].Color);
        }
        var names = new string[Data.Materials.Count+1];
        names[0] = "(None)";
        for (var j = 0; j < Data.Materials.Count; j++) names[j+1] = Data.Materials[j].Name;
        var result = EditorGUILayout.Popup($"pair-{pairIndex}-{label}-material", cur + 1, names, SC2UiScale.PopupStyle()) - 1;
        EditorGUILayout.EndHorizontal();
        return result;
    }

    private static List<SC2ShaderFlag> DefaultFlags()
    {
        var list = new List<SC2ShaderFlag>();
        foreach (var (k,v) in kShaderFlags)
            list.Add(new SC2ShaderFlag{ Key=k, Value=v });
        return list;
    }

    public bool HandleKeyboardShortcut(UnityEngine.Event e) => false;
}
