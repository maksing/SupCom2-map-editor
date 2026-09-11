// SC2EnvironmentEditor.cs
//
// Environment (info.bdf) editor for the unified SC2 Map Editor window's
// "Environment" tab (SC2MapEditorWindow.DrawEnvironmentPanel). Migrated from
// the standalone InfoEditorWindow ("SC2/Edit Environment (info.bdf)") —
// same pattern as SC2CostsTerrainTypesEditor's migration of the old
// CostsPainterWindow: all editing logic (undo/redo stack, per-field reset
// to the loaded file's original value, disk backup/restore, JSON presets,
// Apply-to-Light) is unchanged, only the EditorWindow-specific wrapper
// (menu item, own toolbar with a Map Raw Data object field) is removed,
// since the unified window already owns Map Raw Data selection via
// SC2EditorContext.MapRawData and calls Load(...)/DrawEmbeddedGUI(...)
// directly.
//
// The standalone InfoEditorWindow was later deleted outright once the
// unified window's embedded editor fully
// covered its functionality — this class is now the only Environment
// editing surface.
using UnityEditor;
using UnityEngine;
using BdfIO.Core;
using BdfIO.Formats.Info;

public sealed class SC2EnvironmentEditor
{
    private SC2MapRawData _rawData;
    private InfoModel _m;
    /// <summary>
    /// Snapshot of Header taken RIGHT AFTER Load() — a fixed reference point
    /// for "what the file had". Unlike the Undo stack (which moves forward and
    /// back through edit history), this always stays the original state, and
    /// the ⟲ button on a SPECIFIC field resets just that one value to it,
    /// without touching any other changed field.
    /// </summary>
    private InfoModel _original;
    private Vector2 _scroll;

    private bool _fSun=true, _fShadow, _fWater, _fWMove, _fWColor, _fWTile, _fWOptics, _fWMisc;
    private bool _fMap, _fFog, _fPointCloud, _fGlow, _fGaussian, _fIntel, _fRangeCircles, _fDoF, _fUnknown;
    private bool _fSharpen, _fRenderPasses, _fWind;

    private GUIStyle _sectionStyle;

    // ---- Undo (Ctrl+Z / Ctrl+Y) — stack of Header snapshots ----
    private readonly System.Collections.Generic.List<byte[]> _undoStack = new();
    private int _undoIndex = -1; // index of the CURRENT state in the stack
    private const int MaxUndoSteps = 50;
    private double _lastSnapshotTime;
    private const double SnapshotDebounceSeconds = 0.4; // don't snapshot more often than once per N seconds while dragging a slider

    // ---- On-disk backup (made once, on the first load in a session) ----
    private bool _backupDoneThisSession;

    // ---- Presets ----
    private string _presetName = "";
    private string _presetNote = "";

    public bool IsLoaded => _m != null;
    public System.Action RepaintHost;

    /// <summary>Loads (or reloads) from the given SC2MapRawData — called by
    /// the unified window, mirroring SC2CostsTerrainTypesEditor.Load's
    /// pattern of taking the Context panel's MapRawData directly rather
    /// than owning its own object-field picker.</summary>
    public void Load(SC2MapRawData rawData)
    {
        _rawData = rawData;
        Load();
    }

