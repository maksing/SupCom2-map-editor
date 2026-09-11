/// <summary>
/// Limits (min/max) for info.bdf field sliders — used by both the
/// Editor-only editor (Editor/SC2EnvironmentEditor.cs, for imported
/// maps via SC2MapRawData) and the build-compatible shell
/// (Scripts/Unified/SC2EnvironmentShell.cs, for procedural SC2TerrainData) —
/// one shared set of constants instead of two copies that could drift apart.
/// Previously lived in Editor/InfoFieldLimits.cs under #if UNITY_EDITOR,
/// which prevented build-compatible code from referencing it; the class
/// itself uses no Editor-only API, so moving it here changes nothing about
/// its behavior.
///
/// IMPORTANT: these limits apply ONLY to the sliders (the draggable
/// range) — neither SC2EnvFieldGui.Slider/IntSlider (unified shell) nor
/// SC2EnvironmentEditor.SliderR/SliderIntR (legacy Editor-only editor)
/// clamp a value typed manually into the text field next to the slider:
/// this isn't field validation, the user must still be able to
/// enter a value outside the slider's range.
/// </summary>
public static class InfoFieldLimits
{
    // ---- Sun ----
    // Real range across 56 vanilla maps: 0.5–1.5 (was 0–5 — mostly dead drag space).
    public static readonly (float Min, float Max) SunIntensity = (0f, 2f);

    // ---- Water ----
    // Real max 391.3 across 56 vanilla maps (was 0–200, clipping the tallest map).
    public static readonly (float Min, float Max) MaxWaterDepth = (0f, 400f);
    // Height/Elevation-named fields use the terrain height field's own 0–512
    // world-unit range rather than a data-fitted one, even where a handful of
    // vanilla maps show small negative values (e.g. WaterElevation's real
    // range is -90..310) — by design, per project decision.
    public static readonly (float Min, float Max) WaterElevation              = (0f, 512f);
    public static readonly (float Min, float Max) WaterRenderElevationOverride = (0f, 512f); // audit: was WaterReflectionStartHeight

    public static readonly (float Min, float Max) WaterRefractionScale   = (0f, 1f);
    public static readonly (float Min, float Max) WaterReflectionScale   = (0f, 5f);   // audit: was WaterChromaticScale
    public static readonly (float Min, float Max) WaterChromaticScale    = (0f, 5f);   // audit: was WaterChromaticScale2
    public static readonly (float Min, float Max) WaterFresnelBias       = (0f, 1f);
    public static readonly (float Min, float Max) WaterFresnelPower      = (0f, 512f); // audit: was WaterDepthCastHeight
    // Real max 1000 across 56 vanilla maps (was 0–10 — clipped nearly every map).
    public static readonly (float Min, float Max) WaterDepthScale        = (0f, 1200f);
    public static readonly (float Min, float Max) WaterWhiteCapBrightness = (0f, 64f);  // audit: was WaterSpecPower
    public static readonly (float Min, float Max) WaterSpecPower         = (0f, 100f);  // audit: was WaterSpecPower2
    public static readonly (float Min, float Max) WaterBloomFactor       = (0f, 20f);   // audit: was GlareBrightness
    public static readonly (float Min, float Max) WaterWhiteCapCutoff    = (0f, 10f);   // audit: was GlareThreshold
    public static readonly (float Min, float Max) WaterWaveTiming        = (0f, 20f);   // audit: was WaveTiming
    // Real range 5–11 across 56 vanilla maps (was 0–2 — clipped every single map).
    public static readonly (float Min, float Max) WaterWaveModulus       = (0f, 15f);   // audit: was WaveAmplitude
    public static readonly (float Min, float Max) WaterFoamHeightOffset  = (0f, 512f);  // audit: was FoamHeightOffset
    public static readonly (float Min, float Max) WaterWaveStartDepth    = (0f, 20f);

    // ---- Map ----
    public static readonly (float Min, float Max) AirElevation = (0f, 512f);

