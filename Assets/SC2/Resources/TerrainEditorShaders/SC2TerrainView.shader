// Runtime terrain preview shader for the unified Game View editor.
// uv0 = raw world XZ for tiled textures, uv1 = global terrain UV [0..1].

Shader "Hidden/SC2/TerrainView"
{
    Properties
    {
        _PairMapTex   ("Pair Map",      2D)      = "black" {}
        _BlendTex     ("Blend RT",      2D)      = "black" {}
        _PairColorTex ("Pair Color RT", 2D)      = "grey"  {}
        _PairLUT      ("Pair LUT",      2D)      = "grey"  {}
        _TilingLUT    ("Tiling LUT",    2D)      = "white" {}
        _Diffuse1Arr  ("Diffuse1 Arr",  2DArray) = "" {}
        _Diffuse2Arr  ("Diffuse2 Arr",  2DArray) = "" {}
        _Diffuse1Fallback ("Diffuse 1 Fallback", 2D) = "white" {}
        _Diffuse2Fallback ("Diffuse 2 Fallback", 2D) = "white" {}
        // Baked RNM lighting (SC2RnmBakeService) — 3 basis-direction RGB
        // textures per pair, same _0/_1/_2 layout ExportRnmMaskByName writes
        // to disk, so PreviewMode==4 shows the real baked terrain self-
        // shadow instead of a flat per-vertex-normal approximation once
        // "Bake Lighting" has run. Packed into ONE Texture2DArray (layer =
        // pairIdx*3 + basisIndex) rather than 3 separate arrays — d3d11
        // caps a pixel shader at 16 total sampler registers, and this
        // shader was already close to that with _Diffuse1/2Arr +
        // fallbacks + the overlay samplers; 3 more arrays (+3 fallbacks)
        // pushed it to 17 ("maximum ps_4_0 sampler register index (16)
        // exceeded"). One array + one shared fallback costs only 2 more
        // sampler slots instead of 6. Grey (not black/white) fallback so an
        // un-baked pair still reads as lit rather than going solid black.
        _RnmArr      ("RNM Basis Arr (packed x3)", 2DArray) = "" {}
        _RnmFallback ("RNM Fallback", 2D) = "grey" {}
        // World-space XZ -> RNM mask UV transform (SC2TerrainRuntime.
        // SetRnmUvTransform): rnmUV = (worldXZ - offset) * invScale. Core
        // uses a fixed 1024-unit tiling period (matches SC2TerrainExporter's
        // own core-only Uv2 = worldXZ/1024); the background instead
        // normalizes to its own bounding box (matches the exporter's
        // background branch), since its footprint can span many thousands
        // of world units.
        _RnmUvOffset   ("RNM UV Offset", Vector) = (0,0,0,0)
        _RnmUvInvScale ("RNM UV Inverse Scale", Vector) = (0.0009765625,0.0009765625,0,0)
        _AmbientColor ("Ambient",       Color)   = (1,1,1,1)
        _TexturedBrightness ("Textured Brightness", Float) = 2
        _UseArrayTextures ("Use Array Textures", Int) = 1
        _PreviewMode  ("Mode",          Int)     = 0
        _PairCount    ("Pair Count",    Int)     = 1
        _HeightGradientEnabled ("Height Gradient Enabled", Int) = 0
        _HeightGradientMinMax ("Height Gradient MinMax", Vector) = (0,200,0,0)
        _CostsOverlayTex ("Costs/TerrainTypes Overlay", 2D) = "black" {}
        _CostsOverlayEnabled ("Costs Overlay Enabled", Int) = 0
        _HeightGridEnabled ("Heightmap Grid Enabled", Int) = 0
        _HeightGridCellSize ("Heightmap Grid Cell Size", Float) = 1
        _WireframeEnabled ("Wireframe Enabled", Int) = 0
        _WireframeCellSize ("Wireframe Cell Size (= real mesh CellSize)", Float) = 2
        _WireframeColor ("Wireframe Color", Color) = (0, 0, 0, 0.6)
        // MatCap preset for the debug/no-real-material preview colors
        // (Mats/Pairs/unassigned-Textured) — 0=Standard, 1=Steel, 2=Zebra,
        // 3=Tan, 4=Dark, 5=Normal. See MatCapLit's own doc comment.
        _MatCapPreset ("MatCap Preset", Int) = 0
        // Reference image overlay (View menu) — deliberately REPLACES the
        // entire terrain rendering rather than blending on top of it like
        // the overlays above, so it overrides every other preview mode by
        // construction (see frag()'s own early-return). Sampled from uv1 —
        // safe here (unlike SC2Terrain.shader's own copy of this feature,
        // which must use world-space XZ instead) because THIS shader is
        // only ever used for the live procedural terrain's own chunk mesh,
        // where uv1 (see this file's own top-of-file comment) is always a
        // genuine map-wide [0..1] square — never rendered against an
        // imported terrain.bdf object, which is the only case where uv1/uv2
        // means something else (a per-material/per-layer local UV).
        _ReferenceOverlayTex ("Reference Image Overlay", 2D) = "black" {}
        _ReferenceOverlayEnabled ("Reference Overlay Enabled", Int) = 0
        // 1 = the reference fully replaces the preview; lower values blend it over the selected mode.
        _ReferenceOverlayOpacity ("Reference Overlay Opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            sampler2D _PairMapTex, _BlendTex, _PairColorTex, _PairLUT, _TilingLUT;
            float4 _PairMapTex_TexelSize;
            float4 _BlendTex_TexelSize;
            UNITY_DECLARE_TEX2DARRAY(_Diffuse1Arr);
            UNITY_DECLARE_TEX2DARRAY(_Diffuse2Arr);
            sampler2D _Diffuse1Fallback;
            sampler2D _Diffuse2Fallback;
            UNITY_DECLARE_TEX2DARRAY(_RnmArr);
            sampler2D _RnmFallback;
            float4 _RnmUvOffset;
            float4 _RnmUvInvScale;
            float4 _AmbientColor;
            float _TexturedBrightness;
            float4 _HeightGradientMinMax;
            int _PreviewMode, _PairCount, _HeightGradientEnabled, _UseArrayTextures;
            sampler2D _CostsOverlayTex;
            int _CostsOverlayEnabled;
            int _HeightGridEnabled;
            float _HeightGridCellSize;
            int _WireframeEnabled;
            float _WireframeCellSize;
            float4 _WireframeColor;
            int _MatCapPreset;
            sampler2D _ReferenceOverlayTex;
            int _ReferenceOverlayEnabled;
            float _ReferenceOverlayOpacity;

            // Blends the reference picture over a mode's finished colour (opacity < 1 only).
            float4 ApplyReference(float4 sceneColor, float4 refCol)
            {
                if (_ReferenceOverlayEnabled == 0) return sceneColor;
                return float4(lerp(sceneColor.rgb, refCol.rgb, _ReferenceOverlayOpacity), sceneColor.a);
            }

            struct a2v
            {
                float4 pos : POSITION;
                float3 nrm : NORMAL;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uv2 : TEXCOORD2; // stretch-compensated tiling coordinate (SC2TerrainUv1Unwrap) — diffuse sampling ONLY, uv0 stays raw world XZ for wireframe/costs/height-grid/RNM-basis below
            };

            struct v2f
            {
                float4 clip : SV_POSITION;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                float2 uvTile : TEXCOORD2;
                float3 nrm : TEXCOORD3;
                float worldY : TEXCOORD4;
            };

            v2f vert(a2v v)
            {
                v2f o;
                o.clip = UnityObjectToClipPos(v.pos);
                o.uv0 = v.uv0;
                o.uv1 = v.uv1;
                o.uvTile = v.uv2;
                o.nrm = UnityObjectToWorldNormal(v.nrm);
                o.worldY = mul(unity_ObjectToWorld, v.pos).y;
                return o;
            }

            float2 SafeMapUV(float2 uv1)
            {
                float2 texel = _PairMapTex_TexelSize.xy * 0.5;
                return clamp(uv1, texel, 1.0 - texel);
            }

            float2 SafeBlendUV(float2 uv1)
            {
                float2 texel = _BlendTex_TexelSize.xy * 0.5;
                return clamp(uv1, texel, 1.0 - texel);
            }

            int DecodePair(float2 uv1)
            {
                float r = tex2D(_PairMapTex, SafeMapUV(uv1)).r;
                return (int)round(r * 255.0);
            }

            float2 LutUV(int idx, float row)
            {
                return float2((idx + 0.5) / 72.0, row);
            }

            float GetTiling(int idx)
            {
                return max(tex2Dlod(_TilingLUT, float4(LutUV(idx, .5), 0, 0)).r, 0.001);
            }

            // See RebuildLUTs' own comment on _TilingLUT's alpha channel.
            bool PairHasBakedRnm(int idx)
            {
                return tex2Dlod(_TilingLUT, float4(LutUV(idx, .5), 0, 0)).a > 0.5;
            }

            // MatCap-style shading for the debug/no-real-material preview
            // colors (unassigned checker, Mats, Pairs) — a plain fixed
            // world-space directional light leaves a slope facing away from
            // it reading almost as flat as level ground; from some camera
            // angles the whole surface loses contrast entirely. A matcap
            // keys off the normal in VIEW space instead (transforming world
            // normal through UNITY_MATRIX_V), so shading tracks the camera
            // the way it turns a mesh in Blender's own MatCap solid-shading
            // mode — curvature and slope stay readable from any angle, with
            // no dependency on scene lighting. Procedural — 6 selectable
            // looks (_MatCapPreset, set from the Preview toolbar's own
            // MatCap picker) matching 6 specific balls from Blender's own
            // MatCap gallery (confirmed against real reference screenshots:
            // the default "Standard" ball, plus 5 named picks — Steel, the
            // striped/zebra curvature-check ball, a tan/skin clay, a dark
            // low-key ball, and the RGB normal-direction ball), each
            // expressed purely in math rather than a sampled texture,
            // matching every other helper in this Resources-only shader.

            // Preset 0 — Standard: Blender's own default MatCap ball —
            // a real reflective clay/plastic ball has a fairly crisp
            // light/dark terminator (smoothstep BAND, not a raw dot-product
            // gradient) with darks/highlights/specular pushed apart
            // (0.08..1.2, tight pow-48 spec) for real contrast.
            half3 MatCapStandard(float3 vn)
            {
                float3 keyDir = normalize(float3(-0.45, 0.6, 0.65));
                float ndl = dot(vn, keyDir) * 0.5 + 0.5;
                float band = smoothstep(0.28, 0.64, ndl);
                float fresnel = pow(saturate(1.0 - vn.z), 2.2);
                float spec = pow(saturate(dot(vn, keyDir)), 48.0);
                half3 tone = lerp(half3(0.08, 0.08, 0.10), half3(1.20, 1.20, 1.28), band);
                tone = lerp(tone, tone * 0.4, fresnel);
                tone += spec * 0.9;
                return tone;
            }

            // Preset 1 — Steel: cooler and more reflective than Standard —
            // a narrower band, a bluer base tone, and a tight bright
            // highlight instead of Standard's softer one.
            half3 MatCapSteel(float3 vn)
            {
                float3 keyDir = normalize(float3(-0.3, 0.7, 0.6));
                float ndl = dot(vn, keyDir) * 0.5 + 0.5;
                float band = smoothstep(0.20, 0.55, ndl);
                float fresnel = pow(saturate(1.0 - vn.z), 1.8);
                float spec = pow(saturate(dot(vn, keyDir)), 80.0);
                half3 tone = lerp(half3(0.14, 0.15, 0.18), half3(0.85, 0.88, 0.95), band);
                tone = lerp(tone, tone * 0.6, fresnel);
                tone += spec * 1.3;
                return tone;
            }

            // Preset 2 — Zebra: alternating light/dark bands that follow
            // the surface's own view-space normal (angle + radius around
            // the view axis) — the classic curvature-check matcap: stripes
            // bend and bunch up exactly where the surface bends, making
            // smoothness/slope changes easy to spot independent of albedo.
            half3 MatCapZebra(float3 vn)
            {
                float angle = atan2(vn.y, vn.x);
                float radius = length(vn.xy);
                float stripes = sin(angle * 6.0 + radius * 10.0) * 0.5 + 0.5;
                float band = smoothstep(0.45, 0.55, stripes);
                float fresnel = pow(saturate(1.0 - vn.z), 2.0);
                half3 tone = lerp(half3(0.25, 0.25, 0.27), half3(1.10, 1.10, 1.15), band);
                tone = lerp(tone, tone * 0.5, fresnel);
                return tone;
            }

            // Preset 3 — Tan: same falloff shape as Standard, warm
            // skin/clay tone instead of grey.
            half3 MatCapTan(float3 vn)
            {
                float3 keyDir = normalize(float3(-0.45, 0.6, 0.65));
                float ndl = dot(vn, keyDir) * 0.5 + 0.5;
                float band = smoothstep(0.28, 0.64, ndl);
                float fresnel = pow(saturate(1.0 - vn.z), 2.2);
                float spec = pow(saturate(dot(vn, keyDir)), 48.0);
                half3 tone = lerp(half3(0.14, 0.10, 0.08), half3(1.35, 1.15, 0.95), band);
                tone = lerp(tone, tone * 0.45, fresnel);
                tone += spec * 0.7;
                return tone;
            }

            // Preset 4 — Dark: same falloff shape as Standard, a low-key
            // near-black base instead of mid-grey — reads as a silhouette
            // with just enough gradient/rim to show shape, useful against
            // a bright background.
            half3 MatCapDark(float3 vn)
            {
                float3 keyDir = normalize(float3(-0.45, 0.6, 0.65));
                float ndl = dot(vn, keyDir) * 0.5 + 0.5;
                float band = smoothstep(0.28, 0.64, ndl);
                float fresnel = pow(saturate(1.0 - vn.z), 2.2);
                float spec = pow(saturate(dot(vn, keyDir)), 48.0);
                half3 tone = lerp(half3(0.02, 0.02, 0.025), half3(0.42, 0.42, 0.46), band);
                tone = lerp(tone, tone * 0.5, fresnel);
                tone += spec * 0.5;
                return tone;
            }

            // Preset 5 — Normal: colorizes by the view-space normal
            // itself, same idea as Blender's own RGB "normal" matcap — no
            // single light direction at all, just a direct view of surface
            // orientation, useful as a seam/curvature reference.
            half3 MatCapNormal(float3 vn)
            {
                half3 col = half3(vn.x, vn.y, vn.z) * 0.5 + 0.5;
                float shade = saturate(vn.z * 0.6 + 0.5);
                return col * (0.5 + shade * 0.8);
            }

            half3 MatCapLit(half3 albedo, float3 worldNrm)
            {
                float3 vn = normalize(mul((float3x3)UNITY_MATRIX_V, normalize(worldNrm)));
                half3 tone;
                if (_MatCapPreset == 1) tone = MatCapSteel(vn);
                else if (_MatCapPreset == 2) tone = MatCapZebra(vn);
                else if (_MatCapPreset == 3) tone = MatCapTan(vn);
                else if (_MatCapPreset == 4) tone = MatCapDark(vn);
                else if (_MatCapPreset == 5) tone = MatCapNormal(vn);
                else tone = MatCapStandard(vn);
                return saturate(albedo * tone);
            }

            half3 HeightGradient(float worldY)
            {
                float mn = _HeightGradientMinMax.x;
                float mx = max(_HeightGradientMinMax.y, mn + 0.001);
                float t = saturate((worldY - mn) / (mx - mn));
                half3 low = half3(0.05, 0.30, 0.90);
                half3 midLow = half3(0.10, 0.70, 0.30);
                half3 mid = half3(0.90, 0.85, 0.20);
                half3 high = half3(0.80, 0.40, 0.10);
                half3 peak = half3(0.95, 0.95, 0.95);
                if (t < .25) return lerp(low, midLow, t / .25);
                if (t < .55) return lerp(midLow, mid, (t - .25) / .30);
                if (t < .75) return lerp(mid, high, (t - .55) / .20);
                return lerp(high, peak, (t - .75) / .25);
            }

            half4 ApplyHeightGradient(half4 baseColor, float worldY)
            {
                if (_HeightGradientEnabled == 0)
                    return baseColor;

                half3 grad = HeightGradient(worldY);
                baseColor.rgb = lerp(baseColor.rgb, grad, 0.45);
                return baseColor;
            }

            // Costs/TerrainTypes brush overlay — a persistent shader-texture
            // parameter on the shared chunk material, sampled here every
            // frame as part of normal camera rendering, same approach as
            // _PairColorTex above. This replaced an earlier implementation
            // that issued ad-hoc Graphics.DrawMesh calls from OnGUI: that
            // depended on OnGUI firing at the right point relative to the
            // camera's render pass, which is unreliable (the overlay could
            // vanish on mouse-up, or not render at all in the Editor's Game
            // view vs. a real build) — a plain texture sample here has none
            // of that timing dependency, it just always shows whatever the
            // CPU side last uploaded via SC2TerrainRuntime.SetCostsOverlayTexture.
            half4 ApplyCostsOverlay(half4 baseColor, float2 uv1)
            {
                if (_CostsOverlayEnabled == 0)
                    return baseColor;

                half4 overlay = tex2D(_CostsOverlayTex, SafeMapUV(uv1));
                baseColor.rgb = lerp(baseColor.rgb, overlay.rgb, overlay.a);
                return baseColor;
            }

            // Reported directly: "show the 1-cell heightmap grid across the
            // map", then a follow-up with a real reference screenshot —
            // 4 nested levels (white=1x1, red=10x10, green=100x100,
            // blue=1000x1000), each fading in/out based on camera zoom so
            // a fine grid never aliases into flicker/moiré noise once
            // zoomed out far enough that its cells go sub-pixel — the
            // coarser level takes over instead, same adaptive-LOD grid
            // behavior as Blender/Unreal's own viewport grids. Procedural,
            // not a sampled texture (unlike the Costs overlay above) — a
            // plain distance-to-nearest-cell-edge test in world XZ (uv0,
            // already raw world XZ per this shader's own top comment)
            // needs no upload step and scales to any map size at zero
            // extra CPU cost.
            //
            // levelFade uses fwidth(coord) (screen-space derivative of the
            // cell coordinate) to estimate how many screen pixels one cell
            // spans; below ~3px it fades to 0 (that grid level would just
            // be visual noise), between 3-6px it ramps up to fully visible.
            // Levels are drawn finest-to-coarsest so a boundary that's a
            // multiple of several levels (e.g. every 10th white line is
            // also a red line) ends up colored by the COARSEST applicable
            // level, matching the reference screenshot rather than
            // blending the colors together.
            half3 ApplyGridLevel(half3 baseColor, float2 worldXZ, float cellSize, half3 lineColor)
            {
                float2 coord = worldXZ / max(cellSize, 0.0001);
                float2 distToEdge = min(frac(coord), 1.0 - frac(coord));
                float d = min(distToEdge.x, distToEdge.y);
                float aa = max(fwidth(d), 0.0001) * 1.5;
                // "line" is a reserved token in HLSL/Cg (unexpected-token
                // compile error on d3d11) — named gridLine instead.
                float gridLine = 1.0 - smoothstep(0.0, aa, d);

                float pixelsPerCell = 1.0 / max(length(fwidth(coord)), 0.0001);
                float levelFade = smoothstep(3.0, 6.0, pixelsPerCell);

                return lerp(baseColor, lineColor, gridLine * levelFade * 0.6);
            }

            half4 ApplyHeightGrid(half4 baseColor, float2 worldXZ)
            {
                if (_HeightGridEnabled == 0)
                    return baseColor;

                float baseCell = max(_HeightGridCellSize, 0.001);
                half3 c = baseColor.rgb;
                c = ApplyGridLevel(c, worldXZ, baseCell,          half3(1.00, 1.00, 1.00)); // 1x1 — white
                c = ApplyGridLevel(c, worldXZ, baseCell * 10.0,   half3(1.00, 0.20, 0.20)); // 10x10 — red
                c = ApplyGridLevel(c, worldXZ, baseCell * 100.0,  half3(0.20, 1.00, 0.20)); // 100x100 — green
                c = ApplyGridLevel(c, worldXZ, baseCell * 1000.0, half3(0.30, 0.50, 1.00)); // 1000x1000 — blue
                baseColor.rgb = c;
                return baseColor;
            }

            // A brand new map has no materials/pairs assigned yet, so every
            // triangle hits the flat "unassigned" fallback below — a single
            // dull grey was nearly indistinguishable from the viewport's own
            // background at a glance, giving no sense of scale or slope.
            // Alternating light/dark tiles (the same convention 3D tools use
            // for a "no texture" placeholder) reads shape and distance
            // immediately, same purpose as MatCapLit's own view-space
            // shading below but visible even on perfectly flat ground.
            half3 UnassignedCheckerColor(float2 worldXZ)
            {
                const float cell = 8.0;
                float2 c = floor(worldXZ / cell);
                float parity = frac((c.x + c.y) * 0.5) * 2.0;
                return lerp(half3(0.30, 0.30, 0.32), half3(0.44, 0.44, 0.46), parity);
            }

            // Real mesh wireframe (Blender-style edit-mode overlay), not the
            // arbitrary reference measurement grid above — cellSize is the
            // terrain's real per-quad CellSize (SC2TerrainRuntime.
            // SetWireframeOverlay), so each drawn cell is an actual mesh
            // quad. Procedural rather than a second render pass: each quad
            // splits into 2 triangles along one fixed diagonal
            // (SC2TerrainData.BuildTriangles connects local corners (0,1)-
            // (1,0), i.e. the anti-diagonal f.x+f.y==1), so the whole
            // triangulation is expressible as "distance to the nearest of 2
            // axis edges + 1 diagonal" in cell-local space — no per-vertex
            // barycentric data or geometry shader needed.
            //
            // levelFade mirrors ApplyGridLevel's own adaptive-LOD fade
            // above, but only kills genuinely sub-pixel aliasing noise —
            // the first version's 6-16px band faded out anything past a
            // fairly close working distance, confirmed real from a normal
            // editing camera angle where the whole far half of the terrain
            // (well within resolvable range, just perspective-foreshortened)
            // lost its wireframe entirely, not only the extreme wide-overview
            // shot this was meant to fix. 1.2-3px only fades the last couple
            // of pixels before a cell would actually alias/moiré. Alpha
            // lowered from fully opaque so material colors stay visible
            // THROUGH the lines even at close zoom, not just around them.
            half4 ApplyWireframe(half4 baseColor, float2 worldXZ)
            {
                if (_WireframeEnabled == 0)
                    return baseColor;

                float cell = max(_WireframeCellSize, 0.001);
                float2 coord = worldXZ / cell;
                float2 f = frac(coord);
                float2 distToEdge = min(f, 1.0 - f);
                float diagDist = abs(f.x + f.y - 1.0) * 0.70710678;
                float d = min(min(distToEdge.x, distToEdge.y), diagDist);
                float aa = max(fwidth(d), 0.0001);
                float lineShape = 1.0 - smoothstep(0.0, aa, d);

                float pixelsPerCell = 1.0 / max(length(fwidth(coord)), 0.0001);
                float levelFade = smoothstep(1.2, 3.0, pixelsPerCell);

                float lineMask = lineShape * levelFade * _WireframeColor.a;
                baseColor.rgb = lerp(baseColor.rgb, _WireframeColor.rgb, lineMask);
                return baseColor;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Reference image overlay (View menu). At full opacity it returns immediately, before any
                // other sampling/lighting, so it overrides every other preview mode; below that it is
                // blended over each mode's own result (ApplyReference). See this property's own
                // Properties-block comment for why uv1 is safe to use directly here.
                float4 refCol = float4(0, 0, 0, 1);
                if (_ReferenceOverlayEnabled != 0)
                {
                    refCol = tex2D(_ReferenceOverlayTex, saturate(i.uv1));
                    if (_ReferenceOverlayOpacity >= 0.999)
                        return refCol;
                }

                if (_PreviewMode == 3)
                {
                    float3 n = normalize(i.nrm);
                    float s = saturate(1.0 - n.y) / .30;
                    float3 c = s < .5
                        ? lerp(float3(.05, .85, .1), float3(.95, .85, 0), s * 2)
                        : lerp(float3(.95, .85, 0), float3(.9, .1, .05), (s - .5) * 2);
                    return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(c, 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);
                }

                int pIdx = DecodePair(i.uv1);
                bool unassigned = (pIdx >= 255);
                if (!unassigned)
                    pIdx = clamp(pIdx, 0, max(_PairCount - 1, 0));

                if (_PreviewMode == 2)
                {
                    half3 col = tex2D(_PairColorTex, SafeMapUV(i.uv1)).rgb;
                    return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(MatCapLit(col, i.nrm), 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);
                }

                if (unassigned)
                    return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(MatCapLit(UnassignedCheckerColor(i.uv0), i.nrm), 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);

                half blend = tex2D(_BlendTex, SafeBlendUV(i.uv1)).a;
                float ts = GetTiling(pIdx);
                // i.uvTile is the stretch-compensated coordinate (SC2TerrainUv1Unwrap) —
                // deliberately NOT i.uv0 (raw world XZ), which stays reserved for
                // wireframe/costs-overlay/height-grid/unassigned-checker below.
                float2 uv1_tiled = i.uvTile * ts;

                if (_PreviewMode == 1)
                {
                    half3 c1 = tex2Dlod(_PairLUT, float4(LutUV(pIdx, .25), 0, 0)).rgb;
                    half3 c2 = tex2Dlod(_PairLUT, float4(LutUV(pIdx, .75), 0, 0)).rgb;
                    return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(MatCapLit(lerp(c1, c2, blend), i.nrm), 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);
                }

                half3 d1;
                half3 d2;
                if (_UseArrayTextures != 0)
                {
                    d1 = UNITY_SAMPLE_TEX2DARRAY(_Diffuse1Arr, float3(uv1_tiled, (float)pIdx)).rgb;
                    d2 = UNITY_SAMPLE_TEX2DARRAY(_Diffuse2Arr, float3(uv1_tiled, (float)pIdx)).rgb;
                }
                else
                {
                    d1 = tex2D(_Diffuse1Fallback, uv1_tiled).rgb;
                    d2 = tex2D(_Diffuse2Fallback, uv1_tiled).rgb;
                }
                half3 albedo = lerp(d1, d2, blend);

                if (_PreviewMode == 0)
                {
                    half3 readableDiffuse = saturate(albedo * _TexturedBrightness);
                    return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(readableDiffuse, 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);
                }

                // _HasRNM-style gate, matching the real game's own
                // SC2TerrainMaterialFactory/SC2Terrain.shader: a pair with no
                // real baked mask data falls back to plain sun-lit shading
                // instead of running the RNM formula against a meaningless
                // grey placeholder. Without this, an unbaked pair (e.g. the
                // decorative background before its own "Bake Background
                // Lighting" has been clicked) rendered as a flat, artificially
                // bright blob sitting right next to a properly baked
                // neighbour's real shadow variation — a stark, editor-only
                // seam with no equivalent in the real game (which never mixes
                // RNM-lit and plain-lit rendering paths this visibly, since
                // its own fallback looks like ordinary lit terrain).
                if (!PairHasBakedRnm(pIdx))
                {
                    float3 nrm2 = normalize(i.nrm);
                    float3 L2 = normalize(float3(0.5, 1.0, 0.3));
                    half ndl2 = abs(dot(nrm2, L2)) * 0.3 + 0.7;
                    half3 lit2 = albedo * ndl2;
                    return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(saturate(lit2), 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);
                }

                // Real baked RNM lighting (SC2RnmBakeService) instead of a
                // flat per-vertex-normal approximation. m0/m1/m2 are the 3
                // baked basis-direction textures — terrain self-shadow is
                // already burned into their RGB by FinalizeLighting, so no
                // per-pixel normal-map/light-direction math is needed here
                // (this shader has no detail normal map to modulate it with
                // in the first place, unlike Resources/SC2Terrain.shader's
                // own RNM branch for imported maps). Weighting each basis by
                // its own fixed Z component (all three share 0.5774 by
                // construction, since B0/B1/B2 are equally tilted off a flat
                // reference normal) exactly reproduces FinalizeLighting's own
                // "unperturbed normal" calibration
                // (BasisWeightNormalization = 1/(3*0.5774)), so a baked pair
                // shows the same brightness/shadowing here as in-game.
                float2 rnmUV = (i.uv0 - _RnmUvOffset.xy) * _RnmUvInvScale.xy;
                half3 m0, m1, m2;
                if (_UseArrayTextures != 0)
                {
                    m0 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr, float3(rnmUV, (float)(pIdx * 3 + 0))).rgb;
                    m1 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr, float3(rnmUV, (float)(pIdx * 3 + 1))).rgb;
                    m2 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr, float3(rnmUV, (float)(pIdx * 3 + 2))).rgb;
                }
                else
                {
                    // Rare compatibility-only path (no 2D-array texture
                    // support) — collapses to one shared sample rather than
                    // 3 separate fallback samplers, to stay within d3d11's
                    // 16-sampler cap (see this shader's own _RnmArr comment).
                    m0 = m1 = m2 = tex2D(_RnmFallback, rnmUV).rgb;
                }
                half3 rnmColor = (m0 + m1 + m2) * 0.5774;
                // Confirmed via a debug build that the real baked occlusion
                // signal IS present and correctly shaped/positioned (matches
                // the in-game cast shadow's own outline) — but it's a
                // genuinely subtle hue/brightness shift toward a fairly
                // bright "sky-bounce" ShadowColor by design (real vanilla
                // masks aren't meant to go dark on their own), and it lives
                // in a narrow band close to 1.0. A flat brightness multiplier
                // (tried first) only pushed everything closer to saturating
                // white without making the shadow itself read as darker — a
                // genuine contrast stretch around the midtone is what
                // actually widens the visible gap between lit and shadowed
                // areas, at the cost of some flattening at the very top/
                // bottom of the range (acceptable for a preview mode). The
                // dramatic, sharp-edged cast shadow the real game shows still
                // comes from its OWN real-time terrain shadow map
                // (SC2Terrain.shader's DYNAMIC_SHADOWS/ComputeShadow path,
                // sampling an actual ShadowMap render target) — a separate
                // rendering feature this procedural preview has no
                // equivalent of; this contrast stretch only makes the RNM
                // bake's own (real, but subtle) signal easier to see.
                const half RNM_CONTRAST = 2.2;
                rnmColor = saturate((rnmColor - 0.5) * RNM_CONTRAST + 0.5);
                half3 lit = albedo * rnmColor;
                return ApplyReference(ApplyWireframe(ApplyHeightGrid(ApplyCostsOverlay(ApplyHeightGradient(half4(saturate(lit), 1), i.worldY), i.uv1), i.uv0), i.uv0), refCol);
            }
            ENDCG
        }
    }
}