    public void DrawEmbeddedGUI()
    {
        _sectionStyle ??= new GUIStyle("helpBox") { padding = new RectOffset(8,8,6,6), margin = new RectOffset(0,0,2,2) };

        // ---- Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z must be handled BEFORE
        // the rest of the GUI, or Unity may intercept the event first ----
        HandleUndoRedoShortcuts();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(_rawData != null ? $"info.bdf — {_rawData.name}" : "info.bdf — no Map Raw Data", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            EditorGUI.BeginDisabledGroup(_rawData == null);
            if (GUILayout.Button("Load", GUILayout.Width(SC2UiScale.Pxf(60f)))) Load();
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(_m == null);
            if (GUILayout.Button("Save ✓", GUILayout.Width(SC2UiScale.Pxf(70f)))) Save();
            EditorGUI.EndDisabledGroup();
        }

        if (_m == null || _original == null)
        {
            EditorGUILayout.HelpBox("Assign Map Raw Data in the Context panel, then click Load.", MessageType.Info);
            return;
        }

        // ---- Undo/Redo toolbar ----
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUI.BeginDisabledGroup(_undoIndex <= 0);
            if (GUILayout.Button($"↩ Undo ({_undoIndex})", EditorStyles.toolbarButton, GUILayout.Width(SC2UiScale.Pxf(90f)))) Undo();
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(_undoIndex < 0 || _undoIndex >= _undoStack.Count - 1);
            if (GUILayout.Button($"↪ Redo ({Mathf.Max(0, _undoStack.Count - 1 - _undoIndex)})", EditorStyles.toolbarButton, GUILayout.Width(SC2UiScale.Pxf(90f)))) Redo();
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("⟲ Reset All", EditorStyles.toolbarButton, GUILayout.Width(SC2UiScale.Pxf(110f))))
            {
                if (EditorUtility.DisplayDialog("Reset all changes?",
                    "Every field will be reset to the value from the file (as of the last Load). This can be undone with Undo.",
                    "Reset", "Cancel"))
                {
                    ResetAllToOriginal();
                }
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("📁 Save preset...", EditorStyles.toolbarButton, GUILayout.Width(SC2UiScale.Pxf(150f)))) SavePresetDialog();
            if (GUILayout.Button("📂 Load preset...", EditorStyles.toolbarButton, GUILayout.Width(SC2UiScale.Pxf(150f)))) LoadPresetDialog();
            if (GUILayout.Button("🗄 Restore from backup", EditorStyles.toolbarButton, GUILayout.Width(SC2UiScale.Pxf(170f)))) RestoreFromBackupDialog();
        }

        var m = _m;
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // ---- Version (read-only reference) ----
        EditorGUILayout.LabelField($"Version = {m.Version}", EditorStyles.miniLabel);

        // ---- Sun (@4..28) ----
        if (Fold(ref _fSun, "☀  Sun (@4..28)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var dir = new Vector3(m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ);
                dir = EditorGUILayout.Vector3Field("SunDirection", dir);
                if (GUILayout.Button("Normalize", GUILayout.Width(SC2UiScale.Pxf(90f)))) dir = dir.normalized;
                m.SunDirectionX = dir.x; m.SunDirectionY = dir.y; m.SunDirectionZ = dir.z;

                var col = new Color(m.SunColorR, m.SunColorG, m.SunColorB);
                col = EditorGUILayout.ColorField(new GUIContent("SunColor (HDR)"), col, true, false, true);
                m.SunColorR = col.r; m.SunColorG = col.g; m.SunColorB = col.b;

                m.SunIntensity = SliderR("SunIntensity", m.SunIntensity, InfoFieldLimits.SunIntensity.Min, InfoFieldLimits.SunIntensity.Max, _original.SunIntensity);

                if (GUILayout.Button("▶ Apply to Directional Light")) ApplyToLight(m);
            }
        }

        // ---- ShadowColor (@32..44) ----
        if (Fold(ref _fShadow, "🌑  ShadowColor (@32..44)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var c = new Color(m.ShadowColorR, m.ShadowColorG, m.ShadowColorB, m.ShadowColorA);
                c = EditorGUILayout.ColorField(new GUIContent("ShadowColor"), c, true, true, true);
                m.ShadowColorR = c.r; m.ShadowColorG = c.g; m.ShadowColorB = c.b; m.ShadowColorA = c.a;

                // Moved here from the former "Unverified" foldout — the
                // audit confirms a real name/status ("Strong") for it.
                m.ShadowCastDistance = SliderR("ShadowCastDistance (@492)", m.ShadowCastDistance, InfoFieldLimits.ShadowCastDistance.Min, InfoFieldLimits.ShadowCastDistance.Max, _original.ShadowCastDistance);
            }
        }

        // ---- Water: base (@48, @212..220) ----
        if (Fold(ref _fWater, "🌊  Water (@48, @212..220)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.WaterEnabled = ToggleR("WaterEnabled", m.WaterEnabled, _original.WaterEnabled);
                EditorGUI.BeginDisabledGroup(m.WaterEnabled == 0);
                m.MaxWaterDepth = SliderR("MaxWaterDepth",              m.MaxWaterDepth, InfoFieldLimits.MaxWaterDepth.Min, InfoFieldLimits.MaxWaterDepth.Max, _original.MaxWaterDepth);
                m.WaterElevation = SliderR("WaterElevation",             m.WaterElevation, InfoFieldLimits.WaterElevation.Min, InfoFieldLimits.WaterElevation.Max, _original.WaterElevation);
                m.WaterRenderElevationOverride = SliderR("WaterRenderElevationOverride", m.WaterRenderElevationOverride, InfoFieldLimits.WaterRenderElevationOverride.Min, InfoFieldLimits.WaterRenderElevationOverride.Max, _original.WaterRenderElevationOverride);
                EditorGUI.EndDisabledGroup();
            }
        }

        // ---- Water: Color (@100..112) ----
        if (Fold(ref _fWColor, "🌊  WaterColor (@100..112)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var c = new Color(m.WaterColorR, m.WaterColorG, m.WaterColorB, m.WaterColorA);
                c = EditorGUILayout.ColorField(new GUIContent("WaterColor"), c, true, true, true);
                m.WaterColorR = c.r; m.WaterColorG = c.g; m.WaterColorB = c.b; m.WaterColorA = c.a;
            }
        }

        // ---- Water: Movement (@52..96) ----
        if (Fold(ref _fWMove, "🌊  Water*Movement (@52..96)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                (m.WaterDiffuse1MovementX, m.WaterDiffuse1MovementY) = MoveXY("WaterDiffuse1Movement", m.WaterDiffuse1MovementX, m.WaterDiffuse1MovementY);
                (m.WaterDiffuse2MovementX, m.WaterDiffuse2MovementY) = MoveXY("WaterDiffuse2Movement", m.WaterDiffuse2MovementX, m.WaterDiffuse2MovementY);
                (m.WaterNormal1MovementX, m.WaterNormal1MovementY) = MoveXY("WaterNormal1Movement", m.WaterNormal1MovementX, m.WaterNormal1MovementY);
                (m.WaterNormal2MovementX, m.WaterNormal2MovementY) = MoveXY("WaterNormal2Movement", m.WaterNormal2MovementX, m.WaterNormal2MovementY);
                (m.WaterNormal3MovementX, m.WaterNormal3MovementY) = MoveXY("WaterNormal3Movement", m.WaterNormal3MovementX, m.WaterNormal3MovementY);
                (m.WaterNormal4MovementX, m.WaterNormal4MovementY) = MoveXY("WaterNormal4Movement", m.WaterNormal4MovementX, m.WaterNormal4MovementY);
            }
        }

        // ---- Water: Tiling (@116..136) ----
        if (Fold(ref _fWTile, "🌊  WaterFoam/NormalRepeatRate (@116..136)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var fr = new Vector2(m.WaterFoamRepeatRateX, m.WaterFoamRepeatRateY);
                fr = EditorGUILayout.Vector2Field("WaterFoamRepeatRate", fr);
                m.WaterFoamRepeatRateX = fr.x; m.WaterFoamRepeatRateY = fr.y;

                var nr = new Vector4(m.WaterNormalRepeatRateX, m.WaterNormalRepeatRateY, m.WaterNormalRepeatRateZ, m.WaterNormalRepeatRateW);
                nr = EditorGUILayout.Vector4Field("WaterNormalRepeatRate", nr);
                m.WaterNormalRepeatRateX = nr.x; m.WaterNormalRepeatRateY = nr.y;
                m.WaterNormalRepeatRateZ = nr.z; m.WaterNormalRepeatRateW = nr.w;
            }
        }

        // ---- Water: Optics (@140..212) ----
        if (Fold(ref _fWOptics, "🌊  Water Optics (@140..212)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.WaterRefractionScale = SliderR("WaterRefractionScale", m.WaterRefractionScale, InfoFieldLimits.WaterRefractionScale.Min, InfoFieldLimits.WaterRefractionScale.Max, _original.WaterRefractionScale);
                m.WaterReflectionScale = SliderR("WaterReflectionScale", m.WaterReflectionScale, InfoFieldLimits.WaterReflectionScale.Min, InfoFieldLimits.WaterReflectionScale.Max, _original.WaterReflectionScale);
                m.WaterChromaticScale = SliderR("WaterChromaticScale",  m.WaterChromaticScale, InfoFieldLimits.WaterChromaticScale.Min, InfoFieldLimits.WaterChromaticScale.Max, _original.WaterChromaticScale);
                m.WaterFresnelBias = SliderR("WaterFresnelBias",     m.WaterFresnelBias, InfoFieldLimits.WaterFresnelBias.Min, InfoFieldLimits.WaterFresnelBias.Max, _original.WaterFresnelBias);
                m.WaterFresnelPower = SliderR("WaterFresnelPower", m.WaterFresnelPower, InfoFieldLimits.WaterFresnelPower.Min, InfoFieldLimits.WaterFresnelPower.Max, _original.WaterFresnelPower);
                m.WaterDepthScale = SliderR("WaterDepthScale",      m.WaterDepthScale, InfoFieldLimits.WaterDepthScale.Min, InfoFieldLimits.WaterDepthScale.Max, _original.WaterDepthScale);
                m.WaterWhiteCapBrightness = SliderR("WaterWhiteCapBrightness", m.WaterWhiteCapBrightness, InfoFieldLimits.WaterWhiteCapBrightness.Min, InfoFieldLimits.WaterWhiteCapBrightness.Max, _original.WaterWhiteCapBrightness);
                m.WaterSpecPower = SliderR("WaterSpecPower",    m.WaterSpecPower, InfoFieldLimits.WaterSpecPower.Min, InfoFieldLimits.WaterSpecPower.Max, _original.WaterSpecPower);
                m.WaterBloomFactor = SliderR("WaterBloomFactor",      m.WaterBloomFactor, InfoFieldLimits.WaterBloomFactor.Min, InfoFieldLimits.WaterBloomFactor.Max, _original.WaterBloomFactor);
                m.WaterWhiteCapCutoff = SliderR("WaterWhiteCapCutoff",       m.WaterWhiteCapCutoff, InfoFieldLimits.WaterWhiteCapCutoff.Min, InfoFieldLimits.WaterWhiteCapCutoff.Max, _original.WaterWhiteCapCutoff);
                m.WaterWaveTiming = SliderR("WaterWaveTiming",           m.WaterWaveTiming, InfoFieldLimits.WaterWaveTiming.Min, InfoFieldLimits.WaterWaveTiming.Max, _original.WaterWaveTiming);
                m.WaterWaveModulus = SliderR("WaterWaveModulus",        m.WaterWaveModulus, InfoFieldLimits.WaterWaveModulus.Min, InfoFieldLimits.WaterWaveModulus.Max, _original.WaterWaveModulus);
                m.WaterFoamHeightOffset = SliderR("WaterFoamHeightOffset",     m.WaterFoamHeightOffset, InfoFieldLimits.WaterFoamHeightOffset.Min, InfoFieldLimits.WaterFoamHeightOffset.Max, _original.WaterFoamHeightOffset);
                m.WaterWaveStartDepth = SliderR("WaterWaveStartDepth",  m.WaterWaveStartDepth, InfoFieldLimits.WaterWaveStartDepth.Min, InfoFieldLimits.WaterWaveStartDepth.Max, _original.WaterWaveStartDepth);
            }
        }

        // ---- Water: Misc (@196..208) ----
        if (Fold(ref _fWMisc, "🌊  WaterDepthOrigin / WaterDepthBoundsSize (@196..208)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var off = new Vector2(m.WaterDepthOriginX, m.WaterDepthOriginY);
                off = EditorGUILayout.Vector2Field("WaterDepthOrigin", off);
                m.WaterDepthOriginX = off.x; m.WaterDepthOriginY = off.y;

                var bs = new Vector2(m.WaterDepthBoundsSizeX, m.WaterDepthBoundsSizeY);
                bs = EditorGUILayout.Vector2Field("WaterDepthBoundsSize", bs);
                m.WaterDepthBoundsSizeX = bs.x; m.WaterDepthBoundsSizeY = bs.y;
            }
        }

        // ---- Map (@232) ----
        if (Fold(ref _fMap, "🗺  AirElevation (@232)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
                m.AirElevation = SliderR("AirElevation", m.AirElevation, InfoFieldLimits.AirElevation.Min, InfoFieldLimits.AirElevation.Max, _original.AirElevation);
        }

        // ---- Fog (@260..328) ----
        if (Fold(ref _fFog, "🌫  Fog (@260..328)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var cc = new Color(m.CoreFogColorR, m.CoreFogColorG, m.CoreFogColorB, m.CoreFogColorA);
                cc = EditorGUILayout.ColorField("CoreFogColor", cc);
                m.CoreFogColorR=cc.r; m.CoreFogColorG=cc.g; m.CoreFogColorB=cc.b; m.CoreFogColorA=cc.a;

                var hc = new Color(m.HorizonFogColorR, m.HorizonFogColorG, m.HorizonFogColorB, m.HorizonFogColorA);
                hc = EditorGUILayout.ColorField("HorizonFogColor", hc);
                m.HorizonFogColorR=hc.r; m.HorizonFogColorG=hc.g; m.HorizonFogColorB=hc.b; m.HorizonFogColorA=hc.a;

                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("CoreFogAmounts", EditorStyles.miniBoldLabel);
                m.CoreFogAmount1 = SliderR("CoreFogAmount1", m.CoreFogAmount1, InfoFieldLimits.CoreFogAmount1.Min, InfoFieldLimits.CoreFogAmount1.Max, _original.CoreFogAmount1);
                m.CoreFogAmount2 = SliderR("CoreFogAmount2", m.CoreFogAmount2, InfoFieldLimits.CoreFogAmount2.Min, InfoFieldLimits.CoreFogAmount2.Max, _original.CoreFogAmount2);
                m.CoreFogAmount3 = SliderR("CoreFogAmount3", m.CoreFogAmount3, InfoFieldLimits.CoreFogAmount3.Min, InfoFieldLimits.CoreFogAmount3.Max, _original.CoreFogAmount3);

                EditorGUILayout.LabelField("HorizonFogAmounts", EditorStyles.miniBoldLabel);
                m.HorizonFogAmount1 = SliderR("HorizonFogAmount1", m.HorizonFogAmount1, InfoFieldLimits.HorizonFogAmount1.Min, InfoFieldLimits.HorizonFogAmount1.Max, _original.HorizonFogAmount1);
                m.HorizonFogAmount2 = SliderR("HorizonFogAmount2", m.HorizonFogAmount2, InfoFieldLimits.HorizonFogAmount2.Min, InfoFieldLimits.HorizonFogAmount2.Max, _original.HorizonFogAmount2);
                m.HorizonFogAmount3 = SliderR("HorizonFogAmount3", m.HorizonFogAmount3, InfoFieldLimits.HorizonFogAmount3.Min, InfoFieldLimits.HorizonFogAmount3.Max, _original.HorizonFogAmount3);

                EditorGUILayout.LabelField("CoreFogDists", EditorStyles.miniBoldLabel);
                m.CoreFogDist1 = SliderR("CoreFogDist1", m.CoreFogDist1, InfoFieldLimits.CoreFogDist1.Min, InfoFieldLimits.CoreFogDist1.Max, _original.CoreFogDist1);
                m.CoreFogDist2 = SliderR("CoreFogDist2", m.CoreFogDist2, InfoFieldLimits.CoreFogDist2.Min, InfoFieldLimits.CoreFogDist2.Max, _original.CoreFogDist2);

                EditorGUILayout.LabelField("HorizonFogDists", EditorStyles.miniBoldLabel);
                m.HorizonFogDist1 = SliderR("HorizonFogDist1", m.HorizonFogDist1, InfoFieldLimits.HorizonFogDist1.Min, InfoFieldLimits.HorizonFogDist1.Max, _original.HorizonFogDist1);
                m.HorizonFogDist2 = SliderR("HorizonFogDist2", m.HorizonFogDist2, InfoFieldLimits.HorizonFogDist2.Min, InfoFieldLimits.HorizonFogDist2.Max, _original.HorizonFogDist2);
            }
        }

        // ---- Point Cloud (@332..352) ----
        if (Fold(ref _fPointCloud, "✨  PointCloud (@332..352)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                var origin = new Vector3(m.PointCloudOriginX, m.PointCloudOriginY, m.PointCloudOriginZ);
                origin = EditorGUILayout.Vector3Field("PointCloudOrigin", origin);
                m.PointCloudOriginX = origin.x; m.PointCloudOriginY = origin.y; m.PointCloudOriginZ = origin.z;
                var sz = new Vector3(m.PointCloudSizeX, m.PointCloudSizeY, m.PointCloudSizeZ);
                sz = EditorGUILayout.Vector3Field("PointCloudSize", sz);
                m.PointCloudSizeX = sz.x; m.PointCloudSizeY = sz.y; m.PointCloudSizeZ = sz.z;
                m.PointCloudLightAmount = SliderR("PointCloudLightAmount", m.PointCloudLightAmount, InfoFieldLimits.PointCloudLightAmount.Min, InfoFieldLimits.PointCloudLightAmount.Max, _original.PointCloudLightAmount);
            }
        }

        // ---- Glow (@356..380) ----
        if (Fold(ref _fGlow, "✨  Glow (@356..380)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.GlowEnabled = ToggleR("GlowEnabled", m.GlowEnabled, _original.GlowEnabled);
                m.GlowBlurKernelScale0 = SliderR("GlowBlurKernelScale0", m.GlowBlurKernelScale0, InfoFieldLimits.GlowBlurKernelScale0.Min, InfoFieldLimits.GlowBlurKernelScale0.Max, _original.GlowBlurKernelScale0);
                m.GlowBlurKernelScale1 = SliderR("GlowBlurKernelScale1", m.GlowBlurKernelScale1, InfoFieldLimits.GlowBlurKernelScale1.Min, InfoFieldLimits.GlowBlurKernelScale1.Max, _original.GlowBlurKernelScale1);
                m.GlowBlurPassCount = SliderIntR("GlowBlurPassCount", m.GlowBlurPassCount, InfoFieldLimits.GlowBlurPassCount.Min, InfoFieldLimits.GlowBlurPassCount.Max, _original.GlowBlurPassCount);
                m.GlowBlurFactor0 = SliderR("GlowBlurFactor0", m.GlowBlurFactor0, InfoFieldLimits.GlowBlurFactor0.Min, InfoFieldLimits.GlowBlurFactor0.Max, _original.GlowBlurFactor0);
                m.GlowBlurFactor1 = SliderR("GlowBlurFactor1", m.GlowBlurFactor1, InfoFieldLimits.GlowBlurFactor1.Min, InfoFieldLimits.GlowBlurFactor1.Max, _original.GlowBlurFactor1);
            }
        }

        // ---- Bloom (@368..416) ----
        if (Fold(ref _fGaussian, "🌀  Bloom (@368..416)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.PostProcessSourceWeight = SliderR("PostProcessSourceWeight", m.PostProcessSourceWeight, InfoFieldLimits.PostProcessSourceWeight.Min, InfoFieldLimits.PostProcessSourceWeight.Max, _original.PostProcessSourceWeight);
                m.BloomEnabled = ToggleR("BloomEnabled", m.BloomEnabled, _original.BloomEnabled);
                m.BloomThreshold = SliderR("BloomThreshold", m.BloomThreshold, InfoFieldLimits.BloomThreshold.Min, InfoFieldLimits.BloomThreshold.Max, _original.BloomThreshold);
                m.BloomDirectionCount = SliderIntR("BloomDirectionCount", m.BloomDirectionCount, InfoFieldLimits.BloomDirectionCount.Min, InfoFieldLimits.BloomDirectionCount.Max, _original.BloomDirectionCount);
                m.BloomDirectionPhase = SliderR("BloomDirectionPhase", m.BloomDirectionPhase, InfoFieldLimits.BloomDirectionPhase.Min, InfoFieldLimits.BloomDirectionPhase.Max, _original.BloomDirectionPhase);
                m.BloomSpreadDivisor = SliderR("BloomSpreadDivisor", m.BloomSpreadDivisor, InfoFieldLimits.BloomSpreadDivisor.Min, InfoFieldLimits.BloomSpreadDivisor.Max, _original.BloomSpreadDivisor);
                m.BloomSampleScaleX = SliderR("BloomSampleScaleX", m.BloomSampleScaleX, InfoFieldLimits.BloomSampleScaleX.Min, InfoFieldLimits.BloomSampleScaleX.Max, _original.BloomSampleScaleX);
                m.BloomSampleScaleY = SliderR("BloomSampleScaleY", m.BloomSampleScaleY, InfoFieldLimits.BloomSampleScaleY.Min, InfoFieldLimits.BloomSampleScaleY.Max, _original.BloomSampleScaleY);
                m.BloomResultWeight = SliderR("BloomResultWeight", m.BloomResultWeight, InfoFieldLimits.BloomResultWeight.Min, InfoFieldLimits.BloomResultWeight.Max, _original.BloomResultWeight);
                m.BloomPassCount = SliderIntR("BloomPassCount", m.BloomPassCount, InfoFieldLimits.BloomPassCount.Min, InfoFieldLimits.BloomPassCount.Max, _original.BloomPassCount);
            }
        }

        // ---- Fog of War (@424..432) ----
        if (Fold(ref _fIntel, "🔭  Fog of War (@424..432)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.FogOfWarLowerHeight = SliderR("FogOfWarLowerHeight", m.FogOfWarLowerHeight, InfoFieldLimits.FogOfWarLowerHeight.Min, InfoFieldLimits.FogOfWarLowerHeight.Max, _original.FogOfWarLowerHeight);
                m.FogOfWarHighestHeight = SliderR("FogOfWarHighestHeight", m.FogOfWarHighestHeight, InfoFieldLimits.FogOfWarHighestHeight.Min, InfoFieldLimits.FogOfWarHighestHeight.Max, _original.FogOfWarHighestHeight);
                m.FogOfWarHeightStrength = SliderR("FogOfWarHeightStrength", m.FogOfWarHeightStrength, InfoFieldLimits.FogOfWarHeightStrength.Min, InfoFieldLimits.FogOfWarHeightStrength.Max, _original.FogOfWarHeightStrength);
            }
        }

        // ---- Intel (@436..444) ----
        if (Fold(ref _fRangeCircles, "🔭  Intel (@436..444)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.IntelLowerHeight = SliderR("IntelLowerHeight", m.IntelLowerHeight, InfoFieldLimits.IntelLowerHeight.Min, InfoFieldLimits.IntelLowerHeight.Max, _original.IntelLowerHeight);
                m.IntelHighestHeight = SliderR("IntelHighestHeight", m.IntelHighestHeight, InfoFieldLimits.IntelHighestHeight.Min, InfoFieldLimits.IntelHighestHeight.Max, _original.IntelHighestHeight);
                m.IntelHeightStrength = SliderR("IntelHeightStrength", m.IntelHeightStrength, InfoFieldLimits.IntelHeightStrength.Min, InfoFieldLimits.IntelHeightStrength.Max, _original.IntelHeightStrength);
            }
        }

        // ---- Depth of Field (@448..456) ----
        if (Fold(ref _fDoF, "🎬  DepthOfField (@448..456)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.DepthOfFieldEnabled = ToggleR("DepthOfFieldEnabled", m.DepthOfFieldEnabled, _original.DepthOfFieldEnabled);
                m.DepthOfFieldStart = SliderR("DepthOfFieldStart", m.DepthOfFieldStart, InfoFieldLimits.DepthOfFieldStart.Min, InfoFieldLimits.DepthOfFieldStart.Max, _original.DepthOfFieldStart);
                m.DepthOfFieldEnd = SliderR("DepthOfFieldEnd",   m.DepthOfFieldEnd,   InfoFieldLimits.DepthOfFieldEnd.Min,   InfoFieldLimits.DepthOfFieldEnd.Max, _original.DepthOfFieldEnd);
            }
        }

        // ---- Sharpen (@460..476) — promoted out of "Unverified", now that
        // the audit gives these real, Confirmed names. ----
        if (Fold(ref _fSharpen, "🔪  Sharpen (@460..476)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.SharpenStart = SliderR("SharpenStart", m.SharpenStart, InfoFieldLimits.SharpenStart.Min, InfoFieldLimits.SharpenStart.Max, _original.SharpenStart);
                m.SharpenEnd = SliderR("SharpenEnd", m.SharpenEnd, InfoFieldLimits.SharpenEnd.Min, InfoFieldLimits.SharpenEnd.Max, _original.SharpenEnd);
                m.SharpenBaseAmount = SliderR("SharpenBaseAmount", m.SharpenBaseAmount, InfoFieldLimits.SharpenBaseAmount.Min, InfoFieldLimits.SharpenBaseAmount.Max, _original.SharpenBaseAmount);
                m.SharpenNearOffset = SliderR("SharpenNearOffset", m.SharpenNearOffset, InfoFieldLimits.SharpenNearOffset.Min, InfoFieldLimits.SharpenNearOffset.Max, _original.SharpenNearOffset);
                m.SharpenFarOffset = SliderR("SharpenFarOffset", m.SharpenFarOffset, InfoFieldLimits.SharpenFarOffset.Min, InfoFieldLimits.SharpenFarOffset.Max, _original.SharpenFarOffset);
            }
        }

        // ---- Render Passes (@480..488) — promoted out of "Unverified". ----
        if (Fold(ref _fRenderPasses, "🎞  Render Passes (@480..488)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.RenderPassACEnabled = ToggleR("RenderPassACEnabled", m.RenderPassACEnabled, _original.RenderPassACEnabled);
                m.RenderPassB0Enabled = ToggleR("RenderPassB0Enabled", m.RenderPassB0Enabled, _original.RenderPassB0Enabled);
                m.RenderPassB4Enabled = ToggleR("RenderPassB4Enabled", m.RenderPassB4Enabled, _original.RenderPassB4Enabled);
            }
        }

        // ---- Wind (@496..504) — promoted out of "Unverified". ----
        if (Fold(ref _fWind, "🍃  Wind (@496..504)"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.WindDirectionX = SliderR("WindDirectionX", m.WindDirectionX, InfoFieldLimits.WindDirectionX.Min, InfoFieldLimits.WindDirectionX.Max, _original.WindDirectionX);
                m.WindDirectionZ = SliderR("WindDirectionZ", m.WindDirectionZ, InfoFieldLimits.WindDirectionZ.Min, InfoFieldLimits.WindDirectionZ.Max, _original.WindDirectionZ);
                m.WindSpeed = SliderR("WindSpeed", m.WindSpeed, InfoFieldLimits.WindSpeed.Min, InfoFieldLimits.WindSpeed.Max, _original.WindSpeed);
            }
        }

        // ---- Advanced — effect not confirmed (was "Unknown / low
        // confidence"). Shrunk to just the fields the audit still calls
        // semantically unresolved; still fully editable, same as every
        // other field — the label is a visual flag only, not a gate. ----
        if (Fold(ref _fUnknown, "⚠  Advanced — effect not confirmed"))
        {
            using (new EditorGUILayout.VerticalScope(_sectionStyle))
            {
                m.WaterAuxParameter0 = SliderR("WaterAuxParameter0 (@224)", m.WaterAuxParameter0, InfoFieldLimits.WaterAuxParameter0.Min, InfoFieldLimits.WaterAuxParameter0.Max, _original.WaterAuxParameter0);
                m.WaterAuxParameter1 = SliderR("WaterAuxParameter1 (@228)", m.WaterAuxParameter1, InfoFieldLimits.WaterAuxParameter1.Min, InfoFieldLimits.WaterAuxParameter1.Max, _original.WaterAuxParameter1);
                m.ReservedZero236 = SliderR("ReservedZero236 (@236, zero on all 56 PC maps)", m.ReservedZero236, InfoFieldLimits.ReservedZero236.Min, InfoFieldLimits.ReservedZero236.Max, _original.ReservedZero236);
            }
        }

        // ---- Texture paths (@240..256) ----
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("🖼  Texture Paths (@240..256)", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope(_sectionStyle))
        {
            m.EnvironmentMap         = EditorGUILayout.TextField("EnvironmentMap",         m.EnvironmentMap);
            m.SkyMap                 = EditorGUILayout.TextField("SkyMap",                 m.SkyMap);
            m.ReservedTexturePath    = EditorGUILayout.TextField("ReservedTexturePath",    m.ReservedTexturePath);
            m.ColorCorrectionCube    = EditorGUILayout.TextField("ColorCorrectionCube",    m.ColorCorrectionCube);
            m.FogOfWarCorrectionCube = EditorGUILayout.TextField("FogOfWarCorrectionCube", m.FogOfWarCorrectionCube);
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.EndScrollView();

        // Snapshot the current Header state onto the undo stack if something
        // changed since the last snapshot. Debounce: while dragging a slider
        // the GUI is called every frame — snapshot no more than once per N
        // seconds, so one gesture doesn't fill the stack with hundreds of
        // intermediate states.
        MaybeSnapshot();
    }

    // ================================================================
    // Undo / Redo (stack of Header snapshots)
    // ================================================================

    private void HandleUndoRedoShortcuts()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;
        if (!e.control && !e.command) return; // Ctrl on Win/Linux, Cmd on Mac

        if (e.keyCode == KeyCode.Z && !e.shift)
        {
            Undo(); e.Use();
        }
        else if (e.keyCode == KeyCode.Y || (e.keyCode == KeyCode.Z && e.shift))
        {
            Redo(); e.Use();
        }
    }

    /// <summary>Snapshots the Header if it differs from the last snapshot (debounced).</summary>
    private void MaybeSnapshot()
    {
        if (_m == null) return;
        var now = EditorApplication.timeSinceStartup;

        // Always snapshot the first time (right after Load)
        if (_undoStack.Count == 0)
        {
            PushSnapshot();
            return;
        }

        // Nothing to snapshot if the current Header matches the top of the stack
        var top = _undoStack[_undoIndex];
        if (HeaderEquals(top, _m.Header)) return;

        // Debounce: no more than once per SnapshotDebounceSeconds IF this is
        // a continuous drag (the previous snapshot was very recent). If a
        // longer time has passed, this is more likely a discrete action
        // (a separate click), so snapshot immediately.
        if (now - _lastSnapshotTime < SnapshotDebounceSeconds)
        {
            // Update the LAST snapshot instead of adding a new one — this
            // collapses a slider drag's fast intermediate frames
            // into a single undo step (start to end of the gesture).
            _undoStack[_undoIndex] = (byte[])_m.Header.Clone();
            _lastSnapshotTime = now;
            return;
        }

        PushSnapshot();
    }

    private void PushSnapshot()
    {
        if (_m == null) return;

        // If we're not currently at the top of the stack (after an Undo,
        // then a new edit) — truncate the "future" (standard redo-stack behavior).
        if (_undoIndex < _undoStack.Count - 1)
            _undoStack.RemoveRange(_undoIndex + 1, _undoStack.Count - _undoIndex - 1);

        _undoStack.Add((byte[])_m.Header.Clone());
        _undoIndex = _undoStack.Count - 1;
        _lastSnapshotTime = EditorApplication.timeSinceStartup;

        if (_undoStack.Count > MaxUndoSteps)
        {
            _undoStack.RemoveAt(0);
            _undoIndex--;
        }
    }

    /// <summary>Resets EVERY field to the value from the file (as of Load), as a single undo step.</summary>
    private void ResetAllToOriginal()
    {
        if (_m == null || _original == null) return;
        System.Array.Copy(_original.Header, _m.Header, 508);
        PushSnapshot();
        RepaintHost?.Invoke();
    }

    private void Undo()
    {
        if (_m == null || _undoIndex <= 0) return;
        _undoIndex--;
        System.Array.Copy(_undoStack[_undoIndex], _m.Header, 508);
        _lastSnapshotTime = EditorApplication.timeSinceStartup;
        RepaintHost?.Invoke();
    }

    private void Redo()
    {
        if (_m == null || _undoIndex >= _undoStack.Count - 1) return;
        _undoIndex++;
        System.Array.Copy(_undoStack[_undoIndex], _m.Header, 508);
        _lastSnapshotTime = EditorApplication.timeSinceStartup;
        RepaintHost?.Invoke();
    }

    private static bool HeaderEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ================================================================
    // On-disk file backup
    // ================================================================

    /// <summary>
    /// Makes a backup copy of info.bytes RIGHT NEXT to it (with a timestamp
    /// in the name), once per editing session for this map — in case the
    /// mapmaker wants to manually compare/restore it through a file
    /// manager, independent of this window's own internal Undo stack.
    /// </summary>
    private void BackupCurrentInfoBytesIfNeeded()
    {
        if (_rawData == null || _backupDoneThisSession) return;
        var data = _rawData.GetInfoData();
        if (data.Length == 0) { _backupDoneThisSession = true; return; }

        var assetDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_rawData))!.Replace('\\', '/');
        var backupDir = ResolveAbsolutePath(assetDir + "/InfoBackups");
        System.IO.Directory.CreateDirectory(backupDir);

        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupPath = System.IO.Path.Combine(backupDir, $"info_{stamp}.bytes");
        System.IO.File.WriteAllBytes(backupPath, data);

        _backupDoneThisSession = true;
        Debug.Log($"[SC2EnvironmentEditor] Backed up info.bytes -> {backupPath}");
    }

    private void RestoreFromBackupDialog()
    {
        if (_rawData == null) { EditorUtility.DisplayDialog("No RawData", "Select a SC2MapRawData first.", "OK"); return; }

        var assetDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_rawData))!.Replace('\\', '/');
        var backupDir = ResolveAbsolutePath(assetDir + "/InfoBackups");
        if (!System.IO.Directory.Exists(backupDir))
        {
            EditorUtility.DisplayDialog("No backups", $"Backup folder \"{backupDir}\" not found.", "OK");
            return;
        }

        var picked = EditorUtility.OpenFilePanel("Choose an info.bytes backup", backupDir, "bytes");
        if (string.IsNullOrEmpty(picked)) return;

        try
        {
            var bytes = System.IO.File.ReadAllBytes(picked);
            var container = new BdfContainer { Platform = _rawData.Platform, Data = bytes, OffsetTable = _rawData.GetInfoOt(), NumFiles = 1 };
            _m = InfoBdf.Parse(container);
            _undoStack.Clear(); _undoIndex = -1;
            PushSnapshot();
            Debug.Log($"[SC2EnvironmentEditor] Restored from backup: {picked}");
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("Restore failed", ex.Message, "OK");
        }
    }

    // ================================================================
    // Presets (JSON)
    // ================================================================

    private void SavePresetDialog()
    {
        if (_m == null) return;
        var path = EditorUtility.SaveFilePanel("Save info.bdf preset", "", _presetName.Length > 0 ? _presetName : "preset", "json");
        if (string.IsNullOrEmpty(path)) return;

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var preset = InfoPreset.FromModel(_m, name, _presetNote);
        System.IO.File.WriteAllText(path, preset.ToJson());
        Debug.Log($"[SC2EnvironmentEditor] Preset saved -> {path}");
    }

    private void LoadPresetDialog()
    {
        if (_m == null) { EditorUtility.DisplayDialog("No data", "Load a map first.", "OK"); return; }
        var path = EditorUtility.OpenFilePanel("Load info.bdf preset", "", "json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var json = System.IO.File.ReadAllText(path);
            var preset = InfoPreset.FromJson(json);
            if (preset == null) { EditorUtility.DisplayDialog("Error", "Could not parse the preset JSON.", "OK"); return; }

            preset.ApplyTo(_m);
            PushSnapshot();
            Debug.Log($"[SC2EnvironmentEditor] Preset '{preset.PresetName}' loaded from {path}");
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("Preset load failed", ex.Message, "OK");
        }
    }

    // ================================================================
    // Paths
    // ================================================================

    /// <summary>Converts a Unity Asset path ("Assets/...") into an absolute filesystem path.</summary>
    private static string ResolveAbsolutePath(string assetRelativePath)
    {
        var sub = assetRelativePath.Equals("Assets", System.StringComparison.Ordinal)
            ? ""
            : assetRelativePath["Assets/".Length..];
        return string.IsNullOrEmpty(sub) ? Application.dataPath : System.IO.Path.Combine(Application.dataPath, sub);
    }

    private void Load()
    {
        if (_rawData == null) return;
        var b = _rawData.GetInfoData();
        if (b.Length == 0) { Debug.LogWarning("[SC2EnvironmentEditor] info.bytes is empty"); return; }

        BackupCurrentInfoBytesIfNeeded();

        _m = InfoBdf.Parse(new BdfContainer { Platform=_rawData.Platform, Data=b, OffsetTable=_rawData.GetInfoOt(), NumFiles=1 });

        // Separate, unchanging snapshot of "what the file had" for per-field reset (⟲)
        _original = InfoBdf.Parse(new BdfContainer { Platform=_rawData.Platform, Data=b, OffsetTable=_rawData.GetInfoOt(), NumFiles=1 });

        _undoStack.Clear();
        _undoIndex = -1;
        PushSnapshot(); // first snapshot = state right after loading

        Debug.Log("[SC2EnvironmentEditor] Loaded.");
    }

    private void Save()
    {
        if (_rawData == null || _m == null) return;
        var (d, ot) = InfoBdf.Serialize(_m, _rawData.Platform);
        Debug.Log($"[SC2EnvironmentEditor] Save(): SunDirection=({_m.SunDirectionX:F4},{_m.SunDirectionY:F4},{_m.SunDirectionZ:F4})");

        // AssetDatabase.GetAssetPath ALWAYS returns a path RELATIVE to
        // the project (starting with "Assets/"), not an absolute filesystem
        // path. So assetDir is already the right relative path for
        // AssetDatabase.LoadAssetAtPath, while actually writing to disk
        // (System.IO.File.*) needs a SEPARATE absolute path, built via
        // Application.dataPath.
        var assetDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(_rawData))!.Replace('\\', '/');
        var relInfo   = assetDir + "/info.bytes";
        var relInfoOt = assetDir + "/info_ot.bytes";

        var subPath = assetDir.Equals("Assets", System.StringComparison.Ordinal)
            ? ""
            : assetDir["Assets/".Length..];
        var absDir = string.IsNullOrEmpty(subPath)
            ? Application.dataPath
            : System.IO.Path.Combine(Application.dataPath, subPath);
        var absInfo   = System.IO.Path.Combine(absDir, "info.bytes");
        var absInfoOt = System.IO.Path.Combine(absDir, "info_ot.bytes");

        System.IO.File.WriteAllBytes(absInfo, d);
        var otB = new byte[ot.Length*4]; System.Buffer.BlockCopy(ot,0,otB,0,otB.Length);
        System.IO.File.WriteAllBytes(absInfoOt, otB);

        AssetDatabase.ImportAsset(relInfo,   ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(relInfoOt, ImportAssetOptions.ForceSynchronousImport);

        _rawData.InfoBytes   = AssetDatabase.LoadAssetAtPath<TextAsset>(relInfo);
        _rawData.InfoOtBytes = AssetDatabase.LoadAssetAtPath<TextAsset>(relInfoOt);
        EditorUtility.SetDirty(_rawData);
        AssetDatabase.SaveAssets();

        // Verification: re-read what's ACTUALLY sitting in _rawData.InfoBytes
        // after Refresh+LoadAssetAtPath — this is exactly what
        // ExportInfoOnly/Export will see via data.GetInfoData(). If these
        // values don't match what was just saved, the problem is in
        // TextAsset caching, not the actual disk write.
        if (_rawData.InfoBytes != null)
        {
            var verifyContainer = new BdfContainer
                { Platform = _rawData.Platform, Data = _rawData.GetInfoData(), OffsetTable = _rawData.GetInfoOt(), NumFiles = 1 };
            var verifyModel = InfoBdf.Parse(verifyContainer);
            Debug.Log($"[SC2EnvironmentEditor] Verification after Refresh: SunDirection=({verifyModel.SunDirectionX:F4},{verifyModel.SunDirectionY:F4},{verifyModel.SunDirectionZ:F4})");
        }
        else
        {
            Debug.LogError("[SC2EnvironmentEditor] _rawData.InfoBytes == null after LoadAssetAtPath! Path: " + relInfo);
        }

        Debug.Log("[SC2EnvironmentEditor] Saved.");
    }

    private static void ApplyToLight(InfoModel m)
    {
        var light = Object.FindFirstObjectByType<Light>();
        if (light == null) return;
        light.color = new Color(m.SunColorR, m.SunColorG, m.SunColorB);
        light.intensity = m.SunIntensity;
        var d = new Vector3(m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ);
        if (d != Vector3.zero) light.transform.rotation = Quaternion.LookRotation(-d);
        EditorUtility.SetDirty(light);
    }

    private static bool Fold(ref bool v, string label) =>
        v = EditorGUILayout.Foldout(v, label, true, EditorStyles.foldoutHeader);

    // ----------------------------------------------------------------
    // Variants with a ⟲ button to reset ONE field to its value from the file
    // (see _original). The button is enabled only if the current value
    // differs from the original — otherwise it's disabled, so it doesn't
    // draw attention where there's nothing to reset.
    // ----------------------------------------------------------------

    private const float ResetButtonWidth = 22f;

    private static bool ResetButton(bool changed)
    {
        EditorGUI.BeginDisabledGroup(!changed);
        var clicked = GUILayout.Button(new GUIContent("⟲", "Reset to the value from the file"), GUILayout.Width(ResetButtonWidth));
        EditorGUI.EndDisabledGroup();
        return clicked && changed;
    }

    /// <summary>Slider + reset button. Returns the new value.
    /// [min,max] only bounds the drag handle — Unity's native
    /// EditorGUILayout.Slider already leaves the paired manual-entry number
    /// field unclamped on its own, so no extra Mathf.Clamp wrapping here.</summary>
    private static float SliderR(string l, float v, float min, float max, float original)
    {
        EditorGUILayout.BeginHorizontal();
        var changed = !Mathf.Approximately(v, original);
        var newV = EditorGUILayout.Slider(l, v, min, max);
        if (ResetButton(changed)) newV = original;
        EditorGUILayout.EndHorizontal();
        return newV;
    }

    /// <summary>Discrete slider + reset button for int fields — same
    /// pattern as SliderR, since every numeric field gets a
    /// slider except checkboxes/toggles and color pickers, int fields
    /// included. Same soft-bounded contract as SliderR.</summary>
    private static int SliderIntR(string l, int v, int min, int max, int original)
    {
        EditorGUILayout.BeginHorizontal();
        var changed = v != original;
        var newV = EditorGUILayout.IntSlider(l, v, min, max);
        if (ResetButton(changed)) newV = original;
        EditorGUILayout.EndHorizontal();
        return newV;
    }

    private static int ToggleR(string l, int v, int original)
    {
        EditorGUILayout.BeginHorizontal();
        var changed = v != original;
        var newV = EditorGUILayout.Toggle(l, v != 0) ? 1 : 0;
        if (ResetButton(changed)) newV = original;
        EditorGUILayout.EndHorizontal();
        return newV;
    }

    private static (float x, float y) MoveXY(string label, float x, float y)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(label, GUILayout.Width(SC2UiScale.Pxf(190f)));
        x = EditorGUILayout.FloatField("X", x);
        y = EditorGUILayout.FloatField("Y", y);
        EditorGUILayout.EndHorizontal();
        return (x, y);
    }
}