    // ---- Glow / Bloom ----
    public static readonly (float Min, float Max) GlowBlurKernelScale0 = (0f, 5f); // audit: was GlowFactor1
    public static readonly (float Min, float Max) GlowBlurKernelScale1 = (0f, 5f); // audit: was GlowFactor2
    // Now correctly applies to the real PointCloudLightAmount field (@420) —
    // the @368 field that used to (wrongly) carry this name is
    // PostProcessSourceWeight now and has no range of its own yet.
    public static readonly (float Min, float Max) PointCloudLightAmount = (0f, 5f);
    // Real range 0–5.32 across 56 vanilla maps (constant 0 on most, one
    // outlier at 5.32) — corpus-sampled, replacing the placeholder carried
    // forward from the old (wrongly-named/typed) GaussianStrength slider.
    // BloomDirectionCount (@392, now int) needs no range here, same as
    // every other int field (drawn as a plain int stepper, not a slider).
    public static readonly (float Min, float Max) BloomThreshold = (0f, 6f);

    // ---- Fog of War / Intel ---- (audit Tier 3b: this block's real
    // identity is swapped from what these constants' old names implied —
    // @424..432 is really Fog of War, @436..444 is really Intel. Ranges
    // below are corpus-sampled (56 vanilla maps) against the NEW field
    // identity, not carried forward from the old Intel/RangeCircles names —
    // those were tuned against a different field per the Tier 3b swap.)
    //
    // OPEN QUESTION (unverified beyond byte-offset): "LowerHeight"'s real
    // corpus values (227-512 / 187-512) are consistently HIGHER than
    // "HighestHeight"'s (10-180 / 0-300, ignoring the -512 sentinel below) —
    // the opposite of what the names would suggest if read as a literal
    // min/max pair of the same measurement. The offset->name assignment
    // itself is confirmed correct (matches mapping.csv's own
    // recommended_name per offset, the established source of truth for
    // this rename pass), so this isn't a transcription error — it's
    // either a non-obvious real semantic (e.g. two independently-thresholded
    // effects, not a literal lower/upper bound of one value) or a genuine
    // naming inversion in the underlying audit. Only a real in-game visual
    // check (raise/lower these values, observe which one visibly bounds
    // Fog of War/Intel circle visibility from below vs. above) can settle
    // it — not attempted here.
    // Real range 227–512 across 56 vanilla maps (only 2 distinct values seen).
    public static readonly (float Min, float Max) FogOfWarLowerHeight    = (0f, 512f);
    // Real values: -512 on 51/56 maps (a disabled/no-limit sentinel, not a
    // real height) and 10–180 on the remaining 5 — range widened to include
    // the sentinel rather than clip it.
    public static readonly (float Min, float Max) FogOfWarHighestHeight  = (-512f, 512f);
    // Confirmed constant 10 across all 56 vanilla maps.
    public static readonly (float Min, float Max) FogOfWarHeightStrength = (0f, 50f);
    // Real range 187–512 across 56 vanilla maps (only 2 distinct values seen).
    public static readonly (float Min, float Max) IntelLowerHeight       = (0f, 512f);
    // Real values: -512/-250 sentinel-looking outliers on 3/56 maps, 0–300
    // on the rest (23 distinct values total) — range widened to include the
    // negative outliers rather than clip them.
    public static readonly (float Min, float Max) IntelHighestHeight     = (-512f, 512f);
    // Real range 0–10 across 56 vanilla maps (was 0–50 — mostly dead drag space).
    public static readonly (float Min, float Max) IntelHeightStrength    = (0f, 10f);

    // ---- Depth of Field ----
    public static readonly (float Min, float Max) DepthOfFieldStart = (0f, 2000f);
    // Real max 20000 on one vanilla map (was 0–5000 — clipped 4x).
    public static readonly (float Min, float Max) DepthOfFieldEnd   = (0f, 25000f);

    // ---- Placeholder ranges below ----
    // Every one of these fields had no slider at all before (drawn as a
    // plain number box) — per direction, every numeric field needs a
    // slider except checkboxes/toggles and color pickers, so each gets a
    // reasonable-scale range here now rather than staying box-only. Fields
    // still marked "placeholder" below have no InfoModel property wired to
    // an offset yet (Core/HorizonFog — no confirmed field to sample) or
    // aren't part of the 30-field corpus-sampling pass; wrong here only
    // makes the drag handle feel mis-scaled, never a correctness risk,
    // since manually-typed values are never clamped (see this file's own
    // header comment).

    public static readonly (float Min, float Max) ShadowCastDistance = (0f, 100f);

