// SC2MarkerModel.shader
// The marker gizmo models (SC2MarkerScenePreview) drawn as GPU instances. Every instance carries the Z mirror of the marker (a matrix
// with a negative determinant), and an instanced draw does not flip the winding of such a matrix the way a single renderer does, so the
// faces of the model come out inside out under the Standard shader (which has no cull setting to correct it). This shader culls the
// front faces by default (_Cull = Front), which undoes the mirror.
//
// Lives under Resources/ so a standalone build always includes it (it is only ever bound at runtime through `new Material(shader)`).

Shader "SC2/MarkerModel"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Color", Color) = (1, 1, 1, 1)
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 albedo = tex2D(_MainTex, i.uv) * _Color;
                float3 n = normalize(i.worldNormal);
                // Half-lambert, so a face turned away from the sun is dim rather than black, plus the ambient light.
                float nl = saturate(dot(n, normalize(_WorldSpaceLightPos0.xyz))) * 0.5 + 0.5;
                half3 light = nl * _LightColor0.rgb + ShadeSH9(float4(n, 1));
                return fixed4(albedo.rgb * light, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
