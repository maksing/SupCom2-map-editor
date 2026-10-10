// SC2Terrain.shader
// RNM-блендинг для SC2 terrain, теперь с реальным покрытием всех 12
// ShaderSetup-флагов (AMBIENT_LIGHTING, DIFFUSE_LIGHTING, SPECULAR_LIGHTING,
// GLOW, ENVIRONMENT_MAPPING, RNM_LIGHTING, BLENDING, FOG, DYNAMIC_SHADOWS,
// ALPHA_TEST, INFINITE_DISTANCE — подтверждены реальным разбором всех 56
// ванильных карт) и реальных формул из effects/terrain.hlsl +
// effects/global_constants.hlsl (оба файла предоставлены пользователем из
// установленной игры и прочитаны целиком — не аппроксимация).
//
// REFLECT/CHROMATIC_DISPERSION намеренно НЕ реализованы: оба файла
// подтверждают, что эти 2 флага нигде не читаются ни terrain.hlsl, ни
// global_constants.hlsl несмотря на то что стоят почти на каждом
// материале (672/724 в корпусе) — значит они либо потребляются другим
// шейдером (unit.hlsl использует EnvironmentSampler отдельно), либо это
// метаданные инструмента авторинга без эффекта на рендер террейна.
// Round-trip (сохранение в ShaderSetup при экспорте) остаётся на стороне
// C# (SC2TerrainMaterialFactory/SC2TerrainExporter), в шейдере им просто
// нечего делать — это соответствует реальному поведению игры.
//
// Совместим с Built-in RP и URP (через Legacy/Compatible режим URP).
Shader "SC2/Terrain"
{
    Properties
    {
        _Diffuse1    ("Diffuse 1",          2D) = "white" {}
        _Diffuse2    ("Diffuse 2",          2D) = "white" {}
        _Normal1     ("Normal 1",           2D) = "bump"  {}
        _Normal2     ("Normal 2",           2D) = "bump"  {}
        _RNMMask0    ("RNM Mask 0 (RGB)",   2D) = "black" {}
        _RNMMask1    ("RNM Mask 1 (RGB+A)", 2D) = "black" {}
        _RNMMask2    ("RNM Mask 2 (RGB)",   2D) = "black" {}
        // A material with no real RNM mask data (TexturePaths[10] was null
        // at import — common for non-RNM_LIGHTING materials like the
        // "Shiva Prime" sky/planet presets) leaves _RNMMask0-2 at their
        // "black" {} default with implicit alpha=0, so sunOcclusion (=
        // RNMMask0.a) is 0 too - which zeroes out the RNM branch's OWN
        // shadow-color blend factor (min(X, sunOcclusion)), producing
        // literal black regardless of ShadowColor's own fallback. Same
        // "shader can't tell a real texture from the property default"
        // problem _HasRamp already solves for Ramp, applied here so the
        // RNM branch is only ever entered when there's real data to show -
        // otherwise falls through to the already-correct Sun-lit branch.
        // Also fixes the "RNM" debug preview toolbar (Imported tab) forcing
        // _EnableRNM=1 on every material regardless of real data: those
        // materials now correctly show their real Sun-lit look instead of
        // black even with _EnableRNM force-set.
        [Toggle] _HasRNM ("Has Real RNM Data", Float) = 0
        _Mask1       ("Mask / Specular 1",  2D) = "black" {}
        _Mask2       ("Mask / Specular 2",  2D) = "black" {}
        // RampMap (real terrain.hlsl's RampMap) — real per-map "metal/foam"
        // materials only (TexturePaths[8]); _HasRamp mirrors SC2Water.shader's
        // _HasMask1Tex pattern (a real texture isn't reliably distinguishable
        // from the "black" {} default inside the shader itself).
        _Ramp        ("Ramp",               2D) = "black" {}
        [Toggle] _HasRamp ("Has Real Ramp", Float) = 0
        // Real cubemap (info.bdf EnvironmentMap) — only meaningfully bound
        // when ENVIRONMENT_MAPPING is set on this material.
        _EnvironmentMap ("Environment Map (Cube)", Cube) = "" {}

        // Editor-only debug-view override for surfaces with no chunk-based
        // TerrainView.shader of their own to fall back on the way the core
        // geometry viewport does (background terrain's live preview is the
        // only real user of this - see this shader's frag() for the actual
        // math of each mode). 0 leaves real rendering untouched, so every
        // existing user of this shader (core materials, imported terrain,
        // the real exported game reference) is unaffected unless a caller
        // explicitly opts in. 1=RNM approximation, 2=Mats (Material1/2
        // color blend), 3=Pairs (this pair's own assigned color), 4=Slope
        // (world-normal-based color ramp) — mirrors TerrainView.shader's
        // own PreviewMode values 4/1/2/3 respectively (RNM has no dedicated
        // TerrainView mode of its own; it uses the same fallback-to-4
        // behavior Textured/HeightGradient do there).
        _BgPreviewMode ("Preview: Debug Mode Override", Int) = 0
        _BgPreviewMatColor1 ("Preview: Material 1 Color (Mats mode)", Color) = (0.5, 0.5, 0.5, 1)
        _BgPreviewMatColor2 ("Preview: Material 2 Color (Mats mode)", Color) = (0.5, 0.5, 0.5, 1)
        _BgPreviewPairColor ("Preview: Pair Color (Pairs mode)", Color) = (0.5, 0.5, 0.5, 1)
        // Independent of _BgPreviewMode above — an additive tint blended
        // over whatever color the mode above (or real rendering) produces,
        // same "Height" toggle behavior as TerrainView.shader's own
        // _HeightGradientEnabled/_HeightGradientMinMax (which this mirrors
        // exactly, distinct from _HeightGridEnabled below — that one draws
        // thin cell-boundary lines, this one is a smooth low-to-high color
        // ramp).
        [Toggle] _BgPreviewHeightGradientEnabled ("Preview: Height Gradient Enabled", Float) = 0
        _BgPreviewHeightGradientMinMax ("Preview: Height Gradient MinMax", Vector) = (0, 200, 0, 0)

        _AmbientColor("Ambient Color (from-scratch fallback)", Color) = (0.15, 0.15, 0.15, 1)
        // Real per-map Sun/Shadow/Fog data (InfoModel — same fields/pattern
        // SC2WaterMaterialFactory.ApplyTo already wires for SC2Water.shader).
        _ShadowColor  ("Shadow/Ambient Color", Color) = (0.15, 0.15, 0.15, 1)
        _SunColor     ("Sun Color", Color) = (1, 1, 1, 1)
        _SunDirection ("Sun Direction", Vector) = (0.3, 0.8, 0.5, 0)
        _SC2FogColor     ("SC2 Core Fog Color", Color) = (0, 0, 0, 1)
        _SC2FogAmounts   ("SC2 Core Fog Amounts", Vector) = (0, 0, 0, 0)
        _SC2FogDistances ("SC2 Core Fog Distances", Vector) = (0, 0, 0, 0)

        [Toggle] _EnableRNM               ("RNM Lighting",        Float) = 1
        [Toggle] _EnableAmbient           ("Ambient Lighting",    Float) = 1
        [Toggle] _EnableDiffuseLighting   ("Diffuse Lighting",    Float) = 1
        [Toggle] _EnableSpecularLighting  ("Specular Lighting",   Float) = 1
        [Toggle] _EnableBlending          ("Blending",            Float) = 1
        [Toggle] _EnableFog               ("Fog",                 Float) = 1
        [Toggle] _EnableGlow              ("Glow",                Float) = 0
        [Toggle] _EnableEnvironmentMapping("Environment Mapping", Float) = 0
        [Toggle] _EnableDynamicShadows    ("Dynamic Shadows",     Float) = 0
        [Toggle] _EnableAlphaTest         ("Alpha Test",          Float) = 0
        [Toggle] _EnableInfiniteDistance  ("Infinite Distance",   Float) = 0
        // Real per-material threshold (terrain.hlsl: clip((a*255)-(ALPHA_TEST+1))) —
        // observed real range 4..128 across the 56-map corpus, not just presence.
        _AlphaTestThreshold ("Alpha Test Threshold", Range(0,255)) = 16

        _TextureScroll("Texture Scroll", Vector) = (0,0,0,0)

        // Driven from C# (SC2TerrainMaterialFactory.Create, based on
        // TerrainMaterial.State — "TerrainAlphaBlendState"/"TerrainAdditiveState"/
        // "TerrainOpaqueState" from the .bdf), not edited directly. Defaults are
        // plain opaque (One/Zero, ZWrite on) so existing ground materials are
        // unaffected.
        [HideInInspector] _SrcBlend ("__src", Float) = 1
        [HideInInspector] _DstBlend ("__dst", Float) = 0
        [HideInInspector] _ZWrite   ("__zw",  Float) = 1

        // Costs/TerrainTypes brush overlay (Workstream 12 follow-up,
        // 2026-08-01) — same persistent shader-texture technique
        // Resources/TerrainEditorShaders/SC2TerrainView.shader's own
        // _CostsOverlayTex already uses for the LIVE procedural terrain's
        // shared chunk material, mirrored here so an IMPORTED terrain.bdf
        // object (which uses this shader, not TerrainView) can show the
        // same overlay - previously the overlay only ever rendered on the
        // live terrain, invisible for a fully-imported/ImportOnlyMode map.
        _CostsOverlayTex     ("Costs/TerrainTypes Overlay", 2D) = "black" {}
        [Toggle] _CostsOverlayEnabled ("Costs Overlay Enabled", Float) = 0
        // World units the overlay texture spans (SC2TerrainData.
        // CostsGridSize) — the overlay is sampled from world-space XZ
        // divided by this, NOT from i.uv2: a real imported terrain.bdf's
        // own Uv2 field turned out to be a per-material/per-layer LOCAL
        // UV (used correctly above for RNM mask sampling, which is
        // genuinely per-material data), not a single map-wide [0..1] UV -
        // sampling the whole-map overlay texture through it landed on
        // essentially arbitrary, wrong locations and could alias onto
        // unrelated materials whose local UV2 range happened to overlap.
        _CostsOverlayGridSize ("Costs Overlay Grid Size", Float) = 1024

        // Reported directly: "show the 1-cell heightmap grid across the
        // map" — thin lines at every real terrain cell boundary. Mirrors
        // TerrainView.shader's own procedural (no texture upload) grid so
        // imported terrain shows the exact same overlay as the live
        // procedural terrain, same reasoning as the Costs overlay above.
        [Toggle] _HeightGridEnabled ("Heightmap Grid Enabled", Float) = 0
        _HeightGridCellSize ("Heightmap Grid Cell Size", Float) = 1

        // Reference image overlay (View menu) — deliberately REPLACES the
        // entire terrain rendering rather than blending on top of it like
        // the overlays above, so it overrides every other display mode by
        // construction (see frag()'s own early-return). Sampled from
        // world-space XZ normalized by _ReferenceOverlaySize, NOT i.uv2 —
        // same reasoning as _CostsOverlayGridSize's comment above: i.uv2 is
        // a genuine map-wide [0..1] square only for the live procedural
        // terrain's own chunk mesh; on an imported terrain.bdf object
        // (which shares this same shader), it's a per-material/per-layer
        // LOCAL UV instead and would sample essentially arbitrary
        // locations.
        _ReferenceOverlayTex ("Reference Image Overlay", 2D) = "black" {}
        [Toggle] _ReferenceOverlayEnabled ("Reference Overlay Enabled", Float) = 0
        _ReferenceOverlaySize ("Reference Overlay World Size (X,Z)", Vector) = (1024, 1024, 0, 0)
        // 1 = the reference fully replaces the render (see above); lower values blend it over the
        // selected view mode, so that mode shows through.
        _ReferenceOverlayOpacity ("Reference Overlay Opacity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="ForwardBase" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma target 3.0 // fwidth() for the heightmap grid overlay below
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"

            sampler2D _Diffuse1; float4 _Diffuse1_ST;
            sampler2D _Diffuse2;
            sampler2D _Normal1;
            sampler2D _Normal2;
            sampler2D _RNMMask0;
            sampler2D _RNMMask1;
            sampler2D _RNMMask2;
            sampler2D _Mask1;
            sampler2D _Mask2;
            sampler2D _Ramp;
            float     _HasRamp;
            float     _HasRNM;
            int       _BgPreviewMode;
            fixed4    _BgPreviewMatColor1;
            fixed4    _BgPreviewMatColor2;
            fixed4    _BgPreviewPairColor;
            float     _BgPreviewHeightGradientEnabled;
            float4    _BgPreviewHeightGradientMinMax;
            samplerCUBE _EnvironmentMap;
            fixed4  _AmbientColor;
            fixed4  _ShadowColor;
            fixed4  _SunColor;
            float4  _SunDirection;
            fixed4  _SC2FogColor;
            float4  _SC2FogAmounts;
            float4  _SC2FogDistances;
            float   _EnableRNM, _EnableAmbient, _EnableDiffuseLighting, _EnableSpecularLighting;
            float   _EnableBlending, _EnableFog, _EnableGlow, _EnableEnvironmentMapping;
            float   _EnableDynamicShadows, _EnableAlphaTest, _EnableInfiniteDistance;
            float   _AlphaTestThreshold;
            float4  _TextureScroll;
            sampler2D _CostsOverlayTex;
            float   _CostsOverlayEnabled;
            float   _CostsOverlayGridSize;
            // The Collision2 floor plane (SC2FloorOverlayPlane): where the ground lies below it the overlay is drawn on the plane instead.
            float _SC2OverlayFloorOn;
            float _SC2OverlayFloorY;
            float   _HeightGridEnabled;
            float   _HeightGridCellSize;
            sampler2D _ReferenceOverlayTex;
            float   _ReferenceOverlayEnabled;
            float4  _ReferenceOverlaySize;
            float   _ReferenceOverlayOpacity;

            // RNM базис: azimuth 0/120/240 deg, elevation 35.26 deg
            static const float3 RNM_BASIS_0 = float3( 0.8165,  0.0000,  0.5774);
            static const float3 RNM_BASIS_1 = float3(-0.4082,  0.7071,  0.5774);
            static const float3 RNM_BASIS_2 = float3(-0.4082, -0.7071,  0.5774);

            // Ramp texel offsets — real terrain.hlsl constants (RampMapHeight=8).
            #define RAMP_MAP_HEIGHT       8.0
            #define DIFFUSE_RAMP_OFFSET   (0.5 / RAMP_MAP_HEIGHT)
            #define SPECULAR_RAMP_OFFSET  (1.5 / RAMP_MAP_HEIGHT)
            #define ENVIRONMENT_RAMP_OFFSET (2.5 / RAMP_MAP_HEIGHT)

            struct Attributes {
                float4 pos  : POSITION;
                float3 norm : NORMAL;
                float2 uv1  : TEXCOORD0;
                float2 uv2  : TEXCOORD1;
            };

            struct Varyings {
                float4 pos  : SV_POSITION;
                float2 uv1  : TEXCOORD0;
                float2 uv2  : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                SHADOW_COORDS(3)
                // Real per-vertex WORLD-space normal (from Attributes.norm,
                // recomputed from real triangle geometry at import time by
                // default — see SC2ImportedTerrainExportData.RecomputeNormals).
                // NOT the same as `n` in frag() (the tangent-space normal
                // sampled from _Normal1/_Normal2) — see this field's use
                // below for why both are needed simultaneously.
                float3 worldNormal : TEXCOORD4;
            };

            // Real global_constants.hlsl: max(0.000001, dot(nlight,normal)) —
            // deliberately NOT saturated like the RNM basis dot products below
            // (Lambert can exceed 1 going into a ramp lookup's U coordinate,
            // which the ramp texture itself is authored to handle).
            float Lambert(float3 nlight, float3 normal)
            {
                return max(0.000001, dot(nlight, normal));
            }

            // DiffuseFormula/SpecularFormula (global_constants.hlsl:150-190) —
            // each gated by its own ShaderSetup flag, each with a real-ramp vs
            // analytic-formula branch depending on whether this material has a
            // real Ramp texture (RAMP_MAP in the original — not a stored
            // per-material flag, so keyed off _HasRamp here instead).
            float3 DiffuseFormula(float3 dColor, float3 nlight, float3 normal)
            {
                if (_EnableDiffuseLighting < 0.5) return (float3)0;
                float lam = Lambert(nlight, normal);
                if (_HasRamp > 0.5) return dColor * tex2D(_Ramp, float2(lam, DIFFUSE_RAMP_OFFSET)).rgb;
                return dColor * lam;
            }

            float3 SpecularFormula(float3 sColor, float3 nlight, float3 reflView)
            {
                if (_EnableSpecularLighting < 0.5) return (float3)0;
                float lam = Lambert(nlight, reflView);
                if (_HasRamp > 0.5) return sColor * tex2D(_Ramp, float2(lam, SPECULAR_RAMP_OFFSET)).rgb;
                return sColor * pow(lam, 20);
            }

            float3 ComputeLight(float3 nlight, float3 normal, float3 reflView, float3 lightColor, float3 diffuseColor, float specLevel)
            {
                return DiffuseFormula(lightColor * diffuseColor, nlight, normal) +
                       SpecularFormula(lightColor * specLevel, nlight, reflView);
            }

            // ComputeEnvironment (global_constants.hlsl:217-226) — gated by
            // RAMP_MAP in the original (no ramp = zero contribution, not a
            // fallback formula), same _HasRamp substitution as above.
            // "-ViewDirection" in the real per-frame global is approximated
            // per-pixel here as the (opposite of) the actual view direction at
            // this fragment — a closer, not looser, approximation for a wide
            // FOV camera than a single per-frame constant would be.
            float3 ComputeEnvironment(float3 normal, float3 reflView, float envLevel, float3 viewDir)
            {
                if (_HasRamp < 0.5) return (float3)0;
                float fresnelTerm = tex2D(_Ramp, float2(dot(viewDir, normal), ENVIRONMENT_RAMP_OFFSET)).rgb;
                return texCUBE(_EnvironmentMap, reflView).rgb * (fresnelTerm * envLevel);
            }

            // ComputeFog/ComputeOutColor (global_constants.hlsl:291-306) — exact
            // port, same formula SC2Water.shader's ComputeSC2Fog already uses
            // faithfully for water.
            float ComputeFog(float3 viewVec)
            {
                float viewDist = length(viewVec);
                float nearFog = lerp(_SC2FogAmounts.x, _SC2FogAmounts.y, saturate(viewDist * _SC2FogDistances.y));
                return lerp(nearFog, _SC2FogAmounts.z, saturate((viewDist - _SC2FogDistances.x) * _SC2FogDistances.z));
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 localPos = v.pos.xyz;
                if (_EnableInfiniteDistance > 0.5)
                {
                    // Real terrain.hlsl: Position = (WorldPos - MapCenter) +
                    // ViewPosition, i.e. the object's own authored shape stays
                    // put but re-anchors around the camera instead of around
                    // the map/world origin (classic distant-background/skybox-
                    // follow trick — confirmed real usage: Planet01/Clouds01
                    // materials on SC2_CA_C06/SC2_MP_306, the only 4 real
                    // materials in the 56-map corpus using this flag). Unity
                    // equivalent: rotate/scale the vertex by the object's own
                    // transform (ignoring its world translation) then place it
                    // relative to the camera instead of relative to the
                    // object's actual placed position.
                    float3 rotatedScaled = mul((float3x3)unity_ObjectToWorld, localPos);
                    float3 worldPos = rotatedScaled + _WorldSpaceCameraPos;
                    o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1));
                    o.worldPos = worldPos;
                }
                else
                {
                    o.pos = UnityObjectToClipPos(v.pos);
                    o.worldPos = mul(unity_ObjectToWorld, v.pos).xyz;
                }
                o.uv1 = v.uv1 + _TextureScroll.xy * _Time.y;
                o.uv2 = v.uv2;
                o.worldNormal = UnityObjectToWorldNormal(v.norm);
                TRANSFER_SHADOW(o);
                return o;
            }

            // Heightmap grid overlay helper — one nested LOD level (see
            // TerrainView.shader's ApplyGridLevel for the full reasoning:
            // adaptive fade-out via fwidth() once a level's cells go
            // sub-pixel on screen, so zooming out hands off from fine to
            // coarse grids instead of aliasing into noise). Kept in exact
            // sync with that shader's own copy — imported terrain must show
            // the identical grid the live procedural terrain does.
            half3 ApplyGridLevel(half3 baseColor, float2 worldXZ, float cellSize, half3 lineColor)
            {
                float2 coord = worldXZ / max(cellSize, 0.0001);
                float2 distToEdge = min(frac(coord), 1.0 - frac(coord));
                float d = min(distToEdge.x, distToEdge.y);
                float aa = max(fwidth(d), 0.0001) * 1.5;
                float gridLine = 1.0 - smoothstep(0.0, aa, d);

                float pixelsPerCell = 1.0 / max(length(fwidth(coord)), 0.0001);
                float levelFade = smoothstep(3.0, 6.0, pixelsPerCell);

                return lerp(baseColor, lineColor, gridLine * levelFade * 0.6);
            }

            // Debug-view helpers for _BgPreviewMode (Mats/Pairs/Slope) and
            // the height-gradient overlay below — each mirrors
            // TerrainView.shader's own equivalent exactly (same fixed light
            // direction/response curve, same slope-color ramp, same
            // height-color ramp), since those are what the core geometry
            // viewport's own Textured/RNM/Mats/Pairs/Slope/Height toggle
            // actually shows; background's preview must match it rather
            // than reference this material's own real per-map Sun/Ambient
            // data, same reasoning as the RNM-approximation branch below.
            half3 BgPreviewLit(half3 albedo, float3 nrm)
            {
                float3 L = normalize(float3(.5, 1, .3));
                float ndl = abs(dot(normalize(nrm), L));
                return saturate(albedo * (ndl * .18 + .82));
            }

            half3 BgPreviewSlopeColor(float3 nrm)
            {
                float3 n = normalize(nrm);
                float s = saturate(1.0 - n.y) / .30;
                return s < .5
                    ? lerp(half3(.05, .85, .1), half3(.95, .85, 0), s * 2)
                    : lerp(half3(.95, .85, 0), half3(.9, .1, .05), (s - .5) * 2);
            }

            half3 BgPreviewHeightGradientColor(float worldY)
            {
                float mn = _BgPreviewHeightGradientMinMax.x;
                float mx = max(_BgPreviewHeightGradientMinMax.y, mn + 0.001);
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

            fixed4 fragScene(Varyings i)
            {
                // Blend factor из alpha _RNMMask1
                half blend = (_EnableBlending > 0.5) ? tex2D(_RNMMask1, i.uv2).a : 0.0h;

                // Альбедо
                half4 diffuse1 = tex2D(_Diffuse1, i.uv1);
                half4 diffuse2 = tex2D(_Diffuse2, i.uv1);
                half3 albedo = lerp(diffuse1.rgb, diffuse2.rgb, blend);
                // Реальная альфа источника (не захардкоженная 1) — материалы
                // с TerrainAlphaBlendState (например, плоскости облаков)
                // иначе рендерились сплошными непрозрачными блоками поверх
                // террейна, полностью игнорируя альфа-канал текстуры.
                half outAlpha = lerp(diffuse1.a, diffuse2.a, blend);

                // Нормаль tangent-space
                half3 n = normalize(lerp(
                    UnpackNormal(tex2D(_Normal1, i.uv1)),
                    UnpackNormal(tex2D(_Normal2, i.uv1)),
                    blend));

                // RNM освещение
                half3 m0 = tex2D(_RNMMask0, i.uv2).rgb;
                half4 rnm1Full = tex2D(_RNMMask1, i.uv2);
                half3 m1 = rnm1Full.rgb;
                half3 m2 = tex2D(_RNMMask2, i.uv2).rgb;
                half sunOcclusion = tex2D(_RNMMask0, i.uv2).a;

                // Mask1/Mask2 — r=env level, g=glow level, b=specular level
                // (terrain.hlsl:309-326) — sampled and blended for real now;
                // previously loaded as textures but never read in frag().
                half4 mask1 = tex2D(_Mask1, i.uv1);
                half4 mask2 = tex2D(_Mask2, i.uv1);
                half4 maskColor = lerp(mask1, mask2, blend);

                float3 viewVec = _WorldSpaceCameraPos - i.worldPos;
                float3 viewDir = normalize(viewVec);
                // Real WORLD-space normal (from the mesh's own per-vertex
                // Normal, not the tangent-space normal-map sample `n` above).
                // CONFIRMED BUG, fixed here (2026-08-16, reported as visible
                // "noise" specifically on the non-RNM/Textured-mode ground):
                // `n` is tangent-space (the vertex shader never builds or
                // applies a TBN — matches the real terrain.hlsl's own RNM
                // branch, which is fine there ONLY because RNM_BASIS_0/1/2
                // are ALSO fixed tangent-space constants, so both operands
                // share a consistent frame by construction). `_SunDirection`
                // and `viewDir` are genuine WORLD-space vectors (per-map Sun
                // data, real camera position) with no such guarantee — dotting
                // a tangent-space normal against them (as the code used to do
                // for reflView and for every non-RNM Lambert/specular/
                // environment-mapping calculation) mixes two unrelated
                // coordinate frames, producing a per-pixel-varying, high-
                // frequency artifact driven by the normal map's own fine
                // detail — visible as noise. Using the real world normal for
                // every one of THOSE calculations (leaving the RNM branch's
                // own `dot(n, RNM_BASIS_i)` below untouched, since that one
                // is correctly self-consistent as-is) fixes it without
                // touching the already-validated RNM lighting path.
                half3 worldNormalN = normalize(i.worldNormal);
                float3 reflView = -reflect(viewDir, worldNormalN);

                float shadowScalar = (_EnableDynamicShadows > 0.5) ? SHADOW_ATTENUATION(i) : 1.0;

                half3 color;
                // _HasRNM gate: without real RNM mask data, m0/m1/m2 are all
                // the shader's own "black" {} default (alpha=0 too, so
                // sunOcclusion below reads 0) - entering this branch with no
                // real data behind it collapses to literal black regardless
                // of _EnableRNM's own value, including when _EnableRNM is
                // force-set by the "RNM" debug preview toolbar on a material
                // that was never authored with RNM lighting. Falls through
                // to the already-correct Sun-lit branch instead.
                if (_EnableRNM > 0.5 && _HasRNM > 0.5)
                {
                    half3 rnmColor = (half3)0;
                    rnmColor += m0 * saturate(dot(n, RNM_BASIS_0));
                    rnmColor += m1 * saturate(dot(n, RNM_BASIS_1));
                    rnmColor += m2 * saturate(dot(n, RNM_BASIS_2));

                    // Real formula (terrain.hlsl:390): blend toward
                    // ShadowColor based on sun-occlusion/dynamic-shadow amount —
                    // NOT gated by AMBIENT_LIGHTING in the RNM branch at all
                    // (that flag only gates the non-RNM branch below). This
                    // replaces the old editor-only additive "_EnableAmbient:
                    // lighting += _AmbientColor" behavior.
                    half3 lightBlend = lerp(rnmColor, _ShadowColor.rgb,
                        min((1.0 - shadowScalar) * _ShadowColor.a, sunOcclusion));
                    if (_EnableGlow > 0.5)
                        lightBlend = lerp(lightBlend, half3(1,1,1), maskColor.g);

                    color = albedo * lightBlend;
                    color += SpecularFormula(_SunColor.rgb * maskColor.b, _SunDirection.xyz, reflView) * shadowScalar * sunOcclusion;
                }
                else if (_BgPreviewMode == 1)
                {
                    // Synthetic RNM-preview approximation — same
                    // RNM_BASIS_0/1/2 constants and blend-driven intensity
                    // as Resources/TerrainEditorShaders/SC2TerrainView.shader's
                    // own PreviewMode==4 fallback, so background's live
                    // preview matches the live procedural terrain's "RNM"
                    // viewport toggle instead of always looking flat next
                    // to it. Purely a "what RNM-ish lighting would look
                    // like" reference for a surface with no real baked mask
                    // yet — never reached when _HasRNM is real (branch above).
                    // Uses a fixed white ambient (matching TerrainView.shader's
                    // own chunk material, which always sets _AmbientColor to
                    // Color.white for this same preview) rather than this
                    // material's real per-map _AmbientColor (set from the
                    // map's actual, possibly very dark, ShadowColor by
                    // SC2TerrainMaterialFactory.ApplyEnvironment) — otherwise
                    // any unpainted area (blend == 0) goes near-black on a
                    // map with a dark ambient instead of showing a legible
                    // editing preview.
                    half3 kPreviewAmbient = half3(1, 1, 1);
                    half r0 = saturate(dot(worldNormalN, RNM_BASIS_0));
                    half r1 = saturate(dot(worldNormalN, RNM_BASIS_1));
                    half r2 = saturate(dot(worldNormalN, RNM_BASIS_2));
                    half3 rnmLight = (r0 + r1 + r2) / 3.0 * kPreviewAmbient;
                    float ndl = abs(dot(worldNormalN, _SunDirection.xyz)) * 0.18 + 0.82;
                    color = albedo * (ndl * kPreviewAmbient + rnmLight * blend * 0.5);
                }
                else if (_BgPreviewMode == 2)
                {
                    // Mats: blend between this pair's two materials' own
                    // assigned colors — mirrors TerrainView.shader's
                    // PreviewMode==1 (its _PairLUT-sampled c1/c2, blended by
                    // the same real painted `blend` factor).
                    color = BgPreviewLit(lerp(_BgPreviewMatColor1.rgb, _BgPreviewMatColor2.rgb, blend), worldNormalN);
                }
                else if (_BgPreviewMode == 3)
                {
                    // Pairs: this pair's own single assigned swatch color —
                    // mirrors TerrainView.shader's PreviewMode==2 (its
                    // _PairColorTex, populated from the exact same
                    // SC2MaterialPair.PairColor field).
                    color = BgPreviewLit(_BgPreviewPairColor.rgb, worldNormalN);
                }
                else if (_BgPreviewMode == 4)
                {
                    // Slope: mirrors TerrainView.shader's PreviewMode==3
                    // exactly (world-normal-based color ramp, ignores
                    // texture/pair data entirely).
                    color = BgPreviewSlopeColor(worldNormalN);
                }
                else
                {
                    color = albedo;
                    if (_EnableAmbient > 0.5) color *= _ShadowColor.rgb;
                    half3 sunContrib = ComputeLight(_SunDirection.xyz, worldNormalN, reflView, _SunColor.rgb, albedo, maskColor.b);
                    color += sunContrib * shadowScalar;
                }

                if (_EnableEnvironmentMapping > 0.5)
                    color += ComputeEnvironment(worldNormalN, reflView, maskColor.r, viewDir);

                // GLOW (terrain.hlsl:328-334): self-illumination mask drives
                // output alpha instead of the blended diffuse alpha.
                if (_EnableGlow > 0.5)
                    outAlpha = maskColor.g;

                if (_EnableAlphaTest > 0.5)
                    clip((outAlpha * 255.0) - (_AlphaTestThreshold + 1.0));

                // Height gradient overlay — independent of _BgPreviewMode
                // above (a tint on top of whatever it produced, or real
                // rendering when the mode is off), same "Height" toggle
                // behavior and blend weight as TerrainView.shader's own
                // ApplyHeightGradient.
                if (_BgPreviewHeightGradientEnabled > 0.5)
                    color = lerp(color, BgPreviewHeightGradientColor(i.worldPos.y), 0.45);

                // Costs/TerrainTypes overlay — sampled from world-space XZ /
                // _CostsOverlayGridSize (see this shader's own Properties
                // comment for why i.uv2 is the wrong space here).
                if (_CostsOverlayEnabled > 0.5 && !(_SC2OverlayFloorOn > 0.5 && i.worldPos.y < _SC2OverlayFloorY))
                {
                    half4 overlay = tex2D(_CostsOverlayTex, saturate(i.worldPos.xz / max(_CostsOverlayGridSize, 1.0)));
                    color = lerp(color, overlay.rgb, overlay.a);
                }

                // Heightmap grid overlay — 4 nested adaptive levels (1x1
                // white / 10x10 red / 100x100 green / 1000x1000 blue), see
                // ApplyGridLevel above and TerrainView.shader's own
                // ApplyHeightGrid for the full reasoning.
                if (_HeightGridEnabled > 0.5)
                {
                    float baseCell = max(_HeightGridCellSize, 0.001);
                    color = ApplyGridLevel(color, i.worldPos.xz, baseCell,          half3(1.00, 1.00, 1.00));
                    color = ApplyGridLevel(color, i.worldPos.xz, baseCell * 10.0,   half3(1.00, 0.20, 0.20));
                    color = ApplyGridLevel(color, i.worldPos.xz, baseCell * 100.0,  half3(0.20, 1.00, 0.20));
                    color = ApplyGridLevel(color, i.worldPos.xz, baseCell * 1000.0, half3(0.30, 0.50, 1.00));
                }

                // 2026-08-16, user request: fog removed from the shader —
                // not wanted for editor map display. _EnableFog/_SC2FogColor/
                // _SC2FogAmounts/_SC2FogDistances stay declared (still set
                // from real per-map InfoModel data by
                // SC2TerrainMaterialFactory.ApplyEnvironment/ApplyShaderSetup,
                // and FOG's ShaderSetup entry still round-trips correctly on
                // export) — only the visual application here is gone, so a
                // future re-enable is just restoring this block, not
                // re-plumbing the data.

                return half4(color, outAlpha);
            }

            fixed4 frag(Varyings i) : SV_Target
            {
                // Reference image overlay (View menu). At full opacity it returns immediately,
                // before any other sampling/lighting, so it overrides every other display mode;
                // below that it is blended over the selected mode's own result. See this
                // property's own Properties-block comment for why world-space XZ is used here
                // instead of i.uv2.
                if (_ReferenceOverlayEnabled > 0.5)
                {
                    float2 refUv = saturate(i.worldPos.xz / max(_ReferenceOverlaySize.xy, 1.0));
                    fixed4 refCol = tex2D(_ReferenceOverlayTex, refUv);
                    if (_ReferenceOverlayOpacity >= 0.999)
                        return refCol;
                    fixed4 scene = fragScene(i);
                    return fixed4(lerp(scene.rgb, refCol.rgb, _ReferenceOverlayOpacity), scene.a);
                }
                return fragScene(i);
            }
            ENDCG
        }
    }

    Fallback "Diffuse"
}