    public static readonly (float Min, float Max) CoreFogAmount1 = (0f, 1f);
    public static readonly (float Min, float Max) CoreFogAmount2 = (0f, 1f);
    public static readonly (float Min, float Max) CoreFogAmount3 = (0f, 1f);
    public static readonly (float Min, float Max) HorizonFogAmount1 = (0f, 1f);
    public static readonly (float Min, float Max) HorizonFogAmount2 = (0f, 1f);
    public static readonly (float Min, float Max) HorizonFogAmount3 = (0f, 1f);
    public static readonly (float Min, float Max) CoreFogDist1 = (0f, 5000f);
    public static readonly (float Min, float Max) CoreFogDist2 = (0f, 5000f);
    public static readonly (float Min, float Max) HorizonFogDist1 = (0f, 5000f);
    public static readonly (float Min, float Max) HorizonFogDist2 = (0f, 5000f);

    // ---- Corpus-sampled below (56 vanilla maps) ----
    // Confirmed constant 1.0 across all 56 vanilla maps.
    public static readonly (float Min, float Max) GlowBlurFactor0 = (0f, 5f);
    // Confirmed constant 1.0 across all 56 vanilla maps.
    public static readonly (float Min, float Max) GlowBlurFactor1 = (0f, 5f);

    // Confirmed constant 1.0 across all 56 vanilla maps.
    public static readonly (float Min, float Max) PostProcessSourceWeight = (0f, 5f);
    // Real range 1.2–2.95 across 56 vanilla maps — values cluster near pi,
    // confirming this is radians, not degrees (the old 0–360 placeholder
    // assumed degrees and would have compressed every real value into a
    // sliver of the drag range).
    public static readonly (float Min, float Max) BloomDirectionPhase = (0f, 6.2832f);
    // Real range 0.2–11.1 across 56 vanilla maps.
    public static readonly (float Min, float Max) BloomSpreadDivisor = (0f, 15f);
    // Real range 0.2–1.5 across 56 vanilla maps.
    public static readonly (float Min, float Max) BloomSampleScaleX = (0f, 3f);
    // Real range 0.5–1.34 across 56 vanilla maps.
    public static readonly (float Min, float Max) BloomSampleScaleY = (0f, 3f);
    // Real range 1.0–2.69 across 56 vanilla maps.
    public static readonly (float Min, float Max) BloomResultWeight = (0f, 5f);
    public static readonly (int Min, int Max) BloomPassCount = (1, 8);
    public static readonly (int Min, int Max) BloomDirectionCount = (1, 16);
    public static readonly (int Min, int Max) GlowBlurPassCount = (1, 8);

    // Confirmed constant 0 across all 56 vanilla maps.
    public static readonly (float Min, float Max) SharpenStart = (0f, 2000f);
    // Confirmed constant 250 across all 56 vanilla maps.
    public static readonly (float Min, float Max) SharpenEnd = (0f, 2000f);
    // Confirmed constant 1.0 across all 56 vanilla maps.
    public static readonly (float Min, float Max) SharpenBaseAmount = (0f, 5f);
    // Confirmed constant 0.25 across all 56 vanilla maps (was 0–500 —
    // almost entirely dead drag space for a value that never moves).
    public static readonly (float Min, float Max) SharpenNearOffset = (0f, 2f);
    // Confirmed constant 0 across all 56 vanilla maps (was 0–500 — almost
    // entirely dead drag space).
    public static readonly (float Min, float Max) SharpenFarOffset = (0f, 2f);

    // Confirmed constant 1.0 (full +X) across all 56 vanilla maps.
    public static readonly (float Min, float Max) WindDirectionX = (-1f, 1f);
    // Confirmed constant 0.0 across all 56 vanilla maps.
    public static readonly (float Min, float Max) WindDirectionZ = (-1f, 1f);
    // Confirmed constant 0.3 across all 56 vanilla maps (was 0–50 —
    // almost entirely dead drag space).
    public static readonly (float Min, float Max) WindSpeed = (0f, 2f);

    // Don't constrain range until the real consumer is found — kept deliberately
    // wide rather than a tight guess, matching real vanilla-corpus values
    // seen (0, 15/2, 30/20, and negative pairs).
    public static readonly (float Min, float Max) WaterAuxParameter0 = (-100f, 100f);
    public static readonly (float Min, float Max) WaterAuxParameter1 = (-100f, 100f);
    // Confirmed 0 on all 56 vanilla PC maps — range is nominal only.
    public static readonly (float Min, float Max) ReservedZero236 = (-10f, 10f);
}
