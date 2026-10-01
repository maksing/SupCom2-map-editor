// SC2Decal.shader
// Unity (Built-in RP, CGPROGRAM) port of the original engine's STATIC decal
// path from gamedata.scd/effects/decal.hlsl — reads the exact vertex layout
// DecalsBdf.cs verified against real vanilla decals.win.bdf data (see that
// file's header comment): Position/TexCoord/ScaleOffset/TangentXform0-2,
// packed by SC2DecalsBdfImporter.BuildMesh into Unity's built-in vertex +
// UV0/UV1/UV2 channels (there's no native "3-float TexCoord / 4-float
// ScaleOffset / separate Binormal" channel, so the split is spread across
// those three). If you change the channel mapping in BuildMesh, this
// appdata struct must change to match, or decals render as garbage.
//
// Lives under Resources/ (see SC2CostsOverlay.shader's own comment for why:
// only ever bound via `new Material(shader)`/Resources.Load, never a scene
// material reference, so a build that strips "unreferenced" shaders would
// otherwise drop this and Resources.Load("SC2Decal") returns null in a
// standalone player).
//
// Faithful to the original where practical:
//  - same clip() against the projected decal-box TexCoord (DecalPS)
//  - same diffuse-atlas UV remap: uv = TexCoord.xy * ScaleOffset.xy + ScaleOffset.zw
//  - same tangent-space normal-map reconstruction via the 3 TangentXform columns
//  - same premultiplied-alpha output/blend (decal.hlsl's outDiffuse =
//    float4(color.rgb*alpha, alpha), an unlit write in the original — this
//    shader still shades the color first, since there is no deferred pass
//    here to do it afterward, see the lighting comment below)
// Simplified vs. the original: single forward Blend pass, not the original's
// MRT diffuse/normal G-buffer split — this project's other runtime-generated
// materials (SC2CostsOverlay, SC2TerrainMaterialFactory) are all forward/
// Built-in RP too, so there's no G-buffer to write into here. Lighting: the
// real game's decal pass writes UNLIT color into that G-buffer and lets one
// deferred pass shared with the terrain light it, so a decal always matches
// the terrain tone under it; this shader instead samples the SAME baked RNM
// lighting the terrain itself already uses (core → background → a baked
// imported-terrain world texture → plain directional light as the last
// resort) — see SC2TerrainRuntime.PublishDecalLightingGlobals/
// PublishImportedRnmGlobals and SC2TerrainView.shader's own RNM sampling,
// which this mirrors via GLOBAL shader properties (this project's first use
// of Shader.SetGlobal*) since this decal material has no reference to either
// terrain runtime.

