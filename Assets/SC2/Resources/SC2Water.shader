// Supreme Commander 2 water preview for Unity's Built-in Render Pipeline.
// The fragment pipeline follows effects/water.hlsl in the original order.
// Engine-owned inputs are supplied by SC2WaterRenderController.
//
// 2026-08-10: the game's RefractionMap (screen-space refraction of the
// opaque scene behind water, via a GrabPass) was removed by user request -
// the water body is now always the flat _WaterColor (plus foam/shore/
// reflection/fresnel on top), never a refracted view of the terrain
// beneath.
//
// 2026-08-12: two more user-requested removals. (1) No reflections of the
// terrain/scene at all - only the sun's own specular glint remains; the
// real-time planar reflection camera (SC2WaterRenderController), the
// static environment cubemap, and the Unity reflection-probe fallback are
// all gone. (2) Foam only appears near the shoreline (shoreColor, driven
// by the Mask1 depth texture) - the open-water "whitecap" foam term that
// used to scatter foam across the whole surface based on wave-normal
// steepness is removed.
//
// _WaterScales (all 4 components: refract/reflect/depth/chromatic) is now
// entirely inert - nothing in this shader reads any component anymore -
// left declared as dead data rather than reshaping the property, same
// reasoning the .x/.w removal above already established
// (SC2WaterMaterialFactory.cs still writes all 4 components from the same
// info.bdf fields; touching that isn't needed just to drop 2 more reads).
//
// 2026-08-12, user request: shoreColor's intensity is now a direct,
// explicit function of _Mask1Tex's alpha channel alone (alpha==0 -> no
// water, alpha just above 0 -> strongest foam, alpha==1 -> no foam,
// linear in between) - replacing the original game's own two-term formula
// (Ramp-texture animation + a fixed _WaterWhiteCap.z threshold,
// water.hlsl:275-278). _WaterWhiteCap (all 4 components) is now entirely
// inert for the same "leave declared, don't touch the factory" reasoning
// as _WaterScales above - SC2WaterMaterialFactory.cs still computes and
// writes all 4 from real info.bdf fields.
Shader "SC2/Water"
{
    Properties
    {
        _WaterColor      ("Water Color", Color) = (0.05, 0.2, 0.35, 0.85)
        _MaxWaterDepth   ("Max Depth", Float) = 20
        // 2026-08-12: real per-object visibility toggle. Imported terrain's
        // own real water geometry lives in that object's regular
        // MeshRenderer alongside its other, non-water materials, so the
        // Visibility menu's "Water" checkbox can't just disable/enable the
        // whole GameObject or renderer - it must hide only this one
        // material's own fragments. See SC2WaterPlane.cs's
        // ApplyImportedWaterVisibility for where this gets set.
        _SC2WaterVisible ("SC2 Water Visible", Float) = 1

        _Diffuse1Tex      ("Foam 1", 2D) = "black" {}
        _Diffuse2Tex      ("Foam 2", 2D) = "black" {}
        _Diffuse1Movement ("Foam 1 Move/Tile", Vector) = (0.02, 0, 4, 0)
        _Diffuse2Movement ("Foam 2 Move/Tile", Vector) = (-0.015, 0.01, 4, 0)

        _Normal1Tex      ("Wave Normal 1", 2D) = "bump" {}
        _Normal2Tex      ("Wave Normal 2", 2D) = "bump" {}
        _Normal3Tex      ("Wave Normal 3", 2D) = "bump" {}
        _Normal4Tex      ("Wave Normal 4", 2D) = "bump" {}
        _Normal1Movement ("Normal 1 Move/Tile", Vector) = (0.010, 0.010, 8, 0)
        _Normal2Movement ("Normal 2 Move/Tile", Vector) = (-0.015, 0.008, 8, 0)
        _Normal3Movement ("Normal 3 Move/Tile", Vector) = (0.008, -0.012, 16, 0)
        _Normal4Movement ("Normal 4 Move/Tile", Vector) = (-0.006, -0.009, 16, 0)

        _Mask1Tex        ("Depth Mask", 2D) = "white" {}
        _HasMask1Tex     ("Has real Depth Mask", Float) = 0
        _RampTex         ("Wave Ramp", 2D) = "black" {}

        _WaterDepthArea  ("Depth Area", Vector) = (0, 0, 0.001, 0.001)
        _WaterScales     ("Refract/Reflect/Depth/Chromatic", Vector) = (0.05, 1, 1, 0.02)
        _WaterLighting   ("Fresnel/Spec/Bloom", Vector) = (0.1, 5, 64, 0)
        _WaterWhiteCap   ("Whitecap", Vector) = (0.5, 1, 0.1, 0)
        _WaterWaves      ("Waves", Vector) = (0.5, 4, 1, 1)

        _SunDirection    ("Sun Direction", Vector) = (0.3, 0.8, 0.5, 0)
        _SunColor        ("Sun Color", Color) = (1, 1, 1, 1)
        _SC2FogColor     ("SC2 Core Fog Color", Color) = (0, 0, 0, 1)
        _SC2FogAmounts   ("SC2 Core Fog Amounts", Vector) = (0, 0, 0, 0)
        _SC2FogDistances ("SC2 Core Fog Distances", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 300
        Blend SrcAlpha OneMinusSrcAlpha
        // 2026-08-25 fix, reported directly (screenshot showed a decal's
        // texture visibly painted across the water surface itself, not just
        // the lake floor beneath it): water's Queue (Transparent=3000) is
        // LOWER than decals' (SC2Decal.shader, Transparent+20=3020), so
        // water is drawn first — but with ZWrite Off, it never recorded its
        // own depth, so the later-drawn decal had nothing to depth-test
        // against and painted straight through the water regardless of
        // which one was actually closer to the camera. Turning ZWrite back
        // on here means water now writes real depth as it draws (still
        // BEFORE decals, same queue order as before — nothing about ordering
        // changed, only whether depth gets recorded), so a decal placed on
        // the real lake floor now correctly fails the depth test wherever
        // water covers it and reads as submerged/hidden beneath the surface,
        // exactly like decals on dry land already correctly stayed exactly
        // where placed. Pixels this shader's own clip(_SC2WaterVisible-0.5)
        // discards (map area with no real water) never reach ZWrite either,
        // so this doesn't affect anything outside the water's real extent.
        ZWrite On
        Cull Off

        Pass
        {
            Name "ForwardWater"
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase

            #include "UnityCG.cginc"
            #include "AutoLight.cginc"

            sampler2D _Diffuse1Tex;
            sampler2D _Diffuse2Tex;
            sampler2D _Normal1Tex;
            sampler2D _Normal2Tex;
            sampler2D _Normal3Tex;
            sampler2D _Normal4Tex;
            sampler2D _Mask1Tex;
            sampler2D _RampTex;
            float _SC2WaterVisible;

            float4 _Diffuse1Movement;
            float4 _Diffuse2Movement;
            float4 _Normal1Movement;
            float4 _Normal2Movement;
            float4 _Normal3Movement;
            float4 _Normal4Movement;
            float4 _WaterDepthArea;
            float4 _WaterScales;
            float4 _WaterLighting;
            float4 _WaterWhiteCap;
            float4 _WaterWaves;
            float4 _SunDirection;
            float4 _SunColor;
            float4 _WaterColor;
            float4 _SC2FogColor;
            float4 _SC2FogAmounts;
            float4 _SC2FogDistances;
            float _MaxWaterDepth;
            float _HasMask1Tex;

            struct Attributes
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 posWS : TEXCOORD1;
                half3 tangentWS : TEXCOORD2;
                half3 binormalWS : TEXCOORD3;
                half3 normalWS : TEXCOORD4;
                SHADOW_COORDS(5)
            };

            float3 FallbackTangent(float3 normalWS)
            {
                float3 axis = abs(normalWS.y) > 0.999 ? float3(1, 0, 0) : float3(0, 1, 0);
                return normalize(axis - normalWS * dot(axis, normalWS));
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float4 posWS = mul(unity_ObjectToWorld, v.vertex);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.posWS = posWS.xyz;

                float3 normalWS = normalize(UnityObjectToWorldNormal(v.normal));
                float3 tangentWS = UnityObjectToWorldDir(v.tangent.xyz);
                tangentWS = dot(tangentWS, tangentWS) > 0.000001
                    ? normalize(tangentWS - normalWS * dot(tangentWS, normalWS))
                    : FallbackTangent(normalWS);
                float tangentSign = abs(v.tangent.w) > 0.001 ? v.tangent.w * unity_WorldTransformParams.w : 1.0;
                o.normalWS = normalWS;
                o.tangentWS = tangentWS;
                o.binormalWS = normalize(cross(normalWS, tangentWS) * tangentSign);

                TRANSFER_SHADOW(o);
                return o;
            }

            float ComputeSC2Fog(float3 viewVector)
            {
                float viewDistance = length(viewVector);
                float nearFog = lerp(_SC2FogAmounts.x, _SC2FogAmounts.y,
                    saturate(viewDistance * _SC2FogDistances.y));
                return lerp(nearFog, _SC2FogAmounts.z,
                    saturate((viewDistance - _SC2FogDistances.x) * _SC2FogDistances.z));
            }

            fixed4 frag(Varyings i) : SV_Target
            {
                // Discards the whole fragment when hidden via the Visibility
                // menu's "Water" checkbox - see _SC2WaterVisible's own
                // Properties-block comment for why this can't be a simple
                // renderer.enabled/GameObject.SetActive toggle for imported
                // water specifically.
                clip(_SC2WaterVisible - 0.5);

                float4 diffuse1Color = tex2D(_Diffuse1Tex,
                    i.uv * _Diffuse1Movement.z + _Diffuse1Movement.xy * _Time.y);
                float4 diffuse2Color = tex2D(_Diffuse2Tex,
                    i.uv * _Diffuse2Movement.z + _Diffuse2Movement.xy * _Time.y);
                float3 foamColor = diffuse1Color.rgb + diffuse2Color.rgb;

                float4 normal1Color = tex2D(_Normal1Tex,
                    i.uv * _Normal1Movement.z + _Normal1Movement.xy * _Time.y);
                float4 normal2Color = tex2D(_Normal2Tex,
                    i.uv * _Normal2Movement.z + _Normal2Movement.xy * _Time.y);
                float4 normal3Color = tex2D(_Normal3Tex,
                    i.uv * _Normal3Movement.z + _Normal3Movement.xy * _Time.y);
                float4 normal4Color = tex2D(_Normal4Tex,
                    i.uv * _Normal4Movement.z + _Normal4Movement.xy * _Time.y);

                float2 depthSampleRaw = (i.posWS.xz - _WaterDepthArea.xy) * _WaterDepthArea.zw;
                // 2026-08-12 fix: WaterDepth/_WaterDepthArea maps a real
                // water body's own SMALL authored rect (confirmed against
                // the real decompiled water.hlsl, which also just does a
                // plain saturate() here with no separate "am I inside the
                // real rect" check of its own - the real game's water MESH
                // itself is never drawn any larger than that same real rect,
                // so its shader never had to tell "inside" apart from
                // "clamped to the edge"). This Unity port's decorative water
                // plane (SC2WaterPlane) is deliberately drawn far larger
                // than that (WaterPlaneSize, e.g. 4096 units, vs. a real
                // per-map WaterDepth rect that only covers the map's actual
                // small water body) so the surface visually extends to the
                // horizon - meaning saturate()'s clamping now silently
                // stretches the mask's own EDGE pixel across the entire
                // area outside the real rect, exactly reproducing "foam
                // looks uniform everywhere" even with fully correct,
                // correctly-scaled Mask1/WaterDepth data: most of the
                // visible surface is that one clamped edge sample, not a
                // real position-varying one. inRealDepthArea tracks whether
                // this pixel is genuinely inside the authored rect BEFORE
                // clamping, so shoreColor below can be suppressed outside it
                // instead of trusting a stretched edge value.
                bool inRealDepthArea = all(depthSampleRaw >= 0.0) && all(depthSampleRaw <= 1.0);
                float2 depthSample = saturate(depthSampleRaw);
                // 2026-08-23 fix: restored, confirmed needed by direct in-game
                // testing. The 2026-08-16 removal (see git history) theorized
                // that SC2CustomTextureImporter.TryBuildDdsFlat's DDS-decode
                // flip made this redundant with (and a double-flip against)
                // Mask1Tex's own now-correctly-oriented bytes — that theory
                // didn't hold up: Mask1 is the one sampler here fed a
                // procedurally-computed UV (world position via
                // _WaterDepthArea) rather than the mesh's own authored UV1,
                // so unlike Diffuse1/2/Normal1-4/Ramp (which sample through
                // TerrainMeshBuilder's `1f - v`-baked UV1 and are unaffected
                // either way), it still needs its own explicit V-flip to
                // read the mask right-side-up. If this ever needs revisiting,
                // check both this flip AND the decoder fix together, not one
                // in isolation.
                depthSample.y = 1.0 - depthSample.y;
                float4 depthColor = tex2D(_Mask1Tex, depthSample);
                float depthScalar = saturate((-depthColor.a + _WaterWaves.x) * _WaterWaves.y);
                // Same 2026-08-12 fix as shoreColor below, applied here too:
                // depthScalar also drives the shore-normal blend a few lines
                // down (how much the wave normal bends toward the mask's own
                // shore-normal data) - without this, a pixel outside the
                // real depth area could still read a shore-like depthScalar
                // from the clamped edge sample and bend its normal
                // accordingly, not just paint the wrong foam color. Forcing
                // it to 0 ("deep water, no shore effects") outside the real
                // rect (or with no real mask at all) keeps every downstream
                // use of depthScalar consistent, not just shoreColor's own
                // explicit gate below.
                if (_HasMask1Tex <= 0.5 || !inRealDepthArea) depthScalar = 0.0;
                float4 waveAnim = tex2D(_RampTex, float2(depthScalar,
                    frac(_Time.y * _WaterWaves.z + (depthSample.x + depthSample.y) * _WaterWaves.w)));

                float3 normalSum = normal1Color.rgb + normal2Color.rgb + normal3Color.rgb + normal4Color.rgb;
                float3 shoreNormal = float3(
                    (depthColor.rg * 2.0 - 1.0) * (waveAnim.gg * 4.0 - 2.0), waveAnim.g);
                float3 normalTS = normalize(lerp(normalSum * 2.0 - 4.0, shoreNormal,
                    min(1.0, depthScalar * 2.0)));

                float3 normalWS = normalize(
                    normalTS.x * i.tangentWS + normalTS.y * i.binormalWS + normalTS.z * i.normalWS);
                float3 viewVector = _WorldSpaceCameraPos.xyz - i.posWS;
                float3 viewDir = normalize(viewVector);
                float viewIncidence = max(dot(viewDir, normalWS), 0.0);
                float facingIncidence = 1.0 - viewIncidence;
                float fresnelTerm = max(_WaterLighting.x +
                    (1.0 - _WaterLighting.x) * pow(facingIncidence, _WaterLighting.y), 0.0);

                // 2026-08-12, user request: no whitecap foam on open water
                // anymore - foam only where shoreColor already puts it
                // (driven by the Mask1/depth data below, i.e. genuinely near
                // the shoreline). The open-water term used to add foamColor
                // wherever the wave-normal sum's Z component crested past
                // _WaterWhiteCap.x, scattering foam across the whole surface
                // regardless of depth - removed entirely rather than tuned,
                // since no threshold value made it exclusively shore-only.
                float4 waterColor = _WaterColor;

                // 2026-08-12 fix: shoreColor is entirely driven by _Mask1Tex's
                // alpha channel (depthColor.a) - a real per-map texture most
                // maps (and every from-scratch procedural map's synthetic
                // water plane) simply don't have. Without a real texture
                // bound, Unity silently renders _Mask1Tex's declared
                // "white" {} default UNIFORMLY, collapsing the foam signal to
                // one constant value everywhere instead of a real per-position
                // shore signal - exactly reproducing the "still see foam
                // everywhere" report even after the open-water whitecap term
                // above was already removed. Gated off entirely (rather than
                // guessing from the degenerate default) whenever
                // SC2WaterMaterialFactory couldn't resolve a real Mask1
                // texture for this map, or this pixel falls outside the
                // real authored WaterDepth rect (inRealDepthArea, see above).
                //
                // 2026-08-12, user request: replaced the original game's own
                // two-term formula (Ramp-texture animation + a fixed
                // threshold, water.hlsl:275-278) with a direct, explicit
                // mapping straight off Mask1's alpha channel, since the
                // original formula's behavior depended on exactly which real
                // per-map Ramp/threshold values happened to be authored,
                // which is hard to reason about/tune predictably:
                //   alpha == 0            -> no water here at all (unchanged
                //                             from the gate above - this is
                //                             the mask's own "land/no water"
                //                             sentinel, distinct from a real
                //                             but very shallow depth reading)
                //   alpha just above 0     -> strongest foam (right at the
                //                             shore/waterline)
                //   alpha == 1 (255/255)   -> no foam at all (open/deep water)
                //   everything in between  -> foam intensity falls off with
                //                             alpha (see FoamFalloffPower
                //                             below for the exact curve)
                //
                // 2026-08-12 follow-up: a plain linear (1 - alpha) falloff
                // damped too slowly - foam stayed clearly visible across a
                // wide band of mid-range alpha values instead of hugging
                // just the immediate waterline, per direct user feedback on
                // a real map. Raising (1 - alpha) to a power > 1 keeps the
                // two required endpoints exactly the same (alpha->0 still
                // ->1, alpha==1 still ->0) while pushing the falloff curve
                // down much faster in between - e.g. at alpha=0.5, linear
                // gives 0.5, cubed (3.0) gives 0.125, power 6.0 gives
                // ~0.0156. Bumped further to 16.0 per a third round of
                // direct user feedback asking for an even stronger damping
                // factor - at alpha=0.5 this now gives ~0.0000153, and even
                // alpha=0.2 (0.8^16) only gives ~0.028, so foam is now
                // essentially confined to a thin band right at the
                // waterline (very low alpha only). Not tuned to any
                // specific real per-map data (this whole formula is this
                // tool's own simplified replacement for the original game's
                // own Ramp+threshold approach, not a real info.bdf-driven
                // value), left inline rather than exposed as a material
                // property since nothing in this project's Water tab UI
                // edits arbitrary shader floats (every exposed control maps
                // to a real InfoModel/info.bdf field).
                const float FoamFalloffPower = 16.0;
                float3 shoreColor = 0;
                if (_HasMask1Tex > 0.5 && inRealDepthArea && depthColor.a > 0.0)
                {
                    float foamIntensity = pow(saturate(1.0 - depthColor.a), FoamFalloffPower);
                    shoreColor = foamColor * foamIntensity;
                }

                // 2026-08-12, user request: no reflections of the terrain/
                // scene in the water surface - only the sun's own specular
                // highlight remains (a simple light-source glint, not a
                // reflection of surrounding geometry). Previously this also
                // sampled a real-time planar reflection render target
                // (SC2WaterRenderController's dedicated mirror camera, now
                // removed) with a static environment cubemap / Unity
                // reflection probe as fallbacks - all three deleted together
                // since none of them were the sun highlight itself.
                float3 reflView = -reflect(viewDir, normalWS);
                float sunStrength = pow(max(dot(normalize(_SunDirection.xyz), reflView), 0.000001),
                    _WaterLighting.z);
                sunStrength *= SHADOW_ATTENUATION(i);

                float3 reflectColor = _SunColor.rgb * sunStrength * fresnelTerm;
                float4 finalColor = float4(
                    waterColor.rgb + reflectColor + shoreColor,
                    waterColor.a + sunStrength * _WaterLighting.w);

                float fogAmount = ComputeSC2Fog(viewVector);
                finalColor.rgb = lerp(finalColor.rgb, _SC2FogColor.rgb, fogAmount);
                return finalColor;
            }
            ENDCG
        }
    }

    Fallback "Transparent/Diffuse"
}
