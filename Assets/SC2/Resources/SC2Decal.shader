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
// Simplified vs. the original: single forward Blend pass (SrcAlpha
// OneMinusSrcAlpha) with basic directional-light Lambert shading, not the
// original's MRT diffuse/normal G-buffer split — this project's other
// runtime-generated materials (SC2CostsOverlay, SC2TerrainMaterialFactory)
// are all forward/Built-in RP too, so there's no G-buffer to write into here.

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
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            // Same intent as SC2CostsOverlay's Offset -1,-1: keep decals from
            // z-fighting with the terrain they're projected onto.
            Offset -2, -2

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

                float3 lightDirWS = normalize(_WorldSpaceLightPos0.xyz);
                float ndotl = saturate(dot(normalWS, lightDirWS)) * 0.5 + 0.5; // half-lambert so unlit faces aren't pure black
                fixed3 lit = diffuseColor.rgb * ndotl * _LightColor0.rgb;

                // Costs/TerrainTypes overlay — same world-space XZ sampling
                // as SC2Terrain.shader, so painted passability data shows
                // through a decal instead of being hidden underneath it.
                if (_CostsOverlayEnabled > 0.5)
                {
                    half4 overlay = tex2D(_CostsOverlayTex, saturate(i.worldPos.xz / max(_CostsOverlayGridSize, 1.0)));
                    lit = lerp(lit, overlay.rgb, overlay.a);
                }

                return fixed4(lit, diffuseColor.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