Shader "SC2/Decal"
{
    Properties
    {
        _DiffuseMap ("Diffuse Atlas", 2D) = "white" {}
        _NormalMap ("Normal Atlas", 2D) = "bump" {}
        _HasNormalMap ("Has Normal Map", Float) = 0
        // Reported real bug: the Costs paint overlay is baked into the
        // TERRAIN's own shader (SC2TerrainView.shader for the live
        // procedural terrain, SC2Terrain.shader for imported terrain.bdf
        // materials — see that shader's own Properties comment) — a decal
        // is a separate mesh drawn on top with Offset -2,-2 specifically to
        // win the depth-bias race against the terrain underneath, which
        // means it also completely occluded the Costs overlay wherever it
        // sat, with no way for the paint result to show through. Same
        // technique as SC2Terrain.shader: sampled from world-space XZ
        // (never the decal's own atlasUV, which is atlas-rect space, not
        // map space) divided by _CostsOverlayGridSize.
        _CostsOverlayTex ("Costs/TerrainTypes Overlay", 2D) = "black" {}
        [Toggle] _CostsOverlayEnabled ("Costs Overlay Enabled", Float) = 0
        _CostsOverlayGridSize ("Costs Overlay Grid Size", Float) = 1024
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "LightMode"="ForwardBase" }

        Pass
        {
            // Premultiplied alpha (matches decal.hlsl's own outDiffuse =
            // float4(color.rgb*alpha, alpha)) — straight alpha (the previous
            // SrcAlpha OneMinusSrcAlpha) blended a soft/partial-alpha decal
            // edge to a visibly different color than the real game.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            // Same intent AND same magnitude as SC2CostsOverlay's Offset -1,-1:
            // keep decals from z-fighting with the terrain they're projected
            // onto. This was -2,-2 (double CostsOverlay's own bias despite this
            // comment's claim they match) — strong enough to also win the depth
            // test against nearby real geometry it has no business beating, e.g.
            // the base of a marker gizmo mesh sitting close to ground level,
            // painting over it and making the marker hard to pick out.
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc" // declares _WorldSpaceLightPos0 / _LightColor0 (ForwardBase-populated)

            sampler2D _DiffuseMap;
            sampler2D _NormalMap;
            float _HasNormalMap;
            sampler2D _CostsOverlayTex;
            float _CostsOverlayEnabled;
            float _CostsOverlayGridSize;

            // Decal lighting consistency (GLOBAL properties, no Properties{}
            // block entry needed — see SC2TerrainRuntime.
            // PublishDecalLightingGlobals/PublishImportedRnmGlobals, the C#
            // side that keeps these current). Core/background mirror
            // SC2TerrainView.shader's own _PairMapTex/_TilingLUT/_RnmArr/
            // _RnmUvOffset/_RnmUvInvScale exactly, just suffixed per surface
            // since core and background are separate SC2TerrainRuntime
            // instances with their own pair maps and RNM arrays.
            sampler2D _PairMapTex_Core; float4 _PairMapTex_Core_TexelSize;
            sampler2D _TilingLUT_Core;
            UNITY_DECLARE_TEX2DARRAY(_RnmArr_Core);
            int _RnmValid_Core, _PairCount_Core;
            float4 _RnmUvOffset_Core, _RnmUvInvScale_Core;

            sampler2D _PairMapTex_Bg; float4 _PairMapTex_Bg_TexelSize;
            sampler2D _TilingLUT_Bg;
            UNITY_DECLARE_TEX2DARRAY(_RnmArr_Bg);
            int _RnmValid_Bg, _PairCount_Bg;
            float4 _RnmUvOffset_Bg, _RnmUvInvScale_Bg;

            // Baked world-space lighting for imported terrain.bdf objects
            // (SC2RnmBakeService.BakeImportedWorldRnm) — already the
            // combined/contrast-stretched RGB the core/background branches
            // above compute from 3 separate basis layers, so this is a
            // single plain 2D texture, no array. Alpha is real per-texel
            // bake coverage (0 = never baked/no imported terrain there).
            sampler2D _ImportedRnmTex;
            int _ImportedRnmValid;
            float4 _ImportedRnmWorldOffset, _ImportedRnmInvScale;

            struct appdata
            {
                float4 vertex    : POSITION;
                float3 normal    : NORMAL;    // TangentXform2 (decal.hlsl: NORMAL)
                float4 tangent   : TANGENT;   // TangentXform0.xyz, w unused (decal.hlsl: TANGENT)
                float3 texCoord0 : TEXCOORD0; // TexCoord — projected decal-box coords (decal.hlsl: TEXCOORD0)
                float4 scaleOff  : TEXCOORD1; // ScaleOffset — atlas rect scale+offset (decal.hlsl: TEXCOORD1)
                float3 binormal  : TEXCOORD2; // TangentXform1 (decal.hlsl: BINORMAL)
            };

            struct v2f
            {
                float4 pos          : SV_POSITION;
                float3 texCoord     : TEXCOORD0; // kept for the per-pixel clip() below
                float2 atlasUV      : TEXCOORD1;
                float3 worldNormal  : TEXCOORD2;
                float3 worldTangent : TEXCOORD3;
                float3 worldBinorm  : TEXCOORD4;
                float3 worldPos     : TEXCOORD5;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.texCoord = v.texCoord0;
                // Same remap as the original DecalVS: Out.ScaleOffset = (TexCoord.xy * ScaleOffset.xy) + ScaleOffset.zw
                // — EXCEPT for the V flip below. ScaleOffset.y/.w were baked
                // by the original (D3D-convention) engine, where V=0 is the
                // texture's TOP row. Unity flips imported texture data on
                // import so V=0 is always the BOTTOM row (matching
                // authoring/OpenGL convention) regardless of target platform
                // — so sampling with the original engine's V unmodified
                // lands on the vertically-mirrored atlas row, and every
                // decal shows the wrong slice of the atlas (confirmed
                // visually: shapes/positions line up with the real game,
                // only the sampled content doesn't). Flipping V here, once,
                // after the remap, corrects for that without needing to
                // touch the baked mesh data itself.
                float2 remappedUV = v.texCoord0.xy * v.scaleOff.xy + v.scaleOff.zw;
                o.atlasUV = float2(remappedUV.x, 1.0 - remappedUV.y);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
                o.worldBinorm = UnityObjectToWorldDir(v.binormal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // Exact mirror of SC2TerrainView.shader's own SafeMapUV/DecodePair/
            // PairHasBakedRnm — see that shader for the reasoning (texel-
            // center clamp so bilinear filtering never blends across a real
            // pair boundary; sentinel 255 = unassigned; the LUT alpha gate so
            // an assigned-but-never-baked pair falls through instead of
            // reading a meaningless grey placeholder).
            float2 SafeUV(float2 uv, float4 texelSize)
            {
                float2 half_ = texelSize.xy * 0.5;
                return clamp(uv, half_, 1.0 - half_);
            }

            int DecodePair(sampler2D pairMap, float4 texelSize, float2 uv)
            {
                return (int)round(tex2D(pairMap, SafeUV(uv, texelSize)).r * 255.0);
            }

            bool PairHasBakedRnm(sampler2D tilingLut, int idx)
            {
                return tex2Dlod(tilingLut, float4((idx + 0.5) / 72.0, 0.5, 0, 0)).a > 0.5;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Same projection-box clip as decal.hlsl's DecalPS: discard
                // any pixel whose interpolated TexCoord fell outside [0,1]
                // on any axis (clip() discards when the argument is < 0).
                clip(floor(i.texCoord) * -sign(i.texCoord));

                fixed4 diffuseColor = tex2D(_DiffuseMap, i.atlasUV);

                float3 normalWS = normalize(i.worldNormal);
                if (_HasNormalMap > 0.5)
                {
                    fixed4 normalSample = tex2D(_NormalMap, i.atlasUV);
                    float3 normalTS = normalSample.rgb * 2.0 - 1.0;
                    float3x3 tangentToWorld = float3x3(
                        normalize(i.worldTangent),
                        normalize(i.worldBinorm),
                        normalize(i.worldNormal));
                    normalWS = normalize(mul(normalTS, tangentToWorld));
                }

                // Fallback chain: core RNM -> background RNM -> baked
                // imported-terrain world RNM -> plain directional light. Each
                // step only resolves when its own source actually has real
                // data at this decal's world position — see this shader's
                // own header comment for why.
                float2 worldXZ = i.worldPos.xz;
                half3 lit = half3(0, 0, 0); // always overwritten below; silences a d3d11 "potentially uninitialized" warning the compiler can't resolve through the resolved-flag pattern
                bool resolved = false;

                if (_RnmValid_Core != 0)
                {
                    float2 uv = (worldXZ - _RnmUvOffset_Core.xy) * _RnmUvInvScale_Core.xy;
                    if (all(uv >= 0) && all(uv <= 1))
                    {
                        int pIdx = DecodePair(_PairMapTex_Core, _PairMapTex_Core_TexelSize, uv);
                        if (pIdx < 255)
                        {
                            pIdx = clamp(pIdx, 0, max(_PairCount_Core - 1, 0));
                            if (PairHasBakedRnm(_TilingLUT_Core, pIdx))
                            {
                                half3 m0 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr_Core, float3(uv, (float)(pIdx * 3 + 0))).rgb;
                                half3 m1 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr_Core, float3(uv, (float)(pIdx * 3 + 1))).rgb;
                                half3 m2 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr_Core, float3(uv, (float)(pIdx * 3 + 2))).rgb;
                                half3 rnm = saturate(((m0 + m1 + m2) * 0.5774 - 0.5) * 2.2 + 0.5);
                                lit = diffuseColor.rgb * rnm;
                                resolved = true;
                            }
                        }
                    }
                }

                if (!resolved && _RnmValid_Bg != 0)
                {
                    float2 uv = (worldXZ - _RnmUvOffset_Bg.xy) * _RnmUvInvScale_Bg.xy;
                    if (all(uv >= 0) && all(uv <= 1))
                    {
                        int pIdx = DecodePair(_PairMapTex_Bg, _PairMapTex_Bg_TexelSize, uv);
                        if (pIdx < 255)
                        {
                            pIdx = clamp(pIdx, 0, max(_PairCount_Bg - 1, 0));
                            if (PairHasBakedRnm(_TilingLUT_Bg, pIdx))
                            {
                                half3 m0 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr_Bg, float3(uv, (float)(pIdx * 3 + 0))).rgb;
                                half3 m1 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr_Bg, float3(uv, (float)(pIdx * 3 + 1))).rgb;
                                half3 m2 = UNITY_SAMPLE_TEX2DARRAY(_RnmArr_Bg, float3(uv, (float)(pIdx * 3 + 2))).rgb;
                                half3 rnm = saturate(((m0 + m1 + m2) * 0.5774 - 0.5) * 2.2 + 0.5);
                                lit = diffuseColor.rgb * rnm;
                                resolved = true;
                            }
                        }
                    }
                }

                if (!resolved && _ImportedRnmValid != 0)
                {
                    float2 uv = (worldXZ - _ImportedRnmWorldOffset.xy) * _ImportedRnmInvScale.xy;
                    if (all(uv >= 0) && all(uv <= 1))
                    {
                        half4 baked = tex2D(_ImportedRnmTex, uv);
                        if (baked.a > 0.5)
                        {
                            lit = diffuseColor.rgb * baked.rgb;
                            resolved = true;
                        }
                    }
                }

                if (!resolved)
                {
                    float3 lightDirWS = normalize(_WorldSpaceLightPos0.xyz);
                    float ndotl = saturate(dot(normalWS, lightDirWS)) * 0.5 + 0.5; // half-lambert so unlit faces aren't pure black
                    lit = diffuseColor.rgb * ndotl * _LightColor0.rgb;
                }

                // Costs/TerrainTypes overlay — same world-space XZ sampling
                // as SC2Terrain.shader, so painted passability data shows
                // through a decal instead of being hidden underneath it.
                if (_CostsOverlayEnabled > 0.5)
                {
                    half4 overlay = tex2D(_CostsOverlayTex, saturate(i.worldPos.xz / max(_CostsOverlayGridSize, 1.0)));
                    lit = lerp(lit, overlay.rgb, overlay.a);
                }

                // Premultiplied alpha — see this shader's own header comment
                // and the Blend directive above.
                lit *= diffuseColor.a;
                return fixed4(lit, diffuseColor.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
