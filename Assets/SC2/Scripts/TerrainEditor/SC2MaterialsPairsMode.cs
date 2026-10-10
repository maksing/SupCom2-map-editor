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
        EditorGUILayout.Space(SC2UiScale.Pxf(6f));

        // With several materials, folding the whole list at once is what makes it manageable.
        if (Data.Materials.Count > 1)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Collapse all"))
                foreach (var material in Data.Materials) _collapsedMaterialIds.Add(material.Id);
            if (GUILayout.Button("Expand all"))
                _collapsedMaterialIds.Clear();
            GUILayout.EndHorizontal();
            EditorGUILayout.Space(SC2UiScale.Pxf(6f));
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
        // Every control of the header row has this one height (and the same 2px margin, which the panel's button and text field
        // styles share), so the arrow, colour chip, name, colour button and delete button sit on one line.
        var rowHeight = SC2UiScale.Pxf(24f);
        // A real button for the arrow, big enough to hit, instead of the editor's small foldout glyph.
        var newExpanded = expanded;
        if (GUILayout.Button(expanded ? "▾" : "▸", GUILayout.Width(rowHeight), GUILayout.Height(rowHeight)))
            newExpanded = !expanded;
        if (newExpanded != expanded)
        {
            if (newExpanded) _collapsedMaterialIds.Remove(mat.Id);
            else _collapsedMaterialIds.Add(mat.Id);
        }
        // The chip is laid out without a margin, so it is given the buttons' margin as extra height and drawn inside it.
        var chipMargin = GUI.skin.button.margin.top;
        var swatch = GUILayoutUtility.GetRect(SC2UiScale.Pxf(14f), rowHeight + chipMargin * 2f, GUILayout.Width(SC2UiScale.Pxf(14f)), GUILayout.Height(rowHeight + chipMargin * 2f));
        EditorGUI.DrawRect(new Rect(swatch.x, swatch.y + chipMargin, swatch.width, rowHeight), mat.Color);
        // DelayedTextField, not TextField: this branch executes a real document command AND
        // MarkDirtyAndRepaint (which SC2DecalsShell.EnsurePreview reads as "terrain geometry
        // changed" and reacts to with a full decal rebuild) — a plain TextField fires this on
        // every keystroke, confirmed to make renaming lag hard on a map with many placed decals.
        // DelayedTextField only returns the new value once Enter/Tab commits it or the field
        // loses focus, so this now runs once per rename instead of once per character.
        var newMaterialName = EditorGUILayout.DelayedTextField(mat.Name, GUILayout.MinWidth(44), GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));
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
        // Reported directly (standalone build): the Color swatch did nothing for the Core panel's
        // first two materials whenever Background Terrain is enabled. Root cause: this key used to
        // be "material-color-{i}" — a plain LOOP INDEX — and DrawPanel's own caller
        // (SC2TerrainShell) draws the Core AND Background SC2MaterialsMode instances in the SAME
        // frame when BackgroundTerrainEnabled (see its "Core"/"Background" section labels), so
        // Core material 0 and Background material 0 shared the exact same
        // SC2RuntimeColorPalette field key — the Background swatch's own UpdateAnchor call (which
        // also runs every Repaint while that key IsOpen) kept overwriting the popup's anchor with
        // the BACKGROUND panel's screen position, corrupting or relocating the Core popup. Only
        // materials 0/1 broke because this map's Background terrain has exactly two materials.
        // mat.Id (a GUID, EnsureStableIds) is unique across BOTH Core and Background data, unlike
        // the shared loop index.
        var materialColor = EditorGUILayout.ColorField(new GUIContent("", $"material-color-{mat.Id}"), mat.Color, false, false, false, GUILayout.Width(SC2UiScale.Pxf(42f)), GUILayout.Height(rowHeight));
        if (materialColor != mat.Color)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Material Color");
            mat.Color = materialColor;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        if (GUILayout.Button("x", GUILayout.Width(rowHeight), GUILayout.Height(rowHeight)) &&
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
            // siblingHints: BOTH other slots on this same material, not
            // just whichever one used to be preferred — lets the browser
            // suggest this slot's counterpart(s) by real vanilla suffix
            // convention ("_d"/"_n"/"_s"/"_m", see
            // SC2TextureCatalog.FindAllSiblingMatches) instead of leaving
            // the user to search 1000+ textures for the matching set by
            // hand. Passing both matters when Diffuse and Normal (say) come
            // from two DIFFERENT real texture families — Mask then offers
            // both families' own Mask candidate as separate buttons rather
            // than only ever guessing from one arbitrarily preferred slot.
            DrawTextureField(i, "Diffuse", ref mat.Diffuse, ref mat.DiffuseOverridePath, ref mat.DiffuseForceExport, mat.Normal, mat.Mask);
            DrawTextureField(i, "Normal", ref mat.Normal, ref mat.NormalOverridePath, ref mat.NormalForceExport, mat.Diffuse, mat.Mask);
            DrawTextureField(i, "Mask", ref mat.Mask, ref mat.MaskOverridePath, ref mat.MaskForceExport, mat.Diffuse, mat.Normal);
        }
        else
        {
            DrawCollapsedSlots(i, mat);
        }
        SC2TerrainShell.EndUnifiedPanelBox();
        EditorGUILayout.Space(SC2UiScale.Pxf(8f));
    }

    /// <summary>What a collapsed card still shows: its Diffuse, Normal and Mask as small thumbnails, so a folded list tells which
    /// textures each material uses. A click on one opens the texture browser for that slot, so a texture can be assigned
    /// without unfolding the card.</summary>
    private void DrawCollapsedSlots(int materialIndex, SC2Material mat)
    {
        var size = SC2UiScale.Pxf(34f);
        EditorGUILayout.Space(SC2UiScale.Pxf(4f));
        GUILayout.BeginHorizontal();
        // The sibling hints are the same as in the unfolded card (see DrawMaterialBlock).
        DrawCollapsedSlot(materialIndex, "Diffuse", "D", SC2TextureUsage.Diffuse, size, ref mat.Diffuse, ref mat.DiffuseOverridePath, ref mat.DiffuseForceExport, mat.Normal, mat.Mask);
        DrawCollapsedSlot(materialIndex, "Normal", "N", SC2TextureUsage.Normal, size, ref mat.Normal, ref mat.NormalOverridePath, ref mat.NormalForceExport, mat.Diffuse, mat.Mask);
        DrawCollapsedSlot(materialIndex, "Mask", "M", SC2TextureUsage.Mask, size, ref mat.Mask, ref mat.MaskOverridePath, ref mat.MaskForceExport, mat.Diffuse, mat.Normal);
        GUILayout.EndHorizontal();
    }

    private void DrawCollapsedSlot(int materialIndex, string label, string letter, SC2TextureUsage usage, float size,
        ref Texture2D texture, ref string overridePath, ref bool forceExport, params Texture2D[] siblingHints)
    {
        var previous = texture;
        var picked = SC2TerrainShell.DrawTextureThumb(TextureKey(materialIndex, label), letter, usage, size, texture, siblingHints) as Texture2D;
        if (picked != previous)
        {
            var pathDraft = overridePath ?? "";
            ApplyTextureChange(materialIndex, label, previous, overridePath, picked, ref pathDraft, true, ref forceExport);
            texture = picked;
            overridePath = pathDraft;
        }
    }

    // Data.GetInstanceID() in the key: it also identifies the picker to SC2TextureBrowser.Open/Consume (only one browser
    // session is open at a time, matched back to its opener by this exact string) and to DrawPathLockToggle's own shared
    // lock-state dictionary — a bare "Material{index}:{label}" would let core's Material 0 and background's own Material 0
    // collide on both, now that Materials shows both panels stacked in the same frame.
    private string TextureKey(int materialIndex, string label) => "Material" + Data.GetInstanceID() + "-" + materialIndex + ":" + label;

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

    private void DrawTextureField(int materialIndex, string label, ref Texture2D texture, ref string overridePath, ref bool forceExport, params Texture2D[] siblingHints)
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
        var texKey = TextureKey(materialIndex, label);
        var next = SC2TerrainShell.DrawTexturePickerWithPath(label, texture, texKey, ref pathDraft, out var pickedViaBrowser, fallbackPath, siblingHints) as Texture2D;
        ApplyTextureChange(materialIndex, label, previous, previousPath, next, ref pathDraft, pickedViaBrowser, ref forceExport);
        texture = next;
        overridePath = pathDraft;
        DrawCopyOnExportToggle(materialIndex, label, next, pathDraft, ref forceExport);
    }

    /// <summary>Records and applies what a slot's picker produced this frame: a different texture (browser pick or a path that
    /// resolved) and/or a different override path. Shared by the unfolded card's picker and the thumbnails of a folded one.
    /// <paramref name="pathDraft"/> may be rewritten (a fresh browser pick resets or sets the override path).</summary>
    private void ApplyTextureChange(int materialIndex, string label, Texture2D previous, string previousPath, Texture2D next,
        ref string pathDraft, bool pickedViaBrowser, ref bool forceExport)
    {
        if (next != previous)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Material " + label + " Texture");
            var material = Data.Materials[materialIndex];
            // The "copy into map on export" opt-in belongs to one specific
            // texture — a different texture starts back at the default.
            forceExport = false;
            // See the rename handler's own comment above for why this is
            // gated on Data==_win.Data (background materials have no
            // document representation to lazily create).
            if (_win.Data == Data)
            {
                EnsureMaterialDocument(material);
                _win.ExecuteCommand(new SC2SetMaterialTextureCommand(material.Id, TextureSlot(label), BuildTextureReference(next, forceExport)));
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
    }

    private void DrawCopyOnExportToggle(int materialIndex, string label, Texture2D next, string pathDraft, ref bool forceExport)
    {
        // Only a texture served by one of the three vanilla archives can be
        // skipped at export, so only then is there anything to opt into — a
        // community-pack archive texture and a Custom "file:" import are
        // always copied, and a hand-typed real game path never is, so the
        // box would do nothing for those and is left out rather than shown
        // dead (an archive tag alone is not enough: community archives have
        // one too — IsVanillaSlotPath checks the owning archive).
        var archiveTag = !string.IsNullOrEmpty(pathDraft) && pathDraft.StartsWith("archive:", StringComparison.Ordinal)
            ? pathDraft
            : (next != null ? SC2TerrainMaterialFactory.TryGetResourcePath(next) : null);
        if (archiveTag != null && archiveTag.StartsWith("archive:", StringComparison.Ordinal)
            && SC2VanillaArchiveClassifier.IsVanillaSlotPath(archiveTag))
        {
            var newForceExport = GUILayout.Toggle(forceExport, "Copy into map on export");
            if (newForceExport != forceExport)
            {
                _win.RecordUndoCheckpointFor(Data, "Change Material " + label + " Copy On Export");
                forceExport = newForceExport;
                if (_win.Data == Data)
                {
                    var material = Data.Materials[materialIndex];
                    EnsureMaterialDocument(material);
                    _win.ExecuteCommand(new SC2SetMaterialTextureCommand(material.Id, TextureSlot(label), BuildTextureReference(next, forceExport)));
                }
                EditorUtility.SetDirty(Data);
                _win.MarkDirtyAndRepaint();
            }
        }
    }

    private static SC2MaterialTextureSlot TextureSlot(string label)
    {
        if (string.Equals(label, "Normal", StringComparison.OrdinalIgnoreCase)) return SC2MaterialTextureSlot.Normal;
        if (string.Equals(label, "Mask", StringComparison.OrdinalIgnoreCase)) return SC2MaterialTextureSlot.Mask;
        return SC2MaterialTextureSlot.Diffuse;
    }

    private static SC2TextureReference BuildTextureReference(Texture2D texture, bool forceExport)
    {
        if (texture == null) return new SC2TextureReference { ForceExport = forceExport };
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
            ForceExport = forceExport,
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

    // The Shader flags list is open by default, the Border outline tools are folded away until wanted.
    private bool _shaderFlagsExpanded = true;
    private bool _outlineExpanded;

    // Border outline settings (shared by every pair's panel) and the result line of the last outline.
    private float _outlineWidth = 8f;
    private float _outlineSoftness = 0.5f;
    private float _outlineSmoothing = 1f;
    private float _outlineSpread = 4f;
    // The blend alpha of an outline is always full: the outline is the pair's D2 itself.
    private const float _outlineBlend = 1f;
    private string _outlineStatus = "";

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

        EditorGUILayout.Space(SC2UiScale.Pxf(12f));
        SC2TerrainShell.DrawUnifiedSectionLabel($"Pairs ({Data.Pairs.Count})");
        for (var i = 0; i < Data.Pairs.Count; i++) DrawRow(i);
        if (_sel >= 0 && _sel < Data.Pairs.Count)
        {
            // The selected pair's settings in a card of their own, so they read as one block under the list.
            EditorGUILayout.Space(SC2UiScale.Pxf(10f));
            SC2TerrainShell.BeginUnifiedPanelBox();
            DrawDetails(_sel);
            SC2TerrainShell.EndUnifiedPanelBox();
        }

        // Only on the core terrain's own Pairs tab — this class is also
        // reused, unchanged, for the Background tab's own single pair
        // (Data == bg there, never == _win.Data), where a second copy of a
        // control that already affects the background too would be a
        // confusing duplicate rather than a second independent setting.
        if (Data == _win.Data) DrawTilingSettings();
        if (GUI.changed) { EditorUtility.SetDirty(Data); _win.Repaint(); }
    }

    /// <summary>The settings that apply to every pair at once (a shared tiling size, the cliff UV stretch), kept below the pair
    /// list and the selected pair's card.</summary>
    private void DrawTilingSettings()
    {
        EditorGUILayout.Space(SC2UiScale.Pxf(16f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Texture tiling - all pairs");
        SC2TerrainShell.BeginUnifiedPanelBox();
        var newGlobalUvScale = SC2TerrainShell.DrawCompactSlider("UV Tile Size (world units)", Data.GlobalUvScale, 0.5f, 150f,
            onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change Global UV Scale"));
        if (!Mathf.Approximately(newGlobalUvScale, Data.GlobalUvScale))
        {
            Data.GlobalUvScale = Mathf.Max(0.0001f, newGlobalUvScale);
            EditorUtility.SetDirty(Data);
            _win.RefreshBackgroundRuntime();
            _win.ApplyPreviewModeToChunks();
            _win.MarkDirtyAndRepaint();
        }
        SC2TerrainShell.DrawUnifiedNote("One tiling size for every pair, core and background, so the tiling cannot differ at the seam or between pairs.");
        SC2TerrainShell.EndUnifiedPanelBox();

        EditorGUILayout.Space(SC2UiScale.Pxf(12f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Terrain UV1 - cliff stretching");
        SC2TerrainShell.BeginUnifiedPanelBox();
        var newHeightWeight = SC2TerrainShell.DrawCompactSlider("Height Stretch Weight", Data.Uv1HeightWeight, 0.25f, 10f,
            onValueChangeStart: () => _win.RecordUndoCheckpointFor(Data, "Change UV1 Height Weight"));
        if (!Mathf.Approximately(newHeightWeight, Data.Uv1HeightWeight))
        {
            Data.Uv1HeightWeight = Mathf.Max(0f, newHeightWeight);
            EditorUtility.SetDirty(Data);
        }
        SC2TerrainShell.DrawUnifiedNote("1 = a slope's texture span matches its real 3D surface distance. Higher gives steep cliffs more texture space, for very sharp terrain. Saved with the map; applies to core and background.");
        if (GUILayout.Button("Recalculate UV1", GUILayout.Height(SC2UiScale.Pxf(24f))))
        {
            // Not tied to Bake Lighting (a separate, independent
            // feature/button) — this ONLY refreshes the live preview's
            // own tiling coordinate to match the current terrain shape
            // and Height Stretch Weight above. Export already computes
            // UV1 fresh every time on its own (SC2TerrainExporter)
            // using whatever Uv1HeightWeight is currently saved, so
            // there's nothing to "turn on" there; this button exists so
            // the EDITOR VIEW doesn't keep showing a stale mapping after
            // terrain edits or a weight change until asked to refresh —
            // continuous per-edit recompute was deliberately avoided
            // (see SC2TerrainRuntime.RecalculateUv1's own comment) since
            // this runs during active sculpting/painting.
            _win.Runtime?.RecalculateUv1(Data.Uv1HeightWeight);
            _win.BackgroundRuntime?.RecalculateUv1(Data.Uv1HeightWeight);
            _win.MarkDirtyAndRepaint();
        }
        SC2TerrainShell.DrawUnifiedNote("Fixes cliff texture stretching in the editor preview. Export always unwraps UV1 by real surface distance. This button only refreshes the editor preview after you reshape the terrain or change the weight; it does not bake or touch lighting.");
        SC2TerrainShell.EndUnifiedPanelBox();
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
        if (Event.current.type == EventType.Repaint && _rowRects.TryGetValue(i, out var lastRect))
        {
            // The whole row answers to the mouse (a click anywhere on it unfolds the pair), so it lights up under the pointer.
            if (_sel == i) EditorGUI.DrawRect(lastRect, new Color(0.22f, 0.38f, 0.55f));
            else if (lastRect.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(lastRect, new Color(0.20f, 0.25f, 0.32f));
        }

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

        if (GUILayout.Button(_sel == i ? "▾" : "▸", GUILayout.Width(SC2UiScale.Pxf(24f)), GUILayout.Height(SC2UiScale.Pxf(24f)))) _sel = _sel==i ? -1 : i;
        if (GUILayout.Button("x", GUILayout.Width(SC2UiScale.Pxf(24f)), GUILayout.Height(SC2UiScale.Pxf(24f))) &&
            EditorUtility.DisplayDialog("Delete", $"Delete '{pair.Name}'?", "Yes", "Cancel"))
        {
            _win.RecordUndoCheckpointFor(Data, "Remove Pair");
            // Clear pair from index array and shift higher indices down
            var counts = new int[Data.Pairs.Count - 1];
            // The assignments as they will be after the shift, except that the removed pair's cells still hold it: the
            // difference to the result is exactly the cells that lost their pair.
            var assignmentsBefore = (short[])Data.TrianglePairIndex.Clone();
            for (var t = 0; t < assignmentsBefore.Length; t++)
                if (assignmentsBefore[t] > i) assignmentsBefore[t]--;
            for (var t=0;t<Data.TrianglePairIndex.Length;t++)
            {
                if (Data.TrianglePairIndex[t]==i) Data.TrianglePairIndex[t]=-1;
                else if (Data.TrianglePairIndex[t]>i) Data.TrianglePairIndex[t]--;
                if (Data.TrianglePairIndex[t]>=0) counts[Data.TrianglePairIndex[t]]++;
            }
            Data.Pairs.RemoveAt(i);
            Data.PairTriCounts = counts;
            if (_sel >= Data.Pairs.Count) _sel = Data.Pairs.Count-1;
            // TrianglePairIndex only shifted the CPU array above - the GPU
            // PairMapRT that the terrain shader/paint brush actually read
            // from (_PairMapTex) still holds the stale, pre-shift bytes at
            // this point. Left unsynced, the terrain would keep rendering
            // the old pair mapping until an unrelated Undo/Redo (the only
            // other caller of RestorePairMapFromData besides load) happened
            // to fix it, and worse, the next paint stroke's GPU->CPU
            // readback (SyncPairMapToCPU/Async) would silently reapply
            // those stale bytes and undo this very fix.
            var runtime = Data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
            runtime?.RestorePairMapFromData(Data);
            // RestorePairMapFromData only pushes the raw index texture - the
            // Pairs-preview-mode colour RT and per-pair material/tiling setup
            // are a separate derived cache (same distinction ApplyRestoredSnapshot's
            // own comment calls out for Undo/Redo) and need their own refresh.
            if (Data == _win.Data) _win.ApplyPreviewModeToChunks();
            else _win.RefreshBackgroundRuntime();
            _win.PruneOutlines(Data, assignmentsBefore);
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
        var rowRect = GUILayoutUtility.GetLastRect();
        var rowEvent = Event.current;
        if (rowEvent.type == EventType.Repaint)
        {
            _rowRects[i] = rowRect;
        }
        else if (rowEvent.type == EventType.MouseDown && rowEvent.button == 0 && rowRect.Contains(rowEvent.mousePosition))
        {
            // A click anywhere on the row (swatches, count, name) unfolds or folds the pair, not only on the arrow. The arrow
            // and delete buttons have already used their own clicks by now, which leaves this event no longer a MouseDown.
            _sel = _sel == i ? -1 : i;
            rowEvent.Use();
        }
    }

    /// <summary>"Outline border": a band of the pair's second material (D2, blend alpha) along the inside of its border,
    /// painted into the active layer of the pair's Blend Mask — see SC2PaintMode.OutlinePairBorder.</summary>
    private void DrawOutlineSection(int i)
    {
        _outlineExpanded = SC2TerrainShell.DrawFoldoutHeader("Border outline", _outlineExpanded);
        if (!_outlineExpanded) return;
        var key = $"pair-outline-{Data.GetInstanceID()}-{i}";
        _win.GetOutlineMinimums(Data, out var minWidth, out var minSpread, out var minSmoothing);
        _outlineWidth = SC2TerrainShell.DrawCompactSlider("Width (units)", _outlineWidth, minWidth, 64f, key);
        _outlineSoftness = SC2TerrainShell.DrawCompactSlider("Softness", _outlineSoftness, 0f, 1f, key);
        _outlineSmoothing = SC2TerrainShell.DrawCompactSlider("Smoothing (cells)", _outlineSmoothing, 0f, SC2PairOutlineUpdater.MaximumSmoothingCells, key);
        _outlineSpread = SC2TerrainShell.DrawCompactSlider("Spread onto neighbors (units)", _outlineSpread, 0f, 32f, key);
        if (_outlineSpread > 0f && _outlineSpread < minSpread) _outlineSpread = minSpread;
        if (_outlineSmoothing > 0f && _outlineSmoothing < minSmoothing) _outlineSmoothing = minSmoothing;
        if (GUILayout.Button("Outline border with D2", GUILayout.Height(SC2UiScale.Pxf(22f))))
            _outlineStatus = _win.OutlinePairBorder(Data, i, _outlineWidth, _outlineSoftness, _outlineBlend, _outlineSmoothing, _outlineSpread);
        SC2TerrainShell.DrawUnifiedNote("Paints a band of this pair's D2 along its border onto the \"Outline\" Blend Mask layer (Paint > Blend Mask): inside the pair and, with Spread, a little onto the neighbouring pairs. Outlines that are no longer along a pair border are removed from that layer.");
        if (!string.IsNullOrEmpty(_outlineStatus)) SC2TerrainShell.DrawUnifiedNote(_outlineStatus);
    }

    private void DrawDetails(int i)
    {
        var pair = Data.Pairs[i];
        // DelayedTextField, not TextField — same reasoning as the Material rename field's own
        // comment: this branch's ExecuteCommand+MarkDirtyAndRepaint must run once per rename, not
        // once per keystroke (confirmed to trigger a full SC2DecalsShell decal rebuild each time).
        SC2TerrainShell.DrawUnifiedSectionLabel("Pair");
        var newPairName = EditorGUILayout.DelayedTextField("Name", pair.Name);
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
        // Same latent Core/Background field-key collision as the Material color swatch's own
        // comment describes (SC2MaterialsMode.DrawMaterialBlock) — pair.Id (unique across both
        // Core and Background data) instead of the shared loop index i, so this doesn't break the
        // moment a Background terrain's own Pairs happen to reach the same count.
        var pairColor = EditorGUILayout.ColorField(new GUIContent("Pair Color", $"pair-color-{pair.Id}"), pair.PairColor, false, false, false, GUILayout.Height(SC2UiScale.Pxf(24f)));
        if (pairColor != pair.PairColor)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Pair Color");
            pair.PairColor = pairColor;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        if (GUILayout.Button("Assign unassigned polygons") &&
            EditorUtility.DisplayDialog("Assign", $"Assign every currently-unassigned polygon to '{pair.Name}'?", "Yes", "Cancel"))
        {
            _win.RecordUndoCheckpointFor(Data, "Assign Unassigned Polygons");
            var runtime = Data == _win.Data ? _win.Runtime : _win.BackgroundRuntime;
            var hole = runtime?.HoleTriPairIndex;
            var idx = Data.TrianglePairIndex;
            var counts = Data.PairTriCounts != null && Data.PairTriCounts.Length == Data.Pairs.Count
                ? (int[])Data.PairTriCounts.Clone() : new int[Data.Pairs.Count];
            for (var t = 0; t < idx.Length; t++)
            {
                if (idx[t] != -1) continue;
                // Skip cells the background can't actually draw (covered by
                // the core's own footprint) - same invariant SC2PaintMode's
                // ApplyPairBytes enforces after a GPU readback, so a hole
                // never ends up carrying a pair index that looks assigned
                // but renders nothing.
                if (hole != null && t < hole.Length && hole[t] < 0) continue;
                idx[t] = (short)i;
                counts[i]++;
            }
            Data.PairTriCounts = counts;
            // Same GPU/preview resync the delete-pair button now does -
            // TrianglePairIndex alone isn't what the shader/paint brush read
            // from (see SC2TerrainRuntime.RestorePairMapFromData).
            runtime?.RestorePairMapFromData(Data);
            if (Data == _win.Data) _win.ApplyPreviewModeToChunks();
            else _win.RefreshBackgroundRuntime();
            EditorUtility.SetDirty(Data); _win.MarkDirtyAndRepaint();
        }
        var newRnmMaskName = EditorGUILayout.TextField("RNM Mask", pair.RnmMaskName);
        if (newRnmMaskName != pair.RnmMaskName)
        {
            _win.RecordUndoCheckpointFor(Data, "Change RNM Mask Name");
            pair.RnmMaskName = newRnmMaskName;
        }

        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        SC2TerrainShell.DrawUnifiedSectionLabel("Materials");
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

        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
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
        SC2TerrainShell.DrawUnifiedSectionLabel("Material preset");
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
            // A preset's Ramp / Environment textures only fill a slot that is still empty; a texture the
            // user already picked is never replaced.
            if (!string.IsNullOrEmpty(preset.RampPath) && string.IsNullOrEmpty(pair.RampPath))
            {
                pair.RampPath = preset.RampPath;
                pair.RampSourceTag = "";
                pair.RampForceExport = false;
            }
            if (!string.IsNullOrEmpty(preset.EnvironmentPath) && string.IsNullOrEmpty(pair.EnvironmentPath))
            {
                pair.EnvironmentPath = preset.EnvironmentPath;
                pair.EnvironmentSourceTag = "";
                pair.EnvironmentForceExport = false;
            }
        }
        EditorGUILayout.EndHorizontal();
        if (!string.IsNullOrEmpty(SC2MaterialPreset.All[Mathf.Clamp(_presetPickIndex, 0, presetNames.Length - 1)].Description))
            EditorGUILayout.HelpBox(SC2MaterialPreset.All[Mathf.Clamp(_presetPickIndex, 0, presetNames.Length - 1)].Description, MessageType.None);

        // ── Shader flags ──────────────────────────────────────────────────
        _shaderFlagsExpanded = SC2TerrainShell.DrawFoldoutHeader("Shader flags", _shaderFlagsExpanded);
        if (_shaderFlagsExpanded)
        {
        if (pair.ShaderFlags == null || pair.ShaderFlags.Count == 0)
            pair.ShaderFlags = DefaultFlags();
        // A pair saved with an older, shorter flag list (9 of the 13) would otherwise never show the rest
        // (GLOW, ENVIRONMENT_MAPPING, INFINITE_DISTANCE, ALPHA_TEST); a preset could not set them either.
        if (AddMissingFlags(pair.ShaderFlags))
            EditorUtility.SetDirty(Data);

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
        }
        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        DrawPairRampAndEnvironment(pair, i);

        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        SC2TerrainShell.DrawUnifiedSectionLabel("State and texture scroll");
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
        // second one-off fix. keySuffix includes the pair's own index (and,
        // same reasoning as the Simplify slider above, Data's own instance
        // ID) so two different pairs' X/Y fields — including one from the
        // core and one from the background, now shown stacked in the same
        // frame — never share cached text state.
        EditorGUILayout.LabelField("Texture Scroll", EditorStyles.miniLabel);
        var scrollKeySuffix = $"PairScroll{Data.GetInstanceID()}-{i}";
        var newScrollX = SC2TerrainShell.DrawCompactSlider("X", pair.TextureScroll.x, -10f, 10f, scrollKeySuffix);
        var newScrollY = SC2TerrainShell.DrawCompactSlider("Y", pair.TextureScroll.y, -10f, 10f, scrollKeySuffix);
        var newScroll = new Vector2(newScrollX, newScrollY);
        if (newScroll != pair.TextureScroll)
        {
            _win.RecordUndoCheckpointFor(Data, "Change Texture Scroll");
            pair.TextureScroll = newScroll;
        }
        EditorGUILayout.Space(SC2UiScale.Pxf(14f));
        DrawOutlineSection(i);
    }

    private struct PairTextureCache { public string Path; public Texture Texture; }
    private readonly Dictionary<string, PairTextureCache> _pairTextureCache = new();

    /// <summary>The pair's Ramp (terrain.bdf material slot 8) and Environment (slot 11) textures; export-only, like
    /// the State and Texture Scroll below (the live preview does not draw them).</summary>
    private void DrawPairRampAndEnvironment(SC2MaterialPair pair, int i)
    {
        SC2TerrainShell.DrawUnifiedSectionLabel("Ramp / Environment textures");
        if (DrawPairTextureSlot("Ramp", i, pair.RampPath, pair.RampSourceTag, pair.RampForceExport,
                out var rampPath, out var rampTag, out var rampForce))
        {
            _win.RecordUndoCheckpointFor(Data, "Change Pair Ramp Texture");
            pair.RampPath = rampPath; pair.RampSourceTag = rampTag; pair.RampForceExport = rampForce;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }
        if (DrawPairTextureSlot("Environment", i, pair.EnvironmentPath, pair.EnvironmentSourceTag, pair.EnvironmentForceExport,
                out var envPath, out var envTag, out var envForce))
        {
            _win.RecordUndoCheckpointFor(Data, "Change Pair Environment Texture");
            pair.EnvironmentPath = envPath; pair.EnvironmentSourceTag = envTag; pair.EnvironmentForceExport = envForce;
            EditorUtility.SetDirty(Data);
            _win.MarkDirtyAndRepaint();
        }

        var environmentMapping = false;
        if (pair.ShaderFlags != null)
            foreach (var flag in pair.ShaderFlags)
                if (flag.Key == "ENVIRONMENT_MAPPING") environmentMapping = flag.Value;
        if (environmentMapping && string.IsNullOrEmpty(pair.EnvironmentPath))
            SC2TerrainShell.DrawUnifiedNote("ENVIRONMENT_MAPPING is on but no Environment texture is set.");
        else if (!environmentMapping && !string.IsNullOrEmpty(pair.EnvironmentPath))
            SC2TerrainShell.DrawUnifiedNote("The Environment texture has no effect while ENVIRONMENT_MAPPING is off.");
    }

    /// <summary>One texture-path slot of a pair: thumbnail picker (opens the texture browser), editable game
    /// path, and the "copy into map on export" opt-in for a vanilla archive texture. Returns true when anything
    /// changed; the caller records the undo checkpoint and stores the new values.</summary>
    private bool DrawPairTextureSlot(string label, int pairIndex, string path, string sourceTag, bool forceExport,
        out string newPath, out string newTag, out bool newForce)
    {
        // The key doubles as the browser-session id and picks the browser's starting filter (":Ramp" opens in the
        // Ramp category, a key containing "env" filters to Environment textures); keep both words intact.
        var key = "PairTex" + Data.GetInstanceID() + "-" + pairIndex + ":" + label;
        path ??= "";
        if (!_pairTextureCache.TryGetValue(key, out var cached) || cached.Path != path)
        {
            cached = new PairTextureCache
            {
                Path = path,
                Texture = path.Length > 0 ? SC2TerrainMaterialFactory.LoadTexturePublicAny(path) : null,
            };
            _pairTextureCache[key] = cached;
        }

        var pathDraft = path;
        var picked = SC2TerrainShell.DrawTexturePickerWithPath(label, cached.Texture, key, ref pathDraft, out var viaBrowser);
        newPath = path; newTag = sourceTag ?? ""; newForce = forceExport;
        var changed = false;
        if (viaBrowser)
        {
            // Store the clean game path (never the internal "archive:" tag) and keep the tag next to it, so the
            // export can still copy the file when asked.
            var tag = picked != null ? SC2TerrainMaterialFactory.TryGetResourcePath(picked) : null;
            newPath = picked != null ? SC2TerrainMaterialFactory.ResolveGamePath(picked) : "";
            newTag = tag != null && tag.StartsWith("archive:", StringComparison.Ordinal) ? tag : "";
            newForce = false;
            changed = newPath != path || newTag != (sourceTag ?? "");
            _pairTextureCache[key] = new PairTextureCache { Path = newPath, Texture = picked };
        }
        else if (pathDraft != path)
        {
            newPath = pathDraft; newTag = ""; newForce = false;
            changed = true;
        }

        if (SC2VanillaArchiveClassifier.TagMatchesGamePath(newTag, newPath) && SC2VanillaArchiveClassifier.IsVanillaSlotPath(newTag))
        {
            var force = GUILayout.Toggle(newForce, "Copy into map on export");
            if (force != newForce) { newForce = force; changed = true; }
        }
        return changed;
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
        EditorGUILayout.LabelField(label == "D1" ? "Material 1 (D1)" : "Material 2 (D2)", EditorStyles.miniLabel);
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

    /// <summary>Appends every flag of the full set that <paramref name="flags"/> lacks, switched off (a flag absent from
    /// the list was never exported, so off keeps the pair exactly as it was). Returns true when anything was added.</summary>
    private static bool AddMissingFlags(List<SC2ShaderFlag> flags)
    {
        var added = false;
        foreach (var (key, _) in kShaderFlags)
        {
            var present = false;
            foreach (var f in flags)
                if (f.Key == key) { present = true; break; }
            if (present) continue;
            flags.Add(new SC2ShaderFlag { Key = key, Value = false });
            added = true;
        }
        return added;
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
