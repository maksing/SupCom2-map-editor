Shader "SC2/Unit"
{
    Properties
    {
        _Diffuse ("Diffuse", 2D) = "white" {}
        _Normal ("Normal", 2D) = "bump" {}
        _Mask ("Mask (R Reflection, G Emission, B Spec/Gloss)", 2D) = "black" {}
        _Team ("Team", 2D) = "black" {}
        _MainColor ("Main Color", Color) = (1, 0, 0, 1)
        _AdditionalColor ("Additional Color", Color) = (0.7, 0.7, 0.7, 1)
        [Toggle] _TeamEnabled ("Team Texture Present", Float) = 0
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.01
        _ReflectionTint ("Reflection Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        LOD 200
        // Verified against the real vanilla SCM corpus (e.g. Building04_lod0,
        // UCA0105D1_lod0): triangle winding relative to each triangle's own
        // stored normal is roughly a 50/50 mix, not consistently one
        // direction — these meshes were never authored/exported with a
        // single winding convention Unity's default Cull Back can rely on.
        // With backface culling on, that leaves large "swiss cheese" holes
        // (or an effectively empty-looking model) regardless of view angle.
        // No real geometry is double-sided by design here, so rendering
        // both sides is strictly an improvement, never a regression.
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            // GPU instancing (Objects tab, Units/Props batched rendering —
            // SC2GameAssetScenePreview draws every placed instance of one
            // blueprint in grouped Graphics.DrawMeshInstanced calls instead
            // of one MeshRenderer GameObject per instance). No per-instance
            // property block is used yet (every instance of one blueprint
            // still shares one _MainColor/_AdditionalColor today) — only the
            // per-instance transform matrix needs to vary, which is what
            // UNITY_SETUP_INSTANCE_ID wires up via unity_ObjectToWorld.
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _Diffuse; float4 _Diffuse_ST;
            sampler2D _Normal;
            sampler2D _Mask;
            sampler2D _Team;
            fixed4 _MainColor;
            fixed4 _AdditionalColor;
            fixed4 _ReflectionTint;
            float _TeamEnabled;
            float _Cutoff;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPosition : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float3 worldTangent : TEXCOORD3;
                float3 worldBitangent : TEXCOORD4;
                UNITY_FOG_COORDS(5)
            };

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _Diffuse);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldTangent = UnityObjectToWorldDir(input.tangent.xyz);
                output.worldBitangent = cross(output.worldNormal, output.worldTangent) * input.tangent.w;
                UNITY_TRANSFER_FOG(output, output.position);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 diffuse = tex2D(_Diffuse, input.uv);
                clip(diffuse.a - _Cutoff);

                // SC2 Unit shader: the Team alpha is intentionally used by all
                // three Mix stages, matching the supplied Blender node graph.
                half teamFactor = _TeamEnabled > 0.5h ? tex2D(_Team, input.uv).a : 0.0h;
                half3 mixColor1 = lerp(diffuse.rgb, _AdditionalColor.rgb, teamFactor);
                half3 mixColor2 = lerp(diffuse.rgb, _MainColor.rgb, teamFactor);
                half3 baseColor = lerp(mixColor1, mixColor2, teamFactor);

                // SupCom 2 normals are regular tangent-space RGB textures.
                half3 normalSample = tex2D(_Normal, input.uv).rgb * 2.0h - 1.0h;
                half3 worldNormal = normalize(
                    input.worldTangent * normalSample.x +
                    input.worldBitangent * normalSample.y +
                    input.worldNormal * normalSample.z);

                half3 mask = tex2D(_Mask, input.uv).rgb;
                half3 lightDirection = normalize(UnityWorldSpaceLightDir(input.worldPosition));
                half3 viewDirection = normalize(_WorldSpaceCameraPos.xyz - input.worldPosition);
                half ndotl = saturate(dot(worldNormal, lightDirection));
                half3 lit = baseColor * (UNITY_LIGHTMODEL_AMBIENT.rgb + _LightColor0.rgb * ndotl);

                half3 reflectedLight = reflect(-lightDirection, worldNormal);
                half specularPower = lerp(4.0h, 128.0h, mask.b);
                half specular = pow(saturate(dot(reflectedLight, viewDirection)), specularPower) * mask.b;
                lit += _LightColor0.rgb * specular;

                // Mask R is the SC2 reflection strength. Ambient-tinted
                // reflection is the deterministic fallback until the model
                // browser supplies a per-map reflection probe/cubemap.
                lit += UNITY_LIGHTMODEL_AMBIENT.rgb * _ReflectionTint.rgb * mask.r;
                lit += baseColor * mask.g;
                UNITY_APPLY_FOG(input.fogCoord, lit);
                return fixed4(lit, diffuse.a);
            }
            ENDCG
        }
    }

    Fallback "Diffuse"
}
