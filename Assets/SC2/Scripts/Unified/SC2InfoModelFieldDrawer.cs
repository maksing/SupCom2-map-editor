using UnityEngine;
using BdfIO.Formats.Info;
using SC2.MapEditor.Document;

/// <summary>
/// The per-tab field-drawing logic for an info.bdf InfoModel — extracted out
/// of SC2EnvironmentShell so a second host (the standalone Info.bdf Editor
/// window, which edits an arbitrary loaded InfoModel independent of the
/// active map) can draw the exact same ~80 fields without duplicating this
/// UI. Every method here only touches its InfoModel parameter and `Original`
/// (used purely for the per-field reset-to-original buttons) — never
/// SC2TerrainData/SC2MapSession/anything document- or undo-related, so it has
/// no opinion about WHERE the edited values end up; that's entirely the
/// host's job (SC2EnvironmentShell.MaybeSnapshot/Commit for the embedded
/// shell, SC2InfoBdfEditorWindow's own Save buttons for the standalone
/// window).
///
/// Same GUILayout/GUI-only (via SC2EnvFieldGui) convention as before — this
/// class itself doesn't require Editor-only APIs, even though its one
/// current second consumer happens to be Editor-only.
///
/// Field names/offsets/tab placement here match the mapping.csv audit
/// (outputs/SC2_InfoBdf_Exe_Shader_Audit_20260801) — see the InfoBdf field-
/// mapping alignment plan for the full rename/reorganization rationale.
/// </summary>
public sealed class SC2InfoModelFieldDrawer
{
    /// <summary>Set by the host before drawing each frame — the reset-to-
    /// original comparison target for every per-field ⟲ button below.</summary>
    public InfoModel Original;

    // _fSun/_fShadow/_fPointCloud/_fFogOfWar/_fIntel/_fDoF/_fSharpen/
    // _fRenderPasses/_fAirElevation/_fWind/_fAdvanced/_fBloom/_fGlow
    // removed — those tabs no longer have an inner foldout (single-category
    // tabs don't get a collapse button, per direction — see
    // DrawSunTab/DrawShadowTab/DrawPointCloudTab/DrawFogOfWarTab/
    // DrawAirElevationTab/DrawWindTab/DrawUnverifiedTab/DrawBloomTab/
    // DrawGlowTab, the last of which lost its own foldout once Bloom moved
    // out and left it with only one remaining category).
    // Multi-category tabs (Water, Fog) keep real foldouts, all defaulted to
    // expanded (true) per direction.
    private bool _fWater = true, _fWColor = true;
    private bool _fWMove = true, _fWTile = true, _fWOptics = true, _fWMisc = true;
    private bool _fFogCore = true, _fFogHorizon = true;

    // Per-field Texture/resolved-path cache backing DrawTexturesTab's 5
    // picker rows — see DrawInfoTextureField. Sentinel "\0" (never a real
    // path, including "") so the very first draw always resolves.
    private Texture _texEnvironmentMap; private string _texEnvironmentMapPath = "\0";
    private Texture _texSkyMap; private string _texSkyMapPath = "\0";
    private Texture _texReservedTexturePath; private string _texReservedTexturePathPath = "\0";
    private Texture _texColorCorrectionCube; private string _texColorCorrectionCubePath = "\0";
    private Texture _texFogOfWarCorrectionCube; private string _texFogOfWarCorrectionCubePath = "\0";

    // ---- Sun tab ---- (no inner foldout — the top-level tab selection
    // already scopes visibility, so wrapping the tab's only section in a
    // second collapsible layer was redundant, per direction.)
    public void DrawSunTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("sun", "Sun (@4..28)");
        var dir = new Vector3(m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ);
        dir = SC2EnvFieldGui.Vector3Field("SunDirection", dir, "sun.dir");
        if (SC2EnvFieldGui.Button("Normalize", GUILayout.Width(SC2UiScale.Pxf(90f)))) dir = dir.normalized;
        m.SunDirectionX = dir.x; m.SunDirectionY = dir.y; m.SunDirectionZ = dir.z;

        var col = new Color(m.SunColorR, m.SunColorG, m.SunColorB);
        col = SC2EnvFieldGui.ColorField("SunColor (HDR)", col, "sun.color", hdr: true, showAlpha: false);
        m.SunColorR = col.r; m.SunColorG = col.g; m.SunColorB = col.b;

        m.SunIntensity = SliderR("SunIntensity", m.SunIntensity, InfoFieldLimits.SunIntensity.Min, InfoFieldLimits.SunIntensity.Max, Original.SunIntensity);
        if (SC2EnvFieldGui.Button("▶ Apply to Directional Light")) ApplyToLight(m);

        DrawSunShadowPreview(new Vector3(m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ));
    }

    // ---- Shadow preview (Sun tab) ----
    // A cube on flat ground seen from above (SC2's Z grows down the map, so map up = -Z; right = +X): its shadow shows which way the
    // sun throws shadows and how long they get. Rendered on the CPU into a small texture that is rebuilt
    // only when the sun direction or the cube height changes.
    private float _sunPreviewHeight = 10f;
    private Texture2D _sunPreviewTex;
    private Vector3 _sunPreviewDir = new Vector3(float.NaN, 0f, 0f);
    private float _sunPreviewBuiltHeight = -1f;
    private const int SunPreviewPixels = 240;

    private void DrawSunShadowPreview(Vector3 sunDirection)
    {
        GUILayout.Space(SC2UiScale.Pxf(6f));
        SC2EnvFieldGui.SectionLabel("Shadow preview");
        _sunPreviewHeight = Mathf.Max(0.1f, SC2EnvFieldGui.FloatField("Cube height (units)", _sunPreviewHeight, "sun.preview.height"));

        var shadow = SC2SunShadowMath.Compute(sunDirection);
        if (_sunPreviewTex == null || _sunPreviewDir != sunDirection || !Mathf.Approximately(_sunPreviewBuiltHeight, _sunPreviewHeight))
        {
            RebuildSunPreview(shadow, sunDirection);
            _sunPreviewDir = sunDirection;
            _sunPreviewBuiltHeight = _sunPreviewHeight;
        }

        var size = SC2UiScale.Pxf(SunPreviewPixels);
        var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
        if (Event.current.type == EventType.Repaint)
            GUI.DrawTexture(rect, _sunPreviewTex, ScaleMode.ScaleToFit);

        if (!shadow.HasShadow)
        {
            SC2EnvFieldGui.WrapLabel("The sun is at or below the horizon (SunDirection.Y ≤ 0): objects cast no ground shadow.", size);
            return;
        }
        var length = shadow.LengthPerUnit * _sunPreviewHeight;
        var towards = (shadow.AzimuthDeg + 180f) % 360f;
        SC2EnvFieldGui.WrapLabel(
            $"Sun {shadow.ElevationDeg:F1}° above the horizon, from {shadow.AzimuthDeg:F0}°. " +
            $"A cube {_sunPreviewHeight:0.##} units tall casts a shadow {length:0.#} units long toward {towards:F0}° " +
            "(degrees clockwise from map up). Top view as on the map: Z grows downward, X to the right; yellow line = toward the sun; grid = 10 units.",
            size);
    }

    private void RebuildSunPreview(SC2SunShadowMath.Result shadow, Vector3 sunDirection)
    {
        if (_sunPreviewTex == null)
        {
            _sunPreviewTex = new Texture2D(SunPreviewPixels, SunPreviewPixels, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        var side = _sunPreviewHeight;
        // Picture coordinates: x right, y up the map. SC2's Z grows downward, so it is flipped here.
        var offset = shadow.HasShadow ? new Vector2(shadow.OffsetPerUnit.x, -shadow.OffsetPerUnit.y) * _sunPreviewHeight : Vector2.zero;
        // Frame the cube and its shadow; a near-horizontal sun would need an endless frame, so the shadow
        // is allowed to run off the picture past 7 heights.
        var reach = Mathf.Min(offset.magnitude, side * 7f);
        var halfView = Mathf.Max(side * 1.8f, (reach + side) * 0.6f);
        var centre = offset.sqrMagnitude > 1e-8f ? offset.normalized * (reach * 0.5f) : Vector2.zero;
        var unitsPerPixel = halfView * 2f / SunPreviewPixels;

        var ground = new Color32(92, 104, 92, 255);
        var gridLine = new Color32(110, 124, 110, 255);
        var shadowColor = new Color32(28, 32, 38, 255);
        var cubeColor = new Color32(232, 224, 190, 255);
        var cubeEdge = new Color32(120, 110, 80, 255);
        var sunColor = new Color32(255, 215, 40, 255);
        var pixels = new Color32[SunPreviewPixels * SunPreviewPixels];
        const float gridStep = 10f;
        var lineWidth = unitsPerPixel * 1.2f;

        for (var y = 0; y < SunPreviewPixels; y++)
        {
            for (var x = 0; x < SunPreviewPixels; x++)
            {
                var p = centre + new Vector2((x + 0.5f) * unitsPerPixel - halfView, (y + 0.5f) * unitsPerPixel - halfView);
                var c = ground;
                var gx = Mathf.Abs(Mathf.Repeat(p.x + gridStep * 0.5f, gridStep) - gridStep * 0.5f);
                var gz = Mathf.Abs(Mathf.Repeat(p.y + gridStep * 0.5f, gridStep) - gridStep * 0.5f);
                if (gx < lineWidth * 0.5f || gz < lineWidth * 0.5f) c = gridLine;
                if (shadow.HasShadow && SC2SunShadowMath.InSweptSquare(p, side, offset)) c = shadowColor;
                var inCube = Mathf.Abs(p.x) <= side * 0.5f && Mathf.Abs(p.y) <= side * 0.5f;
                if (inCube)
                {
                    var edge = Mathf.Min(side * 0.5f - Mathf.Abs(p.x), side * 0.5f - Mathf.Abs(p.y)) < unitsPerPixel * 1.5f;
                    c = edge ? cubeEdge : cubeColor;
                }
                pixels[y * SunPreviewPixels + x] = c;
            }
        }

        // Toward-the-sun marker: a line from the cube's centre along the sun's horizontal direction.
        if (new Vector2(sunDirection.x, sunDirection.z).sqrMagnitude > 1e-8f)
        {
            var d = new Vector2(Mathf.Sin(shadow.AzimuthDeg * Mathf.Deg2Rad), Mathf.Cos(shadow.AzimuthDeg * Mathf.Deg2Rad));
            var lengthUnits = halfView * 0.55f;
            for (var s = 0f; s <= lengthUnits; s += unitsPerPixel * 0.5f)
            {
                var wp = d * s;
                var px = Mathf.RoundToInt((wp.x - centre.x + halfView) / unitsPerPixel - 0.5f);
                var py = Mathf.RoundToInt((wp.y - centre.y + halfView) / unitsPerPixel - 0.5f);
                for (var ox = -1; ox <= 1; ox++)
                    for (var oy = -1; oy <= 1; oy++)
                    {
                        var qx = px + ox; var qy = py + oy;
                        if (qx >= 0 && qy >= 0 && qx < SunPreviewPixels && qy < SunPreviewPixels)
                            pixels[qy * SunPreviewPixels + qx] = sunColor;
                    }
            }
        }

        _sunPreviewTex.SetPixels32(pixels);
        _sunPreviewTex.Apply(false, false);
    }

    // ---- Shadow tab ---- (same reasoning as Sun above — no inner foldout.)
    public void DrawShadowTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("moon", "ShadowColor (@32..44)");
        var c = new Color(m.ShadowColorR, m.ShadowColorG, m.ShadowColorB, m.ShadowColorA);
        c = SC2EnvFieldGui.ColorField("ShadowColor", c, "shadow.color");
        m.ShadowColorR = c.r; m.ShadowColorG = c.g; m.ShadowColorB = c.b; m.ShadowColorA = c.a;

        // ShadowCastDistance(@492) moved here from the former "Unverified"
        // tab — the CSV confirms a real name/status ("Strong") for it, it
        // was only ever parked there from an old naming doubt.
        m.ShadowCastDistance = SliderR("ShadowCastDistance (@492)", m.ShadowCastDistance, InfoFieldLimits.ShadowCastDistance.Min, InfoFieldLimits.ShadowCastDistance.Max, Original.ShadowCastDistance);
    }

    // ---- Water tab: merged with the former "Water FX" tab, per direction —
    // one unified tab, split into collapsible foldout categories rather than
    // two separate top-level tabs. ----
    public void DrawWaterTab(InfoModel m)
    {
        if (SC2EnvFieldGui.Foldout("water", ref _fWater, "Water (@48, @212..220)"))
        {
            m.WaterEnabled = ToggleR("WaterEnabled", m.WaterEnabled, Original.WaterEnabled);
            // Restores whatever GUI.enabled was BEFORE this field group
            // (not hardcode true) — a caller can legitimately disable the
            // whole panel around this call (e.g. SC2GameViewEditorUI while
            // a top-panel dropdown is open, to stop clicks reaching panel
            // content underneath it); unconditionally re-enabling here
            // silently undid that for every field drawn after this group.
            var guiEnabledBeforeWaterFields = GUI.enabled;
            GUI.enabled = guiEnabledBeforeWaterFields && m.WaterEnabled != 0;
            m.MaxWaterDepth = SliderR("MaxWaterDepth", m.MaxWaterDepth, InfoFieldLimits.MaxWaterDepth.Min, InfoFieldLimits.MaxWaterDepth.Max, Original.MaxWaterDepth);
            m.WaterElevation = SliderR("WaterElevation", m.WaterElevation, InfoFieldLimits.WaterElevation.Min, InfoFieldLimits.WaterElevation.Max, Original.WaterElevation);
            m.WaterRenderElevationOverride = SliderR("WaterRenderElevationOverride", m.WaterRenderElevationOverride, InfoFieldLimits.WaterRenderElevationOverride.Min, InfoFieldLimits.WaterRenderElevationOverride.Max, Original.WaterRenderElevationOverride);
            GUI.enabled = guiEnabledBeforeWaterFields;
        }

        if (SC2EnvFieldGui.Foldout("water", ref _fWColor, "WaterColor (@100..112)"))
        {
            var c = new Color(m.WaterColorR, m.WaterColorG, m.WaterColorB, m.WaterColorA);
            c = SC2EnvFieldGui.ColorField("WaterColor", c, "water.color");
            m.WaterColorR = c.r; m.WaterColorG = c.g; m.WaterColorB = c.b; m.WaterColorA = c.a;
        }

        if (SC2EnvFieldGui.Foldout("water", ref _fWMove, "WaterDiffuse/NormalMovement (@52..96)"))
        {
            (m.WaterDiffuse1MovementX, m.WaterDiffuse1MovementY) = MoveXY("WaterDiffuse1Movement", m.WaterDiffuse1MovementX, m.WaterDiffuse1MovementY);
            (m.WaterDiffuse2MovementX, m.WaterDiffuse2MovementY) = MoveXY("WaterDiffuse2Movement", m.WaterDiffuse2MovementX, m.WaterDiffuse2MovementY);
            (m.WaterNormal1MovementX, m.WaterNormal1MovementY) = MoveXY("WaterNormal1Movement", m.WaterNormal1MovementX, m.WaterNormal1MovementY);
            (m.WaterNormal2MovementX, m.WaterNormal2MovementY) = MoveXY("WaterNormal2Movement", m.WaterNormal2MovementX, m.WaterNormal2MovementY);
            (m.WaterNormal3MovementX, m.WaterNormal3MovementY) = MoveXY("WaterNormal3Movement", m.WaterNormal3MovementX, m.WaterNormal3MovementY);
            (m.WaterNormal4MovementX, m.WaterNormal4MovementY) = MoveXY("WaterNormal4Movement", m.WaterNormal4MovementX, m.WaterNormal4MovementY);
        }

        if (SC2EnvFieldGui.Foldout("water", ref _fWTile, "WaterFoam/NormalRepeatRate (@116..136)"))
        {
            var fr = new Vector2(m.WaterFoamRepeatRateX, m.WaterFoamRepeatRateY);
            fr = SC2EnvFieldGui.Vector2Field("WaterFoamRepeatRate", fr, "water.foamrate");
            m.WaterFoamRepeatRateX = fr.x; m.WaterFoamRepeatRateY = fr.y;

            var nr = new Vector4(m.WaterNormalRepeatRateX, m.WaterNormalRepeatRateY, m.WaterNormalRepeatRateZ, m.WaterNormalRepeatRateW);
            nr = SC2EnvFieldGui.Vector4Field("WaterNormalRepeatRate", nr, "water.normalrate");
            m.WaterNormalRepeatRateX = nr.x; m.WaterNormalRepeatRateY = nr.y;
            m.WaterNormalRepeatRateZ = nr.z; m.WaterNormalRepeatRateW = nr.w;
        }

        if (SC2EnvFieldGui.Foldout("water", ref _fWOptics, "Water Optics (@140..212)"))
        {
            m.WaterRefractionScale = SliderR("WaterRefractionScale", m.WaterRefractionScale, InfoFieldLimits.WaterRefractionScale.Min, InfoFieldLimits.WaterRefractionScale.Max, Original.WaterRefractionScale);
            m.WaterReflectionScale = SliderR("WaterReflectionScale", m.WaterReflectionScale, InfoFieldLimits.WaterReflectionScale.Min, InfoFieldLimits.WaterReflectionScale.Max, Original.WaterReflectionScale);
            m.WaterChromaticScale = SliderR("WaterChromaticScale", m.WaterChromaticScale, InfoFieldLimits.WaterChromaticScale.Min, InfoFieldLimits.WaterChromaticScale.Max, Original.WaterChromaticScale);
            m.WaterFresnelBias = SliderR("WaterFresnelBias", m.WaterFresnelBias, InfoFieldLimits.WaterFresnelBias.Min, InfoFieldLimits.WaterFresnelBias.Max, Original.WaterFresnelBias);
            m.WaterFresnelPower = SliderR("WaterFresnelPower", m.WaterFresnelPower, InfoFieldLimits.WaterFresnelPower.Min, InfoFieldLimits.WaterFresnelPower.Max, Original.WaterFresnelPower);
            m.WaterDepthScale = SliderR("WaterDepthScale", m.WaterDepthScale, InfoFieldLimits.WaterDepthScale.Min, InfoFieldLimits.WaterDepthScale.Max, Original.WaterDepthScale);
            m.WaterWhiteCapBrightness = SliderR("WaterWhiteCapBrightness", m.WaterWhiteCapBrightness, InfoFieldLimits.WaterWhiteCapBrightness.Min, InfoFieldLimits.WaterWhiteCapBrightness.Max, Original.WaterWhiteCapBrightness);
            m.WaterSpecPower = SliderR("WaterSpecPower", m.WaterSpecPower, InfoFieldLimits.WaterSpecPower.Min, InfoFieldLimits.WaterSpecPower.Max, Original.WaterSpecPower);
            m.WaterBloomFactor = SliderR("WaterBloomFactor", m.WaterBloomFactor, InfoFieldLimits.WaterBloomFactor.Min, InfoFieldLimits.WaterBloomFactor.Max, Original.WaterBloomFactor);
            m.WaterWhiteCapCutoff = SliderR("WaterWhiteCapCutoff", m.WaterWhiteCapCutoff, InfoFieldLimits.WaterWhiteCapCutoff.Min, InfoFieldLimits.WaterWhiteCapCutoff.Max, Original.WaterWhiteCapCutoff);
            m.WaterWaveTiming = SliderR("WaterWaveTiming", m.WaterWaveTiming, InfoFieldLimits.WaterWaveTiming.Min, InfoFieldLimits.WaterWaveTiming.Max, Original.WaterWaveTiming);
            m.WaterWaveModulus = SliderR("WaterWaveModulus", m.WaterWaveModulus, InfoFieldLimits.WaterWaveModulus.Min, InfoFieldLimits.WaterWaveModulus.Max, Original.WaterWaveModulus);
            m.WaterFoamHeightOffset = SliderR("WaterFoamHeightOffset", m.WaterFoamHeightOffset, InfoFieldLimits.WaterFoamHeightOffset.Min, InfoFieldLimits.WaterFoamHeightOffset.Max, Original.WaterFoamHeightOffset);
            m.WaterWaveStartDepth = SliderR("WaterWaveStartDepth", m.WaterWaveStartDepth, InfoFieldLimits.WaterWaveStartDepth.Min, InfoFieldLimits.WaterWaveStartDepth.Max, Original.WaterWaveStartDepth);
        }

        if (SC2EnvFieldGui.Foldout("water", ref _fWMisc, "WaterDepthOrigin / WaterDepthBoundsSize (@196..208)"))
        {
            var off = new Vector2(m.WaterDepthOriginX, m.WaterDepthOriginY);
            off = SC2EnvFieldGui.Vector2Field("WaterDepthOrigin", off, "water.depthorigin");
            m.WaterDepthOriginX = off.x; m.WaterDepthOriginY = off.y;

            var bs = new Vector2(m.WaterDepthBoundsSizeX, m.WaterDepthBoundsSizeY);
            bs = SC2EnvFieldGui.Vector2Field("WaterDepthBoundsSize", bs, "water.depthbounds");
            m.WaterDepthBoundsSizeX = bs.x; m.WaterDepthBoundsSizeY = bs.y;
        }
    }

    // ---- Fog tab: split into Core/Horizon foldouts (was one undifferentiated
    // foldout with both interleaved) ----
    public void DrawFogTab(InfoModel m)
    {
        if (SC2EnvFieldGui.Foldout("fog", ref _fFogCore, "Core (@260..296)"))
        {
            var cc = new Color(m.CoreFogColorR, m.CoreFogColorG, m.CoreFogColorB, m.CoreFogColorA);
            cc = SC2EnvFieldGui.ColorField("CoreFogColor", cc, "fog.core.color");
            m.CoreFogColorR = cc.r; m.CoreFogColorG = cc.g; m.CoreFogColorB = cc.b; m.CoreFogColorA = cc.a;

            m.CoreFogAmount1 = SliderR("CoreFogAmount1", m.CoreFogAmount1, InfoFieldLimits.CoreFogAmount1.Min, InfoFieldLimits.CoreFogAmount1.Max, Original.CoreFogAmount1);
            m.CoreFogAmount2 = SliderR("CoreFogAmount2", m.CoreFogAmount2, InfoFieldLimits.CoreFogAmount2.Min, InfoFieldLimits.CoreFogAmount2.Max, Original.CoreFogAmount2);
            m.CoreFogAmount3 = SliderR("CoreFogAmount3", m.CoreFogAmount3, InfoFieldLimits.CoreFogAmount3.Min, InfoFieldLimits.CoreFogAmount3.Max, Original.CoreFogAmount3);
            m.CoreFogDist1 = SliderR("CoreFogDist1", m.CoreFogDist1, InfoFieldLimits.CoreFogDist1.Min, InfoFieldLimits.CoreFogDist1.Max, Original.CoreFogDist1);
            m.CoreFogDist2 = SliderR("CoreFogDist2", m.CoreFogDist2, InfoFieldLimits.CoreFogDist2.Min, InfoFieldLimits.CoreFogDist2.Max, Original.CoreFogDist2);
        }

        if (SC2EnvFieldGui.Foldout("fog", ref _fFogHorizon, "Horizon (@296..328)"))
        {
            var hc = new Color(m.HorizonFogColorR, m.HorizonFogColorG, m.HorizonFogColorB, m.HorizonFogColorA);
            hc = SC2EnvFieldGui.ColorField("HorizonFogColor", hc, "fog.horizon.color");
            m.HorizonFogColorR = hc.r; m.HorizonFogColorG = hc.g; m.HorizonFogColorB = hc.b; m.HorizonFogColorA = hc.a;

            m.HorizonFogAmount1 = SliderR("HorizonFogAmount1", m.HorizonFogAmount1, InfoFieldLimits.HorizonFogAmount1.Min, InfoFieldLimits.HorizonFogAmount1.Max, Original.HorizonFogAmount1);
            m.HorizonFogAmount2 = SliderR("HorizonFogAmount2", m.HorizonFogAmount2, InfoFieldLimits.HorizonFogAmount2.Min, InfoFieldLimits.HorizonFogAmount2.Max, Original.HorizonFogAmount2);
            m.HorizonFogAmount3 = SliderR("HorizonFogAmount3", m.HorizonFogAmount3, InfoFieldLimits.HorizonFogAmount3.Min, InfoFieldLimits.HorizonFogAmount3.Max, Original.HorizonFogAmount3);
            m.HorizonFogDist1 = SliderR("HorizonFogDist1", m.HorizonFogDist1, InfoFieldLimits.HorizonFogDist1.Min, InfoFieldLimits.HorizonFogDist1.Max, Original.HorizonFogDist1);
            m.HorizonFogDist2 = SliderR("HorizonFogDist2", m.HorizonFogDist2, InfoFieldLimits.HorizonFogDist2.Min, InfoFieldLimits.HorizonFogDist2.Max, Original.HorizonFogDist2);
        }
    }

    // ---- Point Clouds tab ---- (promoted out of Glow into its own
    // top-level tab, per direction — no inner foldout, same reasoning as
    // Sun/Shadow above.)
    public void DrawPointCloudTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("sparkles", "PointCloud (@332..352)");
        SC2EnvFieldGui.WrapLabel("When a point cloud is baked (Terrain > Lighting Bake > Bake Point Cloud), its own box replaces Origin and Size at export.", SC2UiScale.Pxf(240f));
        var origin = new Vector3(m.PointCloudOriginX, m.PointCloudOriginY, m.PointCloudOriginZ);
        origin = SC2EnvFieldGui.Vector3Field("PointCloudOrigin", origin, "pointcloud.origin");
        m.PointCloudOriginX = origin.x; m.PointCloudOriginY = origin.y; m.PointCloudOriginZ = origin.z;
        var sz = new Vector3(m.PointCloudSizeX, m.PointCloudSizeY, m.PointCloudSizeZ);
        sz = SC2EnvFieldGui.Vector3Field("PointCloudSize", sz, "pointcloud.size");
        m.PointCloudSizeX = sz.x; m.PointCloudSizeY = sz.y; m.PointCloudSizeZ = sz.z;
        m.PointCloudLightAmount = SliderR("PointCloudLightAmount", m.PointCloudLightAmount, InfoFieldLimits.PointCloudLightAmount.Min, InfoFieldLimits.PointCloudLightAmount.Max, Original.PointCloudLightAmount);
    }

    // ---- Glow tab (Bloom promoted to its own tab below — this tab is now
    // single-category, no inner foldout). GlowEnabled gates
    // its sibling fields (grayed out, not hidden, when off) — the dependent
    // parameters stay visible but disabled when the branch is off. ----
    public void DrawGlowTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("sparkles", "Glow (@356..380)");
        m.GlowEnabled = ToggleR("GlowEnabled", m.GlowEnabled, Original.GlowEnabled);
        // See DrawWaterTab's own comment for why this restores the prior
        // value instead of hardcoding true.
        var guiEnabledBeforeGlowFields = GUI.enabled;
        GUI.enabled = guiEnabledBeforeGlowFields && m.GlowEnabled != 0;
        m.GlowBlurKernelScale0 = SliderR("GlowBlurKernelScale0", m.GlowBlurKernelScale0, InfoFieldLimits.GlowBlurKernelScale0.Min, InfoFieldLimits.GlowBlurKernelScale0.Max, Original.GlowBlurKernelScale0);
        m.GlowBlurKernelScale1 = SliderR("GlowBlurKernelScale1", m.GlowBlurKernelScale1, InfoFieldLimits.GlowBlurKernelScale1.Min, InfoFieldLimits.GlowBlurKernelScale1.Max, Original.GlowBlurKernelScale1);
        m.GlowBlurPassCount = SliderIntR("GlowBlurPassCount", m.GlowBlurPassCount, InfoFieldLimits.GlowBlurPassCount.Min, InfoFieldLimits.GlowBlurPassCount.Max, Original.GlowBlurPassCount);
        m.GlowBlurFactor0 = SliderR("GlowBlurFactor0", m.GlowBlurFactor0, InfoFieldLimits.GlowBlurFactor0.Min, InfoFieldLimits.GlowBlurFactor0.Max, Original.GlowBlurFactor0);
        m.GlowBlurFactor1 = SliderR("GlowBlurFactor1", m.GlowBlurFactor1, InfoFieldLimits.GlowBlurFactor1.Min, InfoFieldLimits.GlowBlurFactor1.Max, Original.GlowBlurFactor1);
        GUI.enabled = guiEnabledBeforeGlowFields;
    }

    // ---- Bloom tab ---- (promoted out of Glow into its own top-level tab,
    // per direction — no inner foldout, same reasoning as Sun/Shadow/etc.
    // BloomEnabled gates its sibling fields, same CSV-documented pattern as
    // GlowEnabled above.)
    public void DrawBloomTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("cyclone", "Bloom (@368..416)");
        m.BloomEnabled = ToggleR("BloomEnabled", m.BloomEnabled, Original.BloomEnabled);
        // See DrawWaterTab's own comment for why this restores the prior
        // value instead of hardcoding true.
        var guiEnabledBeforeBloomFields = GUI.enabled;
        GUI.enabled = guiEnabledBeforeBloomFields && m.BloomEnabled != 0;
        m.PostProcessSourceWeight = SliderR("PostProcessSourceWeight", m.PostProcessSourceWeight, InfoFieldLimits.PostProcessSourceWeight.Min, InfoFieldLimits.PostProcessSourceWeight.Max, Original.PostProcessSourceWeight);
        m.BloomThreshold = SliderR("BloomThreshold", m.BloomThreshold, InfoFieldLimits.BloomThreshold.Min, InfoFieldLimits.BloomThreshold.Max, Original.BloomThreshold);
        m.BloomDirectionCount = SliderIntR("BloomDirectionCount", m.BloomDirectionCount, InfoFieldLimits.BloomDirectionCount.Min, InfoFieldLimits.BloomDirectionCount.Max, Original.BloomDirectionCount);
        m.BloomDirectionPhase = SliderR("BloomDirectionPhase", m.BloomDirectionPhase, InfoFieldLimits.BloomDirectionPhase.Min, InfoFieldLimits.BloomDirectionPhase.Max, Original.BloomDirectionPhase);
        m.BloomSpreadDivisor = SliderR("BloomSpreadDivisor", m.BloomSpreadDivisor, InfoFieldLimits.BloomSpreadDivisor.Min, InfoFieldLimits.BloomSpreadDivisor.Max, Original.BloomSpreadDivisor);
        m.BloomSampleScaleX = SliderR("BloomSampleScaleX", m.BloomSampleScaleX, InfoFieldLimits.BloomSampleScaleX.Min, InfoFieldLimits.BloomSampleScaleX.Max, Original.BloomSampleScaleX);
        m.BloomSampleScaleY = SliderR("BloomSampleScaleY", m.BloomSampleScaleY, InfoFieldLimits.BloomSampleScaleY.Min, InfoFieldLimits.BloomSampleScaleY.Max, Original.BloomSampleScaleY);
        m.BloomResultWeight = SliderR("BloomResultWeight", m.BloomResultWeight, InfoFieldLimits.BloomResultWeight.Min, InfoFieldLimits.BloomResultWeight.Max, Original.BloomResultWeight);
        m.BloomPassCount = SliderIntR("BloomPassCount", m.BloomPassCount, InfoFieldLimits.BloomPassCount.Min, InfoFieldLimits.BloomPassCount.Max, Original.BloomPassCount);
        GUI.enabled = guiEnabledBeforeBloomFields;
    }

    // ---- Camera's former foldouts, each now its own top-level tab (per
    // direction — the combined "Camera" tab is gone; no inner foldout in
    // any of these 5, same reasoning as Sun/Shadow/PointCloud above) ----
    public void DrawFogOfWarTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("telescope", "Fog of War (@424..432)");
        m.FogOfWarLowerHeight = SliderR("FogOfWarLowerHeight", m.FogOfWarLowerHeight, InfoFieldLimits.FogOfWarLowerHeight.Min, InfoFieldLimits.FogOfWarLowerHeight.Max, Original.FogOfWarLowerHeight);
        m.FogOfWarHighestHeight = SliderR("FogOfWarHighestHeight", m.FogOfWarHighestHeight, InfoFieldLimits.FogOfWarHighestHeight.Min, InfoFieldLimits.FogOfWarHighestHeight.Max, Original.FogOfWarHighestHeight);
        m.FogOfWarHeightStrength = SliderR("FogOfWarHeightStrength", m.FogOfWarHeightStrength, InfoFieldLimits.FogOfWarHeightStrength.Min, InfoFieldLimits.FogOfWarHeightStrength.Max, Original.FogOfWarHeightStrength);
    }

    public void DrawIntelTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("telescope", "Intel (@436..444)");
        m.IntelLowerHeight = SliderR("IntelLowerHeight", m.IntelLowerHeight, InfoFieldLimits.IntelLowerHeight.Min, InfoFieldLimits.IntelLowerHeight.Max, Original.IntelLowerHeight);
        m.IntelHighestHeight = SliderR("IntelHighestHeight", m.IntelHighestHeight, InfoFieldLimits.IntelHighestHeight.Min, InfoFieldLimits.IntelHighestHeight.Max, Original.IntelHighestHeight);
        m.IntelHeightStrength = SliderR("IntelHeightStrength", m.IntelHeightStrength, InfoFieldLimits.IntelHeightStrength.Min, InfoFieldLimits.IntelHeightStrength.Max, Original.IntelHeightStrength);
    }

    // DepthOfFieldEnabled gates Start/End (grayed out, not hidden, when
    // off) — per the CSV's recommended_editor_control for this flag.
    public void DrawDepthOfFieldTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("clapper", "DepthOfField (@448..456)");
        m.DepthOfFieldEnabled = ToggleR("DepthOfFieldEnabled", m.DepthOfFieldEnabled, Original.DepthOfFieldEnabled);
        // See DrawWaterTab's own comment for why this restores the prior
        // value instead of hardcoding true.
        var guiEnabledBeforeDofFields = GUI.enabled;
        GUI.enabled = guiEnabledBeforeDofFields && m.DepthOfFieldEnabled != 0;
        m.DepthOfFieldStart = SliderR("DepthOfFieldStart", m.DepthOfFieldStart, InfoFieldLimits.DepthOfFieldStart.Min, InfoFieldLimits.DepthOfFieldStart.Max, Original.DepthOfFieldStart);
        m.DepthOfFieldEnd = SliderR("DepthOfFieldEnd", m.DepthOfFieldEnd, InfoFieldLimits.DepthOfFieldEnd.Min, InfoFieldLimits.DepthOfFieldEnd.Max, Original.DepthOfFieldEnd);
        GUI.enabled = guiEnabledBeforeDofFields;
    }

    // Promoted out of the former "Unverified" tab, now that the audit gives
    // them real, Confirmed names.
    public void DrawSharpenTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("knife", "Sharpen (@460..476)");
        m.SharpenStart = SliderR("SharpenStart", m.SharpenStart, InfoFieldLimits.SharpenStart.Min, InfoFieldLimits.SharpenStart.Max, Original.SharpenStart);
        m.SharpenEnd = SliderR("SharpenEnd", m.SharpenEnd, InfoFieldLimits.SharpenEnd.Min, InfoFieldLimits.SharpenEnd.Max, Original.SharpenEnd);
        m.SharpenBaseAmount = SliderR("SharpenBaseAmount", m.SharpenBaseAmount, InfoFieldLimits.SharpenBaseAmount.Min, InfoFieldLimits.SharpenBaseAmount.Max, Original.SharpenBaseAmount);
        m.SharpenNearOffset = SliderR("SharpenNearOffset", m.SharpenNearOffset, InfoFieldLimits.SharpenNearOffset.Min, InfoFieldLimits.SharpenNearOffset.Max, Original.SharpenNearOffset);
        m.SharpenFarOffset = SliderR("SharpenFarOffset", m.SharpenFarOffset, InfoFieldLimits.SharpenFarOffset.Min, InfoFieldLimits.SharpenFarOffset.Max, Original.SharpenFarOffset);
    }

    public void DrawRenderPassesTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("film", "Render Passes (@480..488)");
        m.RenderPassACEnabled = ToggleR("RenderPassACEnabled", m.RenderPassACEnabled, Original.RenderPassACEnabled);
        m.RenderPassB0Enabled = ToggleR("RenderPassB0Enabled", m.RenderPassB0Enabled, Original.RenderPassB0Enabled);
        m.RenderPassB4Enabled = ToggleR("RenderPassB4Enabled", m.RenderPassB4Enabled, Original.RenderPassB4Enabled);
    }

    // ---- Map tab: AirElevation, Wind ----
    // "Map" split into separate top-level tabs (AirElevation, Wind), per
    // direction — each single-category, no inner foldout.
    public void DrawAirElevationTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("map", "AirElevation (@232)");
        m.AirElevation = SliderR("AirElevation", m.AirElevation, InfoFieldLimits.AirElevation.Min, InfoFieldLimits.AirElevation.Max, Original.AirElevation);
    }

    public void DrawWindTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("leaf", "Wind (@496..504)");
        m.WindDirectionX = SliderR("WindDirectionX", m.WindDirectionX, InfoFieldLimits.WindDirectionX.Min, InfoFieldLimits.WindDirectionX.Max, Original.WindDirectionX);
        m.WindDirectionZ = SliderR("WindDirectionZ", m.WindDirectionZ, InfoFieldLimits.WindDirectionZ.Min, InfoFieldLimits.WindDirectionZ.Max, Original.WindDirectionZ);
        m.WindSpeed = SliderR("WindSpeed", m.WindSpeed, InfoFieldLimits.WindSpeed.Min, InfoFieldLimits.WindSpeed.Max, Original.WindSpeed);
    }

    // ---- Textures tab ----
    // Wired through the same texture browser terrain materials use
    // (SC2TerrainShell.DrawTexturePicker + SC2TextureBrowser), not plain
    // text fields — EnvironmentMap/SkyMap resolve to real Cubemap assets,
    // ColorCorrectionCube/FogOfWarCorrectionCube to real Texture3D volume
    // LUTs (confirmed via gamedata.scd/effects/*.hlsl: samplerCUBE for the
    // first two, sampler3D for the other two — "Cube" in their names is the
    // standard color-grading "LUT cube" term, not a skybox cube).
    /// <summary>Per-slot export provenance + "copy into map on export" opt-in
    /// for the 5 texture paths below (SC2EnvironmentTextureExport). Set by the
    /// host that owns a real map document (SC2EnvironmentShell mutates it in
    /// place and turns changes into undoable commands); left null by the
    /// standalone Info.bdf editor, which has no map to export into — the
    /// per-texture checkbox is then simply not drawn.</summary>
    public SC2EnvironmentTextureExport TextureExport;

    public void DrawTexturesTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("picture", "Texture Paths (@240..256)");
        m.EnvironmentMap = DrawInfoTextureField(SC2EnvironmentTextureSlot.EnvironmentMap, "EnvironmentMap", "InfoBdf:EnvironmentMap:env", m.EnvironmentMap, ref _texEnvironmentMap, ref _texEnvironmentMapPath);
        m.SkyMap = DrawInfoTextureField(SC2EnvironmentTextureSlot.SkyMap, "SkyMap", "InfoBdf:SkyMap:sky", m.SkyMap, ref _texSkyMap, ref _texSkyMapPath);
        m.ReservedTexturePath = DrawInfoTextureField(SC2EnvironmentTextureSlot.TexturePath3, "ReservedTexturePath", "InfoBdf:ReservedTexturePath", m.ReservedTexturePath, ref _texReservedTexturePath, ref _texReservedTexturePathPath);
        m.ColorCorrectionCube = DrawInfoTextureField(SC2EnvironmentTextureSlot.ColorCorrectionCube, "ColorCorrectionCube", "InfoBdf:ColorCorrectionCube:cube", m.ColorCorrectionCube, ref _texColorCorrectionCube, ref _texColorCorrectionCubePath);
        m.FogOfWarCorrectionCube = DrawInfoTextureField(SC2EnvironmentTextureSlot.FogOfWarCorrectionCube, "FogOfWarCorrectionCube", "InfoBdf:FogOfWarCorrectionCube:cube", m.FogOfWarCorrectionCube, ref _texFogOfWarCorrectionCube, ref _texFogOfWarCorrectionCubePath);
    }

    /// <summary>Bridges an InfoModel string path field to the shared texture
    /// picker widget, mirroring SC2GameViewEditorUI.DrawMaterialTextureSlot's
    /// compare-and-writeback pattern but for a string-backed model field
    /// instead of a Material property. `cache`/`cachedPath` are the calling
    /// field's own per-picker state — resolved once per distinct path
    /// (SC2TerrainMaterialFactory.LoadTexturePublic), re-resolved only when
    /// the model's path actually changes underneath it (e.g. Import/Reset),
    /// and updated in place when the user picks something new via the
    /// browser (SC2TerrainMaterialFactory.TryGetResourcePath gives back the
    /// resource path to persist — the same reverse-lookup Accept() already
    /// populates for every picked entry).</summary>
    private string DrawInfoTextureField(SC2EnvironmentTextureSlot slot, string label, string pickerKey, string currentPath, ref Texture cache, ref string cachedPath)
    {
        if (currentPath != cachedPath)
        {
            cache = SC2TerrainMaterialFactory.LoadTexturePublicAny(currentPath);
            cachedPath = currentPath;
        }
        var pathDraft = cachedPath ?? "";
        var pathBefore = pathDraft;
        var picked = SC2TerrainShell.DrawTexturePickerWithPath(label, cache, pickerKey, ref pathDraft, out var pickedViaBrowser);
        // A browser pick resolves the path from the picked texture (same as
        // before); a typed-path edit already resolved into `picked` inside
        // DrawTexturePickerWithPath and pathDraft already carries what the
        // user typed — keep it verbatim either way it resolved locally or not.
        // Must go through ResolveGamePath, not TryGetResourcePath directly —
        // the latter hands back its own internal "archive:<entry path>"/
        // "file:<path>" round-trip tag, not a real BDF game path, and that
        // raw tag was getting written straight into InfoModel's texture
        // fields (and from there into the exported info.bdf) instead of the
        // clean "/textures/..." path.
        if (pickedViaBrowser)
            pathDraft = SC2TerrainMaterialFactory.ResolveGamePath(picked);
        cache = picked;
        cachedPath = pathDraft;

        // Export provenance: ResolveGamePath above throws away which archive
        // entry the pick came from, so remember the tag next to the path — a
        // fresh pick starts back at "don't copy"; any other edit of the path
        // (typed, reset, import) invalidates the old tag/opt-in.
        if (TextureExport != null)
        {
            if (pickedViaBrowser)
            {
                var tag = picked != null ? SC2TerrainMaterialFactory.TryGetResourcePath(picked) : null;
                TextureExport.Set(slot, tag != null && tag.StartsWith("archive:", System.StringComparison.Ordinal) ? tag : "", false);
            }
            else if (!string.Equals(pathDraft, pathBefore, System.StringComparison.Ordinal))
                TextureExport.Set(slot, "", false);

            var currentTag = TextureExport.GetTag(slot);
            if (SC2VanillaArchiveClassifier.TagMatchesGamePath(currentTag, cachedPath) &&
                SC2VanillaArchiveClassifier.IsVanillaSlotPath(currentTag))
            {
                var force = TextureExport.GetForce(slot);
                var newForce = GUILayout.Toggle(force, "Copy into map on export");
                if (newForce != force) TextureExport.Set(slot, currentTag, newForce);
            }
        }
        return cachedPath ?? "";
    }

    // ---- Advanced tab (was "Unverified") — shrunk to just the fields the
    // audit still calls semantically unresolved. Still fully editable, same
    // Slider control as every other field: per direction, these must stay
    // changeable, not gated behind any read-only/unlock control — the
    // "Advanced" label is a visual flag only. ----
    public void DrawUnverifiedTab(InfoModel m)
    {
        SC2EnvFieldGui.SectionLabel("⚠  Advanced — effect not confirmed");
        m.WaterAuxParameter0 = SliderR("WaterAuxParameter0 (@224)", m.WaterAuxParameter0, InfoFieldLimits.WaterAuxParameter0.Min, InfoFieldLimits.WaterAuxParameter0.Max, Original.WaterAuxParameter0);
        m.WaterAuxParameter1 = SliderR("WaterAuxParameter1 (@228)", m.WaterAuxParameter1, InfoFieldLimits.WaterAuxParameter1.Min, InfoFieldLimits.WaterAuxParameter1.Max, Original.WaterAuxParameter1);
        m.ReservedZero236 = SliderR("ReservedZero236 (@236, zero on all 56 PC maps)", m.ReservedZero236, InfoFieldLimits.ReservedZero236.Min, InfoFieldLimits.ReservedZero236.Max, Original.ReservedZero236);
    }

    public static void ApplyToLight(InfoModel m)
    {
        var light = UnityEngine.Object.FindFirstObjectByType<Light>();
        if (light == null) return;
        light.color = new Color(m.SunColorR, m.SunColorG, m.SunColorB);
        light.intensity = m.SunIntensity;
        var d = new Vector3(m.SunDirectionX, m.SunDirectionY, m.SunDirectionZ);
        if (d != Vector3.zero) light.transform.rotation = Quaternion.LookRotation(-d);
    }

    private const float ResetButtonWidth = 22f;

    private static bool ResetButton(bool changed)
    {
        var prevEnabled = GUI.enabled;
        GUI.enabled = prevEnabled && changed;
        var clicked = SC2EnvFieldGui.Button("⟲", GUILayout.Width(ResetButtonWidth));
        GUI.enabled = prevEnabled;
        return clicked && changed;
    }

    private static float SliderR(string l, float v, float min, float max, float original)
    {
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        var changed = !Mathf.Approximately(v, original);
        var newV = SC2EnvFieldGui.Slider(l, v, min, max, l);
        GUILayout.EndVertical();
        if (ResetButton(changed)) newV = original;
        GUILayout.EndHorizontal();
        return newV;
    }

    /// <summary>Discrete-stepped slider + reset button for int fields — same
    /// pattern as SliderR, per direction that every numeric field gets a
    /// slider except checkboxes/toggles and color pickers, int fields
    /// included.</summary>
    private static int SliderIntR(string l, int v, int min, int max, int original)
    {
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        var changed = v != original;
        var newV = SC2EnvFieldGui.IntSlider(l, v, min, max, l);
        GUILayout.EndVertical();
        if (ResetButton(changed)) newV = original;
        GUILayout.EndHorizontal();
        return newV;
    }

    private static int ToggleR(string l, int v, int original)
    {
        GUILayout.BeginHorizontal();
        var changed = v != original;
        var newV = SC2EnvFieldGui.Toggle(l, v != 0) ? 1 : 0;
        if (ResetButton(changed)) newV = original;
        GUILayout.EndHorizontal();
        return newV;
    }

    /// <summary>Was its own hand-built row (fixed-width label + two full
    /// FloatField-with-FlexibleSpace children nested in one BeginHorizontal)
    /// — that combination could overflow past the left panel's right edge
    /// for longer labels, since nested FlexibleSpace groups don't reliably
    /// shrink to the parent's actual available width in IMGUI. Delegates to
    /// Vector2Field instead: same compact "label above, X/Y boxes below"
    /// layout already used (and confirmed non-overflowing) for
    /// PointCloudOrigin/WaterDepthOrigin/etc.</summary>
    private static (float x, float y) MoveXY(string label, float x, float y)
    {
        var v = SC2EnvFieldGui.Vector2Field(label, new Vector2(x, y), label);
        return (v.x, v.y);
    }
}
